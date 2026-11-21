using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Session;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace OvenTracker.Tests;

/// <summary>Tests that swap the app's console run one at a time.</summary>
[CollectionDefinition(nameof(ConsoleCollection), DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection(nameof(ConsoleCollection))]
public class NoTerminalTests
{
    /// <summary>Runs the app against a console that isn't a terminal (as with output to a file), and returns its exit code and output.</summary>
    static async Task<(int Code, string Output)> RunWithoutATerminal(params string[] args)
    {
        var console = new TestConsole(); // not interactive, no ANSI: like output redirected to a file
        console.Profile.Width = 160;
        var before = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            var app = new CommandApp<RunCommand>();
            var code = await app.RunAsync(args);
            return (code, console.Output);
        }
        finally
        {
            AnsiConsole.Console = before;
        }
    }

    [Fact]
    public async Task AWholeSessionRunsWithoutATerminalAndWritesTheReport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OvenTrackerPlain-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (code, output) = await RunWithoutATerminal(
                "--simulate", "--speed", "3000", "--interval", "10", "-y",
                "--unit", "F", "--modes", "bake", "--setpoints", "350", "--out", dir);

            Assert.Equal(0, code);
            foreach (var file in new[] { "report.md", "results.json", "readings.csv", "session.json" })
                Assert.True(File.Exists(Path.Combine(dir, file)), $"no {file}");
            var progress = output.Split('\n').Where(l => l.Contains("  ·  ") && l.Contains("°F")).ToList();
            Assert.True(progress.Count >= 5, $"only {progress.Count} progress lines:\n{output}");
            Assert.Contains("Measuring", output);
            Assert.Contains("Saved to", output);
            Assert.DoesNotContain("\u001b[", output); // no cursor movement or redraws in a log
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>Reads a file the app may be writing at that moment.</summary>
    static string? ReadShared(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(s).ReadToEnd();
        }
        catch (IOException) { return null; }
    }

    [Fact]
    public async Task EachSetpointIsSavedAsItFinishesNotOnlyAtTheEnd()
    {
        // #43: a crash at the end lost the whole report. While the second setpoint is being measured, the first must
        // already be in results.json and report.md.
        var dir = Path.Combine(Path.GetTempPath(), "OvenTrackerSaved-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = RunWithoutATerminal(
                "--simulate", "--speed", "3000", "--interval", "10", "-y",
                "--unit", "F", "--modes", "bake", "--setpoints", "350,400", "--out", dir);
            string? seen = null;
            while (!session.IsCompleted && seen is null)
            {
                await Task.Delay(5);
                var csv = ReadShared(Path.Combine(dir, "readings.csv"));
                if (csv is null || !csv.Contains(",2,Bake,") || csv.Contains(",2,Bake,") && csv[csv.IndexOf(",2,Bake,")..].Contains("end:")) continue;
                seen = ReadShared(Path.Combine(dir, "results.json"));
            }
            var (code, output) = await session;
            Assert.Equal(0, code);
            Assert.True(seen is not null, "the second setpoint was never seen in progress:\n" + output);
            // The session (with its whole plan) and the first setpoint's result, not yet the second's.
            var saved = System.Text.Json.JsonDocument.Parse(seen!).RootElement.GetProperty("results");
            Assert.Equal(1, saved.GetArrayLength());
            Assert.Equal(1, saved[0].GetProperty("point").GetProperty("index").GetInt32());
            Assert.True(File.Exists(Path.Combine(dir, "report.md")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task WithoutATerminalItSaysWhatItCantAsk()
    {
        var (code, output) = await RunWithoutATerminal("--simulate", "-y", "--unit", "F");
        Assert.Equal(1, code);
        Assert.Contains("can't ask", output);
        Assert.Contains("--modes, --setpoints", output);
    }

    [Fact]
    public void AProgressLineSaysWhereTheSetpointIs()
    {
        var a = new SetpointAnalyzer(176.67);
        for (var t = 0; t < 300; t++) a.Add(t, 100 + t * 0.2);
        var line = PointTracker.PlainLine(a, new UnitFormatter(TempUnit.Fahrenheit));
        Assert.StartsWith("4:59", line);
        Assert.Contains("°F", line);
        Assert.Contains("Heating to the setting", line);
        Assert.Contains("time left", line);
    }
}
