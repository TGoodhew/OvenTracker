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
    public string ResourceHelp => OperatingSystem.IsWindows()
        ? "a serial port, e.g. COM5, optionally with its settings (9600 8N1 by default), e.g. COM5:9600,N,8,1"
        : "a serial device, e.g. /dev/rfcomm0, optionally with its settings (9600 8N1 by default), e.g. /dev/ttyUSB0:9600,N,8,1";

    public string NotFoundHelp => OperatingSystem.IsWindows()
        ? "Check the meter is on, the U1177A is clipped on and switched on (its light blinks), and that it is paired in " +
          "Windows Bluetooth settings: pairing adds a \"Standard Serial over Bluetooth link\" port."
        : "Check the meter is on and the U1177A is clipped on and switched on (its light blinks). On Linux, pair it once " +
          "(bluetoothctl: scan on, pair, trust), then bind it to a serial device with \"sudo rfcomm bind 0 <address>\", " +
          "which makes /dev/rfcomm0. To open it you need to be in the dialout group.";

    public string NotTemperatureHelp => "Turn the dial to temperature (°C/°F) with the thermocouple plugged in.";

    /// <summary>
    /// The U1253B's auto power-off (APO) is 10 minutes from the factory, and reading it over the IR port
    /// doesn't restart the countdown: only a button, turning the dial, dynamic recording or 1 ms peak hold
    /// do (User's and Service Guide, p. 126). It doesn't report the setting remotely that we know of, so
    /// the meter reports it as unknown and the app shows the steps as a reminder.
    /// </summary>
    public AutoPowerOff AutoPowerOff { get; } = new(
        TimeSpan.FromMinutes(10),
        [
            "Hold the button that steps through the functions at the dial's current position (button 4 in the guide's keypad table) for more than a second, to enter Setup.",
            "Use the arrow buttons to go to APO, then press Hz to edit it.",
            "Set every digit to zero, which means OFF.",
            "Press Hz to save, then hold the same button for more than a second to leave Setup.",
        ],
        "Keysight U1253B User's and Service Guide, pp. 34, 110 and 126–127",
        HowToWake: "press any button on the meter, or turn the dial");

    static bool IsU1250(MeterIdentity id) => id.Model.StartsWith("U12", StringComparison.OrdinalIgnoreCase);

    public ProbeResult? Open(MeterRequest request)
    {
        if (request.Resource is { } given)
        {
            if (Check(given) is { } problem) return new ProbeResult(given, null, problem);
            var spec = ResourceSpec.Parse(given)!;
            var port = PortFinder.Normalize(spec.Address);
            request.Progress?.Invoke(port);
            return Probe(port, keepOpen: true, spec.Settings);
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
            p.IsBluetoothOutgoing,
            p.IsBluetooth ? "Bluetooth" : "Serial")).ToList();

    public ProbeResult Probe(string resource)
    {
        if (Check(resource) is { } problem) return new ProbeResult(resource, null, problem);
        var spec = ResourceSpec.Parse(resource)!;
        return Probe(PortFinder.Normalize(spec.Address), keepOpen: false, spec.Settings);
    }

    /// <summary>Why a resource can't be a U1253B's, or null if it can: the U1250 series connects only through a serial port.</summary>
    public static string? Check(string resource) => ResourceSpec.Parse(resource) switch
    {
        null => $"\"{resource}\" isn't a serial port the app understands (e.g. COM5, or COM5:9600,N,8,1).",
        { Visa: true, Transport: var t } => $"the U1253B connects only through a serial port (COM5, /dev/rfcomm0), not {ResourceSpec.Describe(t)} through VISA.",
        { Transport: not Transport.Serial and var t } => $"the U1253B connects only through a serial port (COM5, /dev/rfcomm0), not {ResourceSpec.Describe(t)}.",
        _ => null,
    };

    /// <param name="keepOpen">Hand back the connected meter instead of closing it: reopening a Bluetooth port straight away can fail.</param>
    /// <param name="settings">The port's settings, if given; the U1250's 9600 8N1 otherwise.</param>
    static ProbeResult Probe(string port, bool keepOpen, SerialSettings? settings = null)
    {
        var meter = new U1253BMeter(port, settings);
        var resource = meter.Location; // with the settings when they aren't the usual ones, so they're remembered
        try
        {
            var id = meter.Connect();
            if (keepOpen && IsU1250(id)) return new ProbeResult(resource, id, null, meter);
            meter.Dispose();
            return new ProbeResult(resource, id, IsU1250(id) ? null : "not a U1250-series meter");
        }
        catch (Exception ex)
        {
            meter.Dispose();
            return new ProbeResult(resource, null, Explain(ex));
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
