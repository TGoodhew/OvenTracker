using System.Globalization;
using System.IO.Ports;

namespace OvenTracker.Meter;

/// <summary>
/// An HP / Agilent / Keysight 34401A bench DMM with a type K thermocouple on its DC volts inputs (HI and LO),
/// through GPIB (by VISA) or its RS-232 port. A bench meter: mains powered, no battery, no auto power-off.
/// The thermocouple is a generic type K; --probe says which one, as for any meter.
/// It doesn't search the bus by itself, since writing to unknown instruments isn't always harmless:
/// give it --resource once, and it's remembered.
/// </summary>
public sealed class Hp34401ADriver : IMeterDriver
{
    public const string DriverName = "34401a";

    /// <summary>The 34401A's RS-232 port from the factory: 9600 baud, even parity, 7 data bits, and always 2 stop bits (User's Guide, pp. 156 and 160).</summary>
    public static readonly SerialSettings FactorySerial = new(9600, Parity.Even, 7, StopBits.Two);

    public string Name => DriverName;
    public string Description => "HP/Agilent 34401A with a type K thermocouple (DC mV), through GPIB or RS-232";

    /// <summary>The cold junction's temperature when none is given: a typical room.</summary>
    public const double AssumedColdJunctionC = 23;

    public string ResourceHelp => "a GPIB address through VISA, e.g. GPIB0::22::INSTR (or gpib:22), or a serial port, e.g. " +
        (OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0") + $", with its settings if they aren't the factory {FactorySerial} (e.g. " +
        (OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0") + ":9600,N,8,2)";

    public string NotFoundHelp =>
        "Give the meter's address with --resource, e.g. --resource GPIB0::22::INSTR (the address is under I/O MENU, HP-IB ADDR; 22 from the factory), " +
        "or --resource COM3 for RS-232 (choose RS-232 under I/O MENU, INTERFACE; the app expects 9600 baud, even parity, 7 data bits, and a cable that passes DTR). " +
        "oventracker meters --probe --meter 34401a asks each GPIB instrument VISA can see." + (Visa.IsAvailable ? "" : " " + Visa.MissingHelp);

    public string NotTemperatureHelp => "The 34401A isn't on DC volts: press Local (Shift) and let the app set it up again, or restart the run.";

    public IReadOnlyList<MeterOption> Options { get; } =
    [
        new("cj", "a temperature with its unit, e.g. 22C or 72F",
            "The cold junction's temperature: where the thermocouple meets the meter's terminals, normally the room's. Every reading is off by as much as this is. 23 °C when left off."),
        new("nplc", "0.02, 0.2, 1, 10, 100", "Integration time in power-line cycles: 10 (the default) is quiet and fast enough; 100 is quieter, at about 3 s a reading."),
    ];

    static readonly double[] Nplcs = [0.02, 0.2, 1, 10, 100];

    public string? CheckOptions(IReadOnlyDictionary<string, string> options)
    {
        foreach (var (key, value) in options)
        {
            switch (key)
            {
                case "cj" when ParseTemperatureC(value) is null:
                    return $"--meter-option cj={value}: give the cold junction's temperature with its unit, e.g. cj=22C or cj=72F.";
                case "nplc" when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !Nplcs.Contains(n):
                    return $"--meter-option nplc={value}: the 34401A takes {string.Join(", ", Nplcs.Select(x => x.ToString(CultureInfo.InvariantCulture)))}.";
                case "cj" or "nplc":
                    break;
                default:
                    return $"The 34401A has no --meter-option {key}; it takes cj and nplc.";
            }
        }
        return null;
    }

    /// <summary>"22C", "72F", "22.5 °C" as °C. Null without a unit, or if it isn't a temperature.</summary>
    public static double? ParseTemperatureC(string text)
    {
        var s = text.Trim().Replace("°", "").Replace(" ", "");
        if (s.Length < 2) return null;
        var unit = char.ToUpperInvariant(s[^1]);
        if (unit is not ('C' or 'F') || !double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        var c = unit == 'F' ? (v - 32) / 1.8 : v;
        return c is >= -40 and <= 80 ? c : null; // a room, a workshop or an ice bath, not an oven
    }

    /// <summary>The cold junction's temperature, and whether it was assumed rather than given.</summary>
    static (double C, bool Assumed) ColdJunctionFrom(IReadOnlyDictionary<string, string>? options) =>
        options is not null && options.TryGetValue("cj", out var v) && ParseTemperatureC(v) is { } c ? (c, false) : (AssumedColdJunctionC, true);

    /// <summary>What to say on connecting when the cold junction was assumed.</summary>
    public static string AssumedColdJunctionNote =>
        $"The cold junction (where the thermocouple meets the meter's terminals) is taken as {AssumedColdJunctionC:0} °C, a typical room: " +
        "every reading is off by as much as it differs. Give the room's temperature with --meter-option cj=22C (or cj=72F); it's remembered.";

    static double NplcFrom(IReadOnlyDictionary<string, string>? options) =>
        options is not null && options.TryGetValue("nplc", out var n) && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 10;

    /// <summary>Long enough for one reading at this integration time, DC volts with auto-zero (two integrations), with room to spare.</summary>
    static TimeSpan TimeoutFor(double nplc) => TimeSpan.FromSeconds(Math.Max(5, 4 * nplc / 50 + 3));

    public ProbeResult? Open(MeterRequest request)
    {
        var where = request.Resource ?? request.Preferred;
        if (where is null) return null;
        request.Progress?.Invoke(where);
        return Probe(where, keepOpen: true, request.Options);
    }

    public IReadOnlyList<MeterCandidate> Discover()
    {
        var list = Visa.Find("GPIB?*INSTR")
            .Select(r => new MeterCandidate(r, "GPIB instrument (VISA)", null, true, "GPIB"))
            .ToList();
        list.AddRange(PortFinder.List().Where(p => !p.IsBluetooth).Select(p => new MeterCandidate(
            p.Port, p.Description, $"RS-232 at {FactorySerial} if it's a 34401A", false, "Serial")));
        return list;
    }

    public ProbeResult Probe(string resource) => Probe(resource, keepOpen: false, null);

    /// <summary>Why a resource can't be a 34401A's, or null if it can: GPIB or a serial port (the 34401A has no USB or LAN).</summary>
    public static string? Check(string resource) => ResourceSpec.Parse(resource) switch
    {
        null => $"\"{resource}\" isn't an address the app understands (e.g. GPIB0::22::INSTR, COM3, or COM3:9600,E,7,2).",
        { Transport: Transport.Usb or Transport.Lan or Transport.Ble } => "the 34401A connects by GPIB or RS-232, not USB, LAN or Bluetooth.",
        { Visa: true } when !Visa.IsAvailable => Visa.MissingHelp,
        _ => null,
    };

    /// <summary>The link for a resource: VISA for GPIB (and ASRL), otherwise the serial port itself.</summary>
    public static IScpiPort PortFor(ResourceSpec spec, TimeSpan timeout) => spec.Visa
        ? new VisaScpiPort(spec.Address, timeout)
        : new SerialScpiPort(PortFinder.Normalize(spec.Address), spec.SettingsOr(FactorySerial), timeout);

    ProbeResult Probe(string resource, bool keepOpen, IReadOnlyDictionary<string, string>? options)
    {
        if (Check(resource) is { } problem) return new ProbeResult(resource, null, problem);
        var nplc = NplcFrom(options);
        var spec = ResourceSpec.Parse(resource)!;
        var port = PortFor(spec, keepOpen ? TimeoutFor(nplc) : TimeSpan.FromSeconds(3));
        var where = spec.Visa ? spec.Address : port.Location;
        if (!keepOpen)
        {
            // Just ask what it is: nothing is changed.
            try
            {
                port.Open();
                port.Clear(); // a reply left over from before mustn't be taken for the identity
                if (port.IsSerial) port.WriteLine("SYST:REM");
                port.WriteLine("*IDN?");
                var id = Hp34401AMeter.ParseIdentity(port.ReadLine().Trim());
                port.Release();
                return new ProbeResult(where, id, Hp34401AMeter.Is34401A(id) ? null : "not a 34401A");
            }
            catch (Exception ex) { return new ProbeResult(where, null, Explain(ex)); }
            finally { port.Dispose(); }
        }

        var (cj, assumed) = ColdJunctionFrom(options);
        var meter = new Hp34401AMeter(port, cj, nplc);
        try
        {
            var id = meter.Connect();
            if (Hp34401AMeter.Is34401A(id)) return new ProbeResult(where, id, null, meter, assumed ? [AssumedColdJunctionNote] : null);
            meter.Dispose();
            return new ProbeResult(where, id, "not a 34401A");
        }
        catch (Exception ex)
        {
            meter.Dispose();
            return new ProbeResult(where, null, Explain(ex));
        }
    }

    public static string Explain(Exception ex) => ex switch
    {
        TimeoutException => "no reply (check the address, the interface chosen under I/O MENU, and for RS-232 the settings and a cable that passes DTR)",
        DllNotFoundException or EntryPointNotFoundException => Visa.MissingHelp,
        _ => U1253BDriver.Explain(ex),
    };
}
