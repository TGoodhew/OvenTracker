namespace OvenTracker.Analysis;

/// <summary>What the oven is doing while it is watched between sittings.</summary>
public enum CoolingState
{
    /// <summary>Not enough readings yet to say.</summary>
    Learning,

    /// <summary>Cooling, and the model can predict when it reaches a temperature.</summary>
    Cooling,

    /// <summary>Getting hotter: the oven is on, so it's in use (or hasn't been turned off).</summary>
    Heating,

    /// <summary>Dropping much faster than an oven cools with the door shut: the door is open.</summary>
    DoorOpen,
}

/// <summary>
/// Watches an oven cool and predicts when it reaches a temperature. Uses Newton's law of cooling,
/// T(t) = T_ambient + (T0 − T_ambient)·e^(−t/τ), fitted to the recent readings: ln(T − T_ambient) is a
/// straight line in t, with slope −1/τ. An oven isn't a single thermal mass (the air cools quickly
/// into the walls and racks, which then cool slowly), so τ grows as the oven cools; fitting only the
/// last stretch keeps the prediction close to the oven's current rate and refines it as it goes.
/// </summary>
public sealed class CoolingModel(double ambientC)
{
    /// <summary>Getting hotter by more than this (°C a minute) means the oven is on.</summary>
    public const double HeatingRate = 0.5;

    /// <summary>Dropping faster than this (°C a minute) means the door is open. A shut oven cools a few degrees a minute at most.</summary>
    public const double DoorRate = -6;

    /// <summary>How much cooling the fit looks at.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The least cooling the fit needs before it predicts anything.</summary>
    public static readonly TimeSpan MinimumFit = TimeSpan.FromMinutes(3);

    readonly List<(double T, double C)> samples = [];
    double segmentStart;   // where the current stretch of shut-door cooling began
    double? tau;           // seconds, from the latest good fit

    public double AmbientC => ambientC;
    public CoolingState State { get; private set; } = CoolingState.Learning;
    public double? TauSeconds => tau;
    public double Now => samples.Count == 0 ? 0 : samples[^1].T;
    public IReadOnlyList<(double T, double C)> Samples => samples;

    /// <summary>The current temperature, smoothed over the last half-minute.</summary>
    public double? CurrentC
    {
        get
        {
            if (samples.Count == 0) return null;
            var from = Now - 30;
            double sum = 0; var n = 0;
            for (var i = samples.Count - 1; i >= 0 && samples[i].T >= from; i--) { sum += samples[i].C; n++; }
            return sum / n;
        }
    }

    /// <param name="t">Seconds since watching began.</param>
    public void Add(double t, double c)
    {
        samples.Add((t, c));

        var shortRate = Rate(30);   // over the last half-minute: catches the door quickly
        var longRate = Rate(150);   // over the last 2½ minutes: steady enough to call heating
        if (shortRate is { } sr && sr < DoorRate)
        {
            State = CoolingState.DoorOpen;
            segmentStart = t; // what follows is a new stretch of cooling
            return;
        }
        if (longRate is { } lr && lr > HeatingRate)
        {
            State = CoolingState.Heating;
            segmentStart = t;
            tau = null; // a hotter, re-soaked oven cools differently: learn again
            return;
        }

        Fit();
        State = tau is null ? CoolingState.Learning : CoolingState.Cooling;
    }

    /// <summary>How long until the oven is at or below <paramref name="targetC"/>, or null if the model can't say (or it never will).</summary>
    public TimeSpan? TimeUntil(double targetC)
    {
        if (CurrentC is not { } now) return null;
        if (now <= targetC) return TimeSpan.Zero;
        if (tau is not { } t || State is CoolingState.Heating) return null;
        if (targetC - ambientC < 0.5) return null; // it only gets there in the limit
        var seconds = t * Math.Log((now - ambientC) / (targetC - ambientC));
        return TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    /// <summary>The predicted temperature <paramref name="ahead"/> from now, for drawing the curve.</summary>
    public double? Predict(TimeSpan ahead) =>
        CurrentC is { } now && tau is { } t ? ambientC + (now - ambientC) * Math.Exp(-ahead.TotalSeconds / t) : null;

    /// <summary>The rate of change over the last <paramref name="seconds"/>, in °C a minute.</summary>
    double? Rate(double seconds)
    {
        var from = Now - seconds;
        var i = samples.Count - 1;
        while (i > 0 && samples[i - 1].T >= from) i--;
        if (samples.Count - i < 5 || Now - samples[i].T < seconds * 0.6) return null;
        var fit = Stats.Fit(samples.Skip(i).Select(s => s.T).ToList(), samples.Skip(i).Select(s => s.C).ToList());
        return fit is null ? null : fit.Slope * 60;
    }

    void Fit()
    {
        var from = Math.Max(segmentStart, Now - Window.TotalSeconds);
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var (t, c) in samples)
        {
            if (t < from || c - ambientC < 2) continue;
            xs.Add(t);
            ys.Add(Math.Log(c - ambientC));
        }
        if (xs.Count < 10 || xs[^1] - xs[0] < MinimumFit.TotalSeconds) return; // keep the last good τ meanwhile
        var fit = Stats.Fit(xs, ys);
        if (fit is { Slope: < 0 }) tau = -1 / fit.Slope;
    }
}
