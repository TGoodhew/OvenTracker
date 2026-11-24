using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Session;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace OvenTracker.Tests;

/// <summary><c>run --resume &lt;folder&gt; --redo &lt;list&gt;</c>: measuring finished setpoints of a saved run again.</summary>
[Collection(nameof(ConsoleCollection))]
public class RedoTests
{
    [Theory]
    [InlineData("5-8", new[] { 5, 6, 7, 8 })]
    [InlineData("5,7", new[] { 5, 7 })]
    [InlineData("8, 2,5-6", new[] { 2, 5, 6, 8 })]
    [InlineData("3,3-4", new[] { 3, 4 })]
    [InlineData("4-4", new[] { 4 })]
    public void SetpointNumbersAndRangesAreRead(string text, int[] expected) =>
        Assert.Equal(expected, RunCommand.ParseIndexes(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("8-5")]
    [InlineData("5-")]
    [InlineData("-5")]
    [InlineData("1-2-3")]
    [InlineData("five")]
    [InlineData("2.5")]
    public void AnythingElseIsRefused(string text) => Assert.Null(RunCommand.ParseIndexes(text));

    static readonly double Plus29 = 29 / 1.8;

    /// <summary>The plan of the characterise run this was made for: 350, 400, 450 in both modes at 0, then 450 and 350 at +29 °F.</summary>
    static List<TestPoint> CharacterisePlan()
    {
        var plan = RunCommand.BuildPlan([OvenMode.Bake, OvenMode.Convection], [176.67, 204.44, 232.22], false)
            .Select(p => p with { AdjustmentC = 0 }).ToList();
        foreach (var sp in new[] { 232.22, 176.67 })
            foreach (var mode in new[] { OvenMode.Bake, OvenMode.Convection })
                plan.Add(new TestPoint(plan.Count + 1, mode, sp, Plus29));
        return plan;
    }

    [Fact]
    public void ARedoAtAnotherAdjustmentGoesFirstSoTheOvenEndsAtTheRunsOwn()
    {
        var plan = CharacterisePlan();
        // 1–9 done, 10 still to do; 5 and 6 (at 0) and 7 and 8 (at +29) to measure again.
        var todo = RunCommand.ResumeOrder(plan, Enumerable.Range(1, 9), [5, 6, 7, 8], Plus29);
        Assert.Equal([5, 6, 7, 8, 10], todo.Select(p => p.Index));
    }

    [Fact]
    public void ARedoAtTheRunsAdjustmentAloneNeedsNoChange()
    {
        var plan = CharacterisePlan();
        Assert.Equal([7, 8], RunCommand.ResumeOrder(plan, Enumerable.Range(1, 10), [7, 8], Plus29).Select(p => p.Index));
        // One at each: the other adjustment first.
        Assert.Equal([2, 9], RunCommand.ResumeOrder(plan, Enumerable.Range(1, 10), [9, 2], Plus29).Select(p => p.Index));
    }

    [Fact]
    public void WithoutItsOwnAdjustmentsAResumeKeepsThePlansOrder()
    {
        var plan = RunCommand.BuildPlan([OvenMode.Bake, OvenMode.Convection], [176.67, 204.44], false);
        Assert.Equal([2, 3, 4], RunCommand.ResumeOrder(plan, [1, 2], [2], -5).Select(p => p.Index));
        Assert.Equal([3, 4], RunCommand.ResumeOrder(plan, [1, 2], [], null).Select(p => p.Index));
    }

    static async Task<(int Code, string Output)> Run<TCommand>(params string[] args) where TCommand : class, ICommand
    {
        var console = new TestConsole();
        console.Profile.Width = 160;
        var before = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try { return (await new CommandApp<TCommand>().RunAsync(args), console.Output); }
        finally { AnsiConsole.Console = before; }
    }

    [Fact]
    public async Task RedoNeedsResumeAndTheRunsOwnSetpoints()
    {
        var (code, _) = await Run<RunCommand>("--simulate", "-y", "--redo", "2");
        Assert.NotEqual(0, code);

        var dir = Path.Combine(Path.GetTempPath(), "OvenTrackerRedo-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, (await Run<RunCommand>("--simulate", "--speed", "3000", "--interval", "5", "-y", "--unit", "F", "--modes", "bake",
                "--setpoints", "350", "--out", dir)).Code);
            var (badCode, output) = await Run<RunCommand>("--simulate", "-y", "--resume", dir, "--redo", "1,4");
            Assert.Equal(1, badCode);
            Assert.Contains("no setpoint 4", output);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ACharacteriseRunsSetpointsAreMeasuredAgainAtTheirOwnAdjustments()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OvenTrackerRedo-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Phase 1 bake 325 and 425 at 0, then phase 2 at the new adjustment: 425 and 325 again (setpoints 3 and 4).
            var (code, output) = await Run<CharacteriseCommand>("--simulate", "--speed", "3000", "--interval", "5", "-y", "--unit", "F", "--modes", "bake",
                "--setpoints", "325,425", "--adjustment", "0", "--step", "10", "--out", dir);
            Assert.Equal(0, code);
            var csv = Path.Combine(dir, "readings.csv");
            var info = System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(Path.Combine(dir, "session.json")), Json.Options)!;
            var changedTo = info.Characterise!.ChangedToC!.Value;
            var first = Replay.Analyze(csv, info.Options, info.Plan);
            Assert.Equal([1, 2, 3, 4], first.Select(r => r.Point.Index));

            // Setpoint 2 (425 at 0) and 3 (425 at the new adjustment) again.
            (code, output) = await Run<RunCommand>("--simulate", "--speed", "3000", "--interval", "5", "-y", "--resume", dir, "--redo", "2-3");
            Assert.Equal(0, code);
            Assert.Contains("and 2 to measure again", output);

            var lines = File.ReadAllLines(csv);
            var resumedAt = Array.FindLastIndex(lines, l => l.EndsWith(",resume", StringComparison.Ordinal));
            Assert.True(resumedAt > 0);
            var sitting = lines.Skip(resumedAt).ToList();
            // Setpoint 2 first, at 0 (the oven was set back to it), then 3 at the run's own adjustment.
            var events = sitting.Select(l => l.Split(',')).Where(f => f[5] == "Event").Select(f => (Point: f[1], Name: f[8])).ToList();
            var order = events.Where(e => e.Name == "start" || e.Name.StartsWith("adjustment-set:", StringComparison.Ordinal)).Select(e => e.Name == "start" ? "start " + e.Point : e.Name).ToList();
            Assert.Equal(["adjustment-set:0.000", "start 2", $"adjustment-set:{changedTo:F3}", "start 3"], order);

            // The new attempts replace the old: still four, each once, and measured where they should be.
            var again = Replay.Analyze(csv, info.Options, info.Plan).OrderBy(r => r.Point.Index).ToList(); // in the order measured; analyze and run sort them
            Assert.Equal([1, 2, 3, 4], again.Select(r => r.Point.Index));
            Assert.All(again, r => Assert.Equal(CompletionReason.Converged, r.Metrics.Reason));
            Assert.True(again[1].AfterReset); // the first setpoint of the new sitting
            Assert.NotEqual(first[1].Metrics.TotalMinutes, again[1].Metrics.TotalMinutes);
            Assert.NotEqual(first[2].Metrics.TotalMinutes, again[2].Metrics.TotalMinutes);
            Assert.Equal(first[0].Metrics.MeanC, again[0].Metrics.MeanC);
            // Setpoint 2 was measured at 0 again, not left at the new adjustment: its mean is close to the first attempt's,
            // and the two attempts at 425 still differ by about the change of adjustment.
            Assert.InRange(again[1].Metrics.MeanC!.Value - first[1].Metrics.MeanC!.Value, -1.5, 1.5);
            Assert.InRange(Math.Abs(again[2].Metrics.MeanC!.Value - again[1].Metrics.MeanC!.Value), Math.Abs(changedTo) * 0.6, Math.Abs(changedTo) * 1.4);
            // results.json and the characterisation are from the new attempts.
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "results.json")));
            var savedMeans = saved.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("metrics").GetProperty("meanC").GetDouble()).ToList();
            Assert.Equal(again.Select(r => r.Metrics.MeanC!.Value), savedMeans);
            Assert.True(File.Exists(Path.Combine(dir, "characterisation.md")));

            // With nothing left to do or redo, a plain resume says so.
            (code, output) = await Run<RunCommand>("--simulate", "-y", "--resume", dir);
            Assert.Equal(0, code);
            Assert.Contains("already done", output);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
