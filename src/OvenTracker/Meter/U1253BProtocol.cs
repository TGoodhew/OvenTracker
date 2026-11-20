using System.Globalization;

namespace OvenTracker.Meter;

/// <summary>
/// The text protocol of the Keysight (Agilent) U1250-series handheld meters, as spoken through the
/// U1173 IR-USB cable or the U1177A IR-to-Bluetooth adapter: 9600 baud 8N1, a command ends in LF,
/// a reply ends in CR LF. A reply of <c>*E</c> means the command was not accepted; other lines that
/// start with <c>*</c> are events the meter sends on its own (dial turned, button pressed).
/// </summary>
public static class U1253BProtocol
{
    public const int BaudRate = 9600;
    public const string Identify = "*IDN?";
    public const string Fetch = "FETC?";
    public const string Configuration = "CONF?";
    public const string Battery = "SYST:BATT?";
    public const string BeepCommand = "SYST:BEEP";

    /// <summary>What the meter sends for OL / −OL: ±9.9E+37.</summary>
    public static bool IsOverload(double value) => double.IsNaN(value) || Math.Abs(value) >= 9.0e37;

    public static bool TryParseNumber(string reply, out double value) =>
        double.TryParse(reply.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static bool IsCommandError(string line) => line.Trim() == "*E";

    /// <summary>An unsolicited event line such as <c>*3</c> (dial moved) or <c>*L</c> (button).</summary>
    public static bool IsEvent(string line)
    {
        var s = line.Trim();
        return s.Length is >= 2 and <= 3 && s[0] == '*' && !IsCommandError(s);
    }

    /// <summary>
    /// Parses a CONF? reply. Temperature on a U1253B reads <c>"TEMP:K CEL"</c> or <c>"TEMP:K FAR"</c>
    /// (<c>T1:K</c> on some U1250 models); the letter after the colon is the thermocouple type.
    /// </summary>
    public static MeterConfig ParseConfig(string reply)
    {
        var s = reply.Trim().Trim('"').Trim();
        var parts = s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var function = parts.Length > 0 ? parts[0] : "";
        var isTemp = function.StartsWith("TEMP", StringComparison.OrdinalIgnoreCase)
                     || (function.Length >= 3 && function[0] is 'T' or 't' && function.Contains(':') && char.IsDigit(function[1]));
        TempUnit? unit = null;
        foreach (var p in parts.Skip(1))
        {
            if (p.Equals("CEL", StringComparison.OrdinalIgnoreCase)) unit = TempUnit.Celsius;
            else if (p.StartsWith("FA", StringComparison.OrdinalIgnoreCase)) unit = TempUnit.Fahrenheit;
        }
        var colon = function.IndexOf(':');
        var sensor = isTemp && colon >= 0 ? Sensor.Thermocouple(function[(colon + 1)..]) : null;
        return new MeterConfig(s, isTemp, unit, sensor);
    }

    public static MeterIdentity ParseIdentity(string reply)
    {
        var f = reply.Trim().Split(',');
        string At(int i) => i < f.Length ? f[i].Trim() : "?";
        return new MeterIdentity(At(0), At(1), At(2), At(3));
    }

    /// <summary>Turns a FETC? reply into a reading, given the meter's current configuration.</summary>
    public static MeterReading ToReading(DateTimeOffset time, string reply, MeterConfig? config)
    {
        if (!TryParseNumber(reply, out var v)) return new MeterReading(time, ReadingStatus.Error, null, reply);
        if (config is not { IsTemperature: true }) return new MeterReading(time, ReadingStatus.NotTemperature, null, reply);
        if (IsOverload(v)) return new MeterReading(time, ReadingStatus.Overload, null, reply);
        var c = config.Unit == TempUnit.Fahrenheit ? Temp.FToC(v) : v;
        return new MeterReading(time, ReadingStatus.Ok, c, reply);
    }
}
