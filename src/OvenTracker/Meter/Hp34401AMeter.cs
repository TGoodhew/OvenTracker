using System.Globalization;

namespace OvenTracker.Meter;

/// <summary>
/// An HP / Agilent / Keysight 34401A reading a type K thermocouple. The 34401A has no temperature function, so it
/// reads the thermocouple's voltage on its 100 mV DC range, with the input at its high impedance (over 10 GΩ, so
/// the thermocouple isn't loaded). The app converts it with the type K reference function and the cold junction's
/// temperature: where the thermocouple meets the meter's copper terminals, which the meter can't measure, so it's
/// given (<c>--meter-option cj=</c>). Reached through GPIB (VISA) or RS-232. Found and opened by
/// <see cref="Hp34401ADriver"/>.
/// </summary>
/// <param name="coldJunctionC">The cold junction's temperature, °C: every reading is off by as much as this is.</param>
/// <param name="nplc">Integration time in power-line cycles: 10 (the default) gives 6½ digits and rejects mains hum.</param>
public sealed class Hp34401AMeter(IScpiPort port, double coldJunctionC, double nplc = 10) : ITemperatureMeter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>What the 34401A sends for an overload: more than the range, as an open thermocouple's floating input can give.</summary>
    public const double OverloadValue = 9.9e37;

    /// <summary>The 100 mV range: type K reaches 54.9 mV at its top, 1372 °C.</summary>
    public const double RangeV = 0.1;

    MeterConfig? config;
    int readsSinceConfig;
    bool connected;

    public string Location => port.Location;
    public double ColdJunctionC => coldJunctionC;
    public MeterConfig? LastConfig => config;

    /// <summary>The setup is checked again every this many readings, in case someone pressed Local and changed it.</summary>
    public int ConfigEvery { get; init; } = 30;

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
            if (!Is34401A(id))
            {
                // The driver says it isn't one; nothing is changed on another instrument, and on RS-232 it's given its front panel back.
                try { port.Release(); } catch { }
                return id;
            }
            Send("*RST");
            Send("*CLS");
            Send(string.Create(Inv, $"CONF:VOLT:DC {RangeV:0.0}"));
            Send(string.Create(Inv, $"VOLT:DC:NPLC {nplc}"));
            Send("INP:IMP:AUTO ON"); // over 10 GΩ on the 100 mV range, rather than 10 MΩ
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
        config = ParseConfig(Query("CONF?"), coldJunctionC);
        readsSinceConfig = 0;
        return config;
    }

    /// <summary>
    /// The reply to CONF?, e.g. <c>"VOLT +1.000000E-01,+3.000000E-07"</c>: only DC volts gives a temperature (AC volts
    /// is <c>VOLT:AC</c>). The sensor is a generic type K thermocouple; <c>--probe</c> says which one.
    /// </summary>
    public static MeterConfig ParseConfig(string reply, double coldJunctionC)
    {
        var text = reply.Trim().Trim('"');
        var function = text.Split(' ', 2)[0].ToUpperInvariant();
        return function == "VOLT"
            ? new MeterConfig(string.Create(Inv, $"DC mV, type K, cold junction {coldJunctionC:0.0} °C"), true, null, Sensor.Thermocouple("K"))
            : new MeterConfig(text, false, null);
    }

    public MeterReading Read()
    {
        if (config is null || ++readsSinceConfig >= ConfigEvery) ReadConfig();
        var now = DateTimeOffset.UtcNow;
        if (config is { IsTemperature: false }) return new MeterReading(now, ReadingStatus.NotTemperature, null, config.Description);
        return ToReading(now, Query("READ?"), coldJunctionC);
    }

    /// <summary>A reply to READ? (volts) as a temperature.</summary>
    public static MeterReading ToReading(DateTimeOffset time, string reply, double coldJunctionC)
    {
        if (!double.TryParse(reply.Trim(), NumberStyles.Float, Inv, out var volts) || !double.IsFinite(volts)) return new(time, ReadingStatus.Error, null, reply);
        if (Math.Abs(volts) >= OverloadValue * 0.99) return new(time, ReadingStatus.Overload, null, reply);
        // Beyond type K's range isn't a thermocouple reading: a wiring fault, or no thermocouple at all.
        return TypeK.TemperatureFor(volts * 1000, coldJunctionC) is { } c
            ? new(time, ReadingStatus.Ok, c, reply)
            : new(time, ReadingStatus.Error, null, reply);
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
