using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

public class CombinedRunsTests
{
    /// <summary>Records a simulated run, as `run --simulate` would, with the oven's thermostat adjustment set.</summary>
    static void Record(string dir, int seed, double adjustmentC, DateTimeOffset started, params double[] setpointsC)
    {
        var options = new AnalysisOptions();
        var plan = RunCommand.BuildPlan([OvenMode.Bake], setpointsC, false);
        var info = new SessionInfo("test", started, "OvenTracker simulated meter", "simulator", TempUnit.Fahrenheit, 15 / 1.8, options, plan,
            SimulatedDriver.DriverName, null, Sensor.Thermocouple("K"), adjustmentC, AdjustmentOptions.For(TempUnit.Fahrenheit));
        var oven = new SimulatedOven(seed) { AdjustmentC = adjustmentC };
        var meter = new SimulatedMeter(oven, 1);
        using var rec = new RunRecorder(dir, info);
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
            }
            rec.Event(r.Time, point, a.Now, $"end:{a.Reason}");
            rec.WriteResults(info, [], "");
        }
    }

    /// <summary>Records a run whose points have adjustments of their own, as characterise's do.</summary>
    static void RecordPlanned(string dir, int seed, DateTimeOffset started, IReadOnlyList<TestPoint> plan)
    {
        var options = new AnalysisOptions();
        var info = new SessionInfo("test", started, "sim", "simulator", TempUnit.Fahrenheit, 15 / 1.8, options, plan, AdjustmentC: plan[^1].AdjustmentC);
        var oven = new SimulatedOven(seed);
        var meter = new SimulatedMeter(oven, 1);
        using var rec = new RunRecorder(dir, info);
        foreach (var point in plan)
        {
            oven.AdjustmentC = point.AdjustmentC ?? 0;
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
            }
            rec.Event(r.Time, point, a.Now, $"end:{a.Reason}");
        }
    }

    [Fact]
    public void ACharacterisationAnalysedWithALaterRunKeepsEachPointsAdjustment()
    {
        var root = Path.Combine(Path.GetTempPath(), "OvenTrackerCharCombined-" + Guid.NewGuid().ToString("N"));
        try
        {
            const double minus10F = -10 / 1.8;
            // A characterisation: 350 °F at 0, then 350 °F again at −10 °F.
            RecordPlanned(Path.Combine(root, "char"), 21, DateTimeOffset.Parse("2026-11-20T14:00:00Z"),
                [new TestPoint(1, OvenMode.Bake, 176.67, 0), new TestPoint(2, OvenMode.Bake, 176.67, minus10F)]);
            // A check run afterwards, at −10 °F (a plain run: its points carry no adjustment of their own).
            Record(Path.Combine(root, "check"), 22, minus10F, DateTimeOffset.Parse("2026-11-22T09:00:00Z"), 176.67);

            var runs = new[] { "char", "check" }.Select(n => AnalyzeCommand.Load(Path.Combine(root, n))!).ToList();
            var (info, results) = AnalyzeCommand.Combine(runs, i => i.Options, null);

            Assert.Equal([0, minus10F, minus10F], results.Select(r => r.Point.AdjustmentC!.Value));
            var advice = Assert.Single(new Report(info, results).Advice());
            Assert.True(advice.GainMeasured); // 0 against −10 at the same setpoint
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TwoRunsAtDifferentAdjustmentsMeasureWhatTheAdjustmentDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "OvenTrackerCombined-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The simulated bake runs about 8 °F hot. First at adjustment 0, then two days later at −5 °F.
            double[] setpoints = [176.67, 204.44];
            const double minus5F = -5 / 1.8;
            Record(Path.Combine(root, "before"), 11, 0, DateTimeOffset.Parse("2026-11-20T14:00:00Z"), setpoints);
            Record(Path.Combine(root, "after"), 12, minus5F, DateTimeOffset.Parse("2026-11-22T09:00:00Z"), setpoints);

            var runs = new[] { "after", "before" }.Select(n => AnalyzeCommand.Load(Path.Combine(root, n))!).ToList();
            var (info, results) = AnalyzeCommand.Combine(runs, i => i.Options, null);

            Assert.Equal(4, results.Count);
            Assert.Equal(minus5F, info.AdjustmentC!.Value, 6); // the latest run's adjustment is "now"
            Assert.Equal([0, 0, minus5F, minus5F], results.Select(r => r.Point.AdjustmentC!.Value)); // in time order

            var advice = Assert.Single(new Report(info, results).Advice());
            Assert.True(advice.GainMeasured);
            Assert.InRange(advice.Gain, 0.7, 1.3); // the simulator's adjustment moves it 1:1
            // The whole correction is about the error measured at 0, on the oven's 1 °F steps.
            var newF = advice.NewSettingC!.Value * 1.8;
            Assert.Equal(Math.Round(newF), newF, 6);
            var errorAtZeroF = results.Where(r => r.Point.AdjustmentC == 0).Average(r => r.Metrics.OffsetC!.Value) * 1.8;
            Assert.InRange(newF, -errorAtZeroF - 2, -errorAtZeroF + 2);
            Assert.All(advice.Residuals, r => Assert.InRange(r.AfterC, -1.5, 1.5));

            var md = new Report(info, results).Markdown();
            Assert.Contains("## Best adjustment", md);
            Assert.Contains("Measured from runs at different adjustments", md);
            Assert.Contains("(adj 0 °F)", md); // the earlier run's rows say which adjustment they had
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ASingleRunsAdjustmentCanBeGivenAfterwards()
    {
        var root = Path.Combine(Path.GetTempPath(), "OvenTrackerSingle-" + Guid.NewGuid().ToString("N"));
        try
        {
            Record(root, 13, 0, DateTimeOffset.Parse("2026-11-20T14:00:00Z"), 176.67, 232.22);
            var run = AnalyzeCommand.Load(root)!;
            var (info, results) = AnalyzeCommand.Combine([run], i => i.Options, -5 / 1.8);
            Assert.Equal(-5, info.AdjustmentC!.Value * 1.8, 6);
            var advice = Assert.Single(new Report(info, results).Advice());
            Assert.NotNull(advice.NewSettingC);
            Assert.False(advice.GainMeasured);
            Assert.Contains("hasn't been measured", string.Join(" ", new Report(info, results).AdviceDetails(advice)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
