using System.Globalization;

namespace Daikin2Mqtt.Daikin;

/// <summary>
/// The six parameters of <c>aircon/set_control_info</c>. The module wants all of them in every call, so a change is
/// built from the current <c>get_control_info</c> answer (read-modify-write): see <see cref="Build"/>.
/// </summary>
public sealed record DaikinControlRequest(string Pow, string Mode, string Stemp, string Shum, string FRate, string FDir)
{
    public const string Path = "aircon/set_control_info";

    private const string FallbackTemperature = "22.0";

    /// <summary>The query string, parameters in the order of the documentation.</summary>
    public string ToQuery() =>
        $"pow={Uri.EscapeDataString(Pow)}&mode={Uri.EscapeDataString(Mode)}&stemp={Uri.EscapeDataString(Stemp)}" +
        $"&shum={Uri.EscapeDataString(Shum)}&f_rate={Uri.EscapeDataString(FRate)}&f_dir={Uri.EscapeDataString(FDir)}";

    /// <summary>
    /// Applies <paramref name="change"/> on top of <paramref name="current"/> (the <c>get_control_info</c> values).
    /// <list type="bullet">
    /// <item>Dry mode has no target temperature (<c>stemp=M</c>) and Fan mode neither (<c>stemp=--</c>).</item>
    /// <item>Changing the mode restores the temperature the unit remembers for the new one (<c>dt3</c> cooling, <c>dt4</c>
    /// heating, <c>dt1</c>/<c>dt7</c> auto), else the current one, else 22 °C: leaving Dry or Fan mode must not send "M".</item>
    /// </list>
    /// </summary>
    public static DaikinControlRequest Build(IReadOnlyDictionary<string, string> current, DaikinCommand change)
    {
        string Get(string key, string fallback) =>
            current.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

        var pow = change.Power is { } power ? (power ? "1" : "0") : Get("pow", "0");
        var currentMode = Get("mode", "3");
        var mode = change.Mode ?? currentMode;

        string stemp;
        if (mode == DaikinValues.DryMode)
        {
            stemp = DaikinValues.NoTemperatureDry;
        }
        else if (mode == DaikinValues.FanOnlyMode)
        {
            stemp = DaikinValues.NoTemperatureFan;
        }
        else if (change.Temperature is { } temperature)
        {
            stemp = FormatTemperature(temperature);
        }
        else
        {
            var stored = StoredTemperature(current, mode);
            var keep = DaikinResponseParser.ParseNumber(Get("stemp", string.Empty)) is not null ? Get("stemp", string.Empty) : null;
            // Same mode: keep what the unit has. New mode: the unit's own memory of that mode first.
            stemp = mode == currentMode ? keep ?? stored ?? FallbackTemperature : stored ?? keep ?? FallbackTemperature;
        }

        return new DaikinControlRequest(
            pow,
            mode,
            stemp,
            Get("shum", "0"),
            change.FanRate ?? Get("f_rate", "A"),
            change.SwingDirection ?? Get("f_dir", "0"));
    }

    public static string FormatTemperature(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The temperature the unit stored for a mode: <c>dt1</c> (auto), <c>dt3</c> (cool), <c>dt4</c> (heat), <c>dt7</c> (auto).</summary>
    private static string? StoredTemperature(IReadOnlyDictionary<string, string> current, string mode)
    {
        var key = mode switch
        {
            "0" or "1" => "dt1",
            "7" => "dt7",
            "3" => "dt3",
            "4" => "dt4",
            _ => null,
        };

        return key is not null && current.TryGetValue(key, out var value) && DaikinResponseParser.ParseNumber(value) is not null
            ? value
            : null;
    }
}
