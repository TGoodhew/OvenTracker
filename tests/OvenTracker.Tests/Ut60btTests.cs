using System.Text;
using OvenTracker.Meter;
using OvenTracker.Meter.Ble;

namespace OvenTracker.Tests;

public class Ut60btTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// <summary>A data frame with these fields and a correct checksum. The flag bytes carry 0x30, as real ones do.</summary>
    static byte[] Frame(int mode, string display, byte f14 = 0x30, byte f15 = 0x30, byte f16 = 0x30, char range = '0')
    {
        var f = new byte[19];
        f[0] = 0xAB; f[1] = 0xCD; f[2] = 0x10;
        f[3] = (byte)mode;
        f[4] = (byte)range;
        Encoding.ASCII.GetBytes(display.PadLeft(7)).CopyTo(f, 5);
        f[12] = 0x00; f[13] = 0x00;
        f[14] = f14; f[15] = f15; f[16] = f16;
        var sum = 0;
        for (var i = 0; i < 17; i++) sum += f[i];
        f[17] = (byte)(sum >> 8); f[18] = (byte)sum;
        return f;
    }

    [Fact]
    public void TheCommandsAreTheOnesTheSourcesSend()
    {
        Assert.Equal(Hex("AB CD 03 5F 01 DA"), Ut60btProtocol.GetName);
        Assert.Equal(Hex("AB CD 03 5D 01 D8"), Ut60btProtocol.GetData);
        Assert.Equal(Hex("AB CD 03 5E 01 D9"), Ut60btProtocol.Trigger);
    }

    [Fact]
    public void DecodesUnitUt61ePlusesExampleFrame()
    {
        // From unit_ut61eplus (ut61eplus.py): 53.54 mV AC, manual range.
        var f = new Ut60btFrame(Hex("ab cd 10 01 30 20 20 35 33 2e 35 34 01 00 30 34 30 03 8d"));
        Assert.Equal(1, f.Mode);
        Assert.Equal(0, f.Range);
        Assert.Equal(53.54, f.Value!.Value, 9);
        Assert.True(f.ManualRange);
        Assert.False(f.LowBattery);
        Assert.False(Ut60btProtocol.Config(f).IsTemperature);
        Assert.Equal("AC mV 53.54", Ut60btProtocol.Describe(f));
    }

    [Fact]
    public void ReadsTheNameReply()
    {
        // From libreble's notes: a UT60BT's answer to GetName.
        Assert.Equal("UT60BT", Ut60btProtocol.NameIn(Hex("AB CD 08 55 54 36 30 42 54 03 25")));
        Assert.Null(Ut60btProtocol.NameIn(Hex("AB CD 08 55 54 36 30 42 54 03 26")));
        Assert.Null(Ut60btProtocol.NameIn(Frame(10, "234")));
    }

    [Fact]
    public void RejectsABadChecksumOrAnImpossibleMode()
    {
        var good = Frame(10, "234");
        Assert.True(Ut60btProtocol.IsData(good));
        var bad = (byte[])good.Clone();
        bad[9] ^= 1;
        Assert.False(Ut60btProtocol.IsData(bad));
        Assert.False(Ut60btProtocol.IsData(Frame(31, "234")));
        Assert.False(Ut60btProtocol.IsData(Frame(10, "234", range: 'x')));
    }

    [Fact]
    public void ReadsTemperatureInCelsius()
    {
        var f = new Ut60btFrame(Frame(10, "234"));
        var r = Ut60btProtocol.ToReading(DateTimeOffset.UnixEpoch, f);
        Assert.Equal(ReadingStatus.Ok, r.Status);
        Assert.Equal(234.0, r.Celsius!.Value, 6);
        var config = Ut60btProtocol.Config(f);
        Assert.True(config.IsTemperature);
        Assert.Equal(TempUnit.Celsius, config.Unit);
        Assert.Equal("type K thermocouple", config.Sensor!.Name);
    }

    [Fact]
    public void ConvertsFahrenheit()
    {
        var f = new Ut60btFrame(Frame(11, "450"));
        Assert.Equal(TempUnit.Fahrenheit, Ut60btProtocol.Config(f).Unit);
        Assert.Equal((450 - 32) / 1.8, Ut60btProtocol.ToReading(DateTimeOffset.UnixEpoch, f).Celsius!.Value, 3);
    }

    [Fact]
    public void ReadsANegativeTemperature() =>
        Assert.Equal(-12.0, Ut60btProtocol.ToReading(DateTimeOffset.UnixEpoch, new Ut60btFrame(Frame(10, "-12"))).Celsius!.Value, 6);

    [Theory]
    [InlineData(" OL.  ")]
    [InlineData("O.L")]
    [InlineData("-OL")]
    public void AnOpenThermocoupleIsAnOverload(string display) =>
        Assert.Equal(ReadingStatus.Overload, Ut60btProtocol.ToReading(DateTimeOffset.UnixEpoch, new Ut60btFrame(Frame(10, display))).Status);

    [Theory]
    [InlineData("-  12", -12.0)]   // the sign in a column of its own
    [InlineData("23\0\0\0", 23.0)] // NUL padding
    [InlineData("- 1.5", -1.5)]
    public void ReadsTheDisplayWithoutItsSpaces(string display, double expected) =>
        Assert.Equal(expected, Ut60btProtocol.ToReading(DateTimeOffset.UnixEpoch, new Ut60btFrame(Frame(10, display))).Celsius!.Value, 6);

    [Fact]
    public void ADisplayThatIsntANumberIsAnError() =>
        Assert.Equal(ReadingStatus.Error, Ut60btProtocol.ToReading(DateTimeOffset.UnixEpoch, new Ut60btFrame(Frame(10, "----"))).Status);

    [Theory]
    [InlineData(2, 0x30, 0x30)] // DC volts
    [InlineData(10, 0x32, 0x30)] // HOLD
    [InlineData(10, 0x31, 0x30)] // REL
    [InlineData(10, 0x38, 0x30)] // MAX
    [InlineData(10, 0x30, 0x34)] // peak max
    public void OnlyLiveTemperatureCounts(int mode, byte f14, byte f16)
    {
        var f = new Ut60btFrame(Frame(mode, "234", f14: f14, f16: f16));
        Assert.False(Ut60btProtocol.Config(f).IsTemperature);
        Assert.Equal(ReadingStatus.NotTemperature, Ut60btProtocol.ToReading(DateTimeOffset.UnixEpoch, f).Status);
    }

    [Fact]
    public void SeesTheLowBatterySign()
    {
        Assert.True(new Ut60btFrame(Frame(10, "234", f15: 0x32)).LowBattery);
        Assert.False(new Ut60btFrame(Frame(10, "234", f15: 0x34)).LowBattery);
    }

    [Fact]
    public void ReassemblesFramesSplitAcrossNotifications()
    {
        var name = Hex("AB CD 08 55 54 36 30 42 54 03 25");
        var a = Frame(10, "200");
        var b = Frame(10, "201");
        var stream = new byte[] { 0x00, 0xAB, 0xCD, 0x7F }.Concat(name).Concat(a).Concat(b).ToArray(); // junk, with a stray header, first
        var asm = new Ut60btAssembler();
        var got = new List<byte[]>();
        foreach (var chunk in stream.Chunk(5)) got.AddRange(asm.Add(chunk));
        Assert.Equal(3, got.Count);
        Assert.Equal("UT60BT", Ut60btProtocol.NameIn(got[0]));
        Assert.Equal([200.0, 201.0], got.Skip(1).Select(f => new Ut60btFrame(f).Value!.Value));
    }

    [Fact]
    public void TakesTwoFramesInOneNotification() =>
        Assert.Equal(2, new Ut60btAssembler().Add(Frame(10, "200").Concat(Frame(10, "201")).ToArray()).Count);

    /// <summary>
    /// A meter on a fake link. It answers GetName with its name (unless <see cref="AnswersName"/> is off), and after
    /// one of <see cref="DataCommand"/> either streams (the test sends frames) or, with <see cref="OneFramePerRequest"/>,
    /// answers each request with <see cref="Next"/>.
    /// </summary>
    sealed class FakeMeterLink : IBleLink
    {
        Action<byte[]>? received;
        public bool Opened { get; private set; }
        public bool AnswersName { get; init; } = true;
        public byte[]? DataCommand { get; init; } = Ut60btProtocol.GetData;
        public bool OneFramePerRequest { get; init; }
        public bool Streaming { get; private set; }
        public Func<byte[]> Next { get; set; } = () => Frame(10, "180");
        public List<byte[]> Writes { get; } = [];
        public string Address => "C4:BE:84:12:34:56";
        public void Open(Action<byte[]> r) { received = r; Opened = true; Streaming = false; } // a fresh connection: not streaming yet

        public void Write(byte[] data)
        {
            lock (Writes) Writes.Add(data);
            if (data.SequenceEqual(Ut60btProtocol.GetName) && AnswersName) received!(Hex("AB CD 08 55 54 36 30 42 54 03 25"));
            else if (DataCommand is { } d && data.SequenceEqual(d))
            {
                if (OneFramePerRequest) received!(Next());
                else if (!Streaming) { Streaming = true; received!(Next()); }
            }
        }

        public void Send(byte[] data) => received!(data);
        public void Dispose() => Opened = false;
    }

    [Fact]
    public async Task AStreamingMeterIsAskedForItsNameThenForDataOnce()
    {
        var link = new FakeMeterLink();
        using var meter = new Ut60btMeter(link) { Timeout = TimeSpan.FromSeconds(2), PollEvery = TimeSpan.FromMilliseconds(300) };
        var id = meter.Connect();
        Assert.Equal("UT60BT", id.Model);
        Assert.Equal("UT60BT", meter.DeviceName);
        Assert.Equal("ble:C4:BE:84:12:34:56", meter.Location);
        Assert.Equal(Ut60btProtocol.GetName, link.Writes[0]);
        Assert.Equal(Ut60btProtocol.GetData, link.Writes[1]);
        Assert.True(meter.ReadConfig()!.IsTemperature);

        // The first reading is the frame that came while connecting; after that, Read waits for a newer one.
        Assert.Equal(180.0, meter.Read().Celsius!.Value, 6);
        var reading = Task.Run(meter.Read);
        await Task.Delay(100);
        Assert.False(reading.IsCompleted);
        link.Send(Frame(10, "181", f15: 0x32));
        Assert.Equal(181.0, (await reading).Celsius!.Value, 6);
        Assert.True(meter.ReadLowBattery());
        Assert.Null(((ITemperatureMeter)meter).ReadBatteryPercent());
        Assert.Equal(AutoPowerOffState.Unknown, ((ITemperatureMeter)meter).ReadAutoPowerOff().State);
    }

    [Fact]
    public void AMeterThatAnswersOnlyTheOtherRequestIsPolledWithIt()
    {
        var count = 0;
        var link = new FakeMeterLink { AnswersName = false, DataCommand = Ut60btProtocol.Trigger, OneFramePerRequest = true };
        link.Next = () => Frame(10, (200 + count++).ToString());
        using var meter = new Ut60btMeter(link)
        {
            Timeout = TimeSpan.FromSeconds(3), PollEvery = TimeSpan.FromMilliseconds(50), NameWait = TimeSpan.FromMilliseconds(20),
        };
        meter.Connect();
        Assert.Null(meter.DeviceName);
        var readings = Enumerable.Range(0, 3).Select(_ => meter.Read().Celsius!.Value).ToList();
        Assert.True(readings[0] < readings[1] && readings[1] < readings[2], string.Join(", ", readings));
        // It asked for the name three times, tried GetData once, then kept to the request that worked.
        List<byte[]> writes;
        lock (link.Writes) writes = [.. link.Writes];
        Assert.Equal(3, writes.Count(w => w.SequenceEqual(Ut60btProtocol.GetName)));
        Assert.Single(writes, w => w.SequenceEqual(Ut60btProtocol.GetData));
        Assert.True(writes.Count(w => w.SequenceEqual(Ut60btProtocol.Trigger)) >= 3);
    }

    [Fact]
    public void ReconnectingStartsCleanAndAsksForTheNameAgain()
    {
        // As after a battery swap: the meter is switched off and on, and the poller calls Connect again.
        var link = new FakeMeterLink { Next = () => Frame(10, "180", f15: 0x32) }; // low battery
        using var meter = new Ut60btMeter(link) { Timeout = TimeSpan.FromSeconds(2), PollEvery = TimeSpan.FromMilliseconds(300) };
        meter.Connect();
        Assert.True(meter.ReadLowBattery());
        Assert.Equal(180.0, meter.Read().Celsius!.Value, 6);

        link.Next = () => Frame(10, "181"); // a fresh battery
        int before;
        lock (link.Writes) before = link.Writes.Count;
        meter.Connect();
        List<byte[]> after;
        lock (link.Writes) after = link.Writes.Skip(before).ToList();
        Assert.Equal(Ut60btProtocol.GetName, after[0]);
        Assert.Equal(Ut60btProtocol.GetData, after[1]);
        Assert.False(meter.ReadLowBattery()); // not the frame from before
        Assert.Equal(181.0, meter.Read().Celsius!.Value, 6);
    }

    [Fact]
    public void TheMeterTimesOutWhenNothingAnswers()
    {
        var link = new FakeMeterLink { AnswersName = false, DataCommand = null };
        using var meter = new Ut60btMeter(link) { Timeout = TimeSpan.FromMilliseconds(300), PollEvery = TimeSpan.FromMilliseconds(50), NameWait = TimeSpan.FromMilliseconds(10) };
        Assert.Throws<TimeoutException>(() => meter.Connect());
    }

    [Fact]
    public void ItsDriverIsKnownWithItsAutoPowerOffAndBattery()
    {
        var driver = MeterDrivers.Find("ut60bt")!;
        Assert.IsType<Ut60btDriver>(driver);
        Assert.Equal(TimeSpan.FromMinutes(15), driver.AutoPowerOff!.DefaultTimeout);
        Assert.True(driver.HasBattery);
        Assert.Null(Ut60btDriver.Check("ble:UT60BT"));
        Assert.Contains("Bluetooth LE", Ut60btDriver.Check("COM5"));
        Assert.True(Ut60btDriver.IsUt60bt(new BleDevice("UT60BTk", "C4:BE:84:12:34:56")));
        Assert.False(Ut60btDriver.IsUt60bt(new BleDevice("121GW", "C4:BE:84:12:34:56")));
        // Without Bluetooth (a CI machine, WSL), discovery finds nothing rather than failing.
        _ = driver.Discover();
    }
}
