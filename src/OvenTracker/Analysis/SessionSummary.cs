namespace OvenTracker.Analysis;

public sealed record TestPoint(int Index, OvenMode Mode, double SetpointC);

public sealed record PointResult(TestPoint Point, PointMetrics Metrics);

public enum Verdict { Accurate, Borderline, OutOfTolerance, Inconclusive }

public sealed record ModeSummary(
    OvenMode Mode,
    int Points,
    double MeanOffsetC,
    double MaxAbsOffsetC,
    double? GainPerDegree,
    double? MeanSwingC,
    double? MeanCycleMinutes,
    double SuggestedCalibrationC);

public sealed record ModeComparison(double SetpointC, PointMetrics Bake, PointMetrics Convection)
{
    /// <summary>Convection mean minus bake mean.</summary>
    public double DeltaMeanC => Convection.MeanC!.Value - Bake.MeanC!.Value;
    public double? DeltaSwingC => Convection.SwingC - Bake.SwingC;
    public double? DeltaTimeToSetpointMinutes => Convection.TimeToSetpointMinutes - Bake.TimeToSetpointMinutes;

    /// <summary>Whether the two means differ by more than their combined 95 % uncertainty.</summary>
    public bool Significant
    {
        get
        {
            var a = Bake.HalfWidthC ?? double.PositiveInfinity;
            var b = Convection.HalfWidthC ?? double.PositiveInfinity;
            return Math.Abs(DeltaMeanC) > Math.Sqrt(a * a + b * b);
        }
    }
}

public static class SessionSummary
{
    public static Verdict Judge(PointMetrics m, double toleranceC)
    {
        if (m.OffsetC is not { } off) return Verdict.Inconclusive;
        var hw = m.HalfWidthC ?? double.PositiveInfinity;
        if (double.IsInfinity(hw)) return Math.Abs(off) <= toleranceC ? Verdict.Borderline : Verdict.OutOfTolerance;
        if (Math.Abs(off) + hw <= toleranceC) return Verdict.Accurate;
        if (Math.Abs(off) - hw > toleranceC) return Verdict.OutOfTolerance;
        return Verdict.Borderline;
    }

    public static IReadOnlyList<PointResult> Usable(IEnumerable<PointResult> results) =>
        results.Where(r => r.Metrics.MeanC is not null).ToList();

    public static IReadOnlyList<ModeSummary> ByMode(IEnumerable<PointResult> results)
    {
        var list = new List<ModeSummary>();
        foreach (var g in Usable(results).GroupBy(r => r.Point.Mode).OrderBy(g => g.Key))
        {
            var pts = g.OrderBy(r => r.Point.SetpointC).ToList();
            var offsets = pts.Select(r => r.Metrics.OffsetC!.Value).ToList();
            double? gain = null;
            if (pts.Select(p => p.Point.SetpointC).Distinct().Count() >= 2)
                gain = Stats.Fit(pts.Select(p => p.Point.SetpointC).ToList(), pts.Select(p => p.Metrics.MeanC!.Value).ToList())?.Slope;
            var swings = pts.Where(p => p.Metrics.SwingC is not null).Select(p => p.Metrics.SwingC!.Value).ToList();
            var cycles = pts.Where(p => p.Metrics.CycleMinutes is not null).Select(p => p.Metrics.CycleMinutes!.Value).ToList();
            var meanOffset = offsets.Average();
            list.Add(new ModeSummary(
                g.Key, pts.Count, meanOffset, offsets.Max(Math.Abs), gain,
                swings.Count > 0 ? swings.Average() : null,
                cycles.Count > 0 ? cycles.Average() : null,
                -meanOffset));
        }
        return list;
    }

    public static IReadOnlyList<ModeComparison> Compare(IEnumerable<PointResult> results)
    {
        var usable = Usable(results);
        var list = new List<ModeComparison>();
        foreach (var g in usable.GroupBy(r => Math.Round(r.Point.SetpointC, 2)).OrderBy(g => g.Key))
        {
            var bake = g.LastOrDefault(r => r.Point.Mode == OvenMode.Bake);
            var conv = g.LastOrDefault(r => r.Point.Mode == OvenMode.Convection);
            if (bake is not null && conv is not null)
                list.Add(new ModeComparison(g.First().Point.SetpointC, bake.Metrics, conv.Metrics));
        }
        return list;
    }

    /// <summary>Type K standard limits of error (ASTM E230): the greater of 2.2 °C and 0.75 % of the temperature.</summary>
    public static double TypeKStandardLimitC(double c) => Math.Max(2.2, 0.0075 * Math.Abs(c));
}
