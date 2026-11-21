using OvenTracker.Meter.Ble;

namespace OvenTracker.Meter;

/// <summary>
/// An EEVblog 121GW over Bluetooth LE, with a type K thermocouple in its temperature mode. On Windows the
/// meter must be paired first (Windows Bluetooth settings, Add device). It finds a paired 121GW by itself.
/// On Linux it isn't supported yet (issue #34).
/// </summary>
public sealed class Eevblog121GwDriver : IMeterDriver
{
    public const string DriverName = "121gw";

    /// <summary>The 121GW's auto power-off from the factory: 30 minutes (its manual, as relayed by the dmm-tools write-up).</summary>
    public static readonly TimeSpan DefaultAutoPowerOff = TimeSpan.FromMinutes(30);

    public string Name => DriverName;
    public string Description => "EEVblog 121GW over Bluetooth LE";

    public string ResourceHelp => "a paired Bluetooth LE meter, by address or name, e.g. ble:C4:BE:84:12:34:56 or ble:121GW (found by itself if left out)";

    public string NotFoundHelp => BleLinks.IsSupported
        ? "Check the meter is on, with Bluetooth on (BT shows on its display; holding the 1ms PEAK button turns Bluetooth on or off), " +
          "and that it's paired in Windows Bluetooth settings (Add device, Bluetooth, then choose 121GW)."
        : BleLinks.NotSupportedHelp;

    public string NotTemperatureHelp => "Turn the dial to temperature with the thermocouple plugged in, and turn off HOLD, REL and MIN/MAX.";

    /// <summary>
    /// The 121GW reports whether auto power-off is armed (the APO icon, in every packet), so the app warns only when it is.
    /// The setup item shows as APO.oN or APO.oF. The button presses below weren't checked against the manual.
    /// </summary>
    public AutoPowerOff AutoPowerOff { get; } = new(
        DefaultAutoPowerOff,
        [
            "Press SETUP on the meter to enter its setup menu.",
            "Step through the items until the display shows APO.oN.",
            "Change it to APO.oF, then leave setup.",
            "If that doesn't work on your meter, see the auto power-off section of the 121GW manual. Logging to the SD card also keeps it on.",
        ],
        "EEVblog 121GW manual, auto power-off (as relayed by the dmm-tools 121GW write-up; the steps weren't checked against the manual)",
        HowToWake: "turn the dial, or press a button, then turn Bluetooth on again if BT isn't shown");

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
        order.AddRange(Paired121Gws());
        foreach (var d in order.DistinctBy(d => d.Address))
        {
            request.Progress?.Invoke($"{d.Name} {d.Address} (Bluetooth LE can take a few seconds)");
            var r = Probe(d, keepOpen: true);
            if (r.Meter is not null) return r;
        }
        return null;
    }

    static bool Is121Gw(BleDevice d) => d.Name.StartsWith(Gw121Protocol.AdvertisedName, StringComparison.OrdinalIgnoreCase);

    static IEnumerable<BleDevice> Paired121Gws() => BleLinks.List().Where(Is121Gw);

    /// <summary>A paired device by address, or by name (a 121GW's name).</summary>
    static BleDevice? Resolve(string what)
    {
        var devices = BleLinks.List();
        if (BleLinks.NormalizeAddress(what) is { } address)
            return devices.FirstOrDefault(d => d.Address == address) ?? new BleDevice(Gw121Protocol.AdvertisedName, address);
        return devices.FirstOrDefault(d => string.Equals(d.Name, what, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Name.StartsWith(what, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<MeterCandidate> Discover() =>
        BleLinks.List().Select(d => new MeterCandidate("ble:" + d.Address, d.Name, Is121Gw(d) ? "paired 121GW" : "paired Bluetooth LE device", Is121Gw(d), "Bluetooth LE")).ToList();

    public ProbeResult Probe(string resource)
    {
        if (!BleLinks.IsSupported) return new ProbeResult(resource, null, BleLinks.NotSupportedHelp);
        if (Check(resource) is { } problem) return new ProbeResult(resource, null, problem);
        var device = Resolve(ResourceSpec.Parse(resource)!.Address);
        return device is null ? new ProbeResult(resource, null, "no such paired Bluetooth LE device") : Probe(device, keepOpen: false);
    }

    /// <summary>Why a resource can't be a 121GW's, or null if it can: it connects only by Bluetooth LE.</summary>
    public static string? Check(string resource) => ResourceSpec.Parse(resource) switch
    {
        { Transport: Transport.Ble } => null,
        null => $"\"{resource}\" isn't a Bluetooth LE address the app understands (e.g. ble:C4:BE:84:12:34:56 or ble:121GW).",
        { Transport: var t } => $"the 121GW connects by Bluetooth LE (ble:<address>), not {ResourceSpec.Describe(t)}.",
    };

    static ProbeResult Probe(BleDevice device, bool keepOpen)
    {
        var meter = new Eevblog121GwMeter(BleLinks.For(device.Address, Gw121Protocol.Service, Gw121Protocol.Characteristic));
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
            return new ProbeResult(meter.Location, null, ex is TimeoutException ? "no data (is Bluetooth on? BT shows on the display)" : ex.Message);
        }
    }
}
