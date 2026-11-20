using OvenTracker.Analysis;
using OvenTracker.Meter;
using Xunit.Abstractions;

namespace OvenTracker.Tests;

public class CoolingModelTests(ITestOutputHelper output)
{
    /// <summary>The simulated oven, heated at bake 400 °F for an hour, then turned off.</summary>
    static SimulatedOven HotOven(int seed = 4)
    {
        var oven = new SimulatedOven(seed);
        oven.Set(OvenMode.Bake, 204.44);
        oven.Step(3600);
        oven.Set(OvenMode.Bake, null);
        return oven;
    }

    [Fact]
    public void APureExponentialIsPredictedExactly()
    {
        var m = new CoolingModel(22);
        const double tau = 1800;
        for (var t = 0; t <= 600; t += 1) m.Add(t, 22 + 180 * Math.Exp(-t / tau));
        Assert.Equal(CoolingState.Cooling, m.State);
        Assert.Equal(tau, m.TauSeconds!.Value, 1);
        // From 600 s, the time to 100 °C: τ·ln((T − Ta)/(100 − Ta)).
        var now = 22 + 180 * Math.Exp(-600 / tau);
        var expected = tau * Math.Log((now - 22) / 78);
        Assert.InRange(m.TimeUntil(100)!.Value.TotalSeconds, expected - 20, expected + 20); // CurrentC is a 30 s average
        Assert.Null(m.TimeUntil(22.2)); // ambient is only reached in the limit
        Assert.Equal(TimeSpan.Zero, m.TimeUntil(250));
    }

    [Fact]
    public void ItLearnsBeforePredicting()
    {
        var m = new CoolingModel(22);
        for (var t = 0; t < 60; t++) m.Add(t, 200 - t * 0.05);
        Assert.Equal(CoolingState.Learning, m.State);
        Assert.Null(m.TimeUntil(150));
    }

    [Theory]
    [InlineData(150, 8)]     // 302 °F, eight minutes after turning off (a few minutes of cooling)
    [InlineData(121.1, 10)]  // 250 °F
    [InlineData(65.6, 20)]   // 150 °F: most of the way to cold
    public void TheSimulatedOvensCoolDownIsPredictedWithinTenPercent(double targetC, int afterMinutes)
    {
        var oven = HotOven();
        var m = new CoolingModel(oven.AmbientC);
        double t = 0;
        for (; t < afterMinutes * 60; t++) { oven.Step(1); m.Add(t, oven.ProbeC()); }
        var predicted = m.TimeUntil(targetC)!.Value.TotalSeconds;

        var actual = 0.0;
        while (oven.AirC > targetC) { oven.Step(1); actual++; }
        output.WriteLine($"to {targetC:F1} °C after {afterMinutes} min: predicted {predicted / 60:F1} min, actual {actual / 60:F1} min, τ {m.TauSeconds / 60:F1} min");
        Assert.InRange(predicted, actual * 0.9, actual * 1.1);
    }

    [Fact]
    public void OpeningTheDoorIsNoticedAndThePredictionCarriesOn()
    {
        var oven = HotOven(5);
        var m = new CoolingModel(oven.AmbientC);
        double t = 0;
        void Run(double seconds) { for (var end = t + seconds; t < end; t++) { oven.Step(1); m.Add(t, oven.ProbeC()); } }

        Run(8 * 60);
        Assert.Equal(CoolingState.Cooling, m.State);
        var before = m.TauSeconds!.Value;

        oven.DoorOpen = true;
        Run(40);
        Assert.Equal(CoolingState.DoorOpen, m.State);
        oven.DoorOpen = false;

        // Straight after, the last good τ carries on (the opening's first seconds nudge it a little), so
        // there's still a prediction.
        Run(30);
        Assert.InRange(m.TauSeconds!.Value, before * 0.9, before * 1.1);
        Assert.NotNull(m.TimeUntil(100));

        // Then it learns the oven's rate again, and predicts from that.
        Run(10 * 60);
        Assert.Equal(CoolingState.Cooling, m.State);
        var predicted = m.TimeUntil(100)!.Value.TotalSeconds;
        var actual = 0.0;
        while (oven.AirC > 100) { oven.Step(1); actual++; }
        output.WriteLine($"after the door: predicted {predicted / 60:F1} min, actual {actual / 60:F1} min");
        Assert.InRange(predicted, actual * 0.85, actual * 1.15);
    }

    [Fact]
    public void AnOvenThatIsOnIsInUse()
    {
        var oven = HotOven(6);
        var m = new CoolingModel(oven.AmbientC);
        double t = 0;
        // Just after it's turned off, the element's last heat keeps it warming for a minute or two.
        for (; t < 9 * 60; t++) { oven.Step(1); m.Add(t, oven.ProbeC()); }
        Assert.Equal(CoolingState.Cooling, m.State);

        oven.Set(OvenMode.Bake, 218); // someone turns it on to cook
        for (var end = t + 4 * 60; t < end; t++) { oven.Step(1); m.Add(t, oven.ProbeC()); }
        Assert.Equal(CoolingState.Heating, m.State);
        Assert.Null(m.TimeUntil(150));
    }
}
