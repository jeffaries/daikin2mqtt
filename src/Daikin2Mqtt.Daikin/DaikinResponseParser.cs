using System.Globalization;

namespace Daikin2Mqtt.Daikin;

/// <summary>A parsed <c>ret=OK,key=value,...</c> answer of the module.</summary>
public sealed class DaikinResponse
{
    public DaikinResponse(string ret, IReadOnlyDictionary<string, string> values)
    {
        Ret = ret;
        Values = values;
    }

    /// <summary>Value of <c>ret</c>: "OK", "PARAM NG", "ADV_NG"... empty when the answer has none (not a module answer).</summary>
    public string Ret { get; }

    public bool IsOk => Ret == "OK";

    /// <summary>All the pairs, including <c>ret</c>; values are URL-decoded.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }
}

/// <summary>
/// Parser of the module's <c>key=value,key=value</c> format. Values may be URL-encoded (<c>name=%44%61...</c>), may be empty
/// (<c>adv=</c>) and, for <c>mondata</c>, contain '=' and ',' themselves.
/// </summary>
public static class DaikinResponseParser
{
    /// <summary>Keys whose value is "the rest of the line": they hold pairs of their own.</summary>
    private static readonly string[] RestOfLineKeys = { "mondata" };

    public static DaikinResponse Parse(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DaikinResponse(string.Empty, values);
        }

        var line = text.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        var position = 0;
        while (position < line.Length)
        {
            var end = line.IndexOf(',', position);
            if (end < 0)
            {
                end = line.Length;
            }

            var equals = line.IndexOf('=', position, end - position);
            if (equals > position)
            {
                var key = line[position..equals].Trim();
                if (RestOfLineKeys.Contains(key))
                {
                    values[key] = line[(equals + 1)..];
                    break;
                }

                values[key] = Decode(line[(equals + 1)..end]);
            }

            position = end + 1;
        }

        return new DaikinResponse(values.GetValueOrDefault("ret", string.Empty), values);
    }

    /// <summary>URL-decodes a value; text that is not a valid escape sequence (for example "50%") is left as is.</summary>
    public static string Decode(string value) => value.Contains('%') ? Uri.UnescapeDataString(value) : value;

    /// <summary>A number, or null for the placeholders the module uses when there is none ("-", "--", "M", empty).</summary>
    public static double? ParseNumber(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : null;

    /// <summary>The "a/b/c" lists of the energy endpoints; entries that are not numbers count as 0.</summary>
    public static double[] ParseSlashList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<double>()
            : value.Split('/').Select(item => ParseNumber(item) ?? 0).ToArray();
}
