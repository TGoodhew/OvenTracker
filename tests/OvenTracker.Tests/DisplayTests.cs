using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;
using OvenTracker.Ui;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace OvenTracker.Tests;

/// <summary>What the app shows (issue #41).</summary>
[Collection(nameof(ConsoleCollection))]
public class DisplayTests
{
    [Theory]
    [InlineData("−10 °F", "-10 F")]   // the minus sign the big font can't draw: it showed "10 F", the opposite
    [InlineData("+10 °F", "+10 F")]
    [InlineData("–5 °C", "-5 C")]     // an en dash
    [InlineData("CONVECTION", "CONVECTION")]
    [InlineData("350 °F ✓", "350 F ")]
    public void TheBigTextKeepsItsSign(string text, string drawn) => Assert.Equal(drawn, ChangePrompt.ForFiglet(text));

    [Fact]
    public void TheBigAdjustmentPromptShowsTheMinusSign()
    {
        var console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(new FigletText(ChangePrompt.ForFiglet("−10 °F")));
        var minus = new FigletText("-10 F");
        var expected = new TestConsole();
        expected.Profile.Width = 120;
        expected.Write(minus);
        Assert.Equal(expected.Output, console.Output);
    }

    [Theory]
    [InlineData(0.4, "in under a minute")]
    [InlineData(12, "in about 12 min")]
    [InlineData(125, "in about 2 h 5 min")]
    public void TimesLeftReadProperly(double minutes, string text) => Assert.Equal(text, ResetWaiter.InAbout(minutes));

    static DashboardModel Model(int width, int height, string? alert, MeterPoller? poller = null)
    {
        var oven = new SimulatedOven(3);
        oven.Set(OvenMode.Bake, 176.67);
        var a = new SetpointAnalyzer(176.67, new AnalysisOptions());
        for (var t = 0; t < 40 * 60; t += 2)
        {
            oven.Step(2);
            a.Add(t, oven.ProbeC());
        }
        poller ??= new MeterPoller(new SimulatedMeter(new SimulatedOven(1), 1), TimeSpan.FromSeconds(1), connected: true);
        return new DashboardModel(new TestPoint(1, OvenMode.Bake, 176.67), 4, a, new UnitFormatter(TempUnit.Fahrenheit), poller,
            "simulated meter", "", 15 / 1.8, ChartView.Auto, ReadingStatus.Ok, "Preheat signal noted at 12:00, oven at 350.0 °F.", width, height, alert);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(120, 40)]
    [InlineData(160, 50)]
    public void TheDashboardFitsTheWindow(int width, int height)
    {
        // A long alert wraps, and in a narrow window the two tables stack: the frame must still fit, or the live
        // display crops its header off the top.
        var m = Model(width, height, "No reading for 45 s. The meter has stopped answering: check it's on and in range, and that the link is up.");
        Assert.True(Dashboard.HeightOf(Dashboard.Render(m), width) <= height - 2, $"{Dashboard.HeightOf(Dashboard.Render(m), width)} lines in a {height}-line window\n" + Show(Dashboard.Render(m), width));
    }

    static string Show(Spectre.Console.Rendering.IRenderable r, int width) { var c = new TestConsole(); c.Profile.Width = width; c.Write(r); return c.Output; }

    [Fact]
    public void ASmallWindowStillShowsTheMetersBattery()
    {
        // Compact mode leaves the meter table out; the battery (and drops, and the last error) move into the meter row.
        using var poller = new MeterPoller(new SimulatedMeter(new SimulatedOven(1), 1), TimeSpan.FromSeconds(1), connected: true);
        poller.Start();
        for (var i = 0; i < 200 && poller.BatteryPercent is null; i++) Thread.Sleep(10);
        poller.Stop();
        var shown = Show(Dashboard.Render(Model(80, 24, null, poller)), 80);
        Assert.Contains("simulated meter, stopped, battery 81 %", shown);
    }

    [Fact]
    public void ABigWindowStillGivesTheChartRoom()
    {
        var m = Model(160, 50, null);
        var used = Dashboard.HeightOf(Dashboard.Render(m), 160);
        Assert.InRange(used, 40, 48); // grown to fill the window, up to the chart's largest
    }

    [Fact]
    public async Task AnalyzeOfARealCharacteriseRunSaysHowToSaveTheGains()
    {
        // A copy of the simulated characterise run, as if a real meter had made it.
        var dir = Directory.CreateTempSubdirectory("OvenTracker41-").FullName;
        var source = Path.Combine(SharedTestDataTests.TestData, "runs", "sim-characterise");
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        var session = Path.Combine(dir, "session.json");
        File.WriteAllText(session, File.ReadAllText(session).Replace("\"driver\": \"simulated\"", "\"driver\": \"u1253b\""));

        var console = new TestConsole();
        console.Profile.Width = 200;
        var before = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            var code = await new CommandApp<AnalyzeCommand>().RunAsync([dir, "--out", Path.Combine(dir, "out")]);
            Assert.Equal(0, code);
        }
        finally
        {
            AnsiConsole.Console = before;
            Directory.Delete(dir, true);
        }
        Assert.DoesNotContain("A simulation", console.Output);
        Assert.Contains("--save-gains", console.Output);
    }
}
