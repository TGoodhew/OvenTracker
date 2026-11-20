namespace OvenTracker.Meter;

/// <summary>
/// A Keysight (Agilent) U1253B, or another U1250-series meter, on a serial port: the U1177A's
/// outgoing Bluetooth port, or a U1173 IR-USB cable. It finds the meter by itself on the outgoing
/// Bluetooth ports.
/// </summary>
public sealed class U1253BDriver : IMeterDriver
{
    public const string DriverName = "u1253b";

    public string Name => DriverName;
    public string Description => "Keysight U1253B through a U1177A Bluetooth adapter (or a U1173 cable)";
    public string ResourceHelp => "a serial port, e.g. COM5";

    public string NotFoundHelp =>
        "Check the meter is on, the U1177A is clipped on and switched on (its light blinks), and that it is paired in " +
        "Windows Bluetooth settings: pairing adds a \"Standard Serial over Bluetooth link\" port.";

    public string NotTemperatureHelp => "Turn the dial to temperature (°C/°F) with the thermocouple plugged in.";

    static bool IsU1250(MeterIdentity id) => id.Model.StartsWith("U12", StringComparison.OrdinalIgnoreCase);

    public ProbeResult? Open(MeterRequest request)
    {
        if (request.Resource is { } port)
        {
            request.Progress?.Invoke(port);
            return Probe(port, keepOpen: true);
        }

        // The port that worked last time, then each outgoing Bluetooth port. Other serial ports are
        // only tried when named, since writing to an unknown serial device is not always harmless.
        var order = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Preferred)) order.Add(request.Preferred.ToUpperInvariant());
        order.AddRange(PortFinder.List().Where(p => p.IsBluetoothOutgoing).Select(p => p.Port));
        foreach (var p in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            request.Progress?.Invoke($"{p} (a Bluetooth port can take a few seconds)");
            var r = Probe(p, keepOpen: true);
            if (r.Meter is not null) return r;
        }
        return null;
    }

    public IReadOnlyList<MeterCandidate> Discover() =>
        PortFinder.List().Select(p => new MeterCandidate(
            p.Port,
            p.Description,
            p.IsBluetoothOutgoing ? $"outgoing Bluetooth {p.BluetoothAddress}" : p.IsBluetooth ? "incoming Bluetooth (not usable)" : null,
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
            return new ProbeResult(port, null, ex is TimeoutException ? "no reply" : ex.Message);
        }
    }
}
