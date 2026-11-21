using System.Globalization;
using System.Text;

namespace OvenTracker.Meter;

/// <summary>
/// One 19-byte data frame from a UNI-T UT60BT (UNI-T's "iDMM" framing, shared with the UT61E+ family):
/// <c>AB CD 10</c>, the mode, the range (an ASCII digit), seven ASCII characters of display, two bargraph bytes,
/// three flag bytes, and a 16-bit big-endian sum of the first 17 bytes.
/// <para>
/// Sources: olegv142/dmmpy (adapters/ut61xp.py, its UT60BT class; The Unlicense), ljakob/unit_ut61eplus (the
/// UT61E+ over USB; same frame) and libreble/multimeter's UNI-T notes (docs/protocols/uni-t.md, from captures
/// of a real UT60BT). They agree on the frame, the checksum, the mode numbers and the flag bits used here.
/// </para>
/// </summary>
public sealed class Ut60btFrame
{
    readonly byte[] b;

    public Ut60btFrame(ReadOnlySpan<byte> bytes)
    {
        if (!Ut60btProtocol.IsData(bytes)) throw new ArgumentException("Not a valid UT60BT data frame.", nameof(bytes));
        b = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => b;

    /// <summary>The function the dial is on: 10 is temperature in °C, 11 in °F.</summary>
    public int Mode => b[3];

    public int Range => b[4] - '0';

    /// <summary>The display as the meter sends it: seven characters, with its sign and decimal point, e.g. "  234".</summary>
    public string Display => Encoding.ASCII.GetString(b, 5, 7);

    public bool Max => (b[14] & 0x08) != 0;
    public bool Min => (b[14] & 0x04) != 0;
    public bool Hold => (b[14] & 0x02) != 0;
    public bool Relative => (b[14] & 0x01) != 0;
    public bool ManualRange => (b[15] & 0x04) != 0;

    /// <summary>The low-battery sign: the manual shows it from 4.2 V (three AAA cells), while the meter still measures.</summary>
    public bool LowBattery => (b[15] & 0x02) != 0;

    public bool PeakMax => (b[16] & 0x04) != 0;
    public bool PeakMin => (b[16] & 0x02) != 0;

    /// <summary>
    /// The display without its spaces (and any NUL padding), as dmmpy and unit_ut61eplus read it: the sign has a
    /// column of its own, so "-  12" is -12.
    /// </summary>
    public string Text => new(Display.Where(c => c is not (' ' or '\0')).ToArray());

    /// <summary>"OL" on the display (with or without a dot or sign): for temperature, no thermocouple, or out of range.</summary>
    public bool Overload => Text.Replace(".", "") is "OL" or "-OL";

    /// <summary>The number on the display, or null when it isn't one (an overload, or something the app can't read).</summary>
    public double? Value =>
        !Overload && double.TryParse(Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    public override string ToString() => Convert.ToHexString(b);
}

public static class Ut60btProtocol
{
    public const byte Header1 = 0xAB, Header2 = 0xCD;
    public const int DataLength = 19;
    public const int CelsiusMode = 10, FahrenheitMode = 11;

    /// <summary>
    /// Microchip's (ISSC) "transparent UART" service, which the meter's Bluetooth module offers: the meter notifies
    /// on one characteristic, and takes commands written to the other.
    /// </summary>
    public static readonly Guid Service = new("49535343-fe7d-4ae5-8fa9-9fafd205e455");
    public static readonly Guid Notify = new("49535343-1e4d-4bd9-ba61-23c647249616");
    public static readonly Guid Write = new("49535343-8841-43f4-a8d4-ecbe34729bb3");

    /// <summary>The start of the name it advertises: "UT60BT" (some meters add a letter, e.g. "UT60BTk").</summary>
    public const string AdvertisedName = "UT60BT";

    /// <summary>Asks for the meter's name. libreble found a UT60BT ignores requests for data until it has answered this.</summary>
    public static readonly byte[] GetName = Command(0x5F);

    /// <summary>Asks for data: libreble's command, after which its UT60BT sent frames a few times a second.</summary>
    public static readonly byte[] GetData = Command(0x5D);

    /// <summary>Asks for one frame: dmmpy's and unit_ut61eplus's command (the UT61E+ answers each with a frame).</summary>
    public static readonly byte[] Trigger = Command(0x5E);

    /// <summary>
    /// A command: AB CD 03, the command byte, and the 16-bit big-endian sum of those four bytes (the length, 3,
    /// counts the command and the checksum). So 5E gives AB CD 03 5E 01 D9.
    /// </summary>
    public static byte[] Command(byte command)
    {
        var sum = Header1 + Header2 + 0x03 + command;
        return [Header1, Header2, 0x03, command, (byte)(sum >> 8), (byte)sum];
    }

    static readonly string[] ModeNames =
    [
        "AC V", "AC mV", "DC V", "DC mV", "Hz", "duty cycle", "resistance", "continuity", "diode", "capacitance",
        "temperature (°C)", "temperature (°F)", "DC µA", "AC µA", "DC mA", "AC mA", "DC A", "AC A", "hFE", "live wire",
        "NCV", "LoZ V", "AC A", "DC A", "LPF", "AC/DC", "LPF", "AC+DC", "LPF", "AC+DC", "inrush",
    ];

    public static string ModeName(int mode) => mode >= 0 && mode < ModeNames.Length ? ModeNames[mode] : $"mode {mode}";

    /// <summary>Whether bytes [0..^2] sum to the big-endian number in the last two.</summary>
    public static bool ChecksumOk(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5) return false;
        var sum = 0;
        foreach (var v in frame[..^2]) sum += v;
        return (sum & 0xFFFF) == ((frame[^2] << 8) | frame[^1]);
    }

    /// <summary>Whether these bytes are a data frame: the header, length 16, the checksum, a known mode and an ASCII range digit.</summary>
    public static bool IsData(ReadOnlySpan<byte> f) =>
        f.Length == DataLength && f[0] == Header1 && f[1] == Header2 && f[2] == DataLength - 3 && ChecksumOk(f)
        && f[3] < ModeNames.Length && f[4] is >= (byte)'0' and <= (byte)'9';

    /// <summary>The name in a reply to <see cref="GetName"/> (AB CD, length, ASCII name, checksum), or null if it isn't one.</summary>
    public static string? NameIn(ReadOnlySpan<byte> f)
    {
        if (f.Length < 6 || f.Length == DataLength || f[0] != Header1 || f[1] != Header2 || f[2] != f.Length - 3 || !ChecksumOk(f)) return null;
        var name = f[3..^2];
        foreach (var c in name) if (c is < 0x20 or > 0x7E) return null;
        return Encoding.ASCII.GetString(name).Trim();
    }

    /// <summary>What the meter is set to: only plain temperature (no HOLD, REL, MIN/MAX or peak) gives readings the app can use.</summary>
    public static MeterConfig Config(Ut60btFrame f)
    {
        if (f.Mode is not (CelsiusMode or FahrenheitMode)) return new MeterConfig(ModeName(f.Mode), false, null);
        var unit = f.Mode == FahrenheitMode ? TempUnit.Fahrenheit : TempUnit.Celsius;
        var shown = unit == TempUnit.Fahrenheit ? "°F" : "°C";
        string? held = f.Hold ? "HOLD" : f.Relative ? "REL" : f.Max || f.Min ? "MAX/MIN" : f.PeakMax || f.PeakMin ? "peak" : null;
        return held is null
            ? new MeterConfig($"temperature, {shown}", true, unit, Sensor.Thermocouple("K"))
            : new MeterConfig($"temperature, {shown}, with {held} on", false, unit);
    }

    public static MeterReading ToReading(DateTimeOffset time, Ut60btFrame f)
    {
        var raw = f.ToString();
        if (!Config(f).IsTemperature) return new(time, ReadingStatus.NotTemperature, null, raw);
        if (f.Overload) return new(time, ReadingStatus.Overload, null, raw);
        if (f.Value is not { } shown) return new(time, ReadingStatus.Error, null, raw);
        var c = f.Mode == FahrenheitMode ? (shown - 32) / 1.8 : shown;
        return new(time, ReadingStatus.Ok, Math.Round(c, 4), raw);
    }

    public static string Describe(Ut60btFrame f) => $"{ModeName(f.Mode)} {f.Text}";
}

/// <summary>
/// Picks frames out of the meter's notifications. libreble saw each notification carry one whole frame, but the
/// bytes are treated as one stream anyway, so a frame split across notifications, or two in one, still comes
/// through: find AB CD, read the length, and keep the frame only if its checksum is right.
/// </summary>
public sealed class Ut60btAssembler
{
    /// <summary>Longer than any frame the meter sends; a bigger length byte means AB CD was data, not a header.</summary>
    const int MaxLength = 64;

    readonly List<byte> buffer = [];

    /// <summary>The whole frames (data frames and replies) completed by these bytes.</summary>
    public IReadOnlyList<byte[]> Add(ReadOnlySpan<byte> notification)
    {
        var found = new List<byte[]>();
        buffer.AddRange(notification.ToArray());
        var i = 0;
        while (buffer.Count - i >= 3)
        {
            if (buffer[i] != Ut60btProtocol.Header1 || buffer[i + 1] != Ut60btProtocol.Header2 || buffer[i + 2] is < 3 or > MaxLength - 3)
            {
                i++;
                continue;
            }
            var total = 3 + buffer[i + 2];
            if (buffer.Count - i < total) break; // the rest is still to come
            var candidate = buffer.GetRange(i, total).ToArray();
            if (Ut60btProtocol.ChecksumOk(candidate))
            {
                found.Add(candidate);
                i += total;
            }
            else i++;
        }
        buffer.RemoveRange(0, i);
        if (buffer.Count > 2 * MaxLength) buffer.RemoveRange(0, buffer.Count - MaxLength);
        return found;
    }
}
