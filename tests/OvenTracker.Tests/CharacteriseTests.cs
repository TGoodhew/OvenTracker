using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Ovens;
using OvenTracker.Session;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace OvenTracker.Tests;

[Collection(nameof(ConsoleCollection))]
public class CharacteriseTests
{
    static double T(double f) => (f - 32) / 1.8;
    static double D(double f) => f / 1.8;

    static AdjustPoint P(OvenMode mode, double setF, double offF, double adjF) => new(mode, T(setF), D(offF), D(0.5), D(adjF));

    [Fact]
    public void EachModeHasItsOwnGainAndASharedAdjustmentWeighsThem()
    {
        // Bake moves 0.8 per degree, convection 1.0 (both from the profile); both 8 °F hot.
        var options = AdjustmentOptions.For(TempUnit.Fahrenheit) with
        {
            KnownGains = new Dictionary<OvenMode, double> { [OvenMode.Bake] = 0.8, [OvenMode.Convection] = 1.0 },
        };
        var a = Assert.Single(AdjustmentAdvisor.Advise([P(OvenMode.Bake, 350, 8, 0), P(OvenMode.Convection, 350, 8, 0)], options, 0));
        // −Σgo/Σg² = −14.4/1.64 = −8.8 °F: on 1 °F steps, −9.
        Assert.Equal(-9, Math.Round(a.ChangeC * 1.8));
        Assert.Equal(GainSource.Profile, a.Gains![OvenMode.Bake].Source);
        Assert.Equal(0.8, a.Gains[OvenMode.Bake].Gain);
        Assert.Equal([0.8, -1.0], a.Residuals.Select(r => Math.Round(r.AfterC * 1.8, 1)));
    }

    [Fact]
    public void GainsAreMeasuredWhereTheyCanBeAndAssumedWhereTheyCant()
    {
        var gains = AdjustmentAdvisor.Gains(
            [P(OvenMode.Bake, 350, 10, 0), P(OvenMode.Bake, 350, 1, -10), P(OvenMode.Convection, 350, -5, 0)],
            AdjustmentOptions.For(TempUnit.Fahrenheit));
        Assert.Equal(GainSource.Measured, gains[OvenMode.Bake].Source);
        Assert.Equal(0.9, gains[OvenMode.Bake].Gain, 6);
        Assert.Equal(GainSource.Assumed, gains[OvenMode.Convection].Source);
        Assert.Equal(1, gains[OvenMode.Convection].Gain);
    }

    static PointResult Result(int index, OvenMode mode, double setF, double meanF, double adjF) =>
        new(new TestPoint(index, mode, T(setF), D(adjF)),
            new PointMetrics(T(setF), CompletionReason.Converged, UnitKind.Cycle, 4, 0, T(meanF), D(0.4), 1, 0, 15, T(meanF - 8), T(meanF + 8), 20, T(setF - 100), 20, 5, 30, 60, 90, null, null, 1));

    [Fact]
    public void ACharacterisationGivesGainsShapeAndRepeats()
    {
        // Bake: +8 °F at both setpoints at adjustment 0; +17.5 at +10, so each degree moves it 0.95.
        var results = new List<PointResult>
        {
            Result(1, OvenMode.Bake, 325, 333, 0), Result(2, OvenMode.Bake, 425, 433, 0),
            Result(3, OvenMode.Bake, 425, 442.5, 10), Result(4, OvenMode.Bake, 325, 342.5, 10),
            Result(5, OvenMode.Bake, 325, 342.9, 10), // a repeat of 325 at +10
        };
        var info = new SessionInfo("t", DateTimeOffset.Now, "m", "l", TempUnit.Fahrenheit, 8, new AnalysisOptions(), results.Select(r => r.Point).ToList(),
            AdjustmentC: D(10), Oven: OvenProfiles.Find("ge-phs925", Path.GetTempPath() + "none"), Characterise: new CharacteriseInfo(D(10), 0, D(10)));
        var c = Characterisation.From(info, results);

        var bake = Assert.Single(c.Modes);
        Assert.Equal(GainSource.Measured, bake.Gain!.Source);
        Assert.InRange(bake.Gain.Gain, 0.93, 0.97); // +9.5 °F for +10 °F
        Assert.InRange(bake.SlopePerDegree!.Value, -0.02, 0.02); // the same at 325 and 425
        var repeat = Assert.Single(c.Repeats);
        Assert.Equal(0.4, repeat.SpreadC * 1.8, 1);
        Assert.True(c.MeasuredAny);
        Assert.Contains("each degree of adjustment moves it 0.9", c.Markdown(info));
        Assert.Contains("Repeatability", c.Markdown(info));
    }

    [Fact]
    public void MeasuredGainsAreSavedToTheOvensProfileButNotTheGenericOne()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OvenTrackerGains-" + Guid.NewGuid().ToString("N"));
        var runFolder = Path.Combine(folder, "run");
        Directory.CreateDirectory(runFolder);
        var console = new TestConsole();
        var before = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            var results = new List<PointResult> { Result(1, OvenMode.Bake, 350, 358, 0), Result(2, OvenMode.Bake, 350, 349, -10) };
            var ge = OvenProfiles.Find("ge-phs925", folder)!;
            var info = new SessionInfo("t", DateTimeOffset.Now, "m", "l", TempUnit.Fahrenheit, 8, new AnalysisOptions(), results.Select(r => r.Point).ToList(), AdjustmentC: D(-10), Oven: ge);

            RunCommand.SaveCharacterisation(info, results, runFolder, ge, save: true, ovenFolder: folder);
            var saved = OvenProfiles.Find("ge-phs925", folder)!;
            Assert.Equal(0.9, saved.Gains![OvenMode.Bake].Gain, 6);
            Assert.True(File.Exists(Path.Combine(runFolder, "characterisation.md")));

            RunCommand.SaveCharacterisation(info with { Oven = OvenProfiles.Default }, results, runFolder, OvenProfiles.Default, save: true, ovenFolder: folder);
            Assert.Contains("aren't saved", console.Output);
            Assert.False(File.Exists(Path.Combine(folder, "generic.json")));
        }
        finally
        {
            AnsiConsole.Console = before;
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task CharacteriseMeasuresTheSimulatedOvensGainEndToEnd()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OvenTrackerChar-" + Guid.NewGuid().ToString("N"));
        var console = new TestConsole();
        console.Profile.Width = 160;
        var before = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            var app = new CommandApp<CharacteriseCommand>();
            var code = await app.RunAsync(["--simulate", "--speed", "3000", "--interval", "5", "-y", "--unit", "F", "--modes", "bake",
                "--setpoints", "325,425", "--adjustment", "0", "--step", "10", "--out", dir]);
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(dir, "characterisation.md")), console.Output);

            var info = System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(Path.Combine(dir, "session.json")), Json.Options)!;
            Assert.Equal(4, info.Plan.Count); // 2 setpoints, then the highest and lowest again
            Assert.NotNull(info.Characterise!.ChangedToC);
            Assert.InRange(Math.Abs(info.Characterise.ChangedToC!.Value) * 1.8, 9.9, 35.1);

            var ch = System.Text.Json.JsonSerializer.Deserialize<Characterisation>(File.ReadAllText(Path.Combine(dir, "characterisation.json")), Json.Options)!;
            var bake = Assert.Single(ch.Modes);
            // The simulated oven moves about 0.9 per degree when made cooler, 1.0 hotter (its thermostat's sensor lags the air).
            Assert.InRange(bake.Gain!.Gain, 0.75, 1.15);
            Assert.DoesNotContain("started warm", File.ReadAllText(Path.Combine(dir, "report.md"))); // it started cold
            Assert.Contains("aren't saved", console.Output); // a simulation
        }
        finally
        {
            AnsiConsole.Console = before;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
