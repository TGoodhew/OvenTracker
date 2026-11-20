using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

public class MeterDriverTests
{
    /// <summary>
    /// The least a meter can be: no battery, no beep, and no way to say what it is set to. It reads a
    /// simulated oven, so a whole setpoint can be measured with it.
    /// </summary>
    sealed class BareMeter(SimulatedOven oven) : ITemperatureMeter
    {
        DateTimeOffset clock = DateTimeOffset.UnixEpoch;
        public int Connects { get; private set; }
        public string Location => "nowhere";
        public MeterIdentity Connect() { Connects++; return new("Test", "Bare", "1", "1"); }
        public void Disconnect() { }

        public MeterReading Read()
        {
            oven.Step(1);
            clock = clock.AddSeconds(1);
            return new MeterReading(clock, ReadingStatus.Ok, oven.ProbeC(), "");
        }

        public void Dispose() { }
    }

    [Fact]
    public void AMeterWithOnlyTheRequiredMembersMeasuresASetpoint()
    {
        var oven = new SimulatedOven(5);
        oven.Set(OvenMode.Bake, 180);
        ITemperatureMeter meter = new BareMeter(oven);
        Assert.Null(meter.ReadConfig());
        Assert.Null(meter.LastConfig);
        Assert.Null(meter.ReadBatteryPercent());
        meter.Beep(); // does nothing, and doesn't throw

        using var poller = new MeterPoller(meter, TimeSpan.FromSeconds(1), speed: 20_000, connected: false) { BatteryEvery = 1 };
        poller.Enqueue(m => m.Beep());
        poller.Start();
        var analyzer = new SetpointAnalyzer(180);
        var start = DateTimeOffset.MinValue;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (analyzer.Phase != PointPhase.Complete && DateTime.UtcNow < deadline)
        {
            if (!poller.Readings.TryRead(out var r)) { Thread.Sleep(1); continue; }
            if (start == DateTimeOffset.MinValue) start = r.Time;
            analyzer.Add((r.Time - start).TotalSeconds, r.Celsius!.Value);
        }
        poller.Stop();

        Assert.Equal(1, ((BareMeter)meter).Connects);
        Assert.Null(poller.BatteryPercent);
        Assert.Null(poller.Config);
        Assert.Equal(CompletionReason.Converged, analyzer.Reason);
        Assert.InRange(analyzer.Metrics().OffsetC!.Value, SimulatedOven.BiasC(OvenMode.Bake, 180) - 3, SimulatedOven.BiasC(OvenMode.Bake, 180) + 3);
    }

    [Theory]
    [InlineData("u1253b", typeof(U1253BDriver))]
    [InlineData("U1253B", typeof(U1253BDriver))]
    [InlineData("34401A", typeof(Hp34401ADriver))]
    [InlineData("simulated", typeof(SimulatedDriver))]
    public void DriversAreFoundByName(string name, Type type) => Assert.IsType(type, MeterDrivers.Find(name));

    [Fact]
    public void AnUnknownDriverIsNotFound()
    {
        Assert.Null(MeterDrivers.Find("fluke87"));
        Assert.Null(MeterDrivers.Find(null));
        Assert.Contains(MeterDrivers.All, d => d.Name == MeterDrivers.DefaultName);
    }

    [Fact]
    public void TheDriverIsTheOneAskedForThenTheLastOneThenTheDefault()
    {
        var none = new AppSettings();
        Assert.Equal(MeterDrivers.DefaultName, RunCommand.ChooseDriver(new RunSettings(), none).Name);
        Assert.Equal(SimulatedDriver.DriverName, RunCommand.ChooseDriver(new RunSettings { Simulate = true }, none).Name);
        Assert.Equal(SimulatedDriver.DriverName, RunCommand.ChooseDriver(new RunSettings { Meter = "simulated" }, none).Name);

        // A simulation last time doesn't make the next run a simulation.
        var simulatedLast = new AppSettings();
        simulatedLast.Remember(SimulatedDriver.DriverName, "simulator");
        Assert.Equal(MeterDrivers.DefaultName, RunCommand.ChooseDriver(new RunSettings(), simulatedLast).Name);
    }

    [Fact]
    public void SettingsFromBeforeDriversStillFindTheSerialPort()
    {
        var old = new AppSettings { LastPort = "COM7" };
        Assert.Equal("COM7", old.LastResourceFor("u1253b"));
        Assert.Null(old.LastResourceFor("simulated"));

        old.Remember("u1253b", "COM9");
        Assert.Equal("COM9", old.LastResourceFor("u1253b"));
        Assert.Null(old.LastPort);
    }

    [Fact]
    public void TheSimulatorIsADriverOfItsOwn()
    {
        var driver = MeterDrivers.Find("simulated")!;
        Assert.True(driver.IsSimulation);
        var r = driver.Open(new MeterRequest(null, null, 1))!;
        using var meter = r.Meter!;
        Assert.IsAssignableFrom<IOvenSimulation>(meter);
        Assert.Equal("type K thermocouple", meter.ReadConfig()?.Sensor?.Name);
        Assert.Equal(ReadingStatus.Ok, meter.Read().Status);
    }

    [Theory]
    [InlineData("K", 232.2, 2.2)]   // 0.75 % of 232 °C is 1.74 °C, so the fixed 2.2 °C applies
    [InlineData("K", 400, 3.0)]     // 0.75 % of 400 °C
    [InlineData("T", 100, 1.0)]
    [InlineData("E", 400, 2.0)]
    public void SensorTolerancesFollowTheStandardLimits(string type, double c, double expected) =>
        Assert.Equal(expected, Sensor.Thermocouple(type)!.ToleranceC(c), 3);

    [Fact]
    public void TheReportDescribesTheSensorTheMeterReported()
    {
        var plan = RunCommand.BuildPlan([OvenMode.Bake], [180], false);
        var info = new SessionInfo("test", DateTimeOffset.Now, "Test Bare", "nowhere", TempUnit.Fahrenheit, 8, new AnalysisOptions(), plan);

        var unknown = new Report(info, []).SensorAccuracy(180);
        Assert.Contains("data sheets", unknown);
        Assert.DoesNotContain("type K", unknown);

        var typeT = new Report(info with { Sensor = Sensor.Thermocouple("T") }, []).SensorAccuracy(180);
        Assert.Contains("type T thermocouple", typeT);
        Assert.Contains("1.8 °F", typeT); // ±1.0 °C
    }

    [Fact]
    public void ASessionSavedBeforeDriversStillLoads()
    {
        const string json = """
            {"appVersion":"1.0.0","started":"2026-11-20T11:17:46-08:00","meter":"Agilent Technologies U1253B","location":"COM5",
             "unit":"Fahrenheit","toleranceC":8.33,"options":{},"plan":[]}
            """;
        var info = System.Text.Json.JsonSerializer.Deserialize<SessionInfo>(json, Json.Options)!;
        Assert.Equal("COM5", info.Location);
        Assert.Null(info.Driver);
        Assert.Null(info.Sensor);
    }
}
