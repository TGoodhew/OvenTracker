using System.Globalization;

namespace OvenTracker.Ui;

/// <summary>
/// The meter's battery, the same way on every screen: red below <see cref="LowPercent"/>, and nothing at all
/// for a meter that doesn't report one (a bench DMM has no battery).
/// </summary>
public static class BatteryText
{
    /// <summary>Below this, the battery is shown as low.</summary>
    public const double LowPercent = 20;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool IsLow(double percent) => percent < LowPercent;

    /// <summary>"64 %", or "[red]15 % (low)[/]" as Spectre markup; null when the meter has no battery.</summary>
    public static string? Markup(double? percent) => percent is { } p
        ? IsLow(p) ? $"[red]{p.ToString("F0", Inv)} % (low)[/]" : $"{p.ToString("F0", Inv)} %"
        : null;

    /// <summary>"battery 64 %" or "battery 15 % (low)" for plain output; null when the meter has no battery.</summary>
    public static string? Plain(double? percent) => percent is { } p
        ? $"battery {p.ToString("F0", Inv)} %{(IsLow(p) ? " (low)" : "")}"
        : null;
}
