using Microsoft.Extensions.Logging;

namespace Daikin2Mqtt.Daikin;

/// <summary>
/// The classic local API of one unit (firmware below 2.8.0): HTTP GET on port 80, answers <c>ret=OK,key=value,...</c>.
/// The module serves a few connections at a time and takes 200-500 ms per request, so the requests of a unit are
/// sent one at a time. A command (read, modify, write) holds a second lock so that two commands cannot interleave.
/// </summary>
public sealed class DaikinHttpApi
{
    private readonly HttpClient _http;
    private readonly DaikinUnitOptions _unit;
    private readonly DaikinOptions _options;
    private readonly DaikinDiagnostics _diagnostics;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);

    public DaikinHttpApi(HttpClient http, DaikinUnitOptions unit, DaikinOptions options, DaikinDiagnostics diagnostics, ILogger logger)
    {
        _http = http;
        _unit = unit;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public Task<IReadOnlyDictionary<string, string>> GetBasicInfoAsync(CancellationToken ct) => GetAsync("common/basic_info", ct);

    public Task<IReadOnlyDictionary<string, string>> GetControlInfoAsync(CancellationToken ct) => GetAsync("aircon/get_control_info", ct);

    public Task<IReadOnlyDictionary<string, string>> GetSensorInfoAsync(CancellationToken ct) => GetAsync("aircon/get_sensor_info", ct);

    public Task<IReadOnlyDictionary<string, string>> GetModelInfoAsync(CancellationToken ct) => GetAsync("aircon/get_model_info", ct);

    /// <summary>Reads the four energy endpoints; one that fails is skipped. Throws only when none answers.</summary>
    public async Task<DaikinEnergy> GetEnergyAsync(CancellationToken ct)
    {
        var answers = new Dictionary<string, IReadOnlyDictionary<string, string>?>();
        Exception? last = null;
        foreach (var path in new[] { "aircon/get_week_power", "aircon/get_day_power_ex", "aircon/get_month_power_ex", "aircon/get_year_power_ex" })
        {
            try
            {
                answers[path] = await GetAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DaikinException or HttpRequestException or TimeoutException or IOException && !ct.IsCancellationRequested)
            {
                last = ex;
                answers[path] = null;
                _logger.LogDebug("Unit {Unit}: {Reason}", _unit.Id, ex.Message);
            }
        }

        if (answers.Values.All(a => a is null))
        {
            throw last ?? new DaikinException("No energy endpoint answered.");
        }

        return DaikinEnergy.Parse(
            answers["aircon/get_week_power"], answers["aircon/get_day_power_ex"],
            answers["aircon/get_month_power_ex"], answers["aircon/get_year_power_ex"]);
    }

    /// <summary>
    /// Applies <paramref name="change"/>: reads the current control values, replaces the requested fields and sends the
    /// six parameters back. Atomic for the unit. Returns what was sent.
    /// </summary>
    public async Task<DaikinControlRequest> SetControlAsync(DaikinCommand change, CancellationToken ct)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await GetControlInfoAsync(ct).ConfigureAwait(false);
            var request = DaikinControlRequest.Build(current, change);
            await GetAsync($"{DaikinControlRequest.Path}?{request.ToQuery()}", ct).ConfigureAwait(false);
            return request;
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task SetHolidayAsync(bool enabled, CancellationToken ct)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await GetAsync($"common/set_holiday?en_hol={(enabled ? 1 : 0)}", ct).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    /// <summary>One request: serialised per unit, retried on network errors, answer checked (<c>ret=OK</c>).</summary>
    private async Task<IReadOnlyDictionary<string, string>> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var path = pathAndQuery.Split('?')[0];
        var uri = new Uri($"http://{_unit.Host.Trim()}/{pathAndQuery}");
        var retries = Math.Max(0, _options.RequestRetries);
        var delay = TimeSpan.FromMilliseconds(Math.Max(1, _options.RetryDelayMilliseconds));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var body = await SendAsync(uri, path, ct).ConfigureAwait(false);
                var response = DaikinResponseParser.Parse(body);
                if (!response.IsOk)
                {
                    if (response.Ret.Length == 0)
                    {
                        _diagnostics.RecordRejected($"{path}: no ret", body, _logger);
                        throw new DaikinException($"{path} did not answer like a Daikin module.");
                    }

                    throw new DaikinProtocolException(path, response.Ret);
                }

                return response.Values;
            }
            catch (Exception ex) when (attempt < retries && !ct.IsCancellationRequested && IsTransient(ex))
            {
                _logger.LogDebug("Unit {Unit}: {Path} failed ({Reason}); retry {Attempt}/{Retries} in {Delay}",
                    _unit.Id, path, ex.Message, attempt + 1, retries, delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    private async Task<string> SendAsync(Uri uri, string path, CancellationToken ct)
    {
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));
            try
            {
                using var response = await _http.GetAsync(uri, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new DaikinEndpointNotFoundException(path);
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{path} did not answer within {_options.RequestTimeoutSeconds}s.");
            }
        }
        finally
        {
            _requests.Release();
        }
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or TimeoutException or IOException;
}
