using OvenTracker.Meter;
using OvenTracker.Meter.Ble;

namespace OvenTracker.Tests;

public class Eevblog121GwTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// <summary>A packet with these fields and a correct checksum.</summary>
    static byte[] Packet(int mode, byte b6, int count, byte b15 = 0x04, byte b16 = 0x40, byte b17 = 0x00)
    {
        var p = new byte[19];
        p[0] = 0xF2;
        p[1] = 0x17; p[2] = 0x84; p[3] = 0x21; p[4] = 0x21;
        p[5] = (byte)(mode | ((count >> 16) << 6));
        p[6] = b6;
        p[7] = (byte)(count >> 8); p[8] = (byte)count;
        p[9] = 100; p[10] = 0x01; p[11] = 0x01; p[12] = 0x17;
        p[15] = b15; p[16] = b16; p[17] = b17;
        byte x = 0;
        for (var i = 0; i < 18; i++) x ^= p[i];
        p[18] = x;
        return p;
    }

    // Two captured from real meters (sigrok's wiki, 121gwcli), and four from the dmm-tools write-up.
    [Theory]
    [InlineData("F2 17 84 21 21 08 00 00 00 64 01 01 17 12 37 02 40 00 7D", 8, 0.0, false)]
    [InlineData("F2 17 85 43 21 09 86 00 00 64 01 01 02 01 21 06 40 00 8D", 9, null, true)]
    [InlineData("F2 12 61 23 45 01 00 30 39 64 01 00 F3 00 00 0C 40 00 35", 1, 1.2345, false)]
    [InlineData("F2 12 61 23 45 46 00 56 66 00 00 00 00 00 00 04 40 00 D5", 6, 87.654, false)]
    [InlineData("F2 12 61 23 45 03 40 30 39 00 00 00 00 00 00 0C 40 00 E1", 3, -12.345, false)]
    [InlineData("F2 12 61 23 45 09 86 00 00 00 00 00 00 00 00 04 40 00 2C", 9, null, true)]
    public void DecodesTheKnownPackets(string hex, int mode, double? value, bool overload)
    {
        var p = new Gw121Packet(Hex(hex));
        Assert.Equal(mode, p.Mode);
        Assert.Equal(overload, p.Overload);
        if (value is null) Assert.Null(p.Value);
        else Assert.Equal(value.Value, p.Value!.Value, 9);
    }

    [Fact]
    public void RejectsABadChecksumOrAnImpossibleMode()
    {
        var good = Hex("F2 12 61 23 45 01 00 30 39 64 01 00 F3 00 00 0C 40 00 35");
        Assert.True(Gw121Protocol.IsValid(good));
        var bad = (byte[])good.Clone();
        bad[8] ^= 1;
        Assert.False(Gw121Protocol.IsValid(bad));
        var noSuchMode = Packet(25, 0, 0);
        Assert.False(Gw121Protocol.IsValid(noSuchMode));
        var noSuchRange = Packet(5, 0x23, 0); // temperature has one range
        Assert.False(Gw121Protocol.IsValid(noSuchRange));
    }

    [Fact]
    public void ReadsTemperatureInCelsius()
    {
        var p = new Gw121Packet(Packet(5, 0x20, 2345)); // 234.5 °C
        var r = Gw121Protocol.ToReading(DateTimeOffset.UnixEpoch, p);
        Assert.Equal(ReadingStatus.Ok, r.Status);
        Assert.Equal(234.5, r.Celsius!.Value, 6);
        var config = Gw121Protocol.Config(p);
        Assert.True(config.IsTemperature);
        Assert.Equal(TempUnit.Celsius, config.Unit);
        Assert.Equal("type K thermocouple", config.Sensor!.Name);
    }

    [Fact]
    public void ConvertsFahrenheit()
    {
        var p = new Gw121Packet(Packet(5, 0x10, 4500)); // 450.0 °F
        Assert.Equal(TempUnit.Fahrenheit, Gw121Protocol.Config(p).Unit);
        Assert.Equal((450 - 32) / 1.8, Gw121Protocol.ToReading(DateTimeOffset.UnixEpoch, p).Celsius!.Value, 3);
    }

    [Fact]
    public void AnOlderMeterWithoutUnitFlagsIsTakenAsCelsius()
    {
        var p = new Gw121Packet(Packet(5, 0x00, 1800));
        Assert.Equal(180.0, Gw121Protocol.ToReading(DateTimeOffset.UnixEpoch, p).Celsius!.Value, 6);
    }

    [Fact]
    public void AnOpenThermocoupleIsAnOverload() =>
        Assert.Equal(ReadingStatus.Overload, Gw121Protocol.ToReading(DateTimeOffset.UnixEpoch, new Gw121Packet(Packet(5, 0xA0, 0))).Status);

    [Theory]
    [InlineData(1, 0x00, 0x40, 0x00)] // DC volts
    [InlineData(5, 0x20, 0x40, 0x08)] // temperature, HOLD
    [InlineData(5, 0x20, 0x50, 0x00)] // temperature, REL
    [InlineData(5, 0x20, 0x41, 0x00)] // temperature, MAX
    public void OnlyLiveTemperatureCounts(int mode, byte b6, byte b16, byte b17)
    {
        var p = new Gw121Packet(Packet(mode, b6, 1800, b16: b16, b17: b17));
        Assert.False(Gw121Protocol.Config(p).IsTemperature);
        Assert.Equal(ReadingStatus.NotTemperature, Gw121Protocol.ToReading(DateTimeOffset.UnixEpoch, p).Status);
    }

    [Fact]
    public void ReassemblesPacketsSplitAcrossNotifications()
    {
        var a = Packet(5, 0x20, 2000);
        var b = Packet(5, 0x20, 2010);
        var stream = new byte[] { 0x00, 0xF2, 0x13 }.Concat(a).Concat(b).ToArray(); // junk, including a stray F2, first
        var asm = new Gw121Assembler();
        var got = new List<Gw121Packet>();
        foreach (var chunk in stream.Chunk(7)) got.AddRange(asm.Add(chunk));
        Assert.Equal([200.0, 201.0], got.Select(p => p.Value!.Value));
    }

    [Fact]
    public void TakesTwoPacketsInOneNotification()
    {
        var both = Packet(5, 0x20, 2000).Concat(Packet(5, 0x20, 2010)).ToArray();
        Assert.Equal(2, new Gw121Assembler().Add(both).Count);
    }

    [Fact]
    public void TakesAPacketSentWithoutItsStartByte()
    {
        var p = Packet(5, 0x20, 2000);
        var got = new Gw121Assembler().Add(p.AsSpan(1));
        Assert.Equal(200.0, Assert.Single(got).Value!.Value);
    }

    sealed class FakeLink : IBleLink
    {
        Action<byte[]>? received;
        public bool Opened { get; private set; }
        public string Address => "C4:BE:84:12:34:56";
        public void Open(Action<byte[]> r) { received = r; Opened = true; }
        public void Send(byte[] data) => received!(data);
        public void Dispose() => Opened = false;
    }

    [Fact]
    public void TheMeterConnectsAndReadsEachPacketOnce()
    {
        var link = new FakeLink();
        using var meter = new Eevblog121GwMeter(link) { Timeout = TimeSpan.FromSeconds(2) };
        var feeder = Task.Run(async () =>
        {
            while (!link.Opened) await Task.Delay(5);
            link.Send(Packet(5, 0x20, 1800, b15: 0x06));
        });
        var id = meter.Connect();
        feeder.Wait();
        Assert.Equal("121GW", id.Model);
        Assert.Equal("17842121", id.Serial);
        Assert.Equal("ble:C4:BE:84:12:34:56", meter.Location);
        Assert.Equal(AutoPowerOffState.On, meter.ReadAutoPowerOff().State);
        Assert.True(meter.ReadConfig()!.IsTemperature);

        // The first reading is the packet that arrived while connecting; after that, Read waits for a newer one.
        Assert.Equal(180.0, meter.Read().Celsius!.Value, 6);
        var reading = Task.Run(meter.Read);
        Thread.Sleep(100);
        Assert.False(reading.IsCompleted);
        link.Send(Packet(5, 0x20, 1812, b15: 0x04));
        Assert.Equal(181.2, reading.Result.Celsius!.Value, 6);
        Assert.Equal(AutoPowerOffState.Off, meter.ReadAutoPowerOff().State);
        Assert.Null(((ITemperatureMeter)meter).ReadBatteryPercent());
    }

    [Fact]
    public void TheMeterTimesOutWhenTheStreamStops()
    {
        using var meter = new Eevblog121GwMeter(new FakeLink()) { Timeout = TimeSpan.FromMilliseconds(200) };
        Assert.Throws<TimeoutException>(() => meter.Connect());
    }

    [Theory]
    [InlineData("ble:C4:BE:84:12:34:56", "C4:BE:84:12:34:56")]
    [InlineData("BLE:121GW", "121GW")]
    public void ParsesBluetoothLeResources(string text, string address)
    {
        var spec = ResourceSpec.Parse(text)!;
        Assert.Equal(Transport.Ble, spec.Transport);
        Assert.Equal(address, spec.Address);
        Assert.Null(ResourceSpec.Parse("ble:"));
    }

    [Fact]
    public void EachDriverRefusesTheOthersConnections()
    {
        Assert.Null(Eevblog121GwDriver.Check("ble:121GW"));
        Assert.Contains("Bluetooth LE", Eevblog121GwDriver.Check("COM5"));
        Assert.Contains("not Bluetooth LE", U1253BDriver.Check("ble:121GW"));
        Assert.Contains("Bluetooth", Hp34401ADriver.Check("ble:121GW"));
    }

    [Theory]
    [InlineData("c4be84123456", "C4:BE:84:12:34:56")]
    [InlineData("C4-BE-84-12-34-56", "C4:BE:84:12:34:56")]
    [InlineData("121GW", null)]
    [InlineData("C4:BE:84:12:34", null)]
    public void NormalizesAddresses(string text, string? expected) => Assert.Equal(expected, BleLinks.NormalizeAddress(text));

    [Fact]
    public void ItsAutoPowerOffIsKnownAndOnLinuxItSaysWhy()
    {
        var driver = MeterDrivers.Find("121gw")!;
        Assert.Equal(TimeSpan.FromMinutes(30), driver.AutoPowerOff!.DefaultTimeout);
        if (!BleLinks.IsSupported)
        {
            Assert.Contains("#34", driver.Open(new MeterRequest(null, null, 1))!.Error);
            Assert.Empty(driver.Discover());
        }
    }
}
