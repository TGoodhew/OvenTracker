using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;
using OvenTracker.Ui;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace OvenTracker.Tests;

/// <summary>Pausing a setpoint for a battery swap, and gaps in the readings (#51).</summary>
[Collection(nameof(ConsoleCollection))]
public class BatterySwapTests
{
    const double Setpoint = 176.67;

    static double Cycle(double t, double lo, double hi, double period)
    {
        var x = t % period / period;
        return x < 0.5 ? lo + (hi - lo) * 2 * x : hi - (hi - lo) * 2 * (x - 0.5);
    }

    /// <summary>An oven at temperature cycling 172–185 °C every 12 minutes, with readings missing between gapFrom and gapTo (s).</summary>
    static SetpointAnalyzer Run(double gapFrom, double gapTo, double seconds = 5 * 3600)
    {
        var a = new SetpointAnalyzer(Setpoint, new AnalysisOptions { PrecisionC = 0.2 });
        for (var t = 0.0; t <= seconds && a.Phase != PointPhase.Complete; t += 1)
        {
            if (t > gapFrom && t < gapTo) continue;
            a.Add(t, 172 + 13 * Cycle(t + 360, 0, 1, 720)); // starts at a peak
        }
        return a;
    }

    [Fact]
    public void AGapDropsTheCycleItBrokeAndKeepsTheRest()
    {
        var whole = Run(double.PositiveInfinity, double.PositiveInfinity);
        var gapped = Run(3000, 3300); // five minutes off, mid-cycle, after a few cycles
        var gm = gapped.Metrics();
        // The same oven: the same mean, from cycles either side of the gap, none of them across it.
        Assert.Equal(whole.Metrics().MeanC!.Value, gm.MeanC!.Value, 1);
        Assert.All(gapped.Units, u => Assert.False(u.Start < 3300 && u.End > 3000, $"a cycle {u.Start}–{u.End} crosses the gap"));
        Assert.Contains(gapped.Units, u => u.End <= 3000);
        Assert.Contains(gapped.Units, u => u.Start >= 3300);
        Assert.Equal(300, gm.LongestGapSeconds!.Value, 0);
    }

    [Fact]
    public void AShortDropoutDoesntBreakACycle()
    {
        var whole = Run(double.PositiveInfinity, double.PositiveInfinity);
        var dropped = Run(3000, 3090); // 90 s: a Bluetooth link coming back
        Assert.Equal(whole.Units.Count(u => u.End < 3600), dropped.Units.Count(u => u.End < 3600));
    }

    [Fact]
    public void TheTimeLimitDoesntCountTheGap()
    {
        // A one-hour limit, with a 40-minute gap: still measuring at 90 minutes, as only 50 of them had readings.
        var a = new SetpointAnalyzer(Setpoint, new AnalysisOptions { MaxPointMinutes = 60, PrecisionC = 0.001 });
        for (var t = 0.0; t <= 90 * 60; t += 1)
        {
            if (t > 20 * 60 && t < 60 * 60) continue;
            a.Add(t, 172 + 13 * Cycle(t + 360, 0, 1, 720));
        }
        Assert.NotEqual(PointPhase.Complete, a.Phase);
    }

    static async Task<(int Code, string Output)> RunApp(params string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var before = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try { return (await new CommandApp<RunCommand>().RunAsync(args), console.Output); }
        finally { AnsiConsole.Console = before; }
    }

    [Fact]
    public async Task ASessionPausesForABatterySwapAndCarriesOnWithTheSameSetpoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OvenTrackerSwap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (code, output) = await RunApp("--simulate", "--speed", "3000", "-y", "--unit", "F", "--modes", "bake", "--setpoints", "350",
                "--sim-battery", "40", "--out", dir);
            Assert.Equal(0, code);
            Assert.Contains("PAUSED for a battery swap", output);
            Assert.Contains("Carrying on with this setpoint", output);
            var lines = File.ReadAllLines(Path.Combine(dir, "readings.csv"));
            var events = lines.Where(l => l.Contains(",Event,")).Select(l => l.Split(',')[8]).ToList();
            Assert.Equal(["start", "pause:LowBattery"], events.Take(2));
            Assert.Contains("pause-end", events);
            Assert.Equal(1, events.Count(e => e.StartsWith("end:", StringComparison.Ordinal))); // one setpoint, not restarted
            // The readings while paused are outside the setpoint, so replay leaves them out.
            var pausedAt = Array.FindIndex(lines, l => l.EndsWith("pause:LowBattery"));
            var endedAt = Array.FindIndex(lines, l => l.EndsWith("pause-end"));
            Assert.All(lines[(pausedAt + 1)..endedAt].Where(l => !l.Contains(",Event,")), l => Assert.Equal("0", l.Split(',')[1]));
            var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "results.json"))).RootElement.GetProperty("results")[0].GetProperty("metrics");
            Assert.Equal("Converged", result.GetProperty("reason").GetString());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ThePauseSaysWhatToDo()
    {
        var f = new UnitFormatter(TempUnit.Fahrenheit);
        var battery = PointTracker.PauseMessage(PauseReason.LowBattery, true, null, f, canReadKeys: true);
        Assert.Contains("fresh battery", battery);
        Assert.Contains("cycles so far are kept", battery);
        Assert.Contains("C carries on without a swap", battery);
        var mainsJump = PointTracker.PauseMessage(PauseReason.ImpossibleReading, false, -50, f, canReadKeys: true);
        Assert.Contains("−90.0 °F", mainsJump);
        Assert.Contains("Check the probe", mainsJump);
        Assert.DoesNotContain("battery", mainsJump);
        Assert.Contains("battery", PointTracker.PauseMessage(PauseReason.ImpossibleReading, true, -50, f, canReadKeys: true));
        Assert.Equal(10, PointTracker.MaxRateCPerSecond);
    }

    [Theory]
    [InlineData(41.0, true)]
    [InlineData(59.0, true)]
    [InlineData(85.0, false)]
    public void ALowishBatteryIsMentionedBeforeARun(double percent, bool mentioned) =>
        Assert.Equal(mentioned, RunCommand.BatteryNote(percent) is not null);

    [Fact]
    public void OnlyBatteryMetersHaveABattery()
    {
        Assert.True(MeterDrivers.Find("u1253b")!.HasBattery);
        Assert.True(MeterDrivers.Find("121gw")!.HasBattery);
        Assert.False(MeterDrivers.Find("34401a")!.HasBattery); // mains powered
        Assert.Null(RunCommand.BatteryNote(null));
    }

    sealed class FlagMeter(bool? low, double? percent) : ITemperatureMeter
    {
        public string Location => "flag";
        public MeterIdentity Connect() => new("Test", "Flag", "1", "1");
        public void Disconnect() { }
        public MeterReading Read() => new(DateTimeOffset.UtcNow, ReadingStatus.Ok, 180, "180");
        public double? ReadBatteryPercent() => percent;
        public bool? ReadLowBattery() => low;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true, null, true)]     // the 121GW's flag
    [InlineData(false, 5.0, false)]    // its flag wins over a level
    [InlineData(null, 15.0, true)]     // the U1253B's level
    [InlineData(null, 60.0, false)]
    [InlineData(null, null, null)]     // a bench meter says nothing
    public void ThePollerKnowsWhenTheBatteryIsLow(bool? flag, double? percent, bool? low)
    {
        var poller = new MeterPoller(new FlagMeter(flag, percent), TimeSpan.FromSeconds(1), speed: 100, connected: true);
        poller.Start();
        for (var i = 0; i < 200 && poller.BatteryReads == 0; i++) Thread.Sleep(5);
        poller.Stop();
        Assert.Equal(low, poller.LowBattery);
    }
}
