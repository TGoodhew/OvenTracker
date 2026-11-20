using System.Globalization;
using System.Text;
using OvenTracker.Analysis;

namespace OvenTracker.Session;

/// <summary>The rows of the results tables, as text, shared by the console and the Markdown report.</summary>
public sealed class Report(SessionInfo info, IReadOnlyList<PointResult> results)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly UnitFormatter f = new(info.Unit);

    public SessionInfo Info => info;
    public IReadOnlyList<PointResult> Results => results;
    public UnitFormatter Format => f;

    public static string Minutes(double? m) => m is { } v ? $"{v.ToString("F0", Inv)} min" : "—";

    public static string VerdictText(Verdict v) => v switch
    {
        Verdict.Accurate => "Accurate",
        Verdict.Borderline => "Borderline",
        Verdict.OutOfTolerance => "Out of tolerance",
        _ => "Inconclusive",
    };

    public string ReasonText(PointMetrics m) => m.Reason switch
    {
        CompletionReason.Converged => "settled and measured",
        CompletionReason.TimedOut => $"stopped at the {info.Options.MaxPointMinutes:F0} min limit",
        CompletionReason.Manual => "finished early by you",
        CompletionReason.Aborted => "interrupted",
        CompletionReason.Skipped => "skipped",
        _ => "",
    };

    public static readonly string[] PointColumns =
        ["Mode", "Set", "Measured", "Offset", "±95 %", "Verdict", "Swing", "Range", "Cycle", "To temp", "First peak", "Time", "Used"];

    public IEnumerable<(PointResult R, Verdict V, string[] Cells)> PointRows()
    {
        foreach (var r in results)
        {
            var m = r.Metrics;
            var v = SessionSummary.Judge(m, info.ToleranceC);
            var used = m.Units == 0 ? "—" : $"{m.Units} {(m.Kind == UnitKind.Block ? "blocks" : "cycles")}";
            yield return (r, v,
            [
                Temp.ModeName(r.Point.Mode),
                f.Setpoint(r.Point.SetpointC),
                m.MeanC is { } mean ? f.T(mean) : "—",
                m.OffsetC is { } off ? f.D(off) : "—",
                m.HalfWidthC is { } hw ? f.Span(hw) : "—",
                VerdictText(v),
                m.SwingC is { } sw ? f.Span(sw) : "—",
                m.MinC is { } lo && m.MaxC is { } hi ? $"{f.ToDisplay(lo).ToString("F0", Inv)}–{f.ToDisplay(hi).ToString("F0", Inv)}" : "—",
                m.Kind == UnitKind.Cycle && m.CycleMinutes is { } c ? $"{c.ToString("F1", Inv)} min" : "—",
                Minutes(m.TimeToSetpointMinutes),
                m.OvershootC is { } os ? f.D(os) : "—",
                Minutes(m.TotalMinutes),
                used,
            ]);
        }
    }

    public static readonly string[] ComparisonColumns = ["Set", "Bake", "Convection", "Convection − Bake", "Real difference?", "Swing bake / conv", "To temp bake / conv"];

    public IEnumerable<string[]> ComparisonRows() =>
        SessionSummary.Compare(results).Select(c => new[]
        {
            f.Setpoint(c.SetpointC),
            f.T(c.Bake.MeanC!.Value),
            f.T(c.Convection.MeanC!.Value),
            f.D(c.DeltaMeanC),
            c.Significant ? "Yes" : "No (within uncertainty)",
            $"{(c.Bake.SwingC is { } a ? f.Span(a) : "—")} / {(c.Convection.SwingC is { } b ? f.Span(b) : "—")}",
            $"{Minutes(c.Bake.TimeToSetpointMinutes)} / {Minutes(c.Convection.TimeToSetpointMinutes)}",
        });

    /// <summary>Plain-language findings for each mode.</summary>
    public IEnumerable<string> Findings()
    {
        foreach (var s in SessionSummary.ByMode(results))
        {
            var mode = Temp.ModeName(s.Mode);
            var dir = s.MeanOffsetC >= 0 ? "hot" : "cold";
            var sb = new StringBuilder();
            sb.Append($"{mode}: runs {f.Span(s.MeanOffsetC)} {dir} on average over {s.Points} setpoint{(s.Points == 1 ? "" : "s")} (worst {f.Span(s.MaxAbsOffsetC)}).");
            if (s.GainPerDegree is { } g && s.Points >= 2)
            {
                var spread = results.Where(r => r.Point.Mode == s.Mode && r.Metrics.OffsetC is not null).ToList();
                var lo = spread.MinBy(r => r.Point.SetpointC)!;
                var hi = spread.MaxBy(r => r.Point.SetpointC)!;
                var change = hi.Metrics.OffsetC!.Value - lo.Metrics.OffsetC!.Value;
                if (Math.Abs(change) >= Math.Max(info.Options.PrecisionC * 2, 1.0))
                    sb.Append($" The error {(change > 0 ? "grows" : "shrinks")} with temperature: {f.D(lo.Metrics.OffsetC!.Value)} at {f.Setpoint(lo.Point.SetpointC)}, {f.D(hi.Metrics.OffsetC!.Value)} at {f.Setpoint(hi.Point.SetpointC)}, so a single offset can't correct all of it.");
                else
                    sb.Append(" The error is about the same at every setpoint, so the oven's offset adjustment would correct it.");
            }
            if (Math.Abs(s.MeanOffsetC) > info.ToleranceC / 3)
                sb.Append($" If your oven has a temperature offset setting, setting it to about {f.D(s.SuggestedCalibrationC, 0)} would centre it (rounded to whatever steps the oven allows).");
            if (s.MeanSwingC is { } sw)
                sb.Append($" The thermostat swings {f.Span(sw)} peak to peak" + (s.MeanCycleMinutes is { } cm ? $", one cycle every {cm.ToString("F1", Inv)} min." : "."));
            yield return sb.ToString();
        }

        var modes = SessionSummary.ByMode(results);
        if (modes.Count == 2 && Math.Abs(modes[0].SuggestedCalibrationC - modes[1].SuggestedCalibrationC) > info.ToleranceC / 3)
        {
            var split = (modes[0].SuggestedCalibrationC + modes[1].SuggestedCalibrationC) / 2;
            var splitText = Math.Abs(f.DeltaToDisplay(split)) < 0.5 ? "leaving it at 0 splits the difference" : $"{f.D(split, 0)} splits the difference";
            yield return $"Most ovens have one offset setting for every mode. Bake would want {f.D(modes[0].SuggestedCalibrationC, 0)} and convection " +
                         $"{f.D(modes[1].SuggestedCalibrationC, 0)}, so one setting can't fix both: {splitText}, " +
                         "or set it for the mode you use most and allow for the difference in the other.";
        }

        foreach (var c in SessionSummary.Compare(results))
        {
            yield return c.Significant
                ? $"At {f.Setpoint(c.SetpointC)}, convection is {f.Span(c.DeltaMeanC)} {(c.DeltaMeanC > 0 ? "hotter" : "cooler")} than bake."
                : $"At {f.Setpoint(c.SetpointC)}, bake and convection are the same within the measurement's uncertainty ({f.D(c.DeltaMeanC)}).";
        }
    }

    /// <summary>What the tracking time came to, and what to allow next time.</summary>
    public IEnumerable<string> Durations()
    {
        var done = results.Where(r => r.Metrics.Reason == CompletionReason.Converged).ToList();
        if (done.Count == 0) yield break;
        var first = done[0];
        var rest = done.Skip(1).Select(r => r.Metrics.TotalMinutes).ToList();
        yield return $"The first setpoint took {Minutes(first.Metrics.TotalMinutes)} ({Minutes(first.Metrics.SettleMinutes)} to settle, then {Minutes(first.Metrics.MeasureMinutes)} measuring).";
        if (rest.Count == 1)
            yield return $"The next setpoint took {Minutes(rest[0])}, since the oven was already hot.";
        else if (rest.Count > 1)
            yield return $"Later setpoints took {Minutes(rest.Min())} to {Minutes(rest.Max())} (median {Minutes(Stats.Median(rest))}), since the oven was already hot.";
        var cycles = done.Where(r => r.Metrics.Kind == UnitKind.Cycle).Select(r => r.Metrics.Units).ToList();
        if (cycles.Count > 0)
            yield return $"Each result needed {(cycles.Min() == cycles.Max() ? cycles.Min().ToString(Inv) : $"{cycles.Min()}–{cycles.Max()}")} full thermostat cycles to pin the mean to ±{f.Span(info.Options.PrecisionC)}; the app counts these as it goes, so the time adapts to your oven.";
    }

    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Oven temperature accuracy");
        sb.AppendLine();
        sb.AppendLine($"Run {info.Started.ToLocalTime():yyyy-MM-dd HH:mm} with {info.Meter} on {info.Location}. " +
                      $"A setpoint is accurate when its mean, including the ±95 % interval, is within ±{f.Span(info.ToleranceC)} of the setting.");
        sb.AppendLine();

        var findings = Findings().ToList();
        if (findings.Count > 0)
        {
            sb.AppendLine("## Findings");
            sb.AppendLine();
            foreach (var line in findings) sb.AppendLine($"- {line}");
            sb.AppendLine();
        }

        sb.AppendLine("## Each setpoint");
        sb.AppendLine();
        Table(sb, PointColumns, PointRows().Select(r => r.Cells));
        sb.AppendLine();
        sb.AppendLine("*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. " +
                      "*Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. " +
                      "*To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); " +
                      "*First peak* is the first high point against the setting, which is the overshoot when it is positive.");
        sb.AppendLine();

        var preheat = results.Where(r => r.Metrics.PreheatSignalMinutes is not null).ToList();
        if (preheat.Count > 0)
        {
            sb.AppendLine("## Preheat signal");
            sb.AppendLine();
            Table(sb, ["Mode", "Set", "Signalled after", "Oven was at", "Against setting"], preheat.Select(r => new[]
            {
                Temp.ModeName(r.Point.Mode), f.Setpoint(r.Point.SetpointC), Minutes(r.Metrics.PreheatSignalMinutes),
                r.Metrics.PreheatSignalC is { } c ? f.T(c) : "—",
                r.Metrics.PreheatSignalC is { } c2 ? f.D(c2 - r.Point.SetpointC) : "—",
            }));
            sb.AppendLine();
        }

        var comparison = ComparisonRows().ToList();
        if (comparison.Count > 0)
        {
            sb.AppendLine("## Bake and convection");
            sb.AppendLine();
            Table(sb, ComparisonColumns, comparison);
            sb.AppendLine();
        }

        var durations = Durations().ToList();
        if (durations.Count > 0)
        {
            sb.AppendLine("## How long each setpoint needed");
            sb.AppendLine();
            foreach (var line in durations) sb.AppendLine($"- {line}");
            sb.AppendLine();
            Table(sb, ["Mode", "Set", "Settling", "Measuring", "Total", "Ended"], results.Select(r => new[]
            {
                Temp.ModeName(r.Point.Mode), f.Setpoint(r.Point.SetpointC), Minutes(r.Metrics.SettleMinutes),
                Minutes(r.Metrics.MeasureMinutes), Minutes(r.Metrics.TotalMinutes), ReasonText(r.Metrics),
            }));
            sb.AppendLine();
        }

        sb.AppendLine("## How far to trust it");
        sb.AppendLine();
        var top = results.Count > 0 ? results.Max(r => r.Point.SetpointC) : 200;
        sb.AppendLine($"- The ±95 % column is the statistical uncertainty only. The thermocouple itself adds its own: a standard-grade type K is good to the greater of ±2.2 °C (±4 °F) and ±0.75 % of the temperature, ±{f.Span(SessionSummary.TypeKStandardLimitC(top))} at {f.Setpoint(top)}, and the meter's thermocouple accuracy (in its data sheet) adds to that. An offset smaller than these may be the probe, not the oven.");
        sb.AppendLine("- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.");
        sb.AppendLine("- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.");
        sb.AppendLine();
        sb.AppendLine($"Settings: precision ±{f.Span(info.Options.PrecisionC)}, at least {info.Options.MinCycles} cycles, up to {info.Options.MaxPointMinutes:F0} min per setpoint. Raw data: `readings.csv`.");
        return sb.ToString();
    }

    static void Table(StringBuilder sb, IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => " --- |")));
        foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r.Select(c => c.Replace("|", "\\|"))) + " |");
    }
}
