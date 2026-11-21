using OvenTracker.Analysis;

namespace OvenTracker.Tests;

/// <summary>An oven that heats in steps, and one that really runs cold (#49).</summary>
public class PreheatTests
{
    const double Setpoint = 176.67; // 350 °F

    /// <summary>Feeds a temperature function to an analyzer, one reading a second.</summary>
    static SetpointAnalyzer Run(Func<double, double> c, double seconds)
    {
        var a = new SetpointAnalyzer(Setpoint, new AnalysisOptions());
        for (var t = 0.0; t <= seconds && a.Phase != PointPhase.Complete; t += 1) a.Add(t, c(t));
        return a;
    }

    /// <summary>A thermostat cycle: a triangle between lo and hi, period in seconds.</summary>
    static double Cycle(double t, double lo, double hi, double period)
    {
        var x = t % period / period;
        return x < 0.5 ? lo + (hi - lo) * 2 * x : hi - (hi - lo) * 2 * (x - 0.5);
    }

    /// <summary>
    /// Heats from 25 °C in steps, each a 2-minute climb of 25 °C and then a 1-minute dip of 8 °C (as a GE PHS925's
    /// elements take turns), until it reaches the setting at about 21 minutes; then cycles 172–185 °C every 12 minutes.
    /// </summary>
    static double SteppedPreheat(double t)
    {
        var stepped = 25.0;
        var tt = t;
        while (tt > 0 && stepped < 172)
        {
            var climb = Math.Min(tt, 120);
            stepped += 25 * climb / 120;
            tt -= climb;
            if (tt <= 0 || stepped >= 172) break;
            var dip = Math.Min(tt, 60);
            stepped -= 8 * dip / 60;
            tt -= dip;
        }
        return stepped < 172 ? stepped : Cycle(tt, 172, 185, 720);
    }

    [Fact]
    public void AnOvenHeatingInStepsIsntTakenForCycling()
    {
        var a = Run(SteppedPreheat, 4 * 3600);
        var m = a.Metrics();
        // Not "reached" at the first step (about 2 minutes in, at 50 °C), but when it really got there.
        Assert.True(m.TimeToSetpointMinutes > 15, $"reached after {m.TimeToSetpointMinutes:F1} min");
        // And the cycles are the thermostat's (12 minutes), not the steps' (3 minutes).
        Assert.InRange(m.CycleMinutes!.Value, 11, 13);
        Assert.InRange(m.MeanC!.Value, 176, 180);
    }

    [Fact]
    public void AnOvenThatRunsColdIsStillMeasuredFromItsFirstPeak()
    {
        // Heats to 170 °C in 15 minutes, then cycles 160–170 °C every 12 minutes from that first peak: 10 °C cold on
        // average, never reaching the setting.
        static double Cold(double t) => t < 900 ? 25 + 145 * t / 900 : Cycle(t - 900 + 360, 160, 170, 720);
        var a = Run(Cold, 4 * 3600);
        var m = a.Metrics();
        Assert.Equal(CompletionReason.Converged, m.Reason);
        Assert.Equal(UnitKind.Cycle, m.Kind);
        Assert.InRange(m.TimeToSetpointMinutes!.Value, 15, 17); // its first peak, not withdrawn
        Assert.InRange(m.MeanC!.Value, 164, 166);
        Assert.Equal(0, m.DiscardedUnits);
    }

    [Fact]
    public void TheMarginIsWiderThanCycleToCycleWanderButNarrowerThanAStep()
    {
        var a = new SetpointAnalyzer(Setpoint, new AnalysisOptions());
        Assert.Equal(SetpointAnalyzer.MinArrivalMarginC, a.ArrivalMarginC); // a quiet signal: the floor
        Assert.Equal(5, SetpointAnalyzer.MinArrivalMarginC);
    }
}
