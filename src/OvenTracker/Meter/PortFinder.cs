using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace OvenTracker.Meter;

/// <param name="Port">The name to open: <c>COM5</c> on Windows, <c>/dev/rfcomm0</c> on Linux.</param>
/// <param name="IsBluetoothOutgoing">
/// A link to a paired Bluetooth device. On Windows, a "Standard Serial over Bluetooth link" port
/// that Windows created for the device (it has the device's address); Bluetooth also makes
/// incoming ports, with an all-zero address, which never reach the adapter. On Linux, an RFCOMM
/// device (<c>/dev/rfcommN</c>) bound to the device with <c>rfcomm bind</c>.
/// </param>
public sealed record PortInfo(string Port, string Description, bool IsBluetooth, bool IsBluetoothOutgoing, string? BluetoothAddress);

/// <summary>The serial ports the system knows, and which are Bluetooth links, on Windows and Linux. Used by serial-port drivers.</summary>
public static partial class PortFinder
{
    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComName();

    [GeneratedRegex(@"[&\\]([0-9A-F]{12})_", RegexOptions.IgnoreCase)]
    private static partial Regex BtAddress();

    /// <summary>Port names are case-insensitive on Windows (COM5 is com5) and case-sensitive elsewhere.</summary>
    public static StringComparer NameComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A port name as the system writes it: upper case on Windows, untouched elsewhere.</summary>
    public static string Normalize(string port) => OperatingSystem.IsWindows() ? port.Trim().ToUpperInvariant() : port.Trim();

    public static IReadOnlyList<PortInfo> List()
    {
        var found = new Dictionary<string, PortInfo>(NameComparer);
        try
        {
            if (OperatingSystem.IsWindows()) foreach (var p in FromWmi()) found[p.Port] = p;
            else if (OperatingSystem.IsLinux()) foreach (var p in FromLinux()) found[p.Port] = p;
        }
        catch { /* the details are a nicety: fall back to names only */ }
        foreach (var name in SerialPort.GetPortNames())
            found.TryAdd(name, new PortInfo(name, "Serial port", false, false, null));
        return Sort(found.Values);
    }

    /// <summary>Bluetooth links first, then by name with numbers in numeric order (COM2 before COM10, ttyUSB0 before ttyUSB1).</summary>
    public static IReadOnlyList<PortInfo> Sort(IEnumerable<PortInfo> ports) =>
        ports.OrderByDescending(p => p.IsBluetoothOutgoing)
            .ThenBy(p => SplitNumber(p.Port).Stem, StringComparer.Ordinal)
            .ThenBy(p => SplitNumber(p.Port).Number)
            .ThenBy(p => p.Port, StringComparer.Ordinal)
            .ToList();

    static (string Stem, int Number) SplitNumber(string port)
    {
        var i = port.Length;
        while (i > 0 && char.IsAsciiDigit(port[i - 1])) i--;
        return (port[..i], i < port.Length && int.TryParse(port.AsSpan(i), out var n) ? n : -1);
    }

    /// <summary>
    /// The serial devices a meter might be on, under <c>/dev</c>: RFCOMM Bluetooth links, USB serial
    /// adapters (<c>ttyUSB</c>, e.g. a U1173 cable or a Prolific/FTDI adapter) and USB CDC devices
    /// (<c>ttyACM</c>). The on-board <c>ttyS</c> ports come from <see cref="SerialPort.GetPortNames"/>.
    /// </summary>
    [SupportedOSPlatform("linux")]
    static IEnumerable<PortInfo> FromLinux()
    {
        foreach (var pattern in (string[])["rfcomm*", "ttyUSB*", "ttyACM*"])
            foreach (var path in Directory.EnumerateFileSystemEntries("/dev", pattern))
                yield return DescribeLinux(path, UsbProduct(Path.GetFileName(path)));
    }

    /// <summary>What a Linux serial device is, from its name and, for a USB one, the product name the device reports.</summary>
    public static PortInfo DescribeLinux(string path, string? usbProduct)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("rfcomm", StringComparison.Ordinal)) return new PortInfo(path, "Bluetooth serial link (RFCOMM)", true, true, null);
        if (name.StartsWith("ttyUSB", StringComparison.Ordinal)) return new PortInfo(path, usbProduct ?? "USB serial adapter", false, false, null);
        if (name.StartsWith("ttyACM", StringComparison.Ordinal)) return new PortInfo(path, usbProduct ?? "USB serial device", false, false, null);
        return new PortInfo(path, "Serial port", false, false, null);
    }

    /// <summary>The USB product string behind a tty, found by walking up its sysfs device path.</summary>
    static string? UsbProduct(string tty)
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine("/sys/class/tty", tty, "device"));
            var real = dir.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo ?? dir;
            for (var d = real; d is not null && d.FullName.StartsWith("/sys/devices", StringComparison.Ordinal); d = d.Parent)
            {
                var product = Path.Combine(d.FullName, "product");
                if (File.Exists(product)) return File.ReadAllText(product).Trim() is { Length: > 0 } text ? text : null;
            }
        }
        catch { /* no sysfs, or not a USB device */ }
        return null;
    }

    [SupportedOSPlatform("windows")]
    static IEnumerable<PortInfo> FromWmi()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
        foreach (var o in searcher.Get().Cast<ManagementObject>())
        {
            var name = o["Name"] as string ?? "";
            var id = o["PNPDeviceID"] as string ?? "";
            var m = ComName().Match(name);
            if (!m.Success) continue;
            var isBt = id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);
            string? addr = null;
            if (isBt && BtAddress().Match(id) is { Success: true } a) addr = a.Groups[1].Value.ToUpperInvariant();
            var outgoing = isBt && addr is not null && addr != "000000000000";
            yield return new PortInfo(m.Groups[1].Value.ToUpperInvariant(), name.Replace(m.Value, "").Trim(), isBt, outgoing,
                addr is null ? null : string.Join(":", Enumerable.Range(0, 6).Select(i => addr.Substring(i * 2, 2))));
        }
    }
}
