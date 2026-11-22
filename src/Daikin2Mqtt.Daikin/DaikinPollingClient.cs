using Microsoft.Extensions.Logging;

namespace Daikin2Mqtt.Daikin;

/// <summary>
/// Driver of the classic local API for one unit. The module has no push channel, so it polls: control and sensors every
/// <c>Polling.IntervalSeconds</c>, the identity once, the Wi-Fi block every minute, the energy every
/// <c>Polling.EnergyIntervalSeconds</c>. A unit is reported unavailable once it has been silent for
/// <c>OfflineGraceSeconds</c>.
/// </summary>
public sealed class DaikinPollingClient : IDaikinClient
{
    private static readonly TimeSpan BasicInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan UnsupportedRetryDelay = TimeSpan.FromMinutes(5);

    private readonly DaikinHttpApi _api;
    private readonly DaikinOptions _options;
    private readonly DaikinDiagnostics _diagnostics;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private IReadOnlyDictionary<string, string> _control = DaikinSnapshot.Empty;
    private IReadOnlyDictionary<string, string> _sensor = DaikinSnapshot.Empty;
    private IReadOnlyDictionary<string, string> _basic = DaikinSnapshot.Empty;
    private DaikinEnergy? _energy;
    private DaikinUnitInfo? _info;
    private DaikinSnapshot? _snapshot;
    private bool _available;
    private DateTimeOffset _lastSuccess;
    private DateTimeOffset _lastBasic = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEnergy = DateTimeOffset.MinValue;
    private bool _failing;
    private bool _unsupportedReported;

    public DaikinPollingClient(
        DaikinHttpApi api,
        DaikinUnitOptions unit,
        DaikinOptions options,
        DaikinDiagnostics diagnostics,
        ILogger logger,
        Func<DateTimeOffset>? clock = null)
    {
        _api = api;
        Unit = unit;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DaikinUnitOptions Unit { get; }

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    public DaikinUnitInfo? Info
    {
        get
        {
            lock (_gate)
            {
                return _info;
            }
        }
    }

    public DaikinSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public event Action<DaikinUnitInfo>? InfoAvailable;
    public event Action<DaikinSnapshot>? StateChanged;
    public event Action<bool>? AvailabilityChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Polling.IntervalSeconds));
        var failures = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var delay = interval;
                try
                {
                    await PollOnceAsync(ct).ConfigureAwait(false);
                    failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (DaikinEndpointNotFoundException ex) when (IsCoreEndpoint(ex.Path))
                {
                    ReportUnsupportedFirmware(ex);
                    OnFailure();
                    delay = UnsupportedRetryDelay;
                }
                catch (Exception ex)
                {
                    failures++;
                    ReportFailure(ex);
                    OnFailure();
                    // Wait longer after each failure, never less than the normal period.
                    delay = TimeSpan.FromTicks(Math.Min(interval.Ticks * (1L << Math.Min(failures - 1, 6)), Math.Max(interval.Ticks, MaxRetryDelay.Ticks)));
                }

                _diagnostics.ReportIfDue(_logger, TimeSpan.FromMinutes(5));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
    }

    public async Task SendAsync(DaikinCommand command, CancellationToken ct)
    {
        if (command.HasControlChange)
        {
            var sent = await _api.SetControlAsync(command, ct).ConfigureAwait(false);
            _logger.LogInformation("Unit {Unit}: {Command} -> {Query}", Unit.Id, command, sent.ToQuery());
        }

        if (command.Holiday is { } holiday)
        {
            await _api.SetHolidayAsync(holiday, ct).ConfigureAwait(false);
            _logger.LogInformation("Unit {Unit}: holiday mode {State}", Unit.Id, holiday ? "on" : "off");
        }

        if (command.Streamer is { } streamer)
        {
            await _api.SetStreamerAsync(streamer, ct).ConfigureAwait(false);
            _logger.LogInformation("Unit {Unit}: streamer {State}", Unit.Id, streamer ? "on" : "off");
        }

        foreach (var mode in command.SpecialModes ?? Array.Empty<SpecialModeChange>())
        {
            try
            {
                await _api.SetSpecialModeAsync(mode.Kind, mode.On, ct).ConfigureAwait(false);
                _logger.LogInformation("Unit {Unit}: special mode {Kind} {State}", Unit.Id, mode.Kind, mode.On ? "on" : "off");
            }
            catch (DaikinProtocolException ex) when (!mode.On)
            {
                // Switching off a mode that is not active may be refused: the result is the one wanted anyway.
                _logger.LogDebug("Unit {Unit}: special mode {Kind} off refused: {Reason}", Unit.Id, mode.Kind, ex.Message);
            }
        }

        // The command worked: tell Home Assistant what the unit now reports, without waiting for the next poll.
        try
        {
            await RefreshAsync(includeBasic: command.Holiday is not null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Unit {Unit}: could not read the state back after a command: {Reason}", Unit.Id, ex.Message);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (Info is null)
        {
            await LoadInfoAsync(ct).ConfigureAwait(false);
        }

        var now = _clock();
        await RefreshAsync(includeBasic: now - _lastBasic >= BasicInterval, ct).ConfigureAwait(false);
        OnSuccess();

        if (Info is { SupportsEnergy: true } && _options.EnableEnergy
            && now - _lastEnergy >= TimeSpan.FromSeconds(Math.Max(1, _options.Polling.EnergyIntervalSeconds)))
        {
            try
            {
                var energy = await _api.GetEnergyAsync(ct).ConfigureAwait(false);
                _lastEnergy = now;
                SetEnergy(energy);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Energy is a bonus: its failure must not make the unit look unavailable.
                _lastEnergy = now;
                _logger.LogDebug("Unit {Unit}: energy polling failed: {Reason}", Unit.Id, ex.Message);
            }
        }
    }

    /// <summary>First contact: identity, capabilities and whether the energy endpoints exist.</summary>
    private async Task LoadInfoAsync(CancellationToken ct)
    {
        var basic = await _api.GetBasicInfoAsync(ct).ConfigureAwait(false);

        var capabilities = DaikinCapabilities.Default with
        {
            MinTemperature = Unit.MinTemperature ?? DaikinCapabilities.DefaultMinTemperature,
            MaxTemperature = Unit.MaxTemperature ?? DaikinCapabilities.DefaultMaxTemperature,
        };
        try
        {
            capabilities = DaikinCapabilities.Parse(await _api.GetModelInfoAsync(ct).ConfigureAwait(false), Unit);
        }
        catch (Exception ex) when (ex is DaikinException or HttpRequestException or TimeoutException)
        {
            _logger.LogWarning("Unit {Unit}: get_model_info failed ({Reason}); assuming a standard unit", Unit.Id, ex.Message);
        }

        var supportsEnergy = false;
        DaikinEnergy? energy = null;
        if (_options.EnableEnergy)
        {
            try
            {
                energy = await _api.GetEnergyAsync(ct).ConfigureAwait(false);
                supportsEnergy = true;
            }
            catch (Exception ex) when (ex is DaikinException or HttpRequestException or TimeoutException)
            {
                _logger.LogInformation("Unit {Unit}: no energy data ({Reason}); the energy sensors are not published", Unit.Id, ex.Message);
            }
        }

        var info = new DaikinUnitInfo(
            Unit,
            basic.GetValueOrDefault("name"),
            basic.GetValueOrDefault("mac"),
            basic.GetValueOrDefault("ver"),
            capabilities,
            supportsEnergy,
            SupportsHoliday: basic.ContainsKey("en_hol"));

        lock (_gate)
        {
            _info = info;
            _basic = basic;
            _lastBasic = _clock();
            _energy = energy;
            _lastEnergy = _clock();
        }

        _logger.LogInformation(
            "Unit {Unit} ({Host}): model {Model}, firmware {Firmware}{Energy}",
            Unit.Id, Unit.Host, info.ModelLabel ?? "?", info.Firmware ?? "?", supportsEnergy ? ", energy available" : string.Empty);
        InfoAvailable?.Invoke(info);
    }

    private async Task RefreshAsync(bool includeBasic, CancellationToken ct)
    {
        var control = await _api.GetControlInfoAsync(ct).ConfigureAwait(false);
        var sensor = await _api.GetSensorInfoAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, string>? basic = null;
        if (includeBasic)
        {
            try
            {
                basic = await _api.GetBasicInfoAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DaikinException or HttpRequestException or TimeoutException)
            {
                _logger.LogDebug("Unit {Unit}: basic_info failed: {Reason}", Unit.Id, ex.Message);
            }
        }

        DaikinSnapshot snapshot;
        lock (_gate)
        {
            _control = control;
            _sensor = sensor;
            if (basic is not null)
            {
                _basic = basic;
                _lastBasic = _clock();
            }

            snapshot = _snapshot = BuildSnapshot();
        }

        StateChanged?.Invoke(snapshot);
    }

    private void SetEnergy(DaikinEnergy energy)
    {
        DaikinSnapshot snapshot;
        lock (_gate)
        {
            _energy = energy;
            snapshot = _snapshot = BuildSnapshot();
        }

        StateChanged?.Invoke(snapshot);
    }

    private DaikinSnapshot BuildSnapshot() => new(_clock(), _control, _sensor, _basic, _energy);

    private void OnSuccess()
    {
        bool changed;
        lock (_gate)
        {
            _lastSuccess = _clock();
            changed = !_available;
            _available = true;
        }

        if (_failing)
        {
            _failing = false;
            _logger.LogInformation("Unit {Unit} answers again", Unit.Id);
        }

        _unsupportedReported = false;
        if (changed)
        {
            _logger.LogInformation("Unit {Unit} is now available", Unit.Id);
            AvailabilityChanged?.Invoke(true);
        }
    }

    private void OnFailure()
    {
        bool changed;
        lock (_gate)
        {
            var silent = _clock() - _lastSuccess;
            changed = _available && silent >= TimeSpan.FromSeconds(Math.Max(0, _options.OfflineGraceSeconds));
            if (changed)
            {
                _available = false;
            }
        }

        if (changed)
        {
            _logger.LogWarning("Unit {Unit} is now unavailable", Unit.Id);
            AvailabilityChanged?.Invoke(false);
        }
    }

    private void ReportFailure(Exception ex)
    {
        if (!_failing)
        {
            _failing = true;
            _logger.LogWarning("Unit {Unit} ({Host}): polling failed: {Reason}", Unit.Id, Unit.Host, ex.Message);
        }
        else
        {
            _logger.LogDebug("Unit {Unit}: polling still failing: {Reason}", Unit.Id, ex.Message);
        }
    }

    private void ReportUnsupportedFirmware(DaikinEndpointNotFoundException ex)
    {
        if (_unsupportedReported)
        {
            return;
        }

        _unsupportedReported = true;
        _failing = true;
        _logger.LogError(
            "Unit {Unit} ({Host}): firmware not supported. {Path} does not exist: this module most likely runs firmware 2.8.0 or " +
            "newer, whose API (/dsiot/multireq, JSON) is not implemented yet. See 'Known limitations' in the README.",
            Unit.Id, Unit.Host, ex.Path);
    }

    private static bool IsCoreEndpoint(string path) => path is "common/basic_info" or "aircon/get_control_info" or "aircon/get_sensor_info";
}
