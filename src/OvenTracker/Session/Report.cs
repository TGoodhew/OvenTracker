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
                f.Setpoint(r.Point.SetpointC) + (r.Point.AdjustmentC is { } adj && adj != (info.AdjustmentC ?? 0) ? $" (adj {Adjustment(adj)})" : "")
                    + (r.AfterReset ? " (after a reset)" : "") + (r.StartedWarm ? " (started warm)" : ""),
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
                    sb.Append($" The error {(change > 0 ? "grows" : "shrinks")} with temperature: {f.D(lo.Metrics.OffsetC!.Value)} at {f.Setpoint(lo.Point.SetpointC)}, {f.D(hi.Metrics.OffsetC!.Value)} at {f.Setpoint(hi.Point.SetpointC)}, so a single adjustment can't correct all of it.");
                else
                    sb.Append(" The error is about the same at every setpoint, so the oven's thermostat adjustment can correct it.");
            }
            if (s.MeanSwingC is { } sw)
                sb.Append($" The thermostat swings {f.Span(sw)} peak to peak" + (s.MeanCycleMinutes is { } cm ? $", one cycle every {cm.ToString("F1", Inv)} min." : "."));
            yield return sb.ToString();
        }

        foreach (var a in Advice())
            yield return AdviceHeadline(a) + " Details are under Best adjustment.";

        foreach (var c in SessionSummary.Compare(results))
        {
            yield return c.Significant
                ? $"At {f.Setpoint(c.SetpointC)}, convection is {f.Span(c.DeltaMeanC)} {(c.DeltaMeanC > 0 ? "hotter" : "cooler")} than bake."
                : $"At {f.Setpoint(c.SetpointC)}, bake and convection are the same within the measurement's uncertainty ({f.D(c.DeltaMeanC)}).";
        }
    }

    public AdjustmentOptions AdjustOptions => info.Adjust ?? AdjustmentOptions.For(info.Unit);

    /// <summary>Each measured setpoint, with the adjustment the oven had for it.</summary>
    public IReadOnlyList<AdjustPoint> AdjustPoints() =>
        results.Where(r => r.Metrics.OffsetC is not null)
            .Select(r => new AdjustPoint(r.Point.Mode, r.Point.SetpointC, r.Metrics.OffsetC!.Value, r.Metrics.HalfWidthC,
                r.Point.AdjustmentC ?? info.AdjustmentC ?? 0))
            .ToList();

    public IReadOnlyList<AdjustmentAdvice> Advice() => AdjustmentAdvisor.Advise(AdjustPoints(), AdjustOptions, info.AdjustmentC);

    /// <summary>An adjustment setting as the oven shows it: whole degrees, signed, and plain 0.</summary>
    public string Adjustment(double c) => Math.Abs(f.DeltaToDisplay(c)) < 0.5 ? $"0 {f.Symbol}" : f.D(c, 0);

    string Scope(AdjustmentAdvice a) =>
        a.Mode is { } m ? Temp.ModeName(m) : "Bake and convection (one adjustment for both)";

    /// <summary>The recommendation in a sentence.</summary>
    public string AdviceHeadline(AdjustmentAdvice a)
    {
        var scope = Scope(a);
        if (Math.Abs(f.DeltaToDisplay(a.ChangeC)) < 0.5)
            return $"{scope}: leave the thermostat adjustment as it is{(a.CurrentC is { } c ? $" ({Adjustment(c)})" : "")}; no step would make the oven more accurate.";
        return a.NewSettingC is { } set
            ? $"{scope}: set the oven's thermostat adjustment to {Adjustment(set)} (it's {Adjustment(a.CurrentC!.Value)} now)."
            : $"{scope}: change the oven's thermostat adjustment by {f.D(a.ChangeC, 0)}, from whatever it is set to now.";
    }

    /// <summary>Everything worth saying about a recommendation, after the headline.</summary>
    public IEnumerable<string> AdviceDetails(AdjustmentAdvice a)
    {
        var o = a.Options;
        yield return o.FavourC is { } fav
            ? $"This makes the oven right at {f.Setpoint(fav)}, the temperature you favour."
            : o.Objective == AdjustObjective.Minimax
                ? "This keeps the worst of the setpoints measured as close to its setting as it can."
                : "This makes the overall error across the setpoints measured as small as it can.";
        yield return a.WorstAfterC < AdjustmentAdvisor.WorthC
            ? $"Afterwards every setpoint measured should be within {f.Span(a.WorstAfterC)} of its setting, which is about the probe's own accuracy."
            : $"Afterwards the worst setpoint is still {f.Span(a.WorstAfterC)} out, so the table below says what to set to make up for it.";
        if (a.GainMeasured)
            yield return $"Measured from runs at different adjustments: each degree of adjustment moves this oven {a.Gain.ToString("F2", Inv)} degrees" +
                         (a.GainHalfWidthC is { } hw ? $" (±{hw.ToString("F2", Inv)})." : ".");
        else if (o.Model == AdjustModel.Gain && Math.Abs(a.Gain - 1) > 1e-6)
            yield return $"This assumes the adjustment is scaled by the oven's own error slope, so each degree moves it {a.Gain.ToString("F2", Inv)} degrees. That hasn't been measured; see docs/oven-control-model.md.";
        else
            yield return "This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md).";
        if (a.Clamped)
            yield return $"The oven's adjustment only goes to ±{f.Span(o.RangeC, 0)}, so this is its limit. An error this large is worth a service call: the oven's sensor may be faulty.";
        if (a.CurrentC is null && Math.Abs(f.DeltaToDisplay(a.ChangeC)) >= 0.5)
            yield return "The run didn't record the oven's current adjustment. Give it with --adjustment and the app will say what to set it to.";
        if (a.Curved)
            yield return "The error bends with temperature rather than following a straight line, so where it matters, use the table of what to set.";
        if (a.TooFewSetpoints is { } why)
            yield return $"{why} Worth adding: {string.Join(", ", a.AddSetpointsC.Select(RoundedSetpoint))}.";
    }

    /// <summary>A setpoint to suggest, rounded to one people would set: 25 °F or 10 °C.</summary>
    string RoundedSetpoint(double c)
    {
        var step = info.Unit == TempUnit.Fahrenheit ? 25 : 10;
        return f.Setpoint(f.FromDisplay(Math.Round(f.ToDisplay(c) / step) * step));
    }

    public static readonly string[] ResidualColumns = ["Mode", "Set", "Now", "After adjusting", "±95 %", "Within tolerance?"];

    public IEnumerable<string[]> ResidualRows(AdjustmentAdvice a) => a.Residuals.Select(r =>
    {
        var hw = r.HalfWidthC ?? 0;
        var ok = Math.Abs(r.AfterC) + hw <= info.ToleranceC ? "Yes" : Math.Abs(r.AfterC) - hw > info.ToleranceC ? "No" : "Borderline";
        return new[] { Temp.ModeName(r.Mode), f.Setpoint(r.SetpointC), f.D(r.NowC), f.D(r.AfterC), r.HalfWidthC is { } h ? f.Span(h) : "—", ok };
    });

    public static readonly string[] DialColumns = ["Mode", "For", "Set the oven to"];

    public IEnumerable<string[]> DialRows(AdjustmentAdvice a) => a.DialCorrections.Select(d => new[]
    {
        Temp.ModeName(d.Mode), f.Setpoint(d.WantC), $"{Math.Round(f.ToDisplay(d.SetC)).ToString("0", Inv)} {f.Symbol}",
    });

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
        var sittings = info.Sittings is { Count: > 1 } list
            ? $" in {list.Count} sittings ({string.Join(", ", list.Select(d => d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv)))})"
            : "";
        sb.AppendLine($"Run {info.Started.ToLocalTime():yyyy-MM-dd HH:mm}{sittings} with {info.Meter} on {info.Location}. " +
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
        if (results.Any(r => r.StartedWarm))
            sb.Append(" The setpoint marked *started warm* began with the oven still warm, not from cold, so its *To temp* and *First peak* " +
                      "aren't from cold. Its mean is as good as any.");
        if (results.Any(r => r.AfterReset))
            sb.Append(" Setpoints marked *after a reset* were measured after a break (the run was resumed, or the oven was left to cool first), " +
                      "so their *To temp* and *First peak* aren't comparable with the others. Their mean is.");
        sb.AppendLine();
        sb.AppendLine();

        var advice = Advice();
        if (advice.Count > 0)
        {
            sb.AppendLine("## Best adjustment");
            sb.AppendLine();
            foreach (var a in advice)
            {
                sb.AppendLine($"**{AdviceHeadline(a)}**");
                sb.AppendLine();
                foreach (var line in AdviceDetails(a)) sb.AppendLine($"- {line}");
                sb.AppendLine();
                Table(sb, ResidualColumns, ResidualRows(a));
                sb.AppendLine();
                var dial = DialRows(a).ToList();
                if (dial.Count > 0)
                {
                    sb.AppendLine("What to set after adjusting, where the adjustment alone leaves the oven 5 °F (3 °C) or more out:");
                    sb.AppendLine();
                    Table(sb, DialColumns, dial);
                    sb.AppendLine();
                }
            }
            sb.AppendLine("[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.");
            sb.AppendLine();
        }

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
        sb.AppendLine($"- {SensorAccuracy(top)}");
        foreach (var line in Gaps()) sb.AppendLine($"- {line}");
        sb.AppendLine("- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.");
        sb.AppendLine("- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.");
        sb.AppendLine();
        sb.AppendLine($"Settings: precision ±{f.Span(info.Options.PrecisionC)}, at least {info.Options.MinCycles} cycles, up to {info.Options.MaxPointMinutes:F0} min per setpoint. " +
                      $"Oven's thermostat adjustment during the run: {(info.AdjustmentC is { } adj ? Adjustment(adj) : "not recorded")}. Raw data: `readings.csv`.");
        return sb.ToString();
    }

    /// <summary>A gap in the readings longer than this (a minute) is worth mentioning.</summary>
    public static readonly TimeSpan NotableGap = TimeSpan.FromMinutes(1);

    /// <summary>Setpoints whose readings have a long gap, e.g. while the meter was switched off.</summary>
    public IEnumerable<string> Gaps() =>
        results.Where(r => r.Metrics.LongestGapSeconds is { } g && g >= NotableGap.TotalSeconds)
            .Select(r => $"{Temp.ModeName(r.Point.Mode)} {f.Setpoint(r.Point.SetpointC)} has a {Minutes(r.Metrics.LongestGapSeconds!.Value / 60)} gap in its readings " +
                         "(the meter went quiet, perhaps its auto power-off). The cycle means across the gap are interpolated, so treat it with a little more care.");

    /// <summary>What the sensor and meter add to the statistical uncertainty, at the highest setpoint.</summary>
    public string SensorAccuracy(double topC)
    {
        const string start = "The ±95 % column is the statistical uncertainty only. ";
        const string end = "An offset smaller than these may be the probe, not the oven.";
        if (info.Sensor is not { } s)
            return start + "The probe and the meter each add their own error, given in their data sheets. " + end;
        var limit = s.LimitsAdd
            ? $"±{f.Span(s.LimitC)} plus {(s.LimitFraction * 100).ToString("0.###", Inv)} % of the temperature"
            : $"the greater of ±{f.Span(s.LimitC)} and ±{(s.LimitFraction * 100).ToString("0.###", Inv)} % of the temperature";
        return start + $"The probe itself adds its own: a standard-grade {s.Name} is good to {limit}, ±{f.Span(s.ToleranceC(topC))} at {f.Setpoint(topC)}, " +
               "and the meter's own accuracy (in its data sheet) adds to that. " + end;
    }

    static void Table(StringBuilder sb,IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => " --- |")));
        foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r.Select(c => c.Replace("|", "\\|"))) + " |");
    }
}
