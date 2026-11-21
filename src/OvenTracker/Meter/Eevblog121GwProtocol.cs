using System.Globalization;

namespace OvenTracker.Meter;

/// <summary>
/// One 19-byte data packet from an EEVblog 121GW: F2, 17 bytes of display data, and an XOR checksum that
/// makes all 19 bytes XOR to zero. Multi-byte fields are big-endian.
/// <para>
/// Source: the 121GW Bluetooth packet format V2 (EEVblog, 2018-07-03), as relayed by the dmm-tools project's
/// write-up (docs/research/121gw/reverse-engineered-protocol.md), and QtDMM's 121GW decoder. Both agree,
/// and agree with captures from real meters. The official PDF wasn't checked directly.
/// </para>
/// </summary>
public sealed class Gw121Packet
{
    readonly byte[] b;

    public Gw121Packet(ReadOnlySpan<byte> bytes)
    {
        if (!Gw121Protocol.IsValid(bytes)) throw new ArgumentException("Not a valid 121GW packet.", nameof(bytes));
        b = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => b;

    /// <summary>The main display's mode, 0–24 (5 is temperature).</summary>
    public int Mode => b[5] & 0x1F;

    /// <summary>The range, an index into the mode's ranges: it sets the decimal point.</summary>
    public int Range => b[6] & 0x0F;

    public bool Overload => (b[6] & 0x80) != 0;
    public bool Negative => (b[6] & 0x40) != 0;

    /// <summary>The °C and °F flags (firmware 1.21 and later). An older meter sends neither.</summary>
    public bool Celsius => (b[6] & 0x20) != 0;
    public bool Fahrenheit => (b[6] & 0x10) != 0;

    /// <summary>The main display's count: 18 bits, unsigned (the two high bits from firmware 1.22).</summary>
    public int Count => ((b[5] >> 6) << 16) | (b[7] << 8) | b[8];

    public bool LowBattery => (b[15] & 0x01) != 0;
    public bool AutoPowerOffArmed => (b[15] & 0x02) != 0;
    public bool Relative => (b[16] & 0x10) != 0;
    public int MinMax => b[16] & 0x07;
    public int Hold => (b[17] >> 2) & 0x03;

    /// <summary>The meter's identity bytes (year, month and five serial digits), as hex: enough to tell meters apart.</summary>
    public string Serial => Convert.ToHexString(b, 1, 4);

    /// <summary>The main display's value, with its decimal point and sign. Null on an overload.</summary>
    public double? Value => Overload ? null : (Negative ? -1 : 1) * Count / Math.Pow(10, Gw121Protocol.Decimals(Mode, Range));

    public override string ToString() => Convert.ToHexString(b);
}

public static class Gw121Protocol
{
    public const byte Start = 0xF2;
    public const int Length = 19;
    public const int TemperatureMode = 5;

    /// <summary>The service and characteristic the meter streams on (from EEVblog's and UEi's apps).</summary>
    public static readonly Guid Service = new("0bd51666-e7cb-469b-8e4d-2742f1ba77cc");
    public static readonly Guid Characteristic = new("e7add780-b042-4876-aae1-112855353cc1");

    /// <summary>The name it advertises: "121GW".</summary>
    public const string AdvertisedName = "121GW";

    /// <summary>Decimal places for each mode's ranges.</summary>
    static readonly int[][] RangeDecimals =
    [
        [1],             // 0 Low Z
        [4, 3, 2, 1],    // 1 DC V
        [4, 3, 2, 1],    // 2 AC V
        [3, 2],          // 3 DC mV
        [3, 2],          // 4 AC mV
        [1],             // 5 temperature
        [3, 2, 4, 3, 2], // 6 Hz (Hz, then kHz)
        [4, 3, 2],       // 7 pulse width, ms
        [1],             // 8 duty, %
        [3, 2, 4, 3, 2, 4, 3], // 9 resistance (Ω, kΩ, MΩ)
        [2],             // 10 continuity
        [4, 3],          // 11 diode
        [2, 1, 3, 2, 1, 0], // 12 capacitance
        [2, 1, 1, 0], [3, 2, 2, 1], [1, 0, 3, 2], // 13–15 AC VA
        [3, 2], [3, 2],  // 16–17 µA
        [4, 3], [4, 3],  // 18–19 mA
        [2, 4, 3], [2, 4, 3], // 20–21 A
        [2, 1, 1, 0], [3, 2, 2, 1], [1, 0, 3, 2], // 22–24 DC VA
    ];

    static readonly string[] ModeNames =
    [
        "Low Z", "DC V", "AC V", "DC mV", "AC mV", "temperature", "Hz", "pulse width", "duty cycle", "resistance",
        "continuity", "diode", "capacitance", "AC µVA", "AC mVA", "AC VA", "AC µA", "DC µA", "AC mA", "DC mA",
        "AC A", "DC A", "DC µVA", "DC mVA", "DC VA",
    ];

    public static string ModeName(int mode) => mode >= 0 && mode < ModeNames.Length ? ModeNames[mode] : $"mode {mode}";

    public static int Decimals(int mode, int range) =>
        mode >= 0 && mode < RangeDecimals.Length && range < RangeDecimals[mode].Length ? RangeDecimals[mode][range] : 0;

    /// <summary>Whether these 19 bytes are a packet: the start byte, the checksum, and a mode and range that exist.</summary>
    public static bool IsValid(ReadOnlySpan<byte> p)
    {
        if (p.Length != Length || p[0] != Start) return false;
        byte x = 0;
        foreach (var v in p) x ^= v;
        if (x != 0) return false;
        var mode = p[5] & 0x1F;
        if (mode >= RangeDecimals.Length || (p[6] & 0x0F) >= RangeDecimals[mode].Length) return false;
        return (p[13] & 0xE0) == 0;
    }

    /// <summary>What the meter is set to: only plain temperature (no HOLD, REL or MIN/MAX) gives readings the app can use.</summary>
    public static MeterConfig Config(Gw121Packet p)
    {
        if (p.Mode != TemperatureMode) return new MeterConfig(ModeName(p.Mode), false, null);
        var unit = TemperatureUnit(p);
        var shown = unit == TempUnit.Fahrenheit ? "°F" : "°C";
        string? held = p.Hold != 0 ? "HOLD" : p.Relative ? "REL" : p.MinMax != 0 ? "MIN/MAX" : null;
        return held is null
            ? new MeterConfig($"temperature, {shown}", true, unit, Sensor.Thermocouple("K"))
            : new MeterConfig($"temperature, {shown}, with {held} on", false, unit);
    }

    /// <summary>
    /// The unit shown. Firmware 1.21 and later set a °C or °F flag. An older meter sets neither, and is taken
    /// as °C, as QtDMM does.
    /// </summary>
    public static TempUnit TemperatureUnit(Gw121Packet p) => p.Fahrenheit && !p.Celsius ? TempUnit.Fahrenheit : TempUnit.Celsius;

    public static MeterReading ToReading(DateTimeOffset time, Gw121Packet p)
    {
        var raw = p.ToString();
        if (!Config(p).IsTemperature) return new(time, ReadingStatus.NotTemperature, null, raw);
        if (p.Overload) return new(time, ReadingStatus.Overload, null, raw);
        var shown = p.Value!.Value;
        var c = TemperatureUnit(p) == TempUnit.Fahrenheit ? (shown - 32) / 1.8 : shown;
        return new(time, ReadingStatus.Ok, Math.Round(c, 4), raw);
    }

    public static string Describe(Gw121Packet p) =>
        p.Value is { } v ? $"{ModeName(p.Mode)} {v.ToString(CultureInfo.InvariantCulture)}" : $"{ModeName(p.Mode)} OL";
}

/// <summary>
/// Puts packets back together from the meter's notifications. A packet can straddle notifications, and a
/// notification can hold parts of two, so the bytes are treated as one stream: find F2, take 19 bytes, and
/// keep them only if they check out (a data byte can also be F2). Some meters send each packet as 18 bytes
/// without its F2; those are recognised too.
/// </summary>
public sealed class Gw121Assembler
{
    readonly List<byte> buffer = [];

    public IReadOnlyList<Gw121Packet> Add(ReadOnlySpan<byte> notification)
    {
        var found = new List<Gw121Packet>();
        // One packet without its start byte: the 18 bytes after F2.
        if (notification.Length == Gw121Protocol.Length - 1)
        {
            Span<byte> whole = stackalloc byte[Gw121Protocol.Length];
            whole[0] = Gw121Protocol.Start;
            notification.CopyTo(whole[1..]);
            if (Gw121Protocol.IsValid(whole))
            {
                buffer.Clear();
                found.Add(new Gw121Packet(whole));
                return found;
            }
        }

        buffer.AddRange(notification.ToArray());
        var i = 0;
        while (buffer.Count - i >= Gw121Protocol.Length)
        {
            if (buffer[i] != Gw121Protocol.Start) { i++; continue; }
            var candidate = buffer.GetRange(i, Gw121Protocol.Length).ToArray();
            if (Gw121Protocol.IsValid(candidate))
            {
                found.Add(new Gw121Packet(candidate));
                i += Gw121Protocol.Length;
            }
            else i++;
        }
        buffer.RemoveRange(0, i);
        if (buffer.Count > 4 * Gw121Protocol.Length) buffer.RemoveRange(0, buffer.Count - Gw121Protocol.Length);
        return found;
    }
}
