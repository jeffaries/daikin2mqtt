using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Daikin2Mqtt.Tests;

/// <summary>
/// Minimal in-process imitation of the classic Daikin BRP069C4x HTTP API (HttpListener): <c>ret=OK,key=value,...</c> answers,
/// <c>set_control_info</c> with its six mandatory parameters, energy endpoints, and switches to simulate failures.
/// </summary>
public sealed class MockDaikinServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly CancellationTokenSource _stop = new();
    private int _active;
    private int _maxConcurrent;
    private int _failNext;

    public MockDaikinServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public string Host => $"127.0.0.1:{Port}";

    public ConcurrentDictionary<string, string> Basic { get; } = new(new Dictionary<string, string>
    {
        ["type"] = "aircon", ["reg"] = "EU", ["ver"] = "1_14_88", ["pow"] = "0", ["err"] = "0", ["name"] = "%44%61%69%6b%69%6e",
        ["mac"] = "A1B2C3D4E5F6", ["radio1"] = "-51", ["en_hol"] = "0", ["led"] = "1",
    });

    public ConcurrentDictionary<string, string> Control { get; } = new(new Dictionary<string, string>
    {
        ["pow"] = "1", ["mode"] = "3", ["adv"] = "", ["stemp"] = "22.0", ["shum"] = "0", ["dt1"] = "23.0", ["dt2"] = "M", ["dt3"] = "24.5",
        ["dt4"] = "20.0", ["dt7"] = "23.0", ["alert"] = "255", ["f_rate"] = "A", ["f_dir"] = "0",
    });

    public ConcurrentDictionary<string, string> Sensor { get; } = new(new Dictionary<string, string>
    {
        ["htemp"] = "24.0", ["hhum"] = "-", ["otemp"] = "15.0", ["err"] = "0", ["cmpfreq"] = "999",
    });

    public ConcurrentDictionary<string, string> Model { get; } = new(new Dictionary<string, string>
    {
        ["model"] = "0B66", ["type"] = "N", ["humd"] = "0", ["s_humd"] = "0", ["temp"] = "1", ["en_frate"] = "1", ["en_fdir"] = "1",
        ["s_fdir"] = "1", ["en_spmode"] = "0", ["en_onofftmr"] = "1",
    });

    /// <summary>Complete answers (after "ret=OK,") by path of the energy endpoints.</summary>
    public ConcurrentDictionary<string, string> Energy { get; } = new(new Dictionary<string, string>
    {
        ["aircon/get_week_power"] = "today_runtime=146,datas=10/20/0/0/0/0/30",
        ["aircon/get_day_power_ex"] = "curr_day_heat=0/0/5/5/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0,curr_day_cool=10/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0",
        ["aircon/get_month_power_ex"] = "curr_month_heat=50/50,curr_month_cool=100/0",
        ["aircon/get_year_power_ex"] = "curr_year_heat=100/200,curr_year_cool=300",
    });

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Complete body returned for a path, whatever it is (for example "ret=PARAM NG").</summary>
    public ConcurrentDictionary<string, string> RawAnswers { get; } = new();

    /// <summary>Paths (without query) that answer 404.</summary>
    public ConcurrentDictionary<string, byte> Missing { get; } = new();

    /// <summary>Every request answers 500.</summary>
    public volatile bool Failing;

    /// <summary>Milliseconds each request takes.</summary>
    public volatile int DelayMilliseconds;

    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    /// <summary>The next <paramref name="count"/> requests answer 500.</summary>
    public void FailNext(int count) => Volatile.Write(ref _failNext, count);

    public IEnumerable<string> RequestsTo(string path) => Requests.ToArray().Where(r => r.Split('?')[0] == path);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var active = Interlocked.Increment(ref _active);
        int seen;
        while (active > (seen = Volatile.Read(ref _maxConcurrent)))
        {
            Interlocked.CompareExchange(ref _maxConcurrent, active, seen);
        }

        try
        {
            var path = context.Request.Url!.AbsolutePath.Trim('/');
            Requests.Enqueue(path + context.Request.Url.Query);
            if (DelayMilliseconds > 0)
            {
                await Task.Delay(DelayMilliseconds).ConfigureAwait(false);
            }

            if (Failing || Interlocked.Decrement(ref _failNext) >= 0)
            {
                context.Response.StatusCode = 500;
            }
            else if (Missing.ContainsKey(path))
            {
                context.Response.StatusCode = 404;
            }
            else
            {
                var body = Answer(path, context.Request.QueryString);
                if (body is null)
                {
                    context.Response.StatusCode = 404;
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "text/plain";
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            context.Response.Close();
        }
    }

    private string? Answer(string path, System.Collections.Specialized.NameValueCollection query)
    {
        static string Format(IEnumerable<KeyValuePair<string, string>> values) =>
            "ret=OK," + string.Join(',', values.Select(kv => $"{kv.Key}={kv.Value}"));

        if (RawAnswers.TryGetValue(path, out var raw))
        {
            return raw;
        }

        switch (path)
        {
            case "common/basic_info":
                return Format(Basic);
            case "aircon/get_control_info":
                return Format(Control);
            case "aircon/get_sensor_info":
                return Format(Sensor);
            case "aircon/get_model_info":
                return Format(Model);
            case "common/set_holiday":
                Basic["en_hol"] = query["en_hol"] ?? "0";
                return "ret=OK";
            case "aircon/set_control_info":
                var keys = new[] { "pow", "mode", "stemp", "shum", "f_rate", "f_dir" };
                if (keys.Any(k => query[k] is null))
                {
                    return "ret=PARAM NG";
                }

                foreach (var key in keys)
                {
                    Control[key] = query[key]!;
                }

                return "ret=OK";
            default:
                return Energy.TryGetValue(path, out var energy) ? "ret=OK," + energy : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        await _loop.ConfigureAwait(false);
    }
}
