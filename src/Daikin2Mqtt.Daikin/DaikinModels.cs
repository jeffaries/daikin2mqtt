using System.Globalization;

namespace Daikin2Mqtt.Daikin;

/// <summary>What a unit can do, from <c>aircon/get_model_info</c>. Entities the unit lacks are not published.</summary>
public sealed record DaikinCapabilities
{
    public const double DefaultMinTemperature = 10;
    public const double DefaultMaxTemperature = 32;

    public string? Model { get; init; }

    public string? Type { get; init; }

    /// <summary><c>en_frate</c>: the fan speed can be set.</summary>
    public bool FanRate { get; init; } = true;

    /// <summary><c>s_fdir</c> (or <c>en_fdir</c> when absent): the swing direction can be set.</summary>
    public bool Swing { get; init; } = true;
    /// <summary>Swing axes the unit has (<c>s_fdir</c>): 1 vertical, 2 horizontal, 3 both.</summary>
    public int SwingAxes { get; init; } = 3;

    /// <summary><c>humd</c>: target humidity (not exposed yet).</summary>
    public bool HumidityControl { get; init; }

    /// <summary><c>s_humd</c>: the unit has a humidity sensor.</summary>
    public bool HumiditySensor { get; init; }

    /// <summary><c>temp</c>: the unit has an indoor temperature sensor.</summary>
    public bool IndoorTemperature { get; init; } = true;

    /// <summary><c>en_spmode</c>: the unit has special ("advanced") modes: powerful, eco, streamer.</summary>
    public bool SpecialMode { get; init; }

    /// <summary><c>en_onofftmr</c>: on/off timer (informative, not exposed).</summary>
    public bool OnOffTimer { get; init; }

    public double MinTemperature { get; init; } = DefaultMinTemperature;

    public double MaxTemperature { get; init; } = DefaultMaxTemperature;

    /// <summary>Capabilities used when the unit does not answer <c>get_model_info</c>: everything a plain unit has.</summary>
    public static DaikinCapabilities Default { get; } = new();

    /// <summary>
    /// Reads <c>get_model_info</c>. A missing key means "unknown": fan, swing and temperature sensor are then assumed
    /// to exist (they are on nearly every unit), humidity and special modes are not.
    /// </summary>
    public static DaikinCapabilities Parse(IReadOnlyDictionary<string, string> model, DaikinUnitOptions? unit = null)
    {
        bool Flag(string key, bool whenMissing) =>
            model.TryGetValue(key, out var value) ? value.Trim() == "1" : whenMissing;

        // s_fdir is not a flag: 0 none, 1 vertical only, 2 horizontal only, 3 both.
        var swingAxes = model.TryGetValue("s_fdir", out var fdir) && int.TryParse(fdir.Trim(), out var axes)
            ? axes
            : (Flag("en_fdir", true) ? 3 : 0);
        var swing = swingAxes != 0;
        var min = unit?.MinTemperature ?? DefaultMinTemperature;
        var max = unit?.MaxTemperature ?? DefaultMaxTemperature;
        if (max <= min)
        {
            (min, max) = (DefaultMinTemperature, DefaultMaxTemperature);
        }

        return new DaikinCapabilities
        {
            Model = model.GetValueOrDefault("model"),
            Type = model.GetValueOrDefault("type"),
            FanRate = Flag("en_frate", true),
            Swing = swing,
            SwingAxes = swingAxes,
            HumidityControl = Flag("humd", false),
            HumiditySensor = Flag("s_humd", false),
            IndoorTemperature = Flag("temp", true),
            SpecialMode = Flag("en_spmode", false),
            OnOffTimer = Flag("en_onofftmr", false),
            MinTemperature = min,
            MaxTemperature = max,
        };
    }
}

/// <summary>Identity and capabilities of a unit, known once it has answered.</summary>
public sealed record DaikinUnitInfo(
    DaikinUnitOptions Unit,
    string? Name,
    string? Mac,
    string? Firmware,
    DaikinCapabilities Capabilities,
    bool SupportsEnergy,
    bool SupportsHoliday)
{
    /// <summary>Model shown in Home Assistant: "0B66 (N)" or just the code.</summary>
    public string? ModelLabel =>
        string.IsNullOrEmpty(Capabilities.Model) ? Capabilities.Type
        : string.IsNullOrEmpty(Capabilities.Type) ? Capabilities.Model
        : $"{Capabilities.Model} ({Capabilities.Type})";

    /// <summary>MAC formatted the way Home Assistant expects ("aa:bb:cc:dd:ee:ff").</summary>
    public string? FormattedMac
    {
        get
        {
            var hex = new string((Mac ?? string.Empty).Where(Uri.IsHexDigit).ToArray());
            return hex.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToLowerInvariant() : null;
        }
    }
}

/// <summary>Energy and runtime of a unit. Energies are in kWh (the module reports units of 100 Wh).</summary>
public sealed record DaikinEnergy
{
    /// <summary>Minutes the unit ran today.</summary>
    public double? TodayRuntime { get; init; }

    public double? TodayHeat { get; init; }
    public double? TodayCool { get; init; }
    public double? Today => Sum(TodayHeat, TodayCool);

    /// <summary>Last 7 days (rolling).</summary>
    public double? Week { get; init; }

    public double? MonthHeat { get; init; }
    public double? MonthCool { get; init; }
    public double? Month => Sum(MonthHeat, MonthCool);

    public double? YearHeat { get; init; }
    public double? YearCool { get; init; }

    /// <summary>Year total: heating + cooling when the unit splits them, else the plain yearly figure.</summary>
    public double? Year => Sum(YearHeat, YearCool) ?? YearTotal;

    public double? YearTotal { get; init; }

    private static double? Sum(double? a, double? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    /// <summary>Parses the answers of the four energy endpoints; an endpoint that failed is passed as null.</summary>
    public static DaikinEnergy Parse(
        IReadOnlyDictionary<string, string>? weekPower,
        IReadOnlyDictionary<string, string>? dayPowerEx,
        IReadOnlyDictionary<string, string>? monthPowerEx,
        IReadOnlyDictionary<string, string>? yearPowerEx)
    {
        static double? Kwh(IReadOnlyDictionary<string, string>? values, params string[] keys)
        {
            if (values is null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (values.TryGetValue(key, out var raw))
                {
                    return DaikinResponseParser.ParseSlashList(raw).Sum() / 10.0;
                }
            }

            return null;
        }

        return new DaikinEnergy
        {
            TodayRuntime = weekPower is not null && weekPower.TryGetValue("today_runtime", out var runtime)
                ? DaikinResponseParser.ParseNumber(runtime)
                : null,
            Week = Kwh(weekPower, "datas"),
            TodayHeat = Kwh(dayPowerEx, "curr_day_heat"),
            TodayCool = Kwh(dayPowerEx, "curr_day_cool"),
            MonthHeat = Kwh(monthPowerEx, "curr_month_heat"),
            MonthCool = Kwh(monthPowerEx, "curr_month_cool"),
            YearHeat = Kwh(yearPowerEx, "curr_year_heat", "this_year_heat"),
            YearCool = Kwh(yearPowerEx, "curr_year_cool", "this_year_cool"),
            YearTotal = Kwh(yearPowerEx, "this_year"),
        };
    }
}

/// <summary>Everything known about a unit at one moment: the last answers of each endpoint.</summary>
public sealed record DaikinSnapshot(
    DateTimeOffset Time,
    IReadOnlyDictionary<string, string> Control,
    IReadOnlyDictionary<string, string> Sensor,
    IReadOnlyDictionary<string, string> Basic,
    DaikinEnergy? Energy)
{
    public static IReadOnlyDictionary<string, string> Empty { get; } = new Dictionary<string, string>();

    public bool? Power => Control.TryGetValue("pow", out var pow) ? pow == "1" : null;

    public string? Mode => Control.GetValueOrDefault("mode");

    /// <summary>Target temperature; null in Dry and Fan modes ("M", "--").</summary>
    public double? TargetTemperature => DaikinResponseParser.ParseNumber(Control.GetValueOrDefault("stemp"));

    public double? IndoorTemperature => DaikinResponseParser.ParseNumber(Sensor.GetValueOrDefault("htemp"));

    public double? OutdoorTemperature => DaikinResponseParser.ParseNumber(Sensor.GetValueOrDefault("otemp"));

    public double? IndoorHumidity => DaikinResponseParser.ParseNumber(Sensor.GetValueOrDefault("hhum"));

    /// <summary>Compressor frequency in Hz; 999 (and 0) means it is not running.</summary>
    public double? CompressorFrequency =>
        DaikinResponseParser.ParseNumber(Sensor.GetValueOrDefault("cmpfreq")) is { } frequency
            ? (frequency >= 999 ? 0 : frequency)
            : null;
}

/// <summary>A change requested by Home Assistant. Only the set fields are changed.</summary>
public sealed record DaikinCommand
{
    public bool? Power { get; init; }

    /// <summary>Daikin <c>mode</c> value.</summary>
    public string? Mode { get; init; }

    public double? Temperature { get; init; }

    /// <summary>Daikin <c>f_rate</c> value.</summary>
    public string? FanRate { get; init; }

    /// <summary>Daikin <c>f_dir</c> value.</summary>
    public string? SwingDirection { get; init; }

    public bool? Holiday { get; init; }

    /// <summary>Streamer on or off (<c>aircon/set_special_mode?en_streamer</c>).</summary>
    public bool? Streamer { get; init; }

    /// <summary>Special modes to switch (<c>aircon/set_special_mode</c>).</summary>
    public IReadOnlyList<SpecialModeChange>? SpecialModes { get; init; }

    public bool HasControlChange => Power is not null || Mode is not null || Temperature is not null || FanRate is not null || SwingDirection is not null;

    public override string ToString() =>
        string.Join(' ', new[]
        {
            Power is { } p ? $"pow={(p ? 1 : 0)}" : null,
            Mode is { } m ? $"mode={m}" : null,
            Temperature is { } t ? $"stemp={t.ToString(CultureInfo.InvariantCulture)}" : null,
            FanRate is { } f ? $"f_rate={f}" : null,
            SwingDirection is { } d ? $"f_dir={d}" : null,
            Holiday is { } h ? $"en_hol={(h ? 1 : 0)}" : null,
            Streamer is { } st ? $"en_streamer={(st ? 1 : 0)}" : null,
            SpecialModes is { } modes ? string.Join(' ', modes.Select(m => $"spmode_kind={m.Kind}:{(m.On ? 1 : 0)}")) : null,
        }.Where(s => s is not null));
}

/// <summary>A special mode to switch on or off: <c>Kind</c> is the Daikin <c>spmode_kind</c> (1 powerful, 2 econo).</summary>
public sealed record SpecialModeChange(string Kind, bool On);

public class DaikinException : Exception
{
    public DaikinException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>The module answered with something else than <c>ret=OK</c> (PARAM NG, ADV_NG...).</summary>
public sealed class DaikinProtocolException : DaikinException
{
    public DaikinProtocolException(string path, string ret)
        : base($"{path} answered ret={ret}")
    {
        Ret = ret;
    }

    public string Ret { get; }
}

/// <summary>HTTP 404: the endpoint does not exist on this module.</summary>
public sealed class DaikinEndpointNotFoundException : DaikinException
{
    public DaikinEndpointNotFoundException(string path)
        : base($"{path} answered 404")
    {
        Path = path;
    }

    public string Path { get; }
}
