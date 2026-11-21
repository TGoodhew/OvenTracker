using System.Globalization;
using System.Text;
using OvenTracker.Ovens;
using OvenTracker.Session;

namespace OvenTracker.Analysis;

/// <summary>A characterisation run's plan: the adjustment step, and once phase 2 has begun, the adjustment it moved to.</summary>
/// <param name="StepC">The least change of adjustment to test (--step).</param>
/// <param name="ChangedToC">The adjustment the oven was changed to for phase 2 (relative to phase 1's, if that wasn't known).</param>
public sealed record CharacteriseInfo(double StepC, double? FromC = null, double? ChangedToC = null, DateTimeOffset? ChangedAt = null);

/// <summary>One mode's characterisation: how far the adjustment moves it, and how its error changes with temperature.</summary>
/// <param name="Gain">The gain measured (null if this mode wasn't measured at two adjustments).</param>
/// <param name="SlopePerDegree">How the error changes per degree of setting (0 = a fixed error; 0.1 = 10 °F more per 100 °F).</param>
/// <param name="Curved">The error bends with temperature, more than a straight line explains.</param>
public sealed record ModeCharacter(OvenMode Mode, ModeGain? Gain, double? SlopePerDegree, bool Curved, int Setpoints);

/// <summary>The same setpoint, at the same adjustment, measured more than once: how far apart the results were.</summary>
public sealed record Repeat(OvenMode Mode, double SetpointC, double AdjustmentC, IReadOnlyList<double> MeansC)
{
    public double SpreadC => MeansC.Max() - MeansC.Min();
    public double StdDevC => Stats.StdDev(MeansC);
}

/// <summary>
/// What characterise (or analyze over runs at different adjustments) found about an oven: each mode's gain
/// and error shape, how repeatable the results are, and the best adjustment with the measured gains.
/// </summary>
public sealed record Characterisation(
    IReadOnlyList<ModeCharacter> Modes,
    IReadOnlyList<Repeat> Repeats,
    AdjustmentAdvice? Recommendation)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const double SameC = 0.05;

    /// <summary>The gains worth keeping in the oven's profile: those measured.</summary>
    public IReadOnlyDictionary<OvenMode, MeasuredGain> MeasuredGains(DateTimeOffset when) =>
        Modes.Where(m => m.Gain is { Source: GainSource.Measured })
            .ToDictionary(m => m.Mode, m => new MeasuredGain(m.Gain!.Gain, m.Gain.HalfWidth, when));

    public bool MeasuredAny => Modes.Any(m => m.Gain is { Source: GainSource.Measured });

    public static Characterisation From(SessionInfo info, IReadOnlyList<PointResult> results)
    {
        var report = new Report(info, results);
        var points = report.AdjustPoints();
        var options = report.AdjustOptions;

        // Each mode on its own: its gain, and its error's shape at the current adjustment.
        var perMode = AdjustmentAdvisor.Advise(points, options with { SharedAcrossModes = false }, info.AdjustmentC);
        var modes = new List<ModeCharacter>();
        foreach (var a in perMode)
        {
            var mode = a.Mode ?? a.Residuals.First().Mode;
            var gain = a.Gains is { } g && g.TryGetValue(mode, out var mg) && mg.Source == GainSource.Measured ? mg : null;
            var now = a.Residuals.Where(r => r.Mode == mode).ToList();
            var fit = now.Count >= 2 ? Stats.Fit(now.Select(r => r.SetpointC).ToList(), now.Select(r => r.NowC).ToList()) : null;
            modes.Add(new ModeCharacter(mode, gain, fit?.Slope, a.Curved, now.Count));
        }

        // Repeats: the same mode, setpoint and adjustment, more than once.
        var repeats = results
            .Where(r => r.Metrics.MeanC is not null)
            .GroupBy(r => (r.Point.Mode, Sp: Math.Round(r.Point.SetpointC / SameC), Adj: Math.Round((r.Point.AdjustmentC ?? info.AdjustmentC ?? 0) / SameC)))
            .Where(g => g.Count() >= 2)
            .Select(g => new Repeat(g.Key.Mode, g.First().Point.SetpointC, g.First().Point.AdjustmentC ?? info.AdjustmentC ?? 0, g.Select(r => r.Metrics.MeanC!.Value).ToList()))
            .ToList();

        return new Characterisation(modes, repeats, report.Advice().FirstOrDefault());
    }

    public string Markdown(SessionInfo info)
    {
        var f = new UnitFormatter(info.Unit);
        var sb = new StringBuilder();
        sb.AppendLine($"# Characterisation: {info.Oven?.Name ?? "the oven"}");
        sb.AppendLine();
        // The date as recorded (in the offset it was saved with), so the file reads the same on any PC.
        sb.AppendLine($"Measured {info.Started.ToString("yyyy-MM-dd", Inv)} with {info.Meter}" +
                      (info.Sensor is { } s ? $" and a {s.Name}" : "") + ".");
        sb.AppendLine();
        sb.AppendLine("## How far the adjustment moves the oven");
        sb.AppendLine();
        sb.AppendLine("| Mode | Gain | ±95 % | Meaning |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var m in Modes)
            sb.AppendLine(m.Gain is { } g
                ? $"| {Temp.ModeName(m.Mode)} | {g.Gain.ToString("F2", Inv)} | {(g.HalfWidth is { } hw ? hw.ToString("F2", Inv) : "—")} | each degree of adjustment moves it {g.Gain.ToString("F2", Inv)} degrees |"
                : $"| {Temp.ModeName(m.Mode)} | — | — | not measured at two adjustments |");
        sb.AppendLine();
        sb.AppendLine("A gain of 1.00 means the adjustment does exactly what it says. Under 1, the oven moves less than the setting; over 1, more.");
        sb.AppendLine();
        sb.AppendLine("## How the error changes with temperature");
        sb.AppendLine();
        foreach (var m in Modes)
        {
            var shape = m.Setpoints < 2 ? "one setpoint, so its shape isn't known"
                : m.Curved ? "it bends with temperature, more than a straight line explains"
                // The slope is degrees per degree, the same in °F and °C: 0.1 is 10° more error per 100° of setting.
                : m.SlopePerDegree is { } sl && Math.Abs(sl) >= 0.02
                    ? $"its error changes by about {f.D(f.DeltaFromDisplay(sl * 100), 0)} per 100 {f.Symbol} of setting ({(sl > 0 ? "hotter" : "cooler")} at higher settings), so one adjustment can only centre it"
                    : "its error is about the same at every setting, so one adjustment corrects it everywhere";
            sb.AppendLine($"- {Temp.ModeName(m.Mode)}: {shape}.");
        }
        sb.AppendLine();
        if (Repeats.Count > 0)
        {
            sb.AppendLine("## Repeatability");
            sb.AppendLine();
            sb.AppendLine("| Mode | Set | Adjustment | Means | Spread |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var r in Repeats)
                sb.AppendLine($"| {Temp.ModeName(r.Mode)} | {f.Setpoint(r.SetpointC)} | {f.D(r.AdjustmentC, 0)} | {string.Join(", ", r.MeansC.Select(c => f.T(c)))} | {f.Span(r.SpreadC)} |");
            sb.AppendLine();
            sb.AppendLine("The spread between repeats is the real limit on how finely this oven can be adjusted.");
            sb.AppendLine();
        }
        if (Recommendation is { } a)
        {
            var report = new Report(info, []);
            sb.AppendLine("## Best adjustment, with the measured gains");
            sb.AppendLine();
            sb.AppendLine($"**{report.AdviceHeadline(a)}**");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
