using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Ovens;
using OvenTracker.Session;

namespace OvenTracker.Tests;

public class OvenProfileTests
{
    static readonly UnitFormatter F = new(TempUnit.Fahrenheit);
    static double T(double f) => (f - 32) / 1.8;
    static double D(double f) => f / 1.8;

    static string TempFolder() => Path.Combine(Path.GetTempPath(), "OvenTrackerOvens-" + Guid.NewGuid().ToString("N"));
    static readonly string Empty = TempFolder(); // a folder with no oven files: just the built-ins

    [Fact]
    public void TheBuiltInOvensAreGenericAndThePHS925()
    {
        var all = OvenProfiles.All(Empty);
        Assert.Equal(["generic", "ge-phs925"], all.Select(p => p.Key));
        var ge = OvenProfiles.Find("GE-PHS925", Empty)!;
        Assert.Equal(35, ge.AdjustRangeC * 1.8, 6);
        Assert.Equal(1, ge.AdjustStepC * 1.8, 6);
        Assert.True(ge.SharedAdjustment);
        Assert.Equal(25, ge.ConvectionConversionC!.Value * 1.8, 6);
        Assert.Contains(ge.HowToChange!, s => s.Contains("SF"));
        Assert.Contains("owner's manual", ge.Source);
        Assert.True(OvenProfiles.Default.IsGeneric);
    }

    [Fact]
    public void ASlowCyclingOvenGetsLongerPerSetpoint()
    {
        Assert.Equal(120, OvenProfiles.Default.DefaultMaxMinutes);
        Assert.Equal(180, OvenProfiles.Find("ge-phs925", Empty)!.DefaultMaxMinutes); // an hour plus six 20-minute cycles
        Assert.Equal(120, (OvenProfiles.Default with { TypicalCycleMinutes = 5 }).DefaultMaxMinutes);
    }

    [Fact]
    public void AnOvensAdjustmentSetsTheDefaults()
    {
        var ge = AdjustmentOptions.For(OvenProfiles.Find("ge-phs925", Empty)!, TempUnit.Fahrenheit);
        Assert.Equal(D(1), ge.StepC, 6);
        Assert.Equal(D(35), ge.RangeC, 6);

        var perMode = AdjustmentOptions.For(new OvenProfile("x", "X", 0.5, 15, SharedAdjustment: false), TempUnit.Celsius);
        Assert.False(perMode.SharedAcrossModes);
        Assert.Equal(0.5, perMode.StepC);

        Assert.Equal(1, AdjustmentOptions.For(OvenProfiles.Default, TempUnit.Celsius).StepC); // a °C oven: 1 °C steps
    }

    [Fact]
    public void YourOwnOvenCanBeDescribedInAFile()
    {
        var folder = TempFolder();
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "my.json"), """
                {
                  "key": "whirlpool-wfe505",
                  "name": "Whirlpool WFE505W0HS",
                  "adjustStepC": 0.5556,
                  "adjustRangeC": 16.667,
                  "howToChange": ["Hold Bake for 5 seconds.", "Use + and - to set it, then press Start."],
                  "typicalCycleMinutes": 8
                }
                """);
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");

            var mine = OvenProfiles.Find("whirlpool-wfe505", folder)!;
            Assert.Equal("Whirlpool WFE505W0HS", mine.Name);
            Assert.True(mine.SharedAdjustment); // left out: the default
            Assert.Equal(120, mine.DefaultMaxMinutes); // 60 + 6 × 8 = 108, so the 120-minute floor applies
            Assert.Equal(3, OvenProfiles.All(folder).Count);
            Assert.Single(OvenProfiles.Unreadable(folder), p => p.EndsWith("broken.json"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AFileCanReplaceABuiltInOvenAndCarriesItsMeasuredGains()
    {
        var folder = TempFolder();
        try
        {
            var measured = OvenProfiles.Find("ge-phs925", Empty)! with
            {
                Gains = new Dictionary<OvenMode, MeasuredGain>
                {
                    [OvenMode.Bake] = new(0.93, 0.05, DateTimeOffset.Parse("2026-11-22T12:00:00Z")),
                    [OvenMode.Convection] = new(0.97, 0.06, DateTimeOffset.Parse("2026-11-22T12:00:00Z")),
                },
            };
            var path = OvenProfiles.Save(measured, folder);
            Assert.EndsWith("ge-phs925.json", path);

            var back = OvenProfiles.Find("ge-phs925", folder)!;
            Assert.Equal(0.93, back.Gains![OvenMode.Bake].Gain);
            Assert.Equal(2, OvenProfiles.All(folder).Count); // replaced, not added

            var options = AdjustmentOptions.For(back, TempUnit.Fahrenheit);
            Assert.Equal(0.93, options.KnownGains![OvenMode.Bake]);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TheAdvisorUsesAGainMeasuredBeforeWhenTheRunsDontMeasureOne()
    {
        var options = AdjustmentOptions.For(TempUnit.Fahrenheit) with { KnownGains = new Dictionary<OvenMode, double> { [OvenMode.Bake] = 0.8 } };
        var a = Assert.Single(AdjustmentAdvisor.Advise([new AdjustPoint(OvenMode.Bake, T(350), D(8), D(0.5), 0), new AdjustPoint(OvenMode.Bake, T(400), D(8), D(0.5), 0)], options, 0));
        Assert.True(a.GainFromProfile);
        Assert.False(a.GainMeasured);
        Assert.Equal(0.8, a.Gain);
        Assert.Equal(-10, Math.Round(a.ChangeC * 1.8)); // 8 °F hot, at 0.8 per degree: −10 °F
    }

    [Fact]
    public void TheReportSaysHowToSetItOnThisOven()
    {
        var ge = OvenProfiles.Find("ge-phs925", Empty)!;
        var plan = new[] { new TestPoint(1, OvenMode.Bake, T(350)), new TestPoint(2, OvenMode.Bake, T(400)) };
        PointMetrics Hot(double sp) => new(sp, CompletionReason.Converged, UnitKind.Cycle, 4, 0, sp + D(10), 0.3, 1, 0, 10, sp, sp + 10, 20, sp - 50, 20, 5, 30, 60, 90, null, null, 1);
        var results = plan.Select(p => new PointResult(p, Hot(p.SetpointC))).ToList();

        var withOven = new SessionInfo("t", DateTimeOffset.Now, "m", "l", TempUnit.Fahrenheit, 8, new AnalysisOptions(), plan, AdjustmentC: 0, Oven: ge);
        var r = new Report(withOven, results);
        var details = string.Join("\n", r.AdviceDetails(Assert.Single(r.Advice())));
        Assert.Contains("To set it on the GE Profile PHS925ST1SS: 1. Touch BROIL HI/LO and BAKE", details);

        var generic = new Report(withOven with { Oven = null }, results);
        Assert.DoesNotContain("To set it on", string.Join("\n", generic.AdviceDetails(Assert.Single(generic.Advice()))));
    }

    [Fact]
    public void TheConvectionNoteIsTheOvensOwn()
    {
        var ge = RunCommand.ConvectionNote(OvenProfiles.Find("ge-phs925", Empty)!, F);
        Assert.Contains("lowers convection settings by about 25 °F", ge);
        Assert.Contains("CON OFF", ge);
        Assert.Contains("e.g. by 25 °F", RunCommand.ConvectionNote(OvenProfiles.Default, F));
    }

    [Fact]
    public void AnUnknownOvenIsRejected()
    {
        var result = new RunSettings { Oven = "no-such-oven" }.Validate();
        Assert.False(result.Successful);
        Assert.Contains("adding-an-oven", result.Message);
    }
}
