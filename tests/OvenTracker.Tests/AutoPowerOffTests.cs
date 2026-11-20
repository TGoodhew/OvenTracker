using OvenTracker.Analysis;
using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

public class AutoPowerOffTests
{
    static readonly AutoPowerOff TenMinutes = new(TimeSpan.FromMinutes(10), ["Do this.", "Then that."], "The guide, p. 1");

    [Fact]
    public void AMeterWithoutAutoPowerOffGetsNoWarning()
    {
        Assert.Null(AutoPowerOffAdvice.For(null, AutoPowerOffSetting.Unknown));
        Assert.Null(AutoPowerOffAdvice.For(null, AutoPowerOffSetting.Off));
    }

    [Fact]
    public void AMeterThatCantSayGetsAReminderWithTheSteps()
    {
        var w = AutoPowerOffAdvice.For(TenMinutes, AutoPowerOffSetting.Unknown)!;
        Assert.Equal(AutoPowerOffWarningKind.Reminder, w.Kind);
        Assert.Contains("10 minutes", w.Headline);
        Assert.Equal(["Do this.", "Then that."], w.Steps);
        Assert.Equal("The guide, p. 1", w.Source);
    }

    [Fact]
    public void AMeterThatSaysItsOffGetsNone()
    {
        Assert.Null(AutoPowerOffAdvice.For(TenMinutes, AutoPowerOffSetting.Off));
    }

    [Fact]
    public void AMeterThatSaysItsOnGetsAWarningWithItsOwnTimeout()
    {
        var w = AutoPowerOffAdvice.For(TenMinutes, AutoPowerOffSetting.On(TimeSpan.FromMinutes(15)))!;
        Assert.Equal(AutoPowerOffWarningKind.On, w.Kind);
        Assert.Contains("15 minutes", w.Headline);
        Assert.Contains("middle of a setpoint", w.Headline);
    }

    [Fact]
    public void SayingItsOffQuietensTheReminderButNotTheMetersOwnWord()
    {
        Assert.Null(AutoPowerOffAdvice.For(TenMinutes, AutoPowerOffSetting.Unknown, confirmedOff: true));
        Assert.NotNull(AutoPowerOffAdvice.For(TenMinutes, AutoPowerOffSetting.On(TimeSpan.FromMinutes(10)), confirmedOff: true));
    }

    [Fact]
    public void AMeterKeptOnByRemoteReadsNeedsNoReminder()
    {
        Assert.Null(AutoPowerOffAdvice.For(TenMinutes with { RemoteReadsKeepItOn = true }, AutoPowerOffSetting.Unknown));
    }

    [Fact]
    public void TheU1253BDeclaresItsAutoPowerOffWithTheStepsFromItsGuide()
    {
        IMeterDriver d = new U1253BDriver();
        var apo = d.AutoPowerOff!;
        Assert.Equal(TimeSpan.FromMinutes(10), apo.DefaultTimeout);
        Assert.Equal(4, apo.HowToTurnOff.Count);
        Assert.Contains(apo.HowToTurnOff, s => s.Contains("APO"));
        Assert.Contains("126", apo.Source);
        // It can't report the setting, so a run gets the reminder.
        Assert.Equal(AutoPowerOffWarningKind.Reminder, AutoPowerOffAdvice.For(apo, AutoPowerOffSetting.Unknown)!.Kind);
    }

    [Fact]
    public void TheSimulatedMeterSaysWhetherItWillSwitchOff()
    {
        var meter = new SimulatedMeter(new SimulatedOven(1), 1);
        Assert.Equal(AutoPowerOffState.Off, meter.ReadAutoPowerOff().State);
        Assert.Null(AutoPowerOffAdvice.For(new SimulatedDriver().AutoPowerOff, meter.ReadAutoPowerOff()));

        meter.SetAutoPowerOff(TimeSpan.FromMinutes(10));
        Assert.Equal(AutoPowerOffSetting.On(TimeSpan.FromMinutes(10)), meter.ReadAutoPowerOff());
        Assert.Equal(AutoPowerOffWarningKind.On, AutoPowerOffAdvice.For(new SimulatedDriver().AutoPowerOff, meter.ReadAutoPowerOff())!.Kind);
    }

    [Fact]
    public void TheSimulatedMeterSwitchesOffAndWakesAgain()
    {
        var meter = new SimulatedMeter(new SimulatedOven(2), 1) { AutoPowerOffAfter = TimeSpan.FromMinutes(1), WakesAfter = TimeSpan.FromMinutes(2) };
        // Like the real thing, it switches off again a minute after each wake: on 60 s, off 120 s, on 60 s, ...
        var pattern = new List<(bool On, int Seconds)>();
        for (var i = 0; i < 400; i++)
        {
            bool on;
            try { meter.Read(); on = true; }
            catch (TimeoutException) { on = false; }
            if (pattern.Count > 0 && pattern[^1].On == on) pattern[^1] = (on, pattern[^1].Seconds + 1);
            else pattern.Add((on, 1));
        }
        Assert.True(pattern[0].On);
        Assert.InRange(pattern[0].Seconds, 59, 60);
        Assert.False(pattern[1].On);
        Assert.InRange(pattern[1].Seconds, 119, 120);
        Assert.True(pattern[2].On);
        Assert.InRange(pattern[2].Seconds, 59, 61);
    }

    [Fact]
    public void TheWatchSaysWhenTheMeterGoesQuietAndWhenItsBack()
    {
        var w = new SilenceWatch(TimeSpan.FromSeconds(30));
        Assert.Equal(SilenceChange.None, w.Update(TimeSpan.FromSeconds(1)));
        Assert.Equal(SilenceChange.None, w.Update(TimeSpan.FromSeconds(29)));
        Assert.Equal(SilenceChange.WentQuiet, w.Update(TimeSpan.FromSeconds(31)));
        Assert.True(w.Silent);
        Assert.Equal(SilenceChange.None, w.Update(TimeSpan.FromSeconds(90)));
        Assert.Equal(SilenceChange.CameBack, w.Update(TimeSpan.FromSeconds(1)));
        Assert.False(w.Silent);
    }

    [Fact]
    public void TheAlertTalksAboutAutoPowerOffOnlyForMetersThatHaveIt()
    {
        var withApo = SilenceWatch.Message(TimeSpan.FromSeconds(45), TenMinutes);
        Assert.Contains("No reading for 45 s", withApo);
        Assert.Contains("auto power-off", withApo);
        Assert.Contains("press any button", withApo);

        var without = SilenceWatch.Message(TimeSpan.FromMinutes(3), null);
        Assert.Contains("No reading for 3 min", without);
        Assert.DoesNotContain("auto power-off", without);
    }

    [Fact]
    public void ThePollerKeepsAskingWhileTheMeterIsOffAndCarriesOnWhenItWakes()
    {
        // At 600× real time, the simulated meter goes off after a minute and is woken 90 s later.
        var meter = new SimulatedMeter(new SimulatedOven(3), 1) { AutoPowerOffAfter = TimeSpan.FromMinutes(1), WakesAfter = TimeSpan.FromSeconds(90) };
        using var poller = new MeterPoller(meter, TimeSpan.FromSeconds(1), speed: 600, connected: true);
        var watch = new SilenceWatch();
        var changes = new List<SilenceChange>();
        poller.Start();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var readings = 0;
        while (DateTime.UtcNow < deadline && readings < 200)
        {
            while (poller.Readings.TryRead(out _)) readings++;
            if (watch.Update(poller.SilentFor) is var c and not SilenceChange.None) changes.Add(c);
            Thread.Sleep(5);
        }
        poller.Stop();

        // It goes quiet, comes back, and (as the meter switches off again a minute after each wake) repeats.
        Assert.True(changes.Count >= 2, $"changes: {string.Join(", ", changes)}");
        for (var i = 0; i < changes.Count; i++)
            Assert.Equal(i % 2 == 0 ? SilenceChange.WentQuiet : SilenceChange.CameBack, changes[i]);
        Assert.True(readings >= 200, $"only {readings} readings");
        Assert.Equal(0, poller.Reconnects); // 90 s quiet isn't long enough to suspect the link (2 min is)
    }

    [Fact]
    public void TheReportMentionsALongGapInASetpointsReadings()
    {
        var a = new SetpointAnalyzer(180);
        for (var t = 0; t < 600; t++) a.Add(t, 180 + Math.Sin(t / 30.0));
        for (var t = 900; t < 1500; t++) a.Add(t, 180 + Math.Sin(t / 30.0)); // five minutes with no readings
        var m = a.Metrics();
        Assert.Equal(301, m.LongestGapSeconds!.Value, 6);

        var plan = new[] { new TestPoint(1, OvenMode.Bake, 180) };
        var info = new SessionInfo("test", DateTimeOffset.Now, "sim", "sim", TempUnit.Fahrenheit, 8, new AnalysisOptions(), plan);
        var md = new Report(info, [new PointResult(plan[0], m)]).Markdown();
        Assert.Contains("5 min gap in its readings", md);
    }
}
