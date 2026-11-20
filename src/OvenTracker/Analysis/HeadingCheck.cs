namespace OvenTracker.Analysis;

public enum HeadingState
{
    /// <summary>Too early to say, or the step is too small to tell.</summary>
    Unknown,

    /// <summary>The oven is going the right way.</summary>
    OnItsWay,

    /// <summary>The oven isn't going towards the new setting: it probably wasn't changed.</summary>
    NotHeading,
}

/// <summary>
/// Checks, at the start of a setpoint, that the oven is heading for its new setting: if the setting is
/// well above where the oven was, it should be getting hotter; well below, cooler. An oven left at its
/// old setting (the user forgot to change it) just cycles around where it was. Within
/// <see cref="Within"/>, the oven should have made <see cref="ProgressC"/> of progress: half the step,
/// but no more than 25 °F. A real heat-up or cool-down does that in a minute or two, even from cold,
/// while a thermostat cycling at the old setting swings only about 15–17 °F either side of its mean.
/// So a forgotten change is caught for steps of about 35 °F and more, and the check never cries wolf.
/// A change of mode at the same temperature can't be seen this way.
/// </summary>
/// <param name="fromC">Where the oven was: the previous setpoint's measured mean, or the mean of its last two minutes.</param>
/// <param name="swingC">The thermostat's peak-to-peak swing, if measured (the previous setpoint's): progress must go beyond half of it.</param>
public sealed class HeadingCheck(double fromC, double setpointC, double? swingC = null)
{
    /// <summary>A smaller step (20 °F) can't be told from the thermostat's own swing.</summary>
    public const double MinStepC = 20 / 1.8;

    /// <summary>How long the oven has to make its progress.</summary>
    public static readonly TimeSpan Within = TimeSpan.FromMinutes(6);

    double best = fromC; // the furthest it has got towards the setting

    public double FromC { get; } = fromC;
    public double SetpointC => setpointC;
    public bool Applies => Math.Abs(setpointC - FromC) >= MinStepC;
    public bool Heating => setpointC > FromC;

    /// <summary>The most progress asked for (25 °F): more than a thermostat's swing either side of its mean.</summary>
    public const double MaxProgressC = 25 / 1.8;

    /// <summary>Beyond half the swing, by this much (5 °F), counts as more than the thermostat's own cycle.</summary>
    public const double SwingMarginC = 5 / 1.8;

    /// <summary>
    /// How far towards the setting the oven has to get: half the step, up to 25 °F, but always beyond half
    /// the measured swing (so a normal thermostat peak never counts), and no more than 80 % of the step.
    /// </summary>
    public double ProgressC
    {
        get
        {
            var step = Math.Abs(setpointC - FromC);
            var progress = Math.Min(step / 2, MaxProgressC);
            if (swingC is { } sw) progress = Math.Max(progress, sw / 2 + SwingMarginC);
            return Math.Min(progress, step * 0.8);
        }
    }

    /// <summary>The temperature that shows the oven is on its way.</summary>
    public double TargetC => Heating ? FromC + ProgressC : FromC - ProgressC;
    public HeadingState State { get; private set; } = HeadingState.Unknown;

    /// <param name="t">Seconds since the setpoint started.</param>
    public HeadingState Update(double t, double c)
    {
        if (!Applies) return State;
        best = Heating ? Math.Max(best, c) : Math.Min(best, c);
        var onItsWay = Heating ? best >= TargetC : best <= TargetC;
        if (onItsWay) State = HeadingState.OnItsWay;
        else if (t >= Within.TotalSeconds) State = HeadingState.NotHeading;
        return State;
    }
}
