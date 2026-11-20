using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace OvenTracker.Meter;

/// <param name="IsBluetoothOutgoing">
/// A "Standard Serial over Bluetooth link" port that Windows created for a paired device (it has
/// the device's address). Bluetooth also makes incoming ports, with an all-zero address, which
/// never reach the adapter.
/// </param>
public sealed record PortInfo(string Port, string Description, bool IsBluetooth, bool IsBluetoothOutgoing, string? BluetoothAddress);

/// <summary>The serial ports Windows knows, and which are outgoing Bluetooth links. Used by serial-port drivers.</summary>
public static partial class PortFinder
{
    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComName();

    [GeneratedRegex(@"[&\\]([0-9A-F]{12})_", RegexOptions.IgnoreCase)]
    private static partial Regex BtAddress();

    public static IReadOnlyList<PortInfo> List()
    {
        var found = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            try { foreach (var p in FromWmi()) found[p.Port] = p; }
            catch { /* WMI unavailable: fall back to names only */ }
        }
        foreach (var name in SerialPort.GetPortNames())
            found.TryAdd(name, new PortInfo(name, "Serial port", false, false, null));
        return found.Values
            .OrderByDescending(p => p.IsBluetoothOutgoing)
            .ThenBy(p => int.TryParse(p.Port.AsSpan(3), out var n) ? n : int.MaxValue)
            .ToList();
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
