using System.Globalization;

namespace OvenTracker.Meter;

/// <summary>
/// An HP / Agilent / Keysight 34401A reading a platinum RTD (a PT100) by 4-wire resistance, converted to
/// temperature with the IEC 60751 equation: the 34401A has no temperature function of its own. Reached through
/// GPIB (VISA) or RS-232. Found and opened by <see cref="Hp34401ADriver"/>.
/// </summary>
/// <param name="nplc">Integration time in power-line cycles: 10 (the default) gives 6½ digits and rejects mains hum.</param>
public sealed class Hp34401AMeter(IScpiPort port, Rtd rtd, double nplc = 10) : ITemperatureMeter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>What the 34401A sends for an overload: an open or missing RTD.</summary>
    public const double OverloadValue = 9.9e37;

    MeterConfig? config;
    int readsSinceConfig;
    bool connected;

    public string Location => port.Location;
    public Rtd Rtd => rtd;
    public MeterConfig? LastConfig => config;

    /// <summary>The setup is checked again every this many readings, in case someone pressed Local and changed it.</summary>
    public int ConfigEvery { get; init; } = 30;

    /// <summary>The 1 kΩ range for a PT100 (up to 1.2 kΩ, about 850 °C), 10 kΩ for a PT500 or PT1000.</summary>
    public double Range
    {
        get
        {
            var top = rtd.ResistanceAt(400);
            foreach (var r in (double[])[100, 1e3, 1e4, 1e5, 1e6])
                if (r * 1.2 >= top) return r;
            return 1e6;
        }
    }

    public MeterIdentity Connect()
    {
        Disconnect();
        port.Open();
        try
        {
            port.Clear();
            // Over RS-232 the 34401A must be put in remote first (User's Guide, p. 160).
            if (port.IsSerial) Send("SYST:REM");
            var idn = Query("*IDN?");
            var id = ParseIdentity(idn);
            if (!Is34401A(id)) return id; // the driver says it isn't one; nothing is changed on another instrument
            Send("*RST");
            Send("*CLS");
            Send(string.Create(Inv, $"CONF:FRES {Range:0E+0}"));
            Send(string.Create(Inv, $"FRES:NPLC {nplc}"));
            Send("ZERO:AUTO ON");
            Query("*OPC?"); // waits for it to take all that in, which matters at 9600 baud
            if (Query("SYST:ERR?") is var err && !err.StartsWith("+0", StringComparison.Ordinal))
                throw new MeterException($"The 34401A didn't accept its setup: {err}");
            connected = true;
            ReadConfig();
            return id;
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    public static bool Is34401A(MeterIdentity id) => id.Model.Contains("34401A", StringComparison.OrdinalIgnoreCase);

    /// <summary>"HEWLETT-PACKARD,34401A,0,11-5-2": the 34401A gives 0 for its serial number.</summary>
    public static MeterIdentity ParseIdentity(string idn)
    {
        var p = idn.Split(',').Select(x => x.Trim()).ToArray();
        string At(int i) => i < p.Length ? p[i] : "";
        return new MeterIdentity(At(0), At(1), At(2), At(3));
    }

    public void Disconnect()
    {
        if (connected)
        {
            try { port.Release(); } catch { }
        }
        connected = false;
        config = null;
        port.Dispose();
    }

    public MeterConfig ReadConfig()
    {
        config = ParseConfig(Query("CONF?"), rtd);
        readsSinceConfig = 0;
        return config;
    }

    /// <summary>The reply to CONF?, e.g. <c>"FRES +1.000000E+03,+1.000000E-04"</c>: only 4-wire ohms gives a temperature.</summary>
    public static MeterConfig ParseConfig(string reply, Rtd rtd)
    {
        var text = reply.Trim().Trim('"');
        var function = text.Split(' ', 2)[0].ToUpperInvariant();
        return function == "FRES"
            ? new MeterConfig($"4-wire Ω, {rtd.Sensor.Name}", true, null, rtd.Sensor)
            : new MeterConfig(text, false, null);
    }

    public MeterReading Read()
    {
        if (config is null || ++readsSinceConfig >= ConfigEvery) ReadConfig();
        var now = DateTimeOffset.UtcNow;
        if (config is { IsTemperature: false }) return new MeterReading(now, ReadingStatus.NotTemperature, null, config.Description);
        return ToReading(now, Query("READ?"), rtd);
    }

    /// <summary>A reply to READ? (ohms) as a temperature.</summary>
    public static MeterReading ToReading(DateTimeOffset time, string reply, Rtd rtd)
    {
        if (!double.TryParse(reply.Trim(), NumberStyles.Float, Inv, out var ohms)) return new(time, ReadingStatus.Error, null, reply);
        if (Math.Abs(ohms) >= OverloadValue * 0.99) return new(time, ReadingStatus.Overload, null, reply);
        // Below about -200 °C (18.5 Ω for a PT100) isn't an oven, it's a short or a wiring fault.
        if (ohms < rtd.R0 * 0.185) return new(time, ReadingStatus.Error, null, reply);
        return new(time, ReadingStatus.Ok, rtd.TemperatureFor(ohms), reply);
    }

    public void Beep() => Send("SYST:BEEP");

    void Send(string command)
    {
        port.WriteLine(command);
        if (port.IsSerial) Thread.Sleep(50); // its input buffer is small, and .NET doesn't pace on DSR
    }

    string Query(string command)
    {
        port.WriteLine(command);
        var reply = port.ReadLine().Trim();
        return reply;
    }

    public void Dispose() => Disconnect();
}
