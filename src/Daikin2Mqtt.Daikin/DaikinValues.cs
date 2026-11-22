namespace Daikin2Mqtt.Daikin;

/// <summary>
/// The single bidirectional table between the values of the Daikin API and the values Home Assistant uses.
/// Several Daikin values can mean the same thing (mode 0, 1 and 7 are all "auto"): the first one of each row is the
/// canonical value sent to the unit, the others are only recognised when read.
/// </summary>
public static class DaikinValues
{
    private sealed record Row(string Ha, string[] Daikin);

    public const string ModeOff = "off";

    private static readonly Row[] ModeRows =
    {
        new("heat_cool", new[] { "1", "0", "7" }),
        new("dry", new[] { "2" }),
        new("cool", new[] { "3" }),
        new("heat", new[] { "4" }),
        new("fan_only", new[] { "6" }),
    };

    private static readonly Row[] FanRows =
    {
        new("auto", new[] { "A" }),
        new("quiet", new[] { "B" }),
        new("1", new[] { "3" }),
        new("2", new[] { "4" }),
        new("3", new[] { "5" }),
        new("4", new[] { "6" }),
        new("5", new[] { "7" }),
    };

    private static readonly Row[] SwingRows =
    {
        new("off", new[] { "0" }),
        new("vertical", new[] { "1" }),
        new("horizontal", new[] { "2" }),
        new("both", new[] { "3" }),
    };

    /// <summary>The "advanced" (special) modes. The unit reports them in <c>adv</c>, empty meaning none.</summary>
    private static readonly Row[] PresetRows =
    {
        new("none", new[] { string.Empty }),
        new("powerful", new[] { "2" }),
        new("eco", new[] { "12" }),
        new("streamer", new[] { "13" }),
    };

    /// <summary>Mode numbers of the Daikin API that have no temperature target.</summary>
    public const string DryMode = "2";
    public const string FanOnlyMode = "6";

    /// <summary>"No target temperature" placeholders of <c>stemp</c> (Dry mode and Fan mode).</summary>
    public const string NoTemperatureDry = "M";
    public const string NoTemperatureFan = "--";

    public static IReadOnlyList<string> HvacModes { get; } = new[] { ModeOff }.Concat(ModeRows.Select(r => r.Ha)).ToArray();

    public static IReadOnlyList<string> FanModes { get; } = FanRows.Select(r => r.Ha).ToArray();

    public static IReadOnlyList<string> SwingModes { get; } = SwingRows.Select(r => r.Ha).ToArray();

    /// <summary>
    /// What the unit is doing (Home Assistant <c>hvac_action</c>): off, heating, cooling, drying, fan or idle. Daikin does not
    /// report it: it is deduced from the power, the mode and the compressor frequency. In auto mode the direction comes from
    /// the room temperature against the target. Null when the compressor frequency is unknown.
    /// </summary>
    public static string? HvacAction(bool? power, string? daikinMode, double? compressorHz, double? indoor, double? target)
    {
        if (power == false)
        {
            return "off";
        }

        var mode = ModeToHa(daikinMode);
        if (mode == "fan_only")
        {
            return "fan";
        }

        if (compressorHz is null)
        {
            return null;
        }

        if (compressorHz <= 0)
        {
            return "idle";
        }

        return mode switch
        {
            "heat" => "heating",
            "cool" => "cooling",
            "dry" => "drying",
            "heat_cool" when indoor is { } room && target is { } goal => room < goal ? "heating" : "cooling",
            _ => "idle",
        };
    }

    /// <summary>True when <c>adv</c> contains the streamer (13), alone or with another mode ("2/13").</summary>
    public static bool StreamerActive(string? adv) =>
        adv is not null && adv.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(PresetToDaikin("streamer"));

    /// <summary>Climate preset published for the special mode of <c>adv</c>: powerful is "boost", eco is "eco", anything else "none".</summary>
    public static string ClimatePresetOfAdv(string? adv) => PresetToHa(adv) switch
    {
        "powerful" => "boost",
        "eco" => "eco",
        _ => "none",
    };

    public static IReadOnlyList<string> PresetModes { get; } = PresetRows.Select(r => r.Ha).ToArray();

    /// <summary>Daikin <c>mode</c> to Home Assistant HVAC mode (without the power state); null when unknown.</summary>
    public static string? ModeToHa(string? daikin) => ToHa(ModeRows, daikin);

    /// <summary>Home Assistant HVAC mode to the canonical Daikin <c>mode</c>; null for "off" and unknown values.</summary>
    public static string? ModeToDaikin(string? ha) => ToDaikin(ModeRows, ha);

    /// <summary>HVAC mode published to Home Assistant: "off" while the unit is powered off, whatever its mode.</summary>
    public static string? HvacMode(bool? power, string? daikinMode) => power == false ? ModeOff : ModeToHa(daikinMode);

    public static string? FanToHa(string? daikin) => ToHa(FanRows, daikin);

    public static string? FanToDaikin(string? ha) => ToDaikin(FanRows, ha);

    public static string? SwingToHa(string? daikin) => ToHa(SwingRows, daikin);

    public static string? SwingToDaikin(string? ha) => ToDaikin(SwingRows, ha);

    public static string? PresetToDaikin(string? ha) => ToDaikin(PresetRows, ha);

    /// <summary>
    /// <c>adv</c> to preset. Some units report several modes at once ("2/13"): the first one known, powerful first, wins.
    /// An unknown value gives null.
    /// </summary>
    public static string? PresetToHa(string? adv)
    {
        if (string.IsNullOrWhiteSpace(adv))
        {
            return "none";
        }

        var parts = adv.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var preset in new[] { "powerful", "eco", "streamer" })
        {
            if (parts.Contains(PresetToDaikin(preset)))
            {
                return preset;
            }
        }

        return null;
    }

    private static string? ToHa(Row[] rows, string? daikin) =>
        daikin is null ? null : rows.FirstOrDefault(r => r.Daikin.Contains(daikin.Trim()))?.Ha;

    private static string? ToDaikin(Row[] rows, string? ha) =>
        ha is null ? null : rows.FirstOrDefault(r => r.Ha.Equals(ha.Trim(), StringComparison.OrdinalIgnoreCase))?.Daikin[0];
}
