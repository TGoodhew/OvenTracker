namespace OvenTracker.Meter;

/// <summary>
/// A Keysight (Agilent) U1253B, or another U1250-series meter, on a serial port: the U1177A's
/// Bluetooth serial link (an outgoing Bluetooth COM port on Windows, an RFCOMM device on Linux),
/// or a U1173 IR-USB cable. It finds the meter by itself on the Bluetooth links.
/// </summary>
public sealed class U1253BDriver : IMeterDriver
{
    public const string DriverName = "u1253b";

    public string Name => DriverName;
    public string Description => "Keysight U1253B through a U1177A Bluetooth adapter (or a U1173 cable)";
    public string ResourceHelp => OperatingSystem.IsWindows() ? "a serial port, e.g. COM5" : "a serial device, e.g. /dev/rfcomm0";

    public string NotFoundHelp => OperatingSystem.IsWindows()
        ? "Check the meter is on, the U1177A is clipped on and switched on (its light blinks), and that it is paired in " +
          "Windows Bluetooth settings: pairing adds a \"Standard Serial over Bluetooth link\" port."
        : "Check the meter is on and the U1177A is clipped on and switched on (its light blinks). On Linux, pair it once " +
          "(bluetoothctl: scan on, pair, trust), then bind it to a serial device with \"sudo rfcomm bind 0 <address>\", " +
          "which makes /dev/rfcomm0. To open it you need to be in the dialout group.";

    public string NotTemperatureHelp => "Turn the dial to temperature (°C/°F) with the thermocouple plugged in.";

    static bool IsU1250(MeterIdentity id) => id.Model.StartsWith("U12", StringComparison.OrdinalIgnoreCase);

    public ProbeResult? Open(MeterRequest request)
    {
        if (request.Resource is { } given)
        {
            var port = PortFinder.Normalize(given);
            request.Progress?.Invoke(port);
            return Probe(port, keepOpen: true);
        }

        // The port that worked last time, then each outgoing Bluetooth port. Other serial ports are
        // only tried when named, since writing to an unknown serial device is not always harmless.
        var order = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Preferred)) order.Add(PortFinder.Normalize(request.Preferred));
        order.AddRange(PortFinder.List().Where(p => p.IsBluetoothOutgoing).Select(p => p.Port));
        foreach (var p in order.Distinct(PortFinder.NameComparer))
        {
            request.Progress?.Invoke($"{p} (a Bluetooth link can take a few seconds)");
            var r = Probe(p, keepOpen: true);
            if (r.Meter is not null) return r;
        }
        return null;
    }

    public IReadOnlyList<MeterCandidate> Discover() =>
        PortFinder.List().Select(p => new MeterCandidate(
            p.Port,
            p.Description,
            p.IsBluetoothOutgoing ? $"{(OperatingSystem.IsWindows() ? "outgoing " : "")}Bluetooth {p.BluetoothAddress}".TrimEnd()
                : p.IsBluetooth ? "incoming Bluetooth (not usable)" : null,
            p.IsBluetoothOutgoing)).ToList();

    public ProbeResult Probe(string resource) => Probe(resource, keepOpen: false);

    /// <param name="keepOpen">Hand back the connected meter instead of closing it: reopening a Bluetooth port straight away can fail.</param>
    static ProbeResult Probe(string port, bool keepOpen)
    {
        var meter = new U1253BMeter(port);
        try
        {
            var id = meter.Connect();
            if (keepOpen && IsU1250(id)) return new ProbeResult(port, id, null, meter);
            meter.Dispose();
            return new ProbeResult(port, id, IsU1250(id) ? null : "not a U1250-series meter");
        }
        catch (Exception ex)
        {
            meter.Dispose();
            return new ProbeResult(port, null, Explain(ex));
        }
    }

    /// <summary>Why a port couldn't be used, in terms of what to do about it.</summary>
    public static string Explain(Exception ex)
    {
        if (ex is TimeoutException) return "no reply";
        var denied = ex as UnauthorizedAccessException ?? ex.InnerException as UnauthorizedAccessException;
        if (denied is null) return ex.Message;
        return OperatingSystem.IsWindows()
            ? "in use by another program"
            : "permission denied: add yourself to the dialout group (sudo usermod -aG dialout $USER), then log in again";
    }
}
