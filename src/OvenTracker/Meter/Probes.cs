using System.Globalization;
using System.Text.RegularExpressions;

namespace OvenTracker.Meter;

/// <summary>
/// Probes the report can know by name (<c>--probe</c>), each with its tolerance and range from a cited
/// source, and the parsing of a custom tolerance and maximum (<c>--probe-tolerance</c>, <c>--probe-max</c>).
/// </summary>
public static partial class Probes
{
    /// <summary>A probe the app knows, and where its specification comes from.</summary>
    public sealed record Known(string Key, Sensor Sensor, string Source);

    public static IReadOnlyList<Known> All { get; } =
    [
        new("type-k", new Sensor("type K thermocouple", 2.2, 0.0075),
            "ASTM E230 standard limits of error, above 0 °C"),
        new("type-k-special", new Sensor("type K thermocouple (special limits)", 1.1, 0.004),
            "ASTM E230 special limits of error, above 0 °C"),
        new("80bk-a", new Sensor("Fluke 80BK-A type K probe", 2.2, 0.02, MaxC: 260),
            "Fluke 80BK-A: −40 to 260 °C, ±2.2 °C or 2 % whichever is greater (0 to 260 °C), https://www.fluke.com/en-us/product/accessories/probes/fluke-80bk-a"),
    ];

    public static Known? Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Keys => string.Join(", ", All.Select(p => p.Key));

    [GeneratedRegex(@"^\s*(?<fixed>\d+(\.\d+)?)\s*(?<unit>[CF])?\s*(?<join>[,+])\s*(?<pct>\d+(\.\d+)?)\s*%\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ToleranceSpec();

    [GeneratedRegex(@"^\s*(?<value>-?\d+(\.\d+)?)\s*(?<unit>[CF])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TemperatureSpec();

    /// <summary>
    /// Parses a tolerance such as "2.2C,2%" (the greater of ±2.2 °C and ±2 %), "4F,2%", or "0.3C+0.5%" (the
    /// two added, as RTD classes are written). Without C or F, the degrees are in <paramref name="unit"/>.
    /// </summary>
    public static (double LimitC, double Fraction, bool Adds)? ParseTolerance(string text, TempUnit unit)
    {
        var m = ToleranceSpec().Match(text);
        if (!m.Success) return null;
        var degrees = double.Parse(m.Groups["fixed"].Value, CultureInfo.InvariantCulture);
        var inF = m.Groups["unit"].Success ? m.Groups["unit"].Value.Equals("F", StringComparison.OrdinalIgnoreCase) : unit == TempUnit.Fahrenheit;
        var limitC = inF ? degrees / 1.8 : degrees;
        var fraction = double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture) / 100;
        return (limitC, fraction, m.Groups["join"].Value == "+");
    }

    /// <summary>Parses a temperature such as "260C" or "500F"; without C or F, in <paramref name="unit"/>. In °C.</summary>
    public static double? ParseTemperature(string text, TempUnit unit)
    {
        var m = TemperatureSpec().Match(text);
        if (!m.Success) return null;
        var value = double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
        var inF = m.Groups["unit"].Success ? m.Groups["unit"].Value.Equals("F", StringComparison.OrdinalIgnoreCase) : unit == TempUnit.Fahrenheit;
        return inF ? Temp.FToC(value) : value;
    }

    /// <summary>
    /// The sensor to report: the named probe, or what the meter reported, with a custom tolerance and maximum
    /// on top. Null if nothing is known.
    /// </summary>
    public static Sensor? Resolve(Sensor? reported, string? probe, string? tolerance, string? max, TempUnit unit)
    {
        var sensor = Find(probe)?.Sensor ?? reported;
        if (tolerance is not null && ParseTolerance(tolerance, unit) is { } t)
            sensor = (sensor ?? new Sensor("probe", 0, 0)) with { Name = probe is null ? $"{sensor?.Name ?? "probe"} (custom tolerance)" : sensor!.Name, LimitC = t.LimitC, LimitFraction = t.Fraction, LimitsAdd = t.Adds };
        if (max is not null && ParseTemperature(max, unit) is { } maxC)
            sensor = (sensor ?? new Sensor("probe", 0, 0)) with { MaxC = maxC };
        return sensor;
    }
}
