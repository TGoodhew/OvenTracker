namespace OvenTracker.Analysis;

public enum PointPhase { Heating, Settling, Measuring, Complete }

/// <summary>How the steady state is cut into independent pieces for the statistics.</summary>
public enum UnitKind
{
    /// <summary>One full thermostat cycle, trough to trough.</summary>
    Cycle,
    /// <summary>A fixed-length block, for ovens that hold temperature without a visible on/off cycle.</summary>
    Block,
}

public enum CompletionReason { None, Converged, TimedOut, Manual, Aborted, Skipped }

public sealed record AnalysisOptions
{
    /// <summary>Target half-width of the 95 % confidence interval of the mean (°C).</summary>
    public double PrecisionC { get; init; } = 1.0;

    /// <summary>Fewest full thermostat cycles a result is ever based on.</summary>
    public int MinCycles { get; init; } = 4;

    /// <summary>Fewest blocks a result is based on when the oven does not cycle visibly.</summary>
    public int MinBlocks { get; init; } = 5;

    public double BlockMinutes { get; init; } = 3;

    /// <summary>Smallest reversal counted as a thermostat turning point (°C).</summary>
    public double MinHysteresisC { get; init; } = 1.0;

    public double SmoothingSeconds { get; init; } = 5;

    /// <summary>Longest the app spends at one setpoint before reporting what it has.</summary>
    public double MaxPointMinutes { get; init; } = 120;

    /// <summary>No turning point for this long, and flat: the oven holds without cycling.</summary>
    public double NoCycleMinutes { get; init; } = 15;

    public double FlatWindowMinutes { get; init; } = 10;
    public double FlatSlopeCPerMinute { get; init; } = 0.15;

    /// <summary>
    /// A trend in the cycle means counts as drift when, carried over at least this long, it would
    /// move the mean by more than the precision target. Short windows would otherwise hide a slow
    /// heat-soak drift.
    /// </summary>
    public double DriftHorizonMinutes { get; init; } = 30;

    /// <summary>Used for time estimates before the oven's own cycle has been seen.</summary>
    public double DefaultCycleMinutes { get; init; } = 6;
}

/// <summary>One cycle or block of the steady state.</summary>
public sealed record MeasurementUnit(UnitKind Kind, double Start, double End, double MeanC, double MinC, double MaxC)
{
    public double Duration => End - Start;
    public double Mid => (Start + End) / 2;
    public double SwingC => MaxC - MinC;
}

public sealed record Estimate(int N, double MeanC, double StdDevC, double? HalfWidthC, double? DriftCPerHour);

public sealed record Eta(double Seconds, bool Rough);

public sealed record PointMetrics(
    double SetpointC,
    CompletionReason Reason,
    UnitKind? Kind,
    int Units,
    int DiscardedUnits,
    double? MeanC,
    double? HalfWidthC,
    double? StdDevC,
    double? DriftCPerHour,
    double? SwingC,
    double? MinC,
    double? MaxC,
    double? CycleMinutes,
    double? StartC,
    double? TimeToSetpointMinutes,
    double? OvershootC,
    double? SettleMinutes,
    double? MeasureMinutes,
    double TotalMinutes,
    double? PreheatSignalMinutes,
    double? PreheatSignalC,
    double PrecisionTargetC,
    double? LongestGapSeconds = null)
{
    public double? OffsetC => MeanC - SetpointC;
    public bool MetPrecision => HalfWidthC is { } hw && hw <= PrecisionTargetC + 1e-9;
}

/// <summary>
/// Works out, from the readings alone, when the oven has settled at a setpoint, measures its
/// steady-state mean over whole thermostat cycles, and decides how long it has to keep measuring.
/// </summary>
/// <remarks>
/// The phases are:
/// <list type="number">
/// <item>Heating (or cooling) until the first turning point: the first peak is the overshoot.</item>
/// <item>Settling: the first cycle after the overshoot is not used; measuring starts at the first
/// trough that is not the first turning point.</item>
/// <item>Measuring: each trough closes a cycle, whose time-weighted mean is one independent value.
/// While the cycle means still trend (the walls are still soaking up heat) the oldest cycle is dropped.
/// It finishes once there are at least <see cref="AnalysisOptions.MinCycles"/> cycles and the 95 %
/// interval of their mean is within <see cref="AnalysisOptions.PrecisionC"/>. The number of cycles
/// that needs, times the oven's own cycle length, is the remaining time it shows.</item>
/// </list>
/// An oven that holds temperature without a visible cycle is measured in fixed blocks instead.
/// </remarks>
public sealed class SetpointAnalyzer
{
    readonly AnalysisOptions o;
    readonly SampleSeries series = new();
    readonly TurningPointDetector detector = new();
    readonly List<TurningPoint> turningPoints = [];
    readonly List<MeasurementUnit> units = [];
    readonly List<double> cycleDurations = [];
    readonly Queue<Sample> smoothWindow = new();
    readonly List<double> noiseWindow = [];
    double smoothSum;
    double noiseSigma;
    double unitStart;
    double lastFlatCheck = double.NegativeInfinity;
    double? measuringStart;
    double? timeToSetpoint;
    double? overshootC;
    double? preheatT, preheatC;

    public SetpointAnalyzer(double setpointC, AnalysisOptions? options = null)
    {
        SetpointC = setpointC;
        o = options ?? new AnalysisOptions();
    }

    public double SetpointC { get; }
    public AnalysisOptions Options => o;
    public PointPhase Phase { get; private set; } = PointPhase.Heating;
    public UnitKind? Kind { get; private set; }
    public CompletionReason Reason { get; private set; }
    public double? CompletedAt { get; private set; }
    public SampleSeries Series => series;
    public IReadOnlyList<TurningPoint> TurningPoints => turningPoints;
    public IReadOnlyList<MeasurementUnit> Units => units;
    public int DiscardedUnits { get; private set; }
    public double Now => series.Count == 0 ? 0 : series.Last.T;
    public double? LatestC => series.Count == 0 ? null : series.Last.C;
    public double? SmoothedC => smoothWindow.Count == 0 ? null : smoothSum / smoothWindow.Count;
    public double? StartC { get; private set; }
    public double Hysteresis => Math.Max(o.MinHysteresisC, 5 * noiseSigma);
    public double? MeasuringStart => measuringStart;
    public bool Cooling => StartC is { } s && s > SetpointC;
    public int MinUnits => Kind == UnitKind.Block ? o.MinBlocks : o.MinCycles;

    public void Add(double t, double c)
    {
        if (Phase == PointPhase.Complete) return;
        if (!series.Add(new Sample(t, c))) return;
        StartC ??= c;

        smoothWindow.Enqueue(new Sample(t, c));
        smoothSum += c;
        while (smoothWindow.Peek().T < t - o.SmoothingSeconds)
            smoothSum -= smoothWindow.Dequeue().C;
        var smooth = smoothSum / smoothWindow.Count;

        noiseWindow.Add(c);
        if (noiseWindow.Count > 120) noiseWindow.RemoveAt(0);
        if (series.Count % 30 == 0) noiseSigma = Stats.NoiseSigma(noiseWindow);

        if (timeToSetpoint is null && (Cooling ? smooth <= SetpointC : smooth >= SetpointC))
            timeToSetpoint = t;

        var tp = detector.Add(t, smooth, Hysteresis);
        if (tp is not null) OnTurningPoint(tp);

        if (Phase == PointPhase.Measuring && Kind == UnitKind.Block)
        {
            var len = o.BlockMinutes * 60;
            while (Phase == PointPhase.Measuring && t >= unitStart + len)
            {
                CloseUnit(UnitKind.Block, unitStart, unitStart + len);
                unitStart += len;
            }
        }

        if (Phase != PointPhase.Complete && t - lastFlatCheck >= 10)
        {
            lastFlatCheck = t;
            CheckForNoCycling(t);
        }

        if (Phase != PointPhase.Complete && t >= o.MaxPointMinutes * 60)
            Complete(CompletionReason.TimedOut);
    }

    void OnTurningPoint(TurningPoint tp)
    {
        turningPoints.Add(tp);

        if (Phase == PointPhase.Heating)
        {
            Phase = PointPhase.Settling;
            timeToSetpoint ??= tp.T; // an oven that runs cold turns round before it gets there
            if (tp.Kind == TurningKind.Peak && !Cooling)
                overshootC = series.Range(tp.T - 30, tp.T + 30).Max - SetpointC;
        }

        if (tp.Kind != TurningKind.Trough || Kind == UnitKind.Block) return;

        if (Phase == PointPhase.Measuring)
        {
            CloseUnit(UnitKind.Cycle, unitStart, tp.T);
            unitStart = tp.T;
        }
        else if (Phase == PointPhase.Settling && turningPoints.Count >= 2)
        {
            Phase = PointPhase.Measuring;
            Kind = UnitKind.Cycle;
            measuringStart = tp.T;
            unitStart = tp.T;
        }
    }

    void CheckForNoCycling(double t)
    {
        var lastTurn = turningPoints.Count > 0 ? turningPoints[^1].ConfirmedAt : 0;
        var quietFor = t - lastTurn;
        var noCycle = o.NoCycleMinutes * 60;

        // Measuring cycles, but the cycling has stopped: fall back to blocks from here.
        if (Phase == PointPhase.Measuring && Kind == UnitKind.Cycle)
        {
            var expected = ExpectedUnitSeconds() ?? o.DefaultCycleMinutes * 60;
            if (quietFor < Math.Max(3 * expected, noCycle) || !IsFlat(t)) return;
            DiscardedUnits += units.Count;
            units.Clear();
            StartBlocks(t);
            return;
        }

        if (Phase is PointPhase.Heating or PointPhase.Settling && quietFor >= noCycle && t >= o.FlatWindowMinutes * 60 && IsFlat(t))
            StartBlocks(t);
    }

    bool IsFlat(double t) =>
        series.Slope(t - o.FlatWindowMinutes * 60, t) is { } slope && Math.Abs(slope * 60) < o.FlatSlopeCPerMinute;

    void StartBlocks(double t)
    {
        if (Phase == PointPhase.Heating) Phase = PointPhase.Settling;
        Phase = PointPhase.Measuring;
        Kind = UnitKind.Block;
        measuringStart ??= t;
        unitStart = t;
    }

    void CloseUnit(UnitKind kind, double t0, double t1)
    {
        if (t1 - t0 < 20) return; // a reversal this quick is a disturbance, not a cycle
        var (min, max) = series.Range(t0, t1);
        units.Add(new MeasurementUnit(kind, t0, t1, series.Mean(t0, t1), min, max));
        if (kind == UnitKind.Cycle) cycleDurations.Add(t1 - t0);
        Evaluate();
    }

    void Evaluate()
    {
        DropWarmUpUnits();
        var e = CurrentEstimate;
        if (e is { HalfWidthC: { } hw } && e.N >= MinUnits && hw <= o.PrecisionC)
            Complete(CompletionReason.Converged);
    }

    /// <summary>While the unit means still trend by more than the precision target, the oven is still settling.</summary>
    void DropWarmUpUnits()
    {
        while (units.Count >= 3)
        {
            var fit = Stats.Fit(units.Select(u => u.Mid).ToList(), units.Select(u => u.MeanC).ToList());
            if (fit?.SlopeStdErr is not { } se) return;
            var change = Math.Abs(fit.Slope * Math.Max(units[^1].Mid - units[0].Mid, o.DriftHorizonMinutes * 60));
            var tStat = se > 0 ? Math.Abs(fit.Slope) / se : fit.Slope != 0 ? double.PositiveInfinity : 0;
            if (change <= o.PrecisionC || tStat <= 2) return;
            units.RemoveAt(0);
            DiscardedUnits++;
        }
    }

    void Complete(CompletionReason reason)
    {
        Phase = PointPhase.Complete;
        Reason = reason;
        CompletedAt = Now;
    }

    public void MarkPreheatSignal()
    {
        if (series.Count == 0) return;
        preheatT = Now;
        preheatC = SmoothedC;
    }

    public void Finish(CompletionReason reason)
    {
        if (Phase != PointPhase.Complete) Complete(reason);
    }

    public Estimate? CurrentEstimate
    {
        get
        {
            if (units.Count == 0) return null;
            var means = units.Select(u => u.MeanC).ToList();
            double? drift = null;
            if (units.Count >= 3 && Stats.Fit(units.Select(u => u.Mid).ToList(), means) is { } fit)
                drift = fit.Slope * 3600;
            return new Estimate(units.Count, Stats.Mean(means), Stats.StdDev(means), Stats.HalfWidth95(means), drift);
        }
    }

    /// <summary>How many units the current spread says are needed to reach the precision target.</summary>
    public int RequiredUnits
    {
        get
        {
            if (units.Count < 2) return MinUnits;
            var sd = Stats.StdDev(units.Select(u => u.MeanC).ToList());
            return Math.Max(units.Count, Stats.RequiredCount(sd, o.PrecisionC, MinUnits, 99));
        }
    }

    /// <summary>Expected length of one unit: the oven's own cycle once it has been seen.</summary>
    public double? ExpectedUnitSeconds()
    {
        if (Kind == UnitKind.Block) return o.BlockMinutes * 60;
        if (cycleDurations.Count > 0) return Stats.Median(cycleDurations);
        if (turningPoints.Count >= 2)
        {
            var gaps = new List<double>();
            for (var i = 1; i < turningPoints.Count; i++) gaps.Add(turningPoints[i].T - turningPoints[i - 1].T);
            return 2 * Stats.Median(gaps);
        }
        return null;
    }

    /// <summary>Estimated time still needed at this setpoint.</summary>
    public Eta? EstimateRemaining()
    {
        if (Phase == PointPhase.Complete) return new Eta(0, false);
        var now = Now;
        var cap = Math.Max(0, o.MaxPointMinutes * 60 - now);
        var known = ExpectedUnitSeconds();
        var unit = known ?? o.DefaultCycleMinutes * 60;
        var required = RequiredUnits;
        double seconds;
        bool rough;

        switch (Phase)
        {
            case PointPhase.Measuring:
                var into = now - unitStart;
                seconds = required > units.Count ? (required - units.Count) * unit - into : unit - into;
                seconds = Math.Max(seconds, 0);
                rough = units.Count < 2;
                break;
            case PointPhase.Settling:
                seconds = 0.5 * unit + required * unit;
                rough = true;
                break;
            default:
                var slope = series.Count > 10 ? series.Slope(now - 120, now) : null;
                var toGo = SetpointC - (SmoothedC ?? SetpointC);
                double reach = 0;
                if (slope is { } s && Math.Abs(s) > 0.002 && Math.Sign(s) == Math.Sign(toGo)) reach = toGo / s;
                else if (Math.Abs(toGo) > 5) return null;
                seconds = reach + 1.5 * unit + required * unit;
                rough = true;
                break;
        }
        return new Eta(Math.Min(seconds, cap), rough);
    }

    /// <summary>The longest time between two readings, e.g. while the meter was switched off.</summary>
    double? LongestGap()
    {
        var items = series.Items;
        if (items.Count < 2) return null;
        double longest = 0;
        for (var i = 1; i < items.Count; i++) longest = Math.Max(longest, items[i].T - items[i - 1].T);
        return longest;
    }

    public PointMetrics Metrics()
    {
        var e = CurrentEstimate;
        double? swing = null, min = null, max = null, cycleMin = null, measureMin = null;
        if (units.Count > 0)
        {
            swing = units.Average(u => u.SwingC);
            min = units.Min(u => u.MinC);
            max = units.Max(u => u.MaxC);
            measureMin = (units[^1].End - units[0].Start) / 60;
            if (Kind == UnitKind.Cycle) cycleMin = units.Average(u => u.Duration) / 60;
        }
        return new PointMetrics(
            SetpointC, Reason, Kind, units.Count, DiscardedUnits,
            e?.MeanC, e?.HalfWidthC, e?.StdDevC, e?.DriftCPerHour,
            swing, min, max, cycleMin,
            StartC, timeToSetpoint / 60, overshootC,
            units.Count > 0 ? units[0].Start / 60 : measuringStart / 60,
            measureMin, (CompletedAt ?? Now) / 60,
            preheatT / 60, preheatC, o.PrecisionC, LongestGap());
    }
}
