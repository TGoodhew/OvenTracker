using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;
using OvenTracker.Ui;
using Spectre.Console;

namespace OvenTracker.Tests;

public class StatsTests
{
    [Fact]
    public void StudentTCriticalValues()
    {
        Assert.Equal(12.706, Stats.TCrit95(1));
        Assert.Equal(2.776, Stats.TCrit95(4));
        Assert.Equal(2.042, Stats.TCrit95(30));
        Assert.InRange(Stats.TCrit95(60), 1.99, 2.01);
        Assert.InRange(Stats.TCrit95(1000), 1.96, 1.97);
    }

    [Fact]
    public void RequiredCountGrowsWithTheSpread()
    {
        Assert.Equal(4, Stats.RequiredCount(0.1, 1.0, 4, 99));
        // sd 1.0, target 1.0: t(n-1)/sqrt(n) <= 1 first at n = 7 (2.447/2.646).
        Assert.Equal(7, Stats.RequiredCount(1.0, 1.0, 4, 99));
        Assert.True(Stats.RequiredCount(2.0, 1.0, 4, 99) > 15);
    }

    [Fact]
    public void NoiseOfASteadyRampIsZero()
    {
        var ramp = Enumerable.Range(0, 100).Select(i => 20 + 0.25 * i).ToList();
        Assert.Equal(0, Stats.NoiseSigma(ramp), 9);
    }

    [Fact]
    public void TimeWeightedMeanOfARampIsItsMidpoint()
    {
        var s = new SampleSeries();
        for (var t = 0; t <= 100; t += 2) s.Add(new Sample(t, t * 0.5));
        Assert.Equal(25, s.Mean(10, 90), 9);
        Assert.Equal((5.0, 45.0), s.Range(10, 90));
        Assert.Equal(0.5, s.Slope(0, 100)!.Value, 9);
    }
}

public class TurningPointTests
{
    [Fact]
    public void FindsEveryCycleOfANoisyThermostatAndNothingElse()
    {
        var rng = new Random(5);
        var d = new TurningPointDetector();
        var tps = new List<TurningPoint>();
        // 10 cycles of a 5-minute, ±6 °C swing, with noise well under the hysteresis.
        for (var t = 0; t < 3000; t++)
        {
            var c = 180 + 6 * Math.Sin(2 * Math.PI * t / 300) + (rng.NextDouble() - 0.5) * 0.4;
            if (d.Add(t, c, 1.0) is { } tp) tps.Add(tp);
        }
        Assert.InRange(tps.Count(p => p.Kind == TurningKind.Peak), 9, 10);
        Assert.InRange(tps.Count(p => p.Kind == TurningKind.Trough), 9, 10);
        for (var i = 1; i < tps.Count; i++) Assert.NotEqual(tps[i - 1].Kind, tps[i].Kind);
        Assert.All(tps.Where(p => p.Kind == TurningKind.Peak), p => Assert.InRange(p.C, 185, 187));
    }
}

public class AnalyzerTests
{
    [Fact]
    public void AnOvenThatHoldsWithoutCyclingIsMeasuredInBlocks()
    {
        var rng = new Random(2);
        var a = new SetpointAnalyzer(200);
        double t = 0;
        // Ramp up, then hold at 203 with small noise (a proportional controller).
        for (; a.Phase != PointPhase.Complete && t < 4 * 3600; t++)
        {
            var c = t < 600 ? 25 + (203 - 25) * (1 - Math.Exp(-t / 120)) : 203 + (rng.NextDouble() - 0.5) * 0.6;
            a.Add(t, c);
        }
        var m = a.Metrics();
        Assert.Equal(CompletionReason.Converged, m.Reason);
        Assert.Equal(UnitKind.Block, m.Kind);
        Assert.True(m.Units >= 5);
        Assert.Equal(3, m.OffsetC!.Value, 0);
    }

    [Fact]
    public void AnOvenThatNeverSettlesStopsAtTheLimitAndSaysSo()
    {
        var rng = new Random(3);
        var a = new SetpointAnalyzer(180, new AnalysisOptions { MaxPointMinutes = 60 });
        for (var t = 0; a.Phase != PointPhase.Complete; t++)
        {
            // Cycles whose centre wanders by several degrees: the mean never pins down.
            var centre = 180 + 6 * Math.Sin(2 * Math.PI * t / 1500);
            a.Add(t, centre + 8 * Math.Sin(2 * Math.PI * t / 240) + rng.NextDouble() * 0.2);
        }
        Assert.Equal(CompletionReason.TimedOut, a.Reason);
        Assert.False(a.Metrics().MetPrecision);
    }

    [Fact]
    public void ShowsHowLongIsLeftOnceItKnowsTheCycle()
    {
        var oven = new SimulatedOven(11);
        oven.Set(OvenMode.Bake, 190);
        var a = new SetpointAnalyzer(190);
        Eta? whenMeasuring = null;
        for (var t = 0; a.Phase != PointPhase.Complete; t++)
        {
            oven.Step(1);
            a.Add(t, Math.Round(oven.ProbeC(), 1));
            if (whenMeasuring is null && a.Phase == PointPhase.Measuring && a.Units.Count == 1) whenMeasuring = a.EstimateRemaining();
        }
        Assert.NotNull(whenMeasuring);
        // After one cycle, it expects about three more cycles.
        var cycle = a.Metrics().CycleMinutes!.Value * 60;
        Assert.InRange(whenMeasuring!.Seconds, 2 * cycle, 4 * cycle);
    }
}

public class SessionTests
{
    [Fact]
    public void ThePlanAlternatesModesSoTheOvenOnlyHeatsUp()
    {
        var plan = RunCommand.BuildPlan([OvenMode.Bake, OvenMode.Convection], [175, 200], grouped: false);
        Assert.Equal([(OvenMode.Bake, 175.0), (OvenMode.Convection, 175.0), (OvenMode.Bake, 200.0), (OvenMode.Convection, 200.0)],
            plan.Select(p => (p.Mode, p.SetpointC)));
        Assert.Equal([1, 2, 3, 4], plan.Select(p => p.Index));

        var grouped = RunCommand.BuildPlan([OvenMode.Bake, OvenMode.Convection], [175, 200], grouped: true);
        Assert.Equal([OvenMode.Bake, OvenMode.Bake, OvenMode.Convection, OvenMode.Convection], grouped.Select(p => p.Mode));
    }

    [Theory]
    [InlineData("bake,convection", 2)]
    [InlineData("conv", 1)]
    [InlineData("both", 2)]
    [InlineData("grill", -1)]
    public void ParsesModes(string text, int count) => Assert.Equal(count, RunCommand.ParseModes(text)?.Count ?? -1);

    [Fact]
    public void ReplayingTheRecordingGivesTheSameResultsAsLive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OvenTrackerTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new AnalysisOptions();
            var plan = RunCommand.BuildPlan([OvenMode.Bake, OvenMode.Convection], [180], false);
            var info = new SessionInfo("test", DateTimeOffset.Now, "Simulated U1253B", "simulator", TempUnit.Fahrenheit, 8, options, plan);
            var oven = new SimulatedOven(21);
            var meter = new SimulatedMeter(oven, 1);
            var live = new List<PointResult>();
            using (var rec = new RunRecorder(dir, info))
            {
                for (var i = 0; i < 30; i++) rec.Reading(meter.Read(), null, null); // idle before the first point
                foreach (var point in plan)
                {
                    oven.Set(point.Mode, point.SetpointC);
                    var a = new SetpointAnalyzer(point.SetpointC, options);
                    DateTimeOffset? start = null;
                    MeterReading r = null!;
                    while (a.Phase != PointPhase.Complete)
                    {
                        r = meter.Read();
                        start ??= r.Time;
                        var el = (r.Time - start.Value).TotalSeconds;
                        if (el == 0) rec.Event(r.Time, point, 0, "start");
                        rec.Reading(r, point, el);
                        a.Add(el, r.Celsius!.Value);
                        if (el == 600) { a.MarkPreheatSignal(); rec.Event(r.Time, point, a.Now, "preheat-signal"); }
                    }
                    rec.Event(r.Time, point, a.Now, $"end:{a.Reason}");
                    live.Add(new PointResult(point, a.Metrics()));
                }
            }

            var replayed = Replay.Analyze(Path.Combine(dir, "readings.csv"), options);
            Assert.Equal(live.Count, replayed.Count);
            for (var i = 0; i < live.Count; i++)
            {
                Assert.Equal(live[i].Point, replayed[i].Point);
                var (l, p) = (live[i].Metrics, replayed[i].Metrics);
                Assert.Equal(l.Reason, p.Reason);
                Assert.Equal(l.Units, p.Units);
                Assert.Equal(l.MeanC!.Value, p.MeanC!.Value, 1); // the CSV keeps 0.01 °C
                Assert.Equal(l.PreheatSignalMinutes, p.PreheatSignalMinutes);
            }

            var md = new Report(info, replayed).Markdown();
            Assert.Contains("## Bake and convection", md);
            Assert.Contains("## Preheat signal", md);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheChartIsValidMarkupOfTheRequestedSize()
    {
        var oven = new SimulatedOven(1);
        oven.Set(OvenMode.Bake, 180);
        var samples = new List<Sample>();
        for (var t = 0; t < 3600; t++) { oven.Step(1); samples.Add(new Sample(t, oven.ProbeC())); }
        var text = BraillePlot.Markup(samples, 0, 3600, 180, 8, new UnitFormatter(TempUnit.Fahrenheit), 100, 12);
        _ = new Markup(text); // throws on bad markup
        Assert.Equal(12 + 2, text.Split('\n').Length);
    }
}
