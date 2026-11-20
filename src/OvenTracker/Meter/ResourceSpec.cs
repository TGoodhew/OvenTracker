using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;

namespace OvenTracker.Meter;

/// <summary>How a meter is connected.</summary>
public enum Transport
{
    /// <summary>A serial port: RS-232, a USB serial adapter, or a Bluetooth serial link.</summary>
    Serial,

    /// <summary>GPIB (IEEE-488), through VISA.</summary>
    Gpib,

    /// <summary>USB-TMC, through VISA.</summary>
    Usb,

    /// <summary>LAN (VXI-11 or HiSLIP), through VISA.</summary>
    Lan,
}

/// <summary>A serial port's settings: they must match the meter's (often set on its front panel).</summary>
public sealed record SerialSettings(int Baud = 9600, Parity Parity = Parity.None, int DataBits = 8, StopBits StopBits = StopBits.One, Handshake Handshake = Handshake.None)
{
    /// <summary>The U1250 series through a U1177A or U1173: 9600 baud, 8N1.</summary>
    public static readonly SerialSettings U1250 = new();

    /// <summary>"9600,N,8,1" (with ",dtr" or ",rts" or ",xon" for a handshake).</summary>
    public override string ToString()
    {
        var parity = Parity switch { Parity.Even => "E", Parity.Odd => "O", Parity.Mark => "M", Parity.Space => "S", _ => "N" };
        var stop = StopBits switch { StopBits.Two => "2", StopBits.OnePointFive => "1.5", _ => "1" };
        var hs = Handshake switch { Handshake.RequestToSend => ",rts", Handshake.XOnXOff => ",xon", Handshake.RequestToSendXOnXOff => ",rts+xon", _ => "" };
        return string.Create(CultureInfo.InvariantCulture, $"{Baud},{parity},{DataBits},{stop}{hs}");
    }
}

/// <summary>
/// Where a meter is and how it's connected, from <c>--resource</c>. One option covers every transport, and each
/// driver says which forms it accepts:
/// <list type="bullet">
/// <item><c>COM3</c>, <c>/dev/ttyUSB0</c>, <c>/dev/rfcomm0</c>: a serial port, with the driver's settings.</item>
/// <item><c>COM3:9600,E,7,2</c>, <c>/dev/ttyUSB0:9600,N,8,1,dtr</c>: a serial port with these settings
/// (baud, parity N/E/O/M/S, data bits, stop bits 1/1.5/2, and optionally a handshake: rts, xon, or dtr).</item>
/// <item><c>GPIB0::22::INSTR</c> (or <c>GPIB::22</c>, <c>gpib:22</c>): GPIB address 22, through VISA.</item>
/// <item><c>ASRL3::INSTR</c>: serial port 3, through VISA.</item>
/// <item><c>USB0::…::INSTR</c>, <c>TCPIP0::…::INSTR</c>: USB-TMC and LAN instruments, through VISA.</item>
/// </list>
/// </summary>
/// <param name="Address">For a serial port, its name (COM3, /dev/ttyUSB0); for VISA, the full resource string.</param>
/// <param name="Settings">For a serial port, the settings given (null: the driver's default).</param>
/// <param name="DtrDsr">The DTR/DSR handshake was asked for (",dtr"), which .NET's SerialPort leaves to the caller.</param>
/// <param name="Visa">Opened through VISA (GPIB, USB-TMC, LAN, or ASRL serial).</param>
public sealed partial record ResourceSpec(Transport Transport, string Address, SerialSettings? Settings = null, bool DtrDsr = false, bool Visa = false)
{
    [GeneratedRegex(@"^(?<port>.+?):(?<baud>\d+)\s*,\s*(?<parity>[NEOMS])\s*,\s*(?<data>[5-8])\s*,\s*(?<stop>1|1\.5|2)\s*(,\s*(?<hs>rts\+xon|rts|xon|dtr|none))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SerialWithSettings();

    [GeneratedRegex(@"^gpib(?<board>\d*)(::|:)(?<addr>\d{1,2})(::(?<sec>\d{1,3}))?(::INSTR)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Gpib();

    /// <summary>Reads a resource. Null if it's empty or a serial settings part is malformed.</summary>
    public static ResourceSpec? Parse(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        if (Gpib().Match(s) is { Success: true } g)
        {
            var board = g.Groups["board"].Value is { Length: > 0 } b ? b : "0";
            var addr = int.Parse(g.Groups["addr"].Value, CultureInfo.InvariantCulture);
            if (addr > 30) return null;
            var sec = g.Groups["sec"].Success ? $"::{g.Groups["sec"].Value}" : "";
            return new ResourceSpec(Transport.Gpib, $"GPIB{board}::{addr}{sec}::INSTR", Visa: true);
        }
        var upper = s.ToUpperInvariant();
        if (upper.StartsWith("ASRL", StringComparison.Ordinal)) return new ResourceSpec(Transport.Serial, s, Visa: true);
        if (upper.StartsWith("USB", StringComparison.Ordinal) && s.Contains("::", StringComparison.Ordinal)) return new ResourceSpec(Transport.Usb, s, Visa: true);
        if (upper.StartsWith("TCPIP", StringComparison.Ordinal)) return new ResourceSpec(Transport.Lan, s, Visa: true);

        if (SerialWithSettings().Match(s) is { Success: true } m)
        {
            var parity = char.ToUpperInvariant(m.Groups["parity"].Value[0]) switch
            {
                'E' => Parity.Even, 'O' => Parity.Odd, 'M' => Parity.Mark, 'S' => Parity.Space, _ => Parity.None,
            };
            var stop = m.Groups["stop"].Value switch { "2" => StopBits.Two, "1.5" => StopBits.OnePointFive, _ => StopBits.One };
            var hs = m.Groups["hs"].Success ? m.Groups["hs"].Value.ToLowerInvariant() : "none";
            var handshake = hs switch
            {
                "rts" => Handshake.RequestToSend, "xon" => Handshake.XOnXOff, "rts+xon" => Handshake.RequestToSendXOnXOff, _ => Handshake.None,
            };
            var baud = int.Parse(m.Groups["baud"].Value, CultureInfo.InvariantCulture);
            if (baud is < 50 or > 4_000_000) return null;
            return new ResourceSpec(Transport.Serial, m.Groups["port"].Value.Trim(),
                new SerialSettings(baud, parity, int.Parse(m.Groups["data"].Value, CultureInfo.InvariantCulture), stop, handshake), DtrDsr: hs == "dtr");
        }
        // A colon followed by something that isn't valid settings is a mistake, not a port name (except Windows' \\.\COM10).
        if (s.Contains(':') && !s.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
        return new ResourceSpec(Transport.Serial, s);
    }

    /// <summary>The settings to use: those given, or the driver's.</summary>
    public SerialSettings SettingsOr(SerialSettings driverDefault) => Settings ?? driverDefault;

    /// <summary>A serial port configured from these settings (not opened).</summary>
    public static SerialPort OpenSerial(string port, SerialSettings settings, bool dtrDsr)
    {
        var p = new SerialPort(port, settings.Baud, settings.Parity, settings.DataBits, settings.StopBits) { Handshake = settings.Handshake };
        // DTR/DSR: hold DTR true so the meter may send (the 34401A waits for it); .NET doesn't pace on DSR itself.
        if (dtrDsr) p.DtrEnable = true;
        return p;
    }

    public static string Describe(Transport t) => t switch
    {
        Transport.Gpib => "GPIB",
        Transport.Usb => "USB",
        Transport.Lan => "LAN",
        _ => "Serial",
    };
}
