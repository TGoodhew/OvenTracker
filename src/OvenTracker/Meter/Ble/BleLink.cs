namespace OvenTracker.Meter.Ble;

/// <summary>A Bluetooth LE device the system knows (paired, on Windows).</summary>
/// <param name="Address">Its Bluetooth address, e.g. "C4:BE:84:12:34:56".</param>
public sealed record BleDevice(string Name, string Address);

/// <summary>
/// A subscription to one GATT characteristic of a Bluetooth LE device: the bytes of each notification or
/// indication arrive through the callback given to <see cref="Open"/>, on another thread. Tests use a fake.
/// </summary>
public interface IBleLink : IDisposable
{
    string Address { get; }

    /// <summary>Connects, and subscribes to the characteristic (indications if it has them, otherwise notifications).</summary>
    void Open(Action<byte[]> received);
}

/// <summary>Bluetooth LE on this system: Windows through its GATT API, Linux through BlueZ; not macOS yet.</summary>
public static class BleLinks
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static string NotSupportedHelp => "Bluetooth LE meters work on Windows and Linux; not on this system yet.";

    /// <summary>
    /// The Bluetooth LE devices the system knows, by name: paired ones on Windows; on Linux, those BlueZ knows
    /// (paired, or seen in a scan). Empty where Bluetooth LE isn't supported or can't be reached.
    /// </summary>
    public static IReadOnlyList<BleDevice> List() =>
        OperatingSystem.IsWindows() ? WindowsGattLink.PairedDevices()
        : OperatingSystem.IsLinux() ? BlueZLink.KnownDevices()
        : [];

    /// <summary>
    /// Looks for devices that aren't known yet: on Linux, a scan of a few seconds through BlueZ. Windows' GATT API
    /// needs devices paired first, so there it's the same as <see cref="List"/>.
    /// </summary>
    public static IReadOnlyList<BleDevice> Scan() => OperatingSystem.IsLinux() ? BlueZLink.ScanDevices() : List();

    public static IBleLink For(string address, Guid service, Guid characteristic) =>
        OperatingSystem.IsWindows() ? new WindowsGattLink(address, service, characteristic)
        : OperatingSystem.IsLinux() ? new BlueZLink(address, service, characteristic)
        : throw new MeterException(NotSupportedHelp);

    /// <summary>"c4be84123456", "C4:BE:84:12:34:56" or "C4-BE-84-12-34-56" as "C4:BE:84:12:34:56". Null if it isn't one.</summary>
    public static string? NormalizeAddress(string text)
    {
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        var clean = text.Trim().Replace(":", "").Replace("-", "");
        if (hex.Length != 12 || clean.Length != 12) return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
