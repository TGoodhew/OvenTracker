using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

/// <summary>The analysis and recording are consistent with themselves (issue #36).</summary>
public class ConsistencyTests
{
    static readonly AdjustmentOptions Options = new(StepC: 1 / 1.8, RangeC: 35 / 1.8);

    static string TempDir() => Directory.CreateTempSubdirectory("OvenTracker36-").FullName;

    static SessionInfo Session(params TestPoint[] plan) =>
        new("test", DateTimeOffset.UnixEpoch, "test meter", "nowhere", TempUnit.Celsius, 8, new AnalysisOptions(), plan);

    [Fact]
    public void EventsWithCommasKeepTheRowToNineFields()
    {
        var dir = TempDir();
        using (var rec = new RunRecorder(dir, Session()))
        {
            rec.Note(DateTimeOffset.UnixEpoch, "prompt:PRESS W TO LET IT COOL, OR SET IT TO \"350\"\r\nNOW");
            rec.Reading(new MeterReading(DateTimeOffset.UnixEpoch, ReadingStatus.Ok, 20, "+2,0E+01\r\n"), null, null);
        }
        var rows = File.ReadAllLines(Path.Combine(dir, "readings.csv")).Skip(1).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(9, r.Split(',').Length));
        Assert.EndsWith("prompt:PRESS W TO LET IT COOL; OR SET IT TO 350 NOW", rows[0]);
    }

    [Theory]
    [InlineData(176.66666666666666)]
    [InlineData(0.1 + 0.2)]
    [InlineData(2.0)]
    [InlineData(123.456789012345)]
    public void NumbersReadBackExactly(double v) =>
        Assert.Equal(v, double.Parse(RunRecorder.Number(v), System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void ARecordedSimulationKeepsItsEventsOnTheSimulatedClock()
    {
        var dir = TempDir();
        using var rec = new RunRecorder(dir, Session());
        var simulated = DateTimeOffset.UnixEpoch.AddHours(3);
        rec.Clock = () => rec.LastReadingTime ?? DateTimeOffset.UtcNow;
        rec.Reading(new MeterReading(simulated, ReadingStatus.Ok, 20, "20"), null, null);
        Assert.Equal(simulated, rec.Now);
    }

    [Fact]
    public void TheVerdictAndTheResidualColumnJudgeAMissingIntervalAlike()
    {
        // Within tolerance, but with no ±95 %: neither can call it accurate.
        Assert.Equal(Verdict.Borderline, SessionSummary.Judge(3.0, null, 8));
        Assert.Equal(Verdict.OutOfTolerance, SessionSummary.Judge(9.0, null, 8));
        Assert.Equal(Verdict.Accurate, SessionSummary.Judge(3.0, 1.0, 8));
    }

    [Fact]
    public void ThePrecisionTargetIsOneTest()
    {
        Assert.True(SetpointAnalyzer.WithinPrecision(1.0, 1.0));
        Assert.True(SetpointAnalyzer.WithinPrecision(1.0 + 1e-12, 1.0));
        Assert.False(SetpointAnalyzer.WithinPrecision(1.01, 1.0));
    }

    [Fact]
    public void SetpointsAreTheSameEverywhere()
    {
        // A °F run's 176.667 and a later run's exact 176.66666… are one setpoint, in every grouping.
        Assert.Equal(AdjustmentAdvisor.SetpointKey(176.667), AdjustmentAdvisor.SetpointKey(176.66666666666666));
        Assert.NotEqual(AdjustmentAdvisor.SetpointKey(176.667), AdjustmentAdvisor.SetpointKey(176.8));
    }

    [Fact]
    public void AGainWithAMissingIntervalHasAnUnknownUncertainty()
    {
        var points = new[]
        {
            new AdjustPoint(OvenMode.Bake, 180, 5, 0.5, 0),
            new AdjustPoint(OvenMode.Bake, 180, 0, null, -5),
        };
        var g = AdjustmentAdvisor.Gains(points, Options)[OvenMode.Bake];
        Assert.Equal(GainSource.Measured, g.Source);
        Assert.Equal(1.0, g.Gain, 9);
        Assert.Null(g.HalfWidth);
    }

    [Fact]
    public void WithoutTheCurrentSettingTheSearchDoesntAssumeItIsZero()
    {
        // 30 °C hot: the best change is −30 °C, beyond the ±19.4 °C range around 0, but possible from a setting of +15.
        var points = new[] { new AdjustPoint(OvenMode.Bake, 180, 30, 0.5, 0) };
        var unknown = AdjustmentAdvisor.Advise(points, Options, null).Single();
        Assert.Equal(-30, unknown.ChangeC, 0);
        Assert.False(unknown.Clamped);
        var known = AdjustmentAdvisor.Advise(points, Options, 0).Single();
        Assert.True(known.Clamped);
        Assert.True(known.ChangeC >= -Options.RangeC - 1e-9);
    }

    [Fact]
    public void FavouringATemperatureWithOneAdjustmentWeighsEachModeByItsOwnGain()
    {
        // Bake runs 4 °C hot and convection 2 °C cold, flat across temperature; both move one for one.
        var points = new[]
        {
            new AdjustPoint(OvenMode.Bake, 180, 4, 0.2, 0), new AdjustPoint(OvenMode.Bake, 220, 4, 0.2, 0),
            new AdjustPoint(OvenMode.Convection, 180, -2, 0.2, 0), new AdjustPoint(OvenMode.Convection, 220, -2, 0.2, 0),
            // More convection points must not pull the answer towards convection: one mode, one say.
            new AdjustPoint(OvenMode.Convection, 200, -2, 0.2, 0), new AdjustPoint(OvenMode.Convection, 210, -2, 0.2, 0),
        };
        var advice = AdjustmentAdvisor.Advise(points, Options with { FavourC = 200 }, 0).Single();
        // Least squares over the two modes: −(4 − 2) / 2 = −1 °C, to the nearest step.
        Assert.Equal(-1, advice.ChangeC, 0);
    }

    [Fact]
    public void EachModeKeepsItsReasonForMoreSetpoints()
    {
        var points = new[]
        {
            new AdjustPoint(OvenMode.Bake, 180, 4, 0.2, 0),
            new AdjustPoint(OvenMode.Convection, 180, -2, 0.2, 0), new AdjustPoint(OvenMode.Convection, 182, -2, 0.2, 0),
        };
        var advice = AdjustmentAdvisor.Advise(points, Options, 0).Single();
        Assert.Contains("Bake: With one setpoint per mode", advice.TooFewSetpoints);
        Assert.Contains("Convection: The setpoints are too close together", advice.TooFewSetpoints);
    }

    [Fact]
    public void FavourAndAnObjectiveDontGoTogether()
    {
        var s = new AnalyzeSettings { Paths = [Path.GetTempPath()], Favour = 350, Objective = "minimax" };
        Assert.False(s.Validate().Successful);
    }

    [Fact]
    public void ARunWithoutItsSessionUsesRunsDefaultsForAFahrenheitOven()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "readings.csv"), RunRecorder.Header + "\n");
        var run = AnalyzeCommand.Load(dir)!;
        Assert.Equal(TempUnit.Fahrenheit, run.Info.Unit);
        Assert.Equal(2 / 1.8, run.Info.Options.PrecisionC, 9);
    }

    [Fact]
    public void CombiningRunsKeepsAnUnknownAdjustmentUnknown()
    {
        var a = TempDir();
        var b = TempDir();
        foreach (var d in new[] { a, b })
        {
            using var rec = new RunRecorder(d, Session(new TestPoint(1, OvenMode.Bake, 180)) with { Started = DateTimeOffset.UnixEpoch.AddDays(d == a ? 0 : 1) });
        }
        var (info, _) = AnalyzeCommand.Combine([AnalyzeCommand.Load(a)!, AnalyzeCommand.Load(b)!], i => i.Options, null);
        Assert.Null(info.AdjustmentC);
    }
}
