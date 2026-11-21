using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

public class RtdTests
{
    // IEC 60751 PT100 table values (Ω at °C).
    [Theory]
    [InlineData(0, 100.00)]
    [InlineData(100, 138.51)]
    [InlineData(200, 175.86)]
    [InlineData(260, 197.71)]
    [InlineData(300, 212.05)]
    [InlineData(-50, 80.31)]
    [InlineData(-100, 60.26)]
    public void MatchesTheIecTable(double c, double ohms)
    {
        Assert.Equal(ohms, Rtd.Pt100A.ResistanceAt(c), 2);
        Assert.Equal(c, Rtd.Pt100A.TemperatureFor(ohms), 1); // the table is rounded to 0.01 Ω, about 0.03 °C
    }

    [Theory]
    [InlineData(-150.0)]
    [InlineData(-12.5)]
    [InlineData(0.0)]
    [InlineData(176.67)]
    [InlineData(232.22)]
    [InlineData(500.0)]
    public void ConvertsBothWays(double c)
    {
        Assert.Equal(c, Rtd.Pt100A.TemperatureFor(Rtd.Pt100A.ResistanceAt(c)), 6);
        var pt1000 = new Rtd(1000, RtdClass.B);
        Assert.Equal(c, pt1000.TemperatureFor(pt1000.ResistanceAt(c)), 6);
    }

    [Fact]
    public void ClassesGiveTheirTolerance()
    {
        Assert.Equal(0.15 + 0.002 * 200, Rtd.Pt100A.Sensor.ToleranceC(200), 9);
        Assert.Equal(0.3 + 0.005 * 200, Rtd.Parse("pt100-b")!.Sensor.ToleranceC(200), 9);
        Assert.Equal("PT100 RTD (class A)", Rtd.Pt100A.Sensor.Name);
        Assert.False(Rtd.Pt100A.Sensor.IsGenericType);
    }

    [Theory]
    [InlineData("pt100", 100, RtdClass.A)]
    [InlineData("PT100-B", 100, RtdClass.B)]
    [InlineData("pt1000-aa", 1000, RtdClass.AA)]
    [InlineData(" pt500-a ", 500, RtdClass.A)]
    public void Parses(string text, double r0, RtdClass cls) => Assert.Equal(new Rtd(r0, cls), Rtd.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("pt200")]
    [InlineData("pt100-c")]
    [InlineData("type-k")]
    public void RejectsOthers(string text) => Assert.Null(Rtd.Parse(text));
}

public class Hp34401ATests
{
    /// <summary>A 34401A that answers from a table, and remembers what it was sent.</summary>
    sealed class FakeScpi(bool serial = false) : IScpiPort
    {
        public List<string> Sent { get; } = [];
        public Dictionary<string, Func<string>> Replies { get; } = new()
        {
            ["*IDN?"] = () => "HEWLETT-PACKARD,34401A,0,11-5-2",
            ["*OPC?"] = () => "1",
            ["SYST:ERR?"] = () => "+0,\"No error\"",
            ["CONF?"] = () => "\"FRES +1.000000E+03,+1.000000E-04\"",
            ["READ?"] = () => "+1.38510000E+02",
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
    public void SetsUpFourWireOhmsAndReadsATemperature()
    {
        var port = new FakeScpi();
        using var meter = new Hp34401AMeter(port, Rtd.Pt100A);
        var id = meter.Connect();
        Assert.Equal("34401A", id.Model);
        Assert.Equal("HEWLETT-PACKARD", id.Manufacturer);
        Assert.Contains("*RST", port.Sent);
        Assert.Contains("CONF:FRES 1E+3", port.Sent);
        Assert.Contains("FRES:NPLC 10", port.Sent);
        Assert.DoesNotContain("SYST:REM", port.Sent); // GPIB doesn't need it

        var r = meter.Read();
        Assert.Equal(ReadingStatus.Ok, r.Status);
        Assert.Equal(100, r.Celsius!.Value, 1);
        Assert.Equal("+1.38510000E+02", r.Raw);
        Assert.True(meter.LastConfig!.IsTemperature);
        Assert.Equal(Rtd.Pt100A.Sensor, meter.LastConfig.Sensor);

        meter.Disconnect();
        Assert.True(port.Released);
    }

    [Fact]
    public void PutsItInRemoteFirstOverRs232()
    {
        var port = new FakeScpi(serial: true);
        using var meter = new Hp34401AMeter(port, Rtd.Pt100A);
        meter.Connect();
        Assert.Equal("SYST:REM", port.Sent[0]);
    }

    [Fact]
    public void LeavesAnotherInstrumentAlone()
    {
        var port = new FakeScpi();
        port.Replies["*IDN?"] = () => "HEWLETT-PACKARD,3478A,0,1";
        using var meter = new Hp34401AMeter(port, Rtd.Pt100A);
        Assert.False(Hp34401AMeter.Is34401A(meter.Connect()));
        Assert.Equal(["*IDN?"], port.Sent);
    }

    [Fact]
    public void ARejectedSetupIsAnError()
    {
        var port = new FakeScpi();
        port.Replies["SYST:ERR?"] = () => "-113,\"Undefined header\"";
        using var meter = new Hp34401AMeter(port, Rtd.Pt100A);
        var ex = Assert.Throws<MeterException>(() => meter.Connect());
        Assert.Contains("Undefined header", ex.Message);
    }

    [Fact]
    public void AnOpenRtdIsAnOverloadAndAShortAnError()
    {
        var t = DateTimeOffset.UnixEpoch;
        Assert.Equal(ReadingStatus.Overload, Hp34401AMeter.ToReading(t, "+9.90000000E+37", Rtd.Pt100A).Status);
        Assert.Equal(ReadingStatus.Error, Hp34401AMeter.ToReading(t, "+1.20000000E+00", Rtd.Pt100A).Status);
        Assert.Equal(ReadingStatus.Error, Hp34401AMeter.ToReading(t, "garbage", Rtd.Pt100A).Status);
    }

    [Fact]
    public void NoticesWhenSomeoneChangesTheFunction()
    {
        var port = new FakeScpi();
        using var meter = new Hp34401AMeter(port, Rtd.Pt100A) { ConfigEvery = 2 };
        meter.Connect();
        port.Replies["CONF?"] = () => "\"VOLT +1.000000E+01,+3.000000E-06\"";
        meter.Read();
        var r = meter.Read();
        Assert.Equal(ReadingStatus.NotTemperature, r.Status);
        Assert.False(meter.LastConfig!.IsTemperature);
    }

    [Fact]
    public void ChoosesARangeForTheRtd()
    {
        Assert.Equal(1e3, new Hp34401AMeter(new FakeScpi(), Rtd.Pt100A).Range);
        Assert.Equal(1e4, new Hp34401AMeter(new FakeScpi(), new Rtd(1000, RtdClass.A)).Range);
    }

    [Fact]
    public void IsABenchMeter()
    {
        var driver = MeterDrivers.Find("34401a")!;
        Assert.IsType<Hp34401ADriver>(driver);
        Assert.Null(driver.AutoPowerOff);
        using var meter = new Hp34401AMeter(new FakeScpi(), Rtd.Pt100A);
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

    [Fact]
    public void ChecksItsOptions()
    {
        var d = new Hp34401ADriver();
        Assert.Null(d.CheckOptions(new Dictionary<string, string> { ["rtd"] = "pt100-b", ["nplc"] = "100" }));
        Assert.Contains("rtd=pt42", d.CheckOptions(new Dictionary<string, string> { ["rtd"] = "pt42" }));
        Assert.Contains("nplc=3", d.CheckOptions(new Dictionary<string, string> { ["nplc"] = "3" }));
        Assert.Contains("no --meter-option range", d.CheckOptions(new Dictionary<string, string> { ["range"] = "1k" }));
        Assert.Contains("takes no --meter-option", MeterDrivers.Find("u1253b")!.CheckOptions(new Dictionary<string, string> { ["rtd"] = "pt100" }));
    }

    [Fact]
    public void ParsesMeterOptions()
    {
        var o = MeterDrivers.ParseOptions(["RTD=pt100-b", "nplc=100,x=1"])!;
        Assert.Equal("pt100-b", o["rtd"]);
        Assert.Equal("100", o["nplc"]);
        Assert.Equal("1", o["x"]);
        Assert.Empty(MeterDrivers.ParseOptions(null)!);
        Assert.Null(MeterDrivers.ParseOptions(["rtd"]));
        Assert.Null(MeterDrivers.ParseOptions(["=1"]));
    }

    [Fact]
    public void RemembersOptionsPerMeter()
    {
        var s = new AppSettings();
        s.RememberMeterOptions("34401a", new Dictionary<string, string> { ["rtd"] = "pt100-b" });
        Assert.Equal("pt100-b", s.MeterOptionsFor("34401a")["rtd"]);
        Assert.Empty(s.MeterOptionsFor("u1253b"));
        s.RememberMeterOptions("34401a", new Dictionary<string, string>());
        Assert.Empty(s.MeterOptionsFor("34401a"));
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
