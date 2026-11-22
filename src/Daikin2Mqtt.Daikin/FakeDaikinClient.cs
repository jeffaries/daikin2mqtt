namespace Daikin2Mqtt.Daikin;

/// <summary>
/// In-memory <see cref="IDaikinClient"/>: a simulated unit. Used by <c>Daikin.Fake: true</c> to see the entities in Home
/// Assistant without hardware, and by the tests. Commands go through the same read-modify-write logic as the real unit.
/// </summary>
public sealed class FakeDaikinClient : IDaikinClient
{
    private readonly object _gate = new();
    private readonly List<DaikinCommand> _commands = new();
    private Dictionary<string, string> _control = new()
    {
        ["pow"] = "0", ["mode"] = "3", ["adv"] = string.Empty, ["stemp"] = "24.0", ["shum"] = "0",
        ["dt1"] = "24.0", ["dt3"] = "24.0", ["dt4"] = "21.0", ["dt7"] = "24.0",
        ["alert"] = "255", ["f_rate"] = "A", ["f_dir"] = "0",
    };

    private Dictionary<string, string> _sensor = new() { ["htemp"] = "23.0", ["hhum"] = "-", ["otemp"] = "12.0", ["err"] = "0", ["cmpfreq"] = "999" };
    private Dictionary<string, string> _basic = new() { ["ver"] = "1_14_88", ["radio1"] = "-55", ["en_hol"] = "0", ["err"] = "0" };
    private DaikinEnergy? _energy = new()
    {
        TodayRuntime = 146, TodayHeat = 0.4, TodayCool = 1.2, Week = 9.8, MonthHeat = 3.1, MonthCool = 20.4, YearHeat = 120.5, YearCool = 310.2,
    };

    private DaikinSnapshot? _snapshot;
    private bool _available;

    public FakeDaikinClient(DaikinUnitOptions unit)
    {
        Unit = unit;
        var mac = string.Concat(unit.Id.Select(c => ((int)c % 256).ToString("X2"))).PadRight(12, '0')[..12];
        Identity = new DaikinUnitInfo(
            unit,
            unit.Name ?? unit.Id,
            mac,
            "1_14_88",
            DaikinCapabilities.Parse(
                new Dictionary<string, string> { ["model"] = "0B66", ["type"] = "N", ["en_frate"] = "1", ["en_fdir"] = "1", ["s_fdir"] = "1", ["temp"] = "1", ["en_spmode"] = "1", ["s_humd"] = "1" },
                unit),
            SupportsEnergy: true,
            SupportsHoliday: true);
    }

    public DaikinUnitOptions Unit { get; }

    public bool IsAvailable => _available;

    /// <summary>Null until <see cref="Connect"/>: like a real unit, the fake has no identity before it has answered.</summary>
    public DaikinUnitInfo? Info { get; private set; }

    /// <summary>What <see cref="Info"/> becomes once the unit has answered; tests may change it before <see cref="Connect"/>.</summary>
    public DaikinUnitInfo Identity { get; set; }

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

    /// <summary>Commands received through <see cref="SendAsync"/>.</summary>
    public IReadOnlyList<DaikinCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands.ToArray();
            }
        }
    }

    /// <summary>When set, <see cref="SendAsync"/> throws it (simulates a unit that refuses or does not answer).</summary>
    public Exception? SendFailure { get; set; }

    public event Action<DaikinUnitInfo>? InfoAvailable;
    public event Action<DaikinSnapshot>? StateChanged;
    public event Action<bool>? AvailabilityChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        Connect();
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    /// <summary>The unit answers: identity, first state, then available.</summary>
    public void Connect()
    {
        Info = Identity;
        InfoAvailable?.Invoke(Identity);
        Publish();
        SetAvailable(true);
    }

    public void SetAvailable(bool available)
    {
        if (_available == available)
        {
            return;
        }

        _available = available;
        AvailabilityChanged?.Invoke(available);
    }

    /// <summary>Changes what the unit reports and raises <see cref="StateChanged"/> (like a poll would).</summary>
    public void Update(Action<IDictionary<string, string>>? control = null, Action<IDictionary<string, string>>? sensor = null,
        Action<IDictionary<string, string>>? basic = null, DaikinEnergy? energy = null)
    {
        lock (_gate)
        {
            control?.Invoke(_control);
            sensor?.Invoke(_sensor);
            basic?.Invoke(_basic);
            _energy = energy ?? _energy;
        }

        Publish();
    }

    public Task SendAsync(DaikinCommand command, CancellationToken ct)
    {
        if (SendFailure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            _commands.Add(command);
            if (command.HasControlChange)
            {
                var request = DaikinControlRequest.Build(_control, command);
                _control["pow"] = request.Pow;
                _control["mode"] = request.Mode;
                _control["stemp"] = request.Stemp;
                _control["shum"] = request.Shum;
                _control["f_rate"] = request.FRate;
                _control["f_dir"] = request.FDir;
                if (request.Stemp != DaikinValues.NoTemperatureDry && request.Stemp != DaikinValues.NoTemperatureFan)
                {
                    _control[$"dt{request.Mode}"] = request.Stemp;
                }

                // The fake compressor follows the unit.
                _sensor["cmpfreq"] = request.Pow == "1" ? "42" : "999";
            }

            if (command.Holiday is { } holiday)
            {
                _basic["en_hol"] = holiday ? "1" : "0";
            }

            foreach (var mode in command.SpecialModes ?? Array.Empty<SpecialModeChange>())
            {
                var adv = mode.Kind == "1" ? "2" : "12";
                if (mode.On)
                {
                    _control["adv"] = adv;
                }
                else if (_control.GetValueOrDefault("adv") == adv)
                {
                    _control["adv"] = string.Empty;
                }
            }
        }

        Publish();
        return Task.CompletedTask;
    }

    private void Publish()
    {
        DaikinSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _snapshot = new DaikinSnapshot(
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>(_control),
                new Dictionary<string, string>(_sensor),
                new Dictionary<string, string>(_basic),
                _energy);
        }

        StateChanged?.Invoke(snapshot);
    }
}
