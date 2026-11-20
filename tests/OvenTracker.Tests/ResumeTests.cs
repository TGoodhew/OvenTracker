using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;
using Xunit.Abstractions;

namespace OvenTracker.Tests;

public class ResumeTests(ITestOutputHelper output)
{
    static readonly AnalysisOptions Options = new();

    /// <summary>Measures one setpoint on the simulated oven, recording it as the app does.</summary>
    static PointResult Measure(RunRecorder rec, SimulatedMeter meter, TestPoint point, bool afterReset, int? abortAfterSeconds = null)
    {
        meter.Oven.Set(point.Mode, point.SetpointC);
        if (afterReset) rec.Note(DateTimeOffset.UtcNow, "reset");
        var a = new SetpointAnalyzer(point.SetpointC, Options);
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
            if (abortAfterSeconds is { } stop && el >= stop) a.Finish(CompletionReason.Aborted);
        }
        rec.Event(r.Time, point, a.Now, $"end:{a.Reason}");
        return new PointResult(point, a.Metrics(), afterReset);
    }

    /// <summary>Lets the simulated oven cool (off, door shut) until it's ready for <paramref name="targetC"/>, recording it as the reset wait does.</summary>
    static double Cool(RunRecorder rec, SimulatedMeter meter, double targetC)
    {
        meter.Oven.Set(OvenMode.Bake, null);
        rec.Note(DateTimeOffset.UtcNow, "reset-start");
        var seconds = 0;
        while (meter.Oven.AirC > targetC) { rec.Reading(meter.Read(), null, null); seconds++; }
        rec.Note(DateTimeOffset.UtcNow, "reset-end:Ready");
        return seconds;
    }

    static string TempDir() => Path.Combine(Path.GetTempPath(), "OvenTrackerResume-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ARunResumedAfterUsingTheOvenReplaysAsOneRun()
    {
        var dir = TempDir();
        try
        {
            var plan = RunCommand.BuildPlan([OvenMode.Bake, OvenMode.Convection], [176.67, 204.44], false);
            var info = new SessionInfo("test", DateTimeOffset.Now, "sim", "simulator", TempUnit.Fahrenheit, 15 / 1.8, Options, plan, AmbientC: 22);
            var meter = new SimulatedMeter(new SimulatedOven(31), 1);

            // Sitting 1: points 1 and 2, then point 3 is interrupted.
            using (var rec = new RunRecorder(dir, info))
            {
                Measure(rec, meter, plan[0], false);
                Measure(rec, meter, plan[1], false);
                Measure(rec, meter, plan[2], false, abortAfterSeconds: 600);
            }
            Assert.Equal([CompletionReason.Converged, CompletionReason.Converged, CompletionReason.Aborted],
                Replay.Analyze(Path.Combine(dir, "readings.csv"), Options).Select(r => r.Metrics.Reason));

            // Someone uses the oven at 450 °F, then the run is resumed: wait to cool, then points 3 and 4.
            meter.PretendUsed(232.2);
            using (var rec = new RunRecorder(dir, info with { Sittings = [info.Started, DateTimeOffset.Now] }, append: true))
            {
                rec.Note(DateTimeOffset.UtcNow, "resume");
                Cool(rec, meter, ResetWaiter.TargetFor(plan, plan[2], 22));
                Measure(rec, meter, plan[2], true);
                Measure(rec, meter, plan[3], false);
            }

            var replayed = Replay.Analyze(Path.Combine(dir, "readings.csv"), Options);
            Assert.Equal([1, 2, 3, 4], replayed.Select(r => r.Point.Index));
            Assert.All(replayed, r => Assert.Equal(CompletionReason.Converged, r.Metrics.Reason));
            Assert.Equal([false, false, true, false], replayed.Select(r => r.AfterReset));
            Assert.Single(File.ReadLines(Path.Combine(dir, "readings.csv")), l => l.StartsWith("time_utc", StringComparison.Ordinal)); // one header

            var md = new Report(info with { Sittings = [info.Started, info.Started.AddDays(1)] }, replayed).Markdown();
            Assert.Contains("(after a reset)", md);
            Assert.Contains("in 2 sittings", md);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ASessionSplitByAResetMeasuresTheSameAsOneStraightThrough()
    {
        var plan = RunCommand.BuildPlan([OvenMode.Bake], [176.67, 204.44, 232.22], false);
        var straightDir = TempDir();
        var splitDir = TempDir();
        try
        {
            var info = new SessionInfo("test", DateTimeOffset.Now, "sim", "simulator", TempUnit.Fahrenheit, 15 / 1.8, Options, plan, AmbientC: 22);

            List<PointResult> straight;
            using (var rec = new RunRecorder(straightDir, info))
            {
                var meter = new SimulatedMeter(new SimulatedOven(41), 1);
                straight = plan.Select(p => Measure(rec, meter, p, false)).ToList();
            }

            List<PointResult> split;
            using (var rec = new RunRecorder(splitDir, info))
            {
                var meter = new SimulatedMeter(new SimulatedOven(42), 1);
                split = [Measure(rec, meter, plan[0], false)];
                meter.PretendUsed(246); // a roast at 475 °F in between
                var waited = Cool(rec, meter, ResetWaiter.TargetFor(plan, plan[1], 22));
                output.WriteLine($"waited {waited / 60.0:F0} min to cool below {ResetWaiter.TargetFor(plan, plan[1], 22):F1} °C");
                split.Add(Measure(rec, meter, plan[1], true));
                split.Add(Measure(rec, meter, plan[2], false));
            }

            for (var i = 0; i < plan.Count; i++)
            {
                var (a, b) = (straight[i].Metrics, split[i].Metrics);
                var allowed = Math.Sqrt(Math.Pow(a.HalfWidthC ?? 1, 2) + Math.Pow(b.HalfWidthC ?? 1, 2)) + 0.75;
                output.WriteLine($"{plan[i].SetpointC:F1} °C: straight {a.MeanC:F2}, split {b.MeanC:F2} (allowed ±{allowed:F2})");
                Assert.InRange(b.MeanC!.Value, a.MeanC!.Value - allowed, a.MeanC.Value + allowed);
            }
        }
        finally
        {
            foreach (var d in new[] { straightDir, splitDir }) if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    [Fact]
    public void AFirstSetpointStartedWarmIsMarked()
    {
        var dir = TempDir();
        try
        {
            var plan = RunCommand.BuildPlan([OvenMode.Bake], [176.67], false);
            var info = new SessionInfo("test", DateTimeOffset.Now, "sim", "simulator", TempUnit.Fahrenheit, 15 / 1.8, Options, plan);
            var meter = new SimulatedMeter(new SimulatedOven(51), 1);
            meter.PretendUsed(200); // still warm from cooking
            using (var rec = new RunRecorder(dir, info))
            {
                rec.Note(DateTimeOffset.UtcNow, "started-warm");
                Measure(rec, meter, plan[0], false);
            }
            var r = Assert.Single(Replay.Analyze(Path.Combine(dir, "readings.csv"), Options));
            Assert.True(r.StartedWarm);
            Assert.False(r.AfterReset);
            var md = new Report(info, [r]).Markdown();
            Assert.Contains("350 °F (started warm)", md);
            Assert.Contains("aren't from cold", md);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheNextSetpointStartsFromJustAboveTheOneBeforeItOrFromCold()
    {
        var plan = RunCommand.BuildPlan([OvenMode.Bake, OvenMode.Convection], [176.67, 204.44], false);
        Assert.Equal(22 + ResetWaiter.Margin, ResetWaiter.TargetFor(plan, plan[0], 22), 6);          // the first: from cold
        Assert.Equal(176.67 + ResetWaiter.Margin, ResetWaiter.TargetFor(plan, plan[1], 22), 6);      // convection 350 after bake 350
        Assert.Equal(176.67 + ResetWaiter.Margin, ResetWaiter.TargetFor(plan, plan[2], 22), 6);      // bake 400 after convection 350
        Assert.Equal(10, ResetWaiter.Margin * 1.8, 6);
    }
}
