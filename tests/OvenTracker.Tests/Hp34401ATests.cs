using System.Globalization;
using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

public class TypeKTests
{
    // NIST ITS-90 type K reference table, reference junction at 0 °C (mV at °C).
    [Theory]
    [InlineData(-200, -5.891)]
    [InlineData(-50, -1.889)]
    [InlineData(0, 0.000)]
    [InlineData(25, 1.000)]
    [InlineData(100, 4.096)]
    [InlineData(200, 8.138)]
    [InlineData(260, 10.561)]
    [InlineData(300, 12.209)]
    [InlineData(500, 20.644)]
    [InlineData(1000, 41.276)]
    [InlineData(1372, 54.886)]
    public void MatchesTheNistTable(double c, double mv)
    {
        Assert.Equal(mv, TypeK.MillivoltsAt(c), 3);
        Assert.Equal(c, TypeK.TemperatureFor(mv, 0)!.Value, 1); // the table is rounded to 1 µV, about 0.03 °C
    }

    [Theory]
    [InlineData(176.67, 23.0)]
    [InlineData(232.22, 18.5)]
    [InlineData(-10.0, 22.0)]
    [InlineData(500.0, 0.0)]
    public void TheColdJunctionIsAddedBack(double hot, double coldJunction)
    {
        // The meter sees only the difference between the two junctions.
        var measured = TypeK.MillivoltsAt(hot) - TypeK.MillivoltsAt(coldJunction);
        Assert.Equal(hot, TypeK.TemperatureFor(measured, coldJunction)!.Value, 6);
    }

    [Fact]
    public void AColdJunctionErrorShowsInTheReading()
    {
        // Assuming 23 °C when the room is 28 °C reads about 5 °C low: the reason cj matters.
        var measured = TypeK.MillivoltsAt(200) - TypeK.MillivoltsAt(28);
        Assert.Equal(195, TypeK.TemperatureFor(measured, 23)!.Value, 0);
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(-8.0)] // with the cold junction at 23 °C, below −270 °C
    public void BeyondTypeKIsNoTemperature(double mv) => Assert.Null(TypeK.TemperatureFor(mv, 23));
}

public class Hp34401ATests
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The 34401A's reply at a temperature, with the cold junction at another: volts, as READ? gives them.</summary>
    static string VoltsAt(double hotC, double coldJunctionC) =>
        ((TypeK.MillivoltsAt(hotC) - TypeK.MillivoltsAt(coldJunctionC)) / 1000).ToString("+0.00000000E+00;-0.00000000E+00", Inv);

    /// <summary>A 34401A that answers from a table, and remembers what it was sent.</summary>
    sealed class FakeScpi(bool serial = false) : IScpiPort
    {
        public List<string> Sent { get; } = [];
        public Dictionary<string, Func<string>> Replies { get; } = new()
        {
            ["*IDN?"] = () => "HEWLETT-PACKARD,34401A,0,11-5-2",
            ["*OPC?"] = () => "1",
            ["SYST:ERR?"] = () => "+0,\"No error\"",
            ["CONF?"] = () => "\"VOLT +1.000000E-01,+3.000000E-07\"",
            ["READ?"] = () => VoltsAt(200, 23),
        };
        readonly Queue<string> pending = new();
        public bool Released { get; private set; }

        public string Location => serial ? "COM3:9600,E,7,2" : "GPIB0::22::INSTR";
        public bool IsSerial => serial;
        public void Open() { }

        public void WriteLine(string command)
        {
            Sent.Add(command);
            if (Replies.TryGetValue(command, out var r)) pending.Enqueue(r());
        }

        public string ReadLine() => pending.Count > 0 ? pending.Dequeue() : throw new TimeoutException();
        public void Clear() => pending.Clear();
        public void Release() => Released = true;
        public void Dispose() { }
    }

    [Fact]
    public void SetsUpDcMillivoltsAndReadsATypeKTemperature()
    {
        var port = new FakeScpi();
        using var meter = new Hp34401AMeter(port, coldJunctionC: 23);
        var id = meter.Connect();
        Assert.Equal("34401A", id.Model);
        Assert.Equal("HEWLETT-PACKARD", id.Manufacturer);
        Assert.Contains("*RST", port.Sent);
        Assert.Contains("CONF:VOLT:DC 0.1", port.Sent);
        Assert.Contains("VOLT:DC:NPLC 10", port.Sent);
        Assert.Contains("INP:IMP:AUTO ON", port.Sent); // > 10 GΩ, so the thermocouple isn't loaded
        Assert.DoesNotContain("SYST:REM", port.Sent); // GPIB doesn't need it

        var r = meter.Read();
        Assert.Equal(ReadingStatus.Ok, r.Status);
        Assert.Equal(200, r.Celsius!.Value, 2);
        Assert.Equal(VoltsAt(200, 23), r.Raw);
        Assert.True(meter.LastConfig!.IsTemperature);
        // A generic type K: --probe says which probe, as for any meter.
        Assert.Equal(Sensor.Thermocouple("K"), meter.LastConfig.Sensor);
        Assert.True(meter.LastConfig.Sensor!.IsGenericType);
        Assert.Contains("cold junction 23.0 °C", meter.LastConfig.Description);

        meter.Disconnect();
        Assert.True(port.Released);
    }

    [Fact]
    public void PutsItInRemoteFirstOverRs232()
    {
        var port = new FakeScpi(serial: true);
        using var meter = new Hp34401AMeter(port, 23);
        meter.Connect();
        Assert.Equal("SYST:REM", port.Sent[0]);
    }

    [Fact]
    public void LeavesAnotherInstrumentAlone()
    {
        var port = new FakeScpi();
        port.Replies["*IDN?"] = () => "HEWLETT-PACKARD,3478A,0,1";
        using var meter = new Hp34401AMeter(port, 23);
        Assert.False(Hp34401AMeter.Is34401A(meter.Connect()));
        Assert.Equal(["*IDN?"], port.Sent);
        Assert.True(port.Released); // given its front panel back
    }

    [Fact]
    public void ARejectedSetupIsAnError()
    {
        var port = new FakeScpi();
        port.Replies["SYST:ERR?"] = () => "-113,\"Undefined header\"";
        using var meter = new Hp34401AMeter(port, 23);
        var ex = Assert.Throws<MeterException>(() => meter.Connect());
        Assert.Contains("Undefined header", ex.Message);
    }

    [Fact]
    public void AnOverloadIsOpenAndBeyondTypeKIsAnError()
    {
        var t = DateTimeOffset.UnixEpoch;
        Assert.Equal(ReadingStatus.Overload, Hp34401AMeter.ToReading(t, "+9.90000000E+37", 23).Status);
        Assert.Equal(ReadingStatus.Error, Hp34401AMeter.ToReading(t, "+6.00000000E-02", 23).Status); // 60 mV
        Assert.Equal(ReadingStatus.Error, Hp34401AMeter.ToReading(t, "garbage", 23).Status);
        Assert.Equal(ReadingStatus.Error, Hp34401AMeter.ToReading(t, "NaN", 23).Status); // not a temperature, even though it parses
    }

    [Theory]
    [InlineData("\"VOLT:AC +1.000000E+01,+3.000000E-06\"")]
    [InlineData("\"FRES +1.000000E+03,+1.000000E-04\"")]
    public void NoticesWhenSomeoneChangesTheFunction(string conf)
    {
        var port = new FakeScpi();
        using var meter = new Hp34401AMeter(port, 23) { ConfigEvery = 2 };
        meter.Connect();
        port.Replies["CONF?"] = () => conf;
        meter.Read();
        var r = meter.Read();
        Assert.Equal(ReadingStatus.NotTemperature, r.Status);
        Assert.False(meter.LastConfig!.IsTemperature);
    }

    [Fact]
    public void IsABenchMeter()
    {
        var driver = MeterDrivers.Find("34401a")!;
        Assert.IsType<Hp34401ADriver>(driver);
        Assert.Null(driver.AutoPowerOff);
        using var meter = new Hp34401AMeter(new FakeScpi(), 23);
        Assert.Null(((ITemperatureMeter)meter).ReadBatteryPercent());
    }

    [Fact]
    public void OnlyGpibAndSerialAreItsConnections()
    {
        Assert.Contains("USB, LAN or Bluetooth", Hp34401ADriver.Check("USB0::0x0957::0x0607::MY1::INSTR"));
        Assert.Null(Hp34401ADriver.Check("COM3"));
        Assert.Null(Hp34401ADriver.Check("COM3:9600,N,8,2"));
        if (!Visa.IsAvailable) Assert.Contains("VISA isn't installed", Hp34401ADriver.Check("GPIB0::22::INSTR"));
        else Assert.Null(Hp34401ADriver.Check("GPIB0::22::INSTR"));
    }

    [Fact]
    public void WithoutAnAddressItDoesntGoLooking()
    {
        Assert.Null(new Hp34401ADriver().Open(new MeterRequest(null, null, 1)));
    }

    [Theory]
    [InlineData("22C", 22.0)]
    [InlineData("22.5 °C", 22.5)]
    [InlineData("72F", 22.222222222222221)]
    [InlineData("0c", 0.0)]
    public void ReadsTheColdJunctionWithItsUnit(string text, double c) =>
        Assert.Equal(c, Hp34401ADriver.ParseTemperatureC(text)!.Value, 9);

    [Theory]
    [InlineData("22")]      // no unit: 22 °F or 22 °C?
    [InlineData("350F")]    // an oven, not a room
    [InlineData("warm")]
    public void RefusesAColdJunctionItCantBeSureOf(string text) => Assert.Null(Hp34401ADriver.ParseTemperatureC(text));

    [Fact]
    public void ChecksItsOptions()
    {
        var d = new Hp34401ADriver();
        Assert.Null(d.CheckOptions(new Dictionary<string, string> { ["cj"] = "22C", ["nplc"] = "100" }));
        Assert.Contains("cj=22", d.CheckOptions(new Dictionary<string, string> { ["cj"] = "22" }));
        Assert.Contains("nplc=3", d.CheckOptions(new Dictionary<string, string> { ["nplc"] = "3" }));
        // The PT100 setting is gone.
        Assert.Contains("no --meter-option rtd", d.CheckOptions(new Dictionary<string, string> { ["rtd"] = "pt100" }));
        Assert.Contains("takes no --meter-option", MeterDrivers.Find("u1253b")!.CheckOptions(new Dictionary<string, string> { ["cj"] = "22C" }));
        Assert.Equal(["cj", "nplc"], d.Options.Select(o => o.Key));
    }

    [Fact]
    public void SaysWhenTheColdJunctionIsAssumed()
    {
        Assert.Contains("23 °C", Hp34401ADriver.AssumedColdJunctionNote);
        Assert.Contains("cj=", Hp34401ADriver.AssumedColdJunctionNote);
    }

    [Fact]
    public void ParsesMeterOptions()
    {
        var o = MeterDrivers.ParseOptions(["CJ=22C", "nplc=100,x=1"])!;
        Assert.Equal("22C", o["cj"]);
        Assert.Equal("100", o["nplc"]);
        Assert.Equal("1", o["x"]);
        Assert.Empty(MeterDrivers.ParseOptions(null)!);
        Assert.Null(MeterDrivers.ParseOptions(["cj"]));
        Assert.Null(MeterDrivers.ParseOptions(["=1"]));
    }

    [Fact]
    public void RemembersOptionsPerMeter()
    {
        var s = new AppSettings();
        s.RememberMeterOptions("34401a", new Dictionary<string, string> { ["cj"] = "22C" });
        Assert.Equal("22C", s.MeterOptionsFor("34401a")["cj"]);
        Assert.Empty(s.MeterOptionsFor("u1253b"));
        s.RememberMeterOptions("34401a", new Dictionary<string, string>());
        Assert.Empty(s.MeterOptionsFor("34401a"));
    }

    [Fact]
    public void TheRunRecordsTheMeterOptionsItUsed()
    {
        // The cold junction moves every reading, so session.json says what it was; nothing is written without options.
        var info = new SessionInfo("test", DateTimeOffset.UnixEpoch, "HEWLETT-PACKARD 34401A", "GPIB0::22::INSTR", TempUnit.Fahrenheit, 8,
            new OvenTracker.Analysis.AnalysisOptions(), [], MeterOptions: new Dictionary<string, string> { ["cj"] = "22C" });
        var json = System.Text.Json.JsonSerializer.Serialize(info, Json.Options);
        Assert.Contains("\"meterOptions\": {", json);
        Assert.Contains("\"cj\": \"22C\"", json);
        Assert.DoesNotContain("meterOptions", System.Text.Json.JsonSerializer.Serialize(info with { MeterOptions = null }, Json.Options));
    }

    [Fact]
    public void ItsSerialDefaultsAreTheFactorys()
    {
        Assert.Equal("9600,E,7,2", Hp34401ADriver.FactorySerial.ToString());
        var port = Hp34401ADriver.PortFor(ResourceSpec.Parse("COM3")!, TimeSpan.FromSeconds(1));
        Assert.Equal("COM3:9600,E,7,2", port.Location);
        Assert.IsType<VisaScpiPort>(Hp34401ADriver.PortFor(ResourceSpec.Parse("gpib:22")!, TimeSpan.FromSeconds(1)));
    }
}
