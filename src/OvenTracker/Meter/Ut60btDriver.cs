using OvenTracker.Meter.Ble;

namespace OvenTracker.Meter;

/// <summary>
/// A UNI-T UT60BT over Bluetooth LE, with a type K thermocouple in its temperature mode. On Windows the meter must
/// be paired first (Windows Bluetooth settings, Add device). On Linux it goes through BlueZ, which needs no pairing
/// but finds a known device faster. It finds a UT60BT by itself.
/// </summary>
public sealed class Ut60btDriver : IMeterDriver
{
    public const string DriverName = "ut60bt";

    /// <summary>The UT60BT switches off after 15 minutes without being used (its user manual).</summary>
    public static readonly TimeSpan DefaultAutoPowerOff = TimeSpan.FromMinutes(15);

    const string BluetoothOn = "hold SELECT until BT shows on its display; Bluetooth is off each time it's switched on";

    public string Name => DriverName;
    public bool HasBattery => true;
    public string Description => "UNI-T UT60BT over Bluetooth LE";

    public string ResourceHelp => "a paired Bluetooth LE meter, by address or name, e.g. ble:C4:BE:84:12:34:56 or ble:UT60BT; found by itself if left out";

    public string NotFoundHelp =>
        OperatingSystem.IsWindows()
            ? $"Check the meter is on, with Bluetooth on ({BluetoothOn}), " +
              "and that it's paired in Windows Bluetooth settings (Add device, Bluetooth, then choose UT60BT)."
        : OperatingSystem.IsLinux()
            ? $"Check the meter is on, with Bluetooth on ({BluetoothOn}), " +
              "and that BlueZ is running (sudo systemctl enable --now bluetooth). Making it known speeds things up: " +
              "bluetoothctl, then scan on, wait for UT60BT, then trust <address>."
        : BleLinks.NotSupportedHelp;

    public string NotTemperatureHelp =>
        "Turn the dial to temperature (°C or °F; SELECT switches) with the type K thermocouple plugged in, and turn off HOLD, REL and MAX/MIN.";

    /// <summary>
    /// The UT60BT doesn't say whether auto power-off is on, so the app always warns. It can only be turned off as the
    /// meter is switched on, and comes back the next time.
    /// </summary>
    public AutoPowerOff AutoPowerOff { get; } = new(
        DefaultAutoPowerOff,
        [
            "Turn the dial to OFF.",
            "Hold SELECT while turning the dial to temperature, and keep holding it for more than 2 seconds.",
            "Auto power-off then stays off until the meter is next switched off. Turn Bluetooth on again afterwards (hold SELECT until BT shows).",
        ],
        "UNI-T UT60EU/UT60BT User Manual, auto power-off and Bluetooth",
        HowToWake: "turn the dial to OFF and back, then hold SELECT until BT shows to turn Bluetooth on again");

    public ProbeResult? Open(MeterRequest request)
    {
        if (!BleLinks.IsSupported) return new ProbeResult(request.Resource ?? "ble", null, BleLinks.NotSupportedHelp);
        if (request.Resource is { } given)
        {
            if (Check(given) is { } problem) return new ProbeResult(given, null, problem);
            if (Resolve(ResourceSpec.Parse(given)!.Address) is not { } device)
                return new ProbeResult(given, null, $"no paired Bluetooth LE device called or at \"{ResourceSpec.Parse(given)!.Address}\". {NotFoundHelp}");
            request.Progress?.Invoke($"{device.Name} {device.Address}");
            return Probe(device, keepOpen: true);
        }

        var order = new List<BleDevice>();
        if (request.Preferred is { } pref && ResourceSpec.Parse(pref) is { Transport: Transport.Ble } ps && Resolve(ps.Address) is { } last) order.Add(last);
        order.AddRange(BleLinks.List().Where(IsUt60bt));
        if (order.Count == 0 && OperatingSystem.IsLinux())
        {
            request.Progress?.Invoke("a UT60BT nearby (scanning for a few seconds)");
            order.AddRange(BleLinks.Scan().Where(IsUt60bt));
        }
        foreach (var d in order.DistinctBy(d => d.Address))
        {
            request.Progress?.Invoke($"{d.Name} {d.Address} (Bluetooth LE can take a few seconds)");
            var r = Probe(d, keepOpen: true);
            if (r.Meter is not null) return r;
        }
        return null;
    }

    public static bool IsUt60bt(BleDevice d) => d.Name.StartsWith(Ut60btProtocol.AdvertisedName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A paired device by address, or by name (a UT60BT's name).</summary>
    static BleDevice? Resolve(string what)
    {
        var devices = BleLinks.List();
        if (BleLinks.NormalizeAddress(what) is { } address)
            return devices.FirstOrDefault(d => d.Address == address) ?? new BleDevice(Ut60btProtocol.AdvertisedName, address);
        return Named(devices, what) ?? (OperatingSystem.IsLinux() ? Named(BleLinks.Scan(), what) : null);

        static BleDevice? Named(IReadOnlyList<BleDevice> list, string name) =>
            list.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(d => d.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<MeterCandidate> Discover() =>
        BleLinks.List().Select(d => new MeterCandidate("ble:" + d.Address, d.Name, IsUt60bt(d) ? "paired UT60BT" : "paired Bluetooth LE device", IsUt60bt(d), "Bluetooth LE")).ToList();

    public ProbeResult Probe(string resource)
    {
        if (!BleLinks.IsSupported) return new ProbeResult(resource, null, BleLinks.NotSupportedHelp);
        if (Check(resource) is { } problem) return new ProbeResult(resource, null, problem);
        var device = Resolve(ResourceSpec.Parse(resource)!.Address);
        return device is null ? new ProbeResult(resource, null, "no such paired Bluetooth LE device") : Probe(device, keepOpen: false);
    }

    /// <summary>Why a resource can't be a UT60BT's, or null if it can: it connects only by Bluetooth LE.</summary>
    public static string? Check(string resource) => ResourceSpec.Parse(resource) switch
    {
        { Transport: Transport.Ble } => null,
        null => $"\"{resource}\" isn't a Bluetooth LE address the app understands (e.g. ble:C4:BE:84:12:34:56 or ble:UT60BT).",
        { Transport: var t } => $"the UT60BT connects by Bluetooth LE (ble:<address>), not {ResourceSpec.Describe(t)}.",
    };

    static ProbeResult Probe(BleDevice device, bool keepOpen)
    {
        var meter = new Ut60btMeter(BleLinks.For(device.Address, Ut60btProtocol.Service, Ut60btProtocol.Notify, Ut60btProtocol.Write));
        try
        {
            var id = meter.Connect();
            if (keepOpen) return new ProbeResult(meter.Location, id, null, meter);
            meter.Dispose();
            return new ProbeResult(meter.Location, id, null);
        }
        catch (Exception ex)
        {
            meter.Dispose();
            return new ProbeResult(meter.Location, null, ex is TimeoutException ? "no data (is Bluetooth on? Hold SELECT until BT shows)" : ex.Message);
        }
    }
}
