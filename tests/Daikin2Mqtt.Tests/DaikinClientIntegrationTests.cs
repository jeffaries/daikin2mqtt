using System.Net;
using System.Net.Sockets;
using System.Text;
using Daikin2Mqtt.Daikin;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daikin2Mqtt.Tests;

/// <summary>The HTTP client and the polling client against <see cref="MockDaikinServer"/>.</summary>
public class DaikinClientIntegrationTests : IAsyncLifetime
{
    private readonly MockDaikinServer _server = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DaikinOptions _options = new()
    {
        RequestTimeoutSeconds = 5, RequestRetries = 2, RetryDelayMilliseconds = 1, OfflineGraceSeconds = 0,
        Polling = new PollingSettings { IntervalSeconds = 1, EnergyIntervalSeconds = 300 },
    };

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly CapturingLogger _log = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private DaikinUnitOptions Unit => new() { Id = "salon", Host = _server.Host, Name = null };

    private DaikinHttpApi Api() => new(_http, Unit, _options, new DaikinDiagnostics(), _log);

    private DaikinPollingClient Client() => new(Api(), Unit, _options, new DaikinDiagnostics(), _log);

    private static async Task<T> Next<T>(TaskCompletionSource<T> source, string what)
    {
        var done = await Task.WhenAny(source.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == source.Task, $"Timed out waiting for {what}.");
        return await source.Task;
    }

    // ---- DaikinHttpApi -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Api_ReadsTheEndpoints()
    {
        var api = Api();

        Assert.Equal("Daikin", (await api.GetBasicInfoAsync(_cts.Token))["name"]);
        Assert.Equal("22.0", (await api.GetControlInfoAsync(_cts.Token))["stemp"]);
        Assert.Equal("24.0", (await api.GetSensorInfoAsync(_cts.Token))["htemp"]);
        Assert.Equal("0B66", (await api.GetModelInfoAsync(_cts.Token))["model"]);
    }

    [Fact]
    public async Task Api_SetControl_ReadsThenWritesAllSixParameters()
    {
        var sent = await Api().SetControlAsync(new DaikinCommand { Temperature = 24 }, _cts.Token);

        Assert.Equal("pow=1&mode=3&stemp=24.0&shum=0&f_rate=A&f_dir=0", sent.ToQuery());
        var write = Assert.Single(_server.RequestsTo("aircon/set_control_info"));
        Assert.Equal("?pow=1&mode=3&stemp=24.0&shum=0&f_rate=A&f_dir=0", write[write.IndexOf('?')..]);
        Assert.Equal("24.0", _server.Control["stemp"]);
        // read before write
        var requests = _server.Requests.ToArray();
        Assert.True(Array.IndexOf(requests, "aircon/get_control_info") < Array.FindIndex(requests, r => r.StartsWith("aircon/set_control_info")));
    }

    [Fact]
    public async Task Api_SetControl_DryAndFanModesSendNoTemperature()
    {
        var api = Api();

        await api.SetControlAsync(new DaikinCommand { Mode = "2" }, _cts.Token);
        Assert.Equal("M", _server.Control["stemp"]);

        await api.SetControlAsync(new DaikinCommand { Mode = "6" }, _cts.Token);
        Assert.Equal("--", _server.Control["stemp"]);

        // back to cooling: the unit's stored cooling target, not "M" nor "--"
        _server.Control["dt3"] = "25.0";
        await api.SetControlAsync(new DaikinCommand { Mode = "3" }, _cts.Token);
        Assert.Equal("25.0", _server.Control["stemp"]);
    }

    [Fact]
    public async Task Api_SerialisesRequestsPerUnit()
    {
        _server.DelayMilliseconds = 40;
        var api = Api();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => api.GetControlInfoAsync(_cts.Token)));

        Assert.Equal(1, _server.MaxConcurrent);
        Assert.Equal(6, _server.RequestsTo("aircon/get_control_info").Count());
    }

    [Fact]
    public async Task Api_Commands_AreAtomic()
    {
        // Without the command lock both read the same state and the second write undoes the first change.
        _server.DelayMilliseconds = 20;
        var api = Api();

        await Task.WhenAll(
            api.SetControlAsync(new DaikinCommand { Power = false }, _cts.Token),
            api.SetControlAsync(new DaikinCommand { FanRate = "5" }, _cts.Token),
            api.SetControlAsync(new DaikinCommand { SwingDirection = "3" }, _cts.Token));

        Assert.Equal("0", _server.Control["pow"]);
        Assert.Equal("5", _server.Control["f_rate"]);
        Assert.Equal("3", _server.Control["f_dir"]);
    }

    [Fact]
    public async Task Api_SetHoliday_CallsTheEndpoint()
    {
        await Api().SetHolidayAsync(true, _cts.Token);

        Assert.Equal("1", _server.Basic["en_hol"]);
        Assert.Contains("common/set_holiday?en_hol=1", _server.Requests);
    }

    [Fact]
    public async Task Api_NotFound_ThrowsEndpointNotFound()
    {
        _server.Missing["aircon/get_control_info"] = 0;

        var ex = await Assert.ThrowsAsync<DaikinEndpointNotFoundException>(() => Api().GetControlInfoAsync(_cts.Token));
        Assert.Equal("aircon/get_control_info", ex.Path);
        Assert.Single(_server.RequestsTo("aircon/get_control_info")); // a 404 is not retried
    }

    [Fact]
    public async Task Api_ParamNg_ThrowsProtocolException()
    {
        // The mock itself answers PARAM NG when a mandatory parameter is missing.
        Assert.Equal("ret=PARAM NG", await _http.GetStringAsync($"http://{_server.Host}/aircon/set_control_info?pow=1", _cts.Token));

        _server.RawAnswers["aircon/get_sensor_info"] = "ret=ADV_NG";
        var ex = await Assert.ThrowsAsync<DaikinProtocolException>(() => Api().GetSensorInfoAsync(_cts.Token));
        Assert.Equal("ADV_NG", ex.Ret);
        Assert.Single(_server.RequestsTo("aircon/get_sensor_info")); // a refusal is not retried
    }

    [Fact]
    public async Task Api_RetriesNetworkErrorsThenGivesUp()
    {
        _server.FailNext(2);
        Assert.Equal("22.0", (await Api().GetControlInfoAsync(_cts.Token))["stemp"]);
        Assert.Equal(3, _server.RequestsTo("aircon/get_control_info").Count());

        _server.Requests.Clear();
        _server.Failing = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => Api().GetControlInfoAsync(_cts.Token));
        Assert.Equal(3, _server.RequestsTo("aircon/get_control_info").Count()); // 1 + 2 retries
    }

    [Fact]
    public async Task Api_AnswerThatIsNotDaikinIsRejected()
    {
        using var raw = new HttpListenerRawServer("<html>hello</html>");
        var unit = new DaikinUnitOptions { Id = "x", Host = raw.Host };
        var api = new DaikinHttpApi(_http, unit, _options, new DaikinDiagnostics(), _log);

        await Assert.ThrowsAsync<DaikinException>(() => api.GetControlInfoAsync(_cts.Token));
    }

    [Fact]
    public async Task Api_EnergyIsReadFromTheFourEndpoints()
    {
        var energy = await Api().GetEnergyAsync(_cts.Token);

        Assert.Equal(146, energy.TodayRuntime);
        Assert.Equal(2.0, energy.Today);
        Assert.Equal(6.0, energy.Week);
        Assert.Equal(20.0, energy.Month);
        Assert.Equal(60.0, energy.Year);
    }

    [Fact]
    public async Task Api_EnergySurvivesOneMissingEndpointButNotAllOfThem()
    {
        _server.Missing["aircon/get_month_power_ex"] = 0;
        var energy = await Api().GetEnergyAsync(_cts.Token);
        Assert.Null(energy.Month);
        Assert.Equal(2.0, energy.Today);

        foreach (var path in _server.Energy.Keys)
        {
            _server.Missing[path] = 0;
        }

        await Assert.ThrowsAsync<DaikinEndpointNotFoundException>(() => Api().GetEnergyAsync(_cts.Token));
    }

    // ---- DaikinPollingClient -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Client_PublishesInfoThenStateThenIsAvailable()
    {
        var client = Client();
        var info = new TaskCompletionSource<DaikinUnitInfo>();
        var state = new TaskCompletionSource<DaikinSnapshot>();
        var available = new TaskCompletionSource<bool>();
        client.InfoAvailable += i => info.TrySetResult(i);
        client.StateChanged += s => state.TrySetResult(s);
        client.AvailabilityChanged += a => available.TrySetResult(a);

        var run = client.RunAsync(_cts.Token);

        var i = await Next(info, "the unit info");
        Assert.Equal("Daikin", i.Name);
        Assert.Equal("a1:b2:c3:d4:e5:f6", i.FormattedMac);
        Assert.Equal("1_14_88", i.Firmware);
        Assert.Equal("0B66 (N)", i.ModelLabel);
        Assert.True(i.SupportsEnergy);
        Assert.True(i.SupportsHoliday);
        Assert.True(i.Capabilities.FanRate);

        var s = await Next(state, "the first state");
        Assert.True(s.Power);
        Assert.Equal(22.0, s.TargetTemperature);
        Assert.Equal(24.0, s.IndoorTemperature);
        Assert.Equal(15.0, s.OutdoorTemperature);
        Assert.Null(s.IndoorHumidity);
        Assert.Equal(0, s.CompressorFrequency); // 999 = idle
        Assert.Equal(-51, DaikinResponseParser.ParseNumber(s.Basic["radio1"]));

        Assert.True(await Next(available, "availability"));
        Assert.True(client.IsAvailable);
        Assert.Equal(2.0, client.Snapshot!.Energy!.Today);

        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Client_PollsEveryInterval()
    {
        var client = Client();
        var states = 0;
        client.StateChanged += _ => Interlocked.Increment(ref states);

        var run = client.RunAsync(_cts.Token);
        for (var i = 0; i < 60 && Volatile.Read(ref states) < 3; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(Volatile.Read(ref states) >= 3, "expected at least 3 polls");
        // identity, model and the Wi-Fi block are not read at every poll
        Assert.Single(_server.RequestsTo("aircon/get_model_info"));
        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Client_SendReadsTheStateBackImmediately()
    {
        var client = Client();
        var first = new TaskCompletionSource<DaikinSnapshot>();
        client.StateChanged += s => first.TrySetResult(s);
        var run = client.RunAsync(_cts.Token);
        await Next(first, "the first state");

        var updated = new TaskCompletionSource<DaikinSnapshot>();
        client.StateChanged += s =>
        {
            if (s.TargetTemperature == 25.5)
            {
                updated.TrySetResult(s);
            }
        };

        await client.SendAsync(new DaikinCommand { Temperature = 25.5 }, _cts.Token);

        Assert.Equal("25.5", _server.Control["stemp"]);
        Assert.Equal(25.5, (await Next(updated, "the new state")).TargetTemperature);
        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Client_SendFailureIsReportedToTheCaller()
    {
        var client = Client();
        _server.Failing = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new DaikinCommand { Power = false }, _cts.Token));
    }

    [Fact]
    public async Task Client_GoesOfflineThenComesBack()
    {
        var client = Client();
        var changes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        client.AvailabilityChanged += changes.Enqueue;
        var run = client.RunAsync(_cts.Token);

        await WaitFor(() => changes.Count >= 1);
        _server.Failing = true;
        await WaitFor(() => changes.Count >= 2);
        _server.Failing = false;
        await WaitFor(() => changes.Count >= 3);

        Assert.Equal(new[] { true, false, true }, changes.ToArray());
        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Client_ToleratesAShortSilenceWithinTheGracePeriod()
    {
        _options.OfflineGraceSeconds = 600;
        var client = Client();
        var changes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        client.AvailabilityChanged += changes.Enqueue;
        var run = client.RunAsync(_cts.Token);
        await WaitFor(() => changes.Count >= 1);

        _server.Failing = true;
        _server.Requests.Clear();
        await WaitFor(() => _server.Requests.Count >= 2);

        Assert.Equal(new[] { true }, changes.ToArray());
        Assert.True(client.IsAvailable);
        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Client_UnsupportedFirmwareIsReportedClearly()
    {
        _server.Missing["common/basic_info"] = 0;
        var client = Client();
        var run = client.RunAsync(_cts.Token);

        await WaitFor(() => _log.Errors.Count > 0);

        Assert.Contains("firmware not supported", _log.Errors[0]);
        Assert.Contains("2.8.0", _log.Errors[0]);
        Assert.False(client.IsAvailable);
        Assert.Null(client.Info);
        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Client_WithoutEnergyEndpointsDoesNotOfferEnergy()
    {
        foreach (var path in _server.Energy.Keys)
        {
            _server.Missing[path] = 0;
        }

        var client = Client();
        var info = new TaskCompletionSource<DaikinUnitInfo>();
        client.InfoAvailable += i => info.TrySetResult(i);
        var run = client.RunAsync(_cts.Token);

        Assert.False((await Next(info, "the unit info")).SupportsEnergy);
        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Client_DisabledEnergyIsNeverRequested()
    {
        _options.EnableEnergy = false;
        var client = Client();
        var info = new TaskCompletionSource<DaikinUnitInfo>();
        client.InfoAvailable += i => info.TrySetResult(i);
        var run = client.RunAsync(_cts.Token);

        Assert.False((await Next(info, "the unit info")).SupportsEnergy);
        Assert.Empty(_server.RequestsTo("aircon/get_week_power"));
        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Client_ModelInfoFailureFallsBackOnAStandardUnit()
    {
        _server.Missing["aircon/get_model_info"] = 0;
        var client = Client();
        var info = new TaskCompletionSource<DaikinUnitInfo>();
        client.InfoAvailable += i => info.TrySetResult(i);
        var run = client.RunAsync(_cts.Token);

        var i = await Next(info, "the unit info");
        Assert.True(i.Capabilities.FanRate);
        Assert.False(i.Capabilities.SpecialMode);
        _cts.Cancel();
        await run;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "Condition not reached in time.");
    }
}

/// <summary>A tiny server answering the same body to everything (what a non-Daikin device on that address would do).</summary>
internal sealed class HttpListenerRawServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public HttpListenerRawServer(string body)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Host = $"127.0.0.1:{port}";
        _listener.Prefixes.Add($"http://{Host}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var context = await _listener.GetContextAsync();
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
            catch
            {
                // closed
            }
        });
    }

    public string Host { get; }

    public void Dispose() => _listener.Close();
}

public class DaikinScannerTests
{
    [Fact]
    public async Task Scan_ListsTheUnitsThatAnswer()
    {
        using var module = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)module.Client.LocalEndPoint!).Port;
        string? received = null;
        var responder = Task.Run(async () =>
        {
            var request = await module.ReceiveAsync();
            received = Encoding.ASCII.GetString(request.Buffer);
            var answer = Encoding.ASCII.GetBytes("ret=OK,type=aircon,reg=EU,ver=1_14_88,mac=A1B2C3D4E5F6,name=%44%61%69%6b%69%6e");
            await module.SendAsync(answer, answer.Length, request.RemoteEndPoint);
        });

        var scanner = new DaikinScanner(port, () => new[] { IPAddress.Loopback });
        var found = await scanner.ScanAsync(TimeSpan.FromMilliseconds(800), CancellationToken.None);
        await responder;

        Assert.Equal("DAIKIN_UDP/common/basic_info", received);
        var unit = Assert.Single(found);
        Assert.Equal("127.0.0.1", unit.Host);
        Assert.Equal("Daikin", unit.Name);
        Assert.Equal("A1B2C3D4E5F6", unit.Mac);
        Assert.Equal("1_14_88", unit.Firmware);
    }

    [Fact]
    public async Task Scan_NothingAnswersGivesAnEmptyList()
    {
        var scanner = new DaikinScanner(9, () => new[] { IPAddress.Loopback });

        var found = await scanner.ScanAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact]
    public void BroadcastAddresses_AlwaysIncludeTheLimitedBroadcast() =>
        Assert.Contains(IPAddress.Broadcast, DaikinScanner.BroadcastAddresses());
}
