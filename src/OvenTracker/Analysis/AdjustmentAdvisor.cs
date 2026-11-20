namespace OvenTracker.Analysis;

/// <summary>What the recommended adjustment should make as small as it can.</summary>
public enum AdjustObjective
{
    /// <summary>The overall error: least squares across the measured setpoints.</summary>
    Balanced,

    /// <summary>The worst setpoint's error.</summary>
    Minimax,
}

/// <summary>How the oven's thermostat adjustment is assumed to act, when the runs don't show it.</summary>
public enum AdjustModel
{
    /// <summary>An adjustment of k moves the oven by k at every setting.</summary>
    Shift,

    /// <summary>An adjustment of k moves the oven by b·k, where b is the slope of measured against set.</summary>
    Gain,
}

/// <param name="StepC">The oven's adjustment step (1 °F on the GE PHS925).</param>
/// <param name="RangeC">How far the adjustment goes either way (±35 °F on the PHS925).</param>
/// <param name="FavourC">Make the oven right at this temperature, rather than across all of them.</param>
/// <param name="SharedAcrossModes">One adjustment for bake and convection, as on most ovens.</param>
/// <param name="KnownGains">Gains measured on this oven before (its profile's, from characterise), used when the runs don't measure one.</param>
public sealed record AdjustmentOptions(
    double StepC,
    double RangeC,
    AdjustObjective Objective = AdjustObjective.Balanced,
    double? FavourC = null,
    AdjustModel Model = AdjustModel.Shift,
    bool SharedAcrossModes = true,
    IReadOnlyDictionary<OvenMode, double>? KnownGains = null)
{
    /// <summary>The defaults for an oven that works in this unit: 1° steps, up to ±35 °F (±19 °C).</summary>
    public static AdjustmentOptions For(TempUnit unit) =>
        unit == TempUnit.Fahrenheit ? new(1 / 1.8, 35 / 1.8) : new(1, 19);

    /// <summary>
    /// The defaults for a known oven: its step, range and sharing, and any gains measured on it. The generic
    /// profile gives the unit's defaults, so a °C oven gets 1 °C steps.
    /// </summary>
    public static AdjustmentOptions For(Ovens.OvenProfile oven, TempUnit unit) => oven.IsGeneric
        ? For(unit) with { KnownGains = Gains(oven) }
        : new(oven.AdjustStepC, oven.AdjustRangeC, SharedAcrossModes: oven.SharedAdjustment, KnownGains: Gains(oven));

    static IReadOnlyDictionary<OvenMode, double>? Gains(Ovens.OvenProfile oven) =>
        oven.Gains is { Count: > 0 } g ? g.ToDictionary(kv => kv.Key, kv => kv.Value.Gain) : null;
}

/// <summary>One measured setpoint, and the adjustment the oven had while it was measured.</summary>
/// <param name="AdjustmentC">The oven's thermostat adjustment during the run; 0 when the run didn't record one.</param>
public sealed record AdjustPoint(OvenMode Mode, double SetpointC, double OffsetC, double? HalfWidthC, double AdjustmentC);

/// <summary>A setpoint's error now and after the recommended adjustment.</summary>
public sealed record Residual(OvenMode Mode, double SetpointC, double NowC, double AfterC, double? HalfWidthC);

/// <summary>After adjusting, what to set to get a temperature, where the adjustment leaves it out.</summary>
public sealed record DialCorrection(OvenMode Mode, double WantC, double SetC);

/// <param name="ChangeC">How much to change the adjustment by.</param>
/// <param name="CurrentC">The adjustment the oven has now, if the run recorded it.</param>
/// <param name="NewSettingC">What to set the adjustment to, when the current one is known.</param>
/// <param name="Clamped">The adjustment wanted was beyond the oven's range, so this is the limit.</param>
/// <param name="Gain">How far the oven moves per degree of adjustment.</param>
/// <param name="GainMeasured">Whether <see cref="Gain"/> was measured (runs at different adjustments) or assumed.</param>
/// <param name="GainFromProfile">Whether <see cref="Gain"/> came from the oven's profile (measured before by characterise).</param>
/// <param name="GainHalfWidthC">The measured gain's uncertainty, roughly, per degree.</param>
/// <param name="Mode">The mode this advice is for, or null for one adjustment across both.</param>
/// <param name="AddSetpointsC">Setpoints worth adding to tell a fixed offset from one that changes with temperature.</param>
/// <param name="TooFewSetpoints">Why the setpoints can't show the error's shape, when they can't.</param>
/// <param name="Curved">The error bends with temperature, more than a straight line explains.</param>
public sealed record AdjustmentAdvice(
    double ChangeC,
    double? CurrentC,
    double? NewSettingC,
    bool Clamped,
    double Gain,
    bool GainMeasured,
    double? GainHalfWidthC,
    AdjustmentOptions Options,
    OvenMode? Mode,
    IReadOnlyList<Residual> Residuals,
    IReadOnlyList<DialCorrection> DialCorrections,
    IReadOnlyList<double> AddSetpointsC,
    string? TooFewSetpoints,
    bool Curved,
    bool GainFromProfile = false)
{
    public double WorstAfterC => Residuals.Count == 0 ? 0 : Residuals.Max(r => Math.Abs(r.AfterC));
}

/// <summary>
/// Works out the thermostat adjustment that fits the oven best across its setpoints. It models the
/// error at each setpoint, how far the oven's adjustment really moves it, and what the adjustment
/// leaves behind. See docs/oven-control-model.md.
/// </summary>
public static class AdjustmentAdvisor
{
    /// <summary>An error below this isn't worth correcting: about the probe's own accuracy (5 °F).</summary>
    public const double WorthC = 5 / 1.8;

    /// <summary>Setpoints closer together than this (50 °F) can't show how the error changes with temperature.</summary>
    public const double ShapeSpanC = 50 / 1.8;

    /// <summary>Setpoints within this of each other count as the same (runs in °F store 176.667 and 176.67).</summary>
    const double SameSetpointC = 0.05;

    /// <param name="currentC">The oven's adjustment now (the latest run's), if known.</param>
    public static IReadOnlyList<AdjustmentAdvice> Advise(IReadOnlyList<AdjustPoint> points, AdjustmentOptions options, double? currentC)
    {
        if (points.Count == 0) return [];
        var modes = points.Select(p => p.Mode).Distinct().ToList();
        List<(OvenMode? Mode, IReadOnlyList<AdjustPoint> Points)> groups = options.SharedAcrossModes
            // With only one mode measured, "shared" advice is that mode's advice.
            ? [(modes.Count == 1 ? modes[0] : null, points)]
            : points.GroupBy(p => p.Mode).OrderBy(g => g.Key).Select(g => ((OvenMode?)g.Key, (IReadOnlyList<AdjustPoint>)g.ToList())).ToList();
        return groups.Select(g => AdviseGroup(g.Points, options, currentC, g.Mode)).ToList();
    }

    static AdjustmentAdvice AdviseGroup(IReadOnlyList<AdjustPoint> points, AdjustmentOptions options, double? currentC, OvenMode? mode)
    {
        var reference = currentC ?? points[^1].AdjustmentC;
        var (gain, measured, gainHw, fromProfile) = Gain(points, options);

        // Every setpoint's error as it would be at the reference adjustment, repeats averaged.
        var now = points
            .Select(p => p with { OffsetC = p.OffsetC + gain * (reference - p.AdjustmentC), AdjustmentC = reference })
            .GroupBy(p => (p.Mode, Key: Math.Round(p.SetpointC / SameSetpointC)))
            .Select(g => new AdjustPoint(g.Key.Mode, g.Average(p => p.SetpointC), g.Average(p => p.OffsetC),
                g.All(p => p.HalfWidthC is not null) ? Math.Sqrt(g.Sum(p => p.HalfWidthC!.Value * p.HalfWidthC.Value)) / g.Count() : null, reference))
            .OrderBy(p => p.Mode).ThenBy(p => p.SetpointC)
            .ToList();

        var change = Best(now, options, gain);
        var clamped = false;
        var lo = -options.RangeC - (currentC ?? 0);
        var hi = options.RangeC - (currentC ?? 0);
        if (change < lo - 1e-9 || change > hi + 1e-9)
        {
            clamped = true;
            change = Math.Clamp(change, SnapUp(lo, options.StepC), SnapDown(hi, options.StepC));
        }

        var residuals = now.Select(p => new Residual(p.Mode, p.SetpointC, p.OffsetC, p.OffsetC + gain * change, p.HalfWidthC)).ToList();
        return new AdjustmentAdvice(
            change, currentC, currentC is { } c ? c + change : null, clamped, gain, measured, gainHw, options, mode,
            residuals, DialCorrections(residuals), AddSetpoints(now, out var why), why, IsCurved(now), fromProfile);
    }

    /// <summary>
    /// How far the oven moves per degree of adjustment. Measured, from the same setpoint run at
    /// different adjustments, when there are such pairs; otherwise assumed from the model.
    /// </summary>
    static (double Gain, bool Measured, double? HalfWidth, bool FromProfile) Gain(IReadOnlyList<AdjustPoint> points, AdjustmentOptions options)
    {
        double num = 0, den = 0, variance = 0;
        foreach (var g in points.GroupBy(p => (p.Mode, Key: Math.Round(p.SetpointC / SameSetpointC))))
        {
            var list = g.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var dk = list[j].AdjustmentC - list[i].AdjustmentC;
                    if (Math.Abs(dk) < 2 * options.StepC - 1e-9) continue; // too small a change to measure anything
                    num += (list[j].OffsetC - list[i].OffsetC) * dk;
                    den += dk * dk;
                    var hw = Math.Pow(list[i].HalfWidthC ?? 1, 2) + Math.Pow(list[j].HalfWidthC ?? 1, 2);
                    variance += hw * dk * dk;
                }
        }
        if (den > 0) return (num / den, true, Math.Sqrt(variance) / den, false);

        // Measured on this oven before: the profile's gains for the modes here (their mean, when they share one adjustment).
        if (options.KnownGains is { } known && points.Select(p => p.Mode).Distinct().Where(known.ContainsKey).ToList() is { Count: > 0 } modes)
            return (modes.Average(m => known[m]), false, null, true);

        if (options.Model == AdjustModel.Gain)
        {
            var fit = Stats.Fit(points.Select(p => p.SetpointC).ToList(), points.Select(p => p.SetpointC + p.OffsetC).ToList());
            if (fit is { Slope: > 0.5 and < 1.5 }) return (fit.Slope, false, null, false);
        }
        return (1, false, null, false);
    }

    /// <summary>The change that best meets the objective, on the oven's steps.</summary>
    static double Best(IReadOnlyList<AdjustPoint> now, AdjustmentOptions options, double gain)
    {
        if (gain <= 0.05) return 0; // the adjustment doesn't move the oven: nothing to recommend
        if (options.FavourC is { } favour)
        {
            var at = ErrorAt(now, favour);
            return Snap(-at / gain, options.StepC);
        }

        var offsets = now.Select(p => p.OffsetC).ToList();
        var ideal = options.Objective == AdjustObjective.Minimax
            ? -(offsets.Max() + offsets.Min()) / 2 / gain
            : -offsets.Average() / gain;
        // The objective is convex, so the best step is on one side of the ideal or the other.
        var down = SnapDown(ideal, options.StepC);
        var up = SnapUp(ideal, options.StepC);
        double Cost(double change) => options.Objective == AdjustObjective.Minimax
            ? offsets.Max(o => Math.Abs(o + gain * change))
            : offsets.Sum(o => (o + gain * change) * (o + gain * change));
        var (a, b) = (Cost(down), Cost(up));
        return Math.Abs(a - b) < 1e-9 ? (Math.Abs(down) <= Math.Abs(up) ? down : up) : a < b ? down : up;
    }

    /// <summary>The error at a temperature: from a straight line through the setpoints, or their mean with only one.</summary>
    static double ErrorAt(IReadOnlyList<AdjustPoint> now, double c)
    {
        var fit = Stats.Fit(now.Select(p => p.SetpointC).ToList(), now.Select(p => p.OffsetC).ToList());
        return fit is null ? now.Average(p => p.OffsetC) : fit.Intercept + fit.Slope * c;
    }

    static IReadOnlyList<DialCorrection> DialCorrections(IReadOnlyList<Residual> residuals)
    {
        if (residuals.All(r => Math.Abs(r.AfterC) < WorthC)) return [];
        // Where the oven still runs hot by r at a setting, set that much lower to get the temperature wanted.
        return residuals.Where(r => Math.Abs(r.AfterC) >= WorthC)
            .Select(r => new DialCorrection(r.Mode, r.SetpointC, r.SetpointC - r.AfterC))
            .ToList();
    }

    /// <summary>Setpoints to add when the ones measured can't show whether the error changes with temperature.</summary>
    static IReadOnlyList<double> AddSetpoints(IReadOnlyList<AdjustPoint> now, out string? why)
    {
        why = null;
        var add = new List<double>();
        foreach (var g in now.GroupBy(p => p.Mode))
        {
            var sps = g.Select(p => p.SetpointC).Distinct().Order().ToList();
            var span = sps[^1] - sps[0];
            if (sps.Count == 1)
                why = "With one setpoint per mode, the app can't tell an error that's the same at every setting from one that grows with temperature.";
            else if (span < ShapeSpanC)
                why = "The setpoints are too close together to show how the error changes with temperature.";
            else if (sps.Count == 2 && Math.Abs(g.Last().OffsetC - g.First().OffsetC) >= WorthC)
                why = "The error changes between the two setpoints. A third, in between, would show whether it changes in a straight line.";
            else continue;

            if (sps.Count == 2 && span >= ShapeSpanC) add.Add((sps[0] + sps[1]) / 2);
            else
            {
                add.Add(sps[0] - ShapeSpanC);
                add.Add(sps[^1] + ShapeSpanC);
            }
        }
        // Keep to ordinary baking temperatures (250–500 °F).
        return add.Select(c => Math.Clamp(c, 121, 260)).Distinct().Order().ToList();
    }

    /// <summary>With three or more setpoints in a mode, whether the error bends more than a straight line and the uncertainty explain.</summary>
    static bool IsCurved(IReadOnlyList<AdjustPoint> now)
    {
        foreach (var g in now.GroupBy(p => p.Mode))
        {
            var list = g.ToList();
            if (list.Count < 3) continue;
            var fit = Stats.Fit(list.Select(p => p.SetpointC).ToList(), list.Select(p => p.OffsetC).ToList());
            if (fit is null) continue;
            var worst = list.Max(p => Math.Abs(p.OffsetC - (fit.Intercept + fit.Slope * p.SetpointC)) - (p.HalfWidthC ?? 0));
            if (worst > Math.Max(1.0, WorthC / 2)) return true;
        }
        return false;
    }

    static double Snap(double c, double step) => Math.Round(c / step, MidpointRounding.AwayFromZero) * step;
    static double SnapDown(double c, double step) => Math.Floor(c / step + 1e-9) * step;
    static double SnapUp(double c, double step) => Math.Ceiling(c / step - 1e-9) * step;
}
