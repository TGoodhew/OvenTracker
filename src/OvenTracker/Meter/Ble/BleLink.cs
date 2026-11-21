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

/// <summary>Bluetooth LE on this system: Windows through its GATT API; elsewhere, not yet (issue #34).</summary>
public static class BleLinks
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string NotSupportedHelp =>
        "Bluetooth LE meters work only on Windows so far. Linux support (through BlueZ) is issue #34 on GitHub.";

    /// <summary>The Bluetooth LE devices the system knows, by name. Empty where Bluetooth LE isn't supported.</summary>
    public static IReadOnlyList<BleDevice> List() => OperatingSystem.IsWindows() ? WindowsGattLink.PairedDevices() : [];

    public static IBleLink For(string address, Guid service, Guid characteristic) => OperatingSystem.IsWindows()
        ? new WindowsGattLink(address, service, characteristic)
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
