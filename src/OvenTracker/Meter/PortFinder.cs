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

/// <param name="Meter">Still connected, when the probe was asked to keep it open.</param>
public sealed record ProbeResult(string Port, MeterIdentity? Identity, string? Error, U1253BMeter? Meter = null);

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

    /// <param name="keepOpen">Hand back the connected meter instead of closing it: reopening a Bluetooth port straight away can fail.</param>
    public static ProbeResult Probe(string port, bool keepOpen = false)
    {
        var meter = new U1253BMeter(port);
        try
        {
            var id = meter.Connect();
            if (keepOpen && id.Model.StartsWith("U12", StringComparison.OrdinalIgnoreCase)) return new ProbeResult(port, id, null, meter);
            meter.Dispose();
            return new ProbeResult(port, id, null);
        }
        catch (Exception ex)
        {
            meter.Dispose();
            return new ProbeResult(port, null, ex is TimeoutException ? "no reply" : ex.Message);
        }
    }

    /// <summary>
    /// Finds the meter: the port that worked last time, then each outgoing Bluetooth port. Other
    /// serial ports are only tried when <paramref name="includeAll"/> is set, since writing to an
    /// unknown serial device is not always harmless.
    /// </summary>
    public static ProbeResult? Find(string? preferred, bool includeAll, Action<string>? progress = null, bool keepOpen = false)
    {
        var ports = List();
        var order = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred)) order.Add(preferred.ToUpperInvariant());
        order.AddRange(ports.Where(p => p.IsBluetoothOutgoing).Select(p => p.Port));
        if (includeAll) order.AddRange(ports.Select(p => p.Port));
        foreach (var port in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            progress?.Invoke(port);
            var r = Probe(port, keepOpen);
            if (r.Identity is { } id && id.Model.StartsWith("U12", StringComparison.OrdinalIgnoreCase)) return r;
        }
        return null;
    }
}
