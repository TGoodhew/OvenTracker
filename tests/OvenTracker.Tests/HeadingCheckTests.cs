using OvenTracker.Analysis;
using OvenTracker.Meter;
using Xunit.Abstractions;

namespace OvenTracker.Tests;

public class HeadingCheckTests(ITestOutputHelper output)
{
    /// <summary>The simulated oven settled at one setting, and the mean of its last two minutes (where the app starts from).</summary>
    static (SimulatedOven Oven, double FromC) Settled(OvenMode mode, double setpointC, int seed)
    {
        var oven = new SimulatedOven(seed);
        oven.Set(mode, setpointC);
        oven.Step(90 * 60);
        double sum = 0;
        for (var i = 0; i < 120; i++) { oven.Step(1); sum += oven.ProbeC(); }
        return (oven, sum / 120);
    }

    static (HeadingState State, double Seconds) Watch(SimulatedOven oven, HeadingCheck check, int seconds = 10 * 60)
    {
        for (var t = 0; t < seconds; t++)
        {
            oven.Step(1);
            if (check.Update(t, oven.ProbeC()) != HeadingState.Unknown) return (check.State, t);
        }
        return (check.State, seconds);
    }

    [Theory]
    [InlineData(OvenMode.Bake, 176.67, 204.44, 1)]       // 350 → 400 °F
    [InlineData(OvenMode.Convection, 176.67, 204.44, 2)]
    [InlineData(OvenMode.Bake, 204.44, 232.22, 3)]       // 400 → 450 °F
    [InlineData(OvenMode.Convection, 162.78, 182.22, 4)] // 325 → 360 °F
    public void ForgettingToRaiseTheSettingIsNoticed(OvenMode mode, double fromSet, double toSet, int seed)
    {
        var (oven, from) = Settled(mode, fromSet, seed);
        var check = new HeadingCheck(from, toSet);
        var (state, at) = Watch(oven, check); // the oven is left at its old setting
        output.WriteLine($"{mode} {fromSet:F0}→{toSet:F0} °C forgotten: {state} after {at / 60:F1} min");
        Assert.Equal(HeadingState.NotHeading, state);
    }

    [Theory]
    [InlineData(OvenMode.Bake, 176.67, 204.44, 5)]
    [InlineData(OvenMode.Convection, 176.67, 204.44, 6)]
    [InlineData(OvenMode.Bake, 204.44, 232.22, 7)]
    [InlineData(OvenMode.Bake, 176.67, 193.33, 8)]       // 350 → 380 °F: small steps never cry wolf
    [InlineData(OvenMode.Bake, 176.67, 187.78, 13)]      // 350 → 370 °F
    public void RaisingTheSettingIsOnItsWay(OvenMode mode, double fromSet, double toSet, int seed)
    {
        var (oven, from) = Settled(mode, fromSet, seed);
        oven.Set(mode, toSet);
        var check = new HeadingCheck(from, toSet);
        var (state, at) = Watch(oven, check);
        output.WriteLine($"{mode} {fromSet:F0}→{toSet:F0} °C changed: {state} after {at / 60:F1} min");
        // A changed setting is never called "not heading". A small step may not be checked at all (Unknown).
        Assert.NotEqual(HeadingState.NotHeading, state);
        if (check.Applies && Math.Abs(toSet - fromSet) >= 25) Assert.Equal(HeadingState.OnItsWay, state);
    }

    [Fact]
    public void LoweringTheSettingIsCheckedToo()
    {
        var (forgot, from) = Settled(OvenMode.Bake, 232.22, 9);
        Assert.Equal(HeadingState.NotHeading, Watch(forgot, new HeadingCheck(from, 204.44)).State);

        var (changed, from2) = Settled(OvenMode.Bake, 232.22, 10);
        changed.Set(OvenMode.Bake, 204.44);
        var (state, at) = Watch(changed, new HeadingCheck(from2, 204.44));
        output.WriteLine($"450→400 °F changed: {state} after {at / 60:F1} min");
        Assert.Equal(HeadingState.OnItsWay, state);
    }

    [Theory]
    [InlineData(OvenMode.Bake, 14)]
    [InlineData(OvenMode.Bake, 15)]
    [InlineData(OvenMode.Convection, 16)]
    public void AForgottenChangeStaysNoticedThroughTheThermostatsPeaks(OvenMode mode, int seed)
    {
        // As the app does it: from the previous setpoint's measured mean, beyond half its swing.
        var oven = new SimulatedOven(seed);
        oven.Set(mode, 176.67);
        var a = new SetpointAnalyzer(176.67);
        for (var t = 0; a.Phase != PointPhase.Complete; t++) { oven.Step(1); a.Add(t, oven.ProbeC()); }
        var m = a.Metrics();
        var check = new HeadingCheck(m.MeanC!.Value, 204.44, m.SwingC);

        var everOnItsWay = false;
        for (var t = 0; t < 20 * 60; t++)
        {
            oven.Step(1); // still at 350 °F
            everOnItsWay |= check.Update(t, oven.ProbeC()) == HeadingState.OnItsWay;
        }
        output.WriteLine($"{mode}: from {m.MeanC:F1}, swing {m.SwingC:F1}, needs {check.TargetC:F1} °C; ended {check.State}");
        Assert.False(everOnItsWay);
        Assert.Equal(HeadingState.NotHeading, check.State);
    }

    [Fact]
    public void FromColdTheFirstSetpointIsOnItsWay()
    {
        var oven = new SimulatedOven(11);
        oven.Set(OvenMode.Bake, 176.67);
        var check = new HeadingCheck(oven.AmbientC, 176.67);
        Assert.Equal(HeadingState.OnItsWay, Watch(oven, check).State);
    }

    [Fact]
    public void ASmallStepOrAModeChangeIsntChecked()
    {
        // Bake 350 → convection 350: the same temperature, so nothing to see.
        var (oven, from) = Settled(OvenMode.Bake, 176.67, 12);
        var check = new HeadingCheck(from, 176.67);
        Assert.False(check.Applies);
        Assert.Equal(HeadingState.Unknown, Watch(oven, check).State);
        Assert.False(new HeadingCheck(176.67, 176.67 + 15 / 1.8).Applies); // 15 °F
    }
}
