using OvenTracker.Analysis;
using OvenTracker.Meter;
using Xunit.Abstractions;

namespace OvenTracker.Tests;

public class SimulationTests(ITestOutputHelper output)
{
    static SetpointAnalyzer Run(SimulatedOven oven, OvenMode mode, double setpointC, AnalysisOptions? options = null)
    {
        oven.Set(mode, setpointC);
        var a = new SetpointAnalyzer(setpointC, options);
        for (var t = 0.0; a.Phase != PointPhase.Complete; t += 1)
        {
            oven.Step(1);
            a.Add(t, Math.Round(oven.ProbeC(), 1));
        }
        return a;
    }

    /// <summary>The truth to compare with: what the probe averages over three more hours at the same setting.</summary>
    static double LongRunMean(SimulatedOven oven)
    {
        double sum = 0;
        const int seconds = 3 * 3600;
        for (var i = 0; i < seconds; i++)
        {
            oven.Step(1);
            sum += oven.AirC;
        }
        return sum / seconds;
    }

    void Describe(SetpointAnalyzer a, OvenMode mode, double? truth = null)
    {
        var m = a.Metrics();
        output.WriteLine($"{mode} {a.SetpointC:F1} °C: {m.Reason}, {m.Units} {m.Kind} (dropped {m.DiscardedUnits}), " +
                         $"mean {m.MeanC:F2} ±{m.HalfWidthC:F2} (truth {truth:F2}), offset {m.OffsetC:F2}, swing {m.SwingC:F1}, cycle {m.CycleMinutes:F1} min, " +
                         $"reach {m.TimeToSetpointMinutes:F1} min, overshoot {m.OvershootC:F1}, settle {m.SettleMinutes:F1} min, total {m.TotalMinutes:F1} min");
    }

    [Theory]
    [InlineData(OvenMode.Bake, 176.67, 7)]
    [InlineData(OvenMode.Bake, 232.22, 8)]
    [InlineData(OvenMode.Convection, 176.67, 9)]
    [InlineData(OvenMode.Convection, 232.22, 10)]
    public void FromColdTheAnalyzerFindsTheOvensRealMean(OvenMode mode, double sp, int seed)
    {
        var oven = new SimulatedOven(seed);
        var a = Run(oven, mode, sp);
        var truth = LongRunMean(oven);
        Describe(a, mode, truth);
        var m = a.Metrics();

        Assert.Equal(CompletionReason.Converged, m.Reason);
        Assert.Equal(UnitKind.Cycle, m.Kind);
        Assert.True(m.Units >= 3);
        Assert.True(m.HalfWidthC <= 1.0);
        Assert.InRange(m.MeanC!.Value, truth - 1.0, truth + 1.0);
        Assert.InRange(m.CycleMinutes!.Value, 2, 15);
        Assert.InRange(m.TotalMinutes, 15, 120);
    }

    [Fact]
    public void ASessionMovesBetweenSetpointsAndModesWithoutRestartingTheOven()
    {
        var oven = new SimulatedOven(3);
        foreach (var (mode, sp) in new[] { (OvenMode.Bake, 176.67), (OvenMode.Convection, 176.67), (OvenMode.Bake, 204.44), (OvenMode.Convection, 204.44) })
        {
            var a = Run(oven, mode, sp);
            Describe(a, mode);
            Assert.Equal(CompletionReason.Converged, a.Reason);
            var bias = SimulatedOven.BiasC(mode, sp);
            Assert.InRange(a.Metrics().OffsetC!.Value, bias - 3, bias + 3);
        }
    }

    [Fact]
    public void CoolingToALowerSetpointIsMeasuredToo()
    {
        var oven = new SimulatedOven(4);
        Run(oven, OvenMode.Bake, 232.22);
        var a = Run(oven, OvenMode.Bake, 176.67);
        Describe(a, OvenMode.Bake);
        Assert.True(a.Cooling);
        Assert.Equal(CompletionReason.Converged, a.Reason);
        Assert.Null(a.Metrics().OvershootC);
    }
}
