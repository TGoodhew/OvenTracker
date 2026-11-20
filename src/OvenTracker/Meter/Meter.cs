namespace OvenTracker.Meter;

public enum ReadingStatus { Ok, Overload, NotTemperature, Error }

/// <param name="Celsius">The reading in °C, when it is a valid temperature.</param>
/// <param name="Raw">Exactly what the meter sent, for the recording.</param>
public sealed record MeterReading(DateTimeOffset Time, ReadingStatus Status, double? Celsius, string Raw);

/// <summary>What the meter is set to measure, as far as it can say.</summary>
/// <param name="Description">A short description for people, e.g. <c>TEMP:K CEL</c> or <c>type K, °C</c>.</param>
/// <param name="IsTemperature">Whether it is measuring temperature, so its readings can be used.</param>
/// <param name="Unit">The unit the meter itself shows, if it says.</param>
/// <param name="Sensor">The temperature sensor it is set up for, if it says.</param>
public sealed record MeterConfig(string Description, bool IsTemperature, TempUnit? Unit, Sensor? Sensor = null);

public sealed record MeterIdentity(string Manufacturer, string Model, string Serial, string Firmware)
{
    public override string ToString() => $"{Manufacturer} {Model} (s/n {Serial}, fw {Firmware})";
}

/// <summary>
/// A temperature sensor and its standard tolerance: the larger of <see cref="LimitC"/> and
/// <see cref="LimitFraction"/> of the temperature, or the two added together when
/// <see cref="LimitsAdd"/> is set (as RTD classes are written).
/// </summary>
/// <param name="Name">For people, e.g. "type K thermocouple" or "Fluke 80BK-A type K probe".</param>
/// <param name="MaxC">The highest temperature the probe is rated for, when known (260 °C for a Fluke 80BK-A).</param>
public sealed record Sensor(string Name, double LimitC, double LimitFraction, bool LimitsAdd = false, double? MaxC = null)
{
    /// <summary>A generic sensor type ("type K thermocouple"), as opposed to a particular probe: its tolerance is that grade's.</summary>
    public bool IsGenericType => Name.StartsWith("type ", StringComparison.Ordinal);

    /// <summary>The sensor's standard tolerance at a temperature, in °C either way.</summary>
    public double ToleranceC(double c) =>
        LimitsAdd ? LimitC + LimitFraction * Math.Abs(c) : Math.Max(LimitC, LimitFraction * Math.Abs(c));

    /// <summary>Standard-grade thermocouples, above 0 °C (ASTM E230 standard limits of error).</summary>
    public static Sensor? Thermocouple(string type) => type.Trim().ToUpperInvariant() switch
    {
        "K" => new("type K thermocouple", 2.2, 0.0075),
        "J" => new("type J thermocouple", 2.2, 0.0075),
        "N" => new("type N thermocouple", 2.2, 0.0075),
        "T" => new("type T thermocouple", 1.0, 0.0075),
        "E" => new("type E thermocouple", 1.7, 0.005),
        _ => null,
    };
}

/// <summary>
/// A thermometer the app can poll. Calls are synchronous and come from one thread. Only
/// <see cref="Location"/>, <see cref="Connect"/>, <see cref="Disconnect"/> and <see cref="Read"/>
/// are required: a meter that can't report a battery, beep, say what it is set to or report its auto
/// power-off leaves the rest as they are, and the app does without. docs/adding-a-meter.md explains each.
/// </summary>
public interface ITemperatureMeter : IDisposable
{
    /// <summary>Where the meter is, e.g. "COM5" or "GPIB0::22::INSTR".</summary>
    string Location { get; }

    MeterIdentity Connect();
    void Disconnect();
    MeterReading Read();

    /// <summary>Asks the meter what it is set to measure. Null if it can't say.</summary>
    MeterConfig? ReadConfig() => null;

    /// <summary>What the meter last said it was set to, without asking again where it keeps one.</summary>
    MeterConfig? LastConfig => ReadConfig();

    double? ReadBatteryPercent() => null;

    /// <summary>
    /// What the meter's auto power-off is set to, if it can say. Unknown by default; the driver's
    /// <see cref="IMeterDriver.AutoPowerOff"/> says whether the meter has one at all.
    /// </summary>
    AutoPowerOffSetting ReadAutoPowerOff() => AutoPowerOffSetting.Unknown;

    /// <summary>Makes the meter beep when a setpoint is done, if it can.</summary>
    void Beep() { }
}

/// <summary>A meter that drives a simulated oven, so the app can tell it what the oven is set to.</summary>
public interface IOvenSimulation
{
    void SetOven(OvenMode mode, double? setpointC);

    /// <summary>Sets the simulated oven's thermostat adjustment, as --adjustment says it is.</summary>
    void SetAdjustment(double adjustmentC);

    /// <summary>Makes the simulated oven as if it had just been used at this temperature and turned off.</summary>
    void PretendUsed(double setpointC);

    /// <summary>Gives the simulated meter an auto power-off (--sim-apo), or none.</summary>
    void SetAutoPowerOff(TimeSpan? after);
}

public sealed class MeterException(string message, Exception? inner = null) : Exception(message, inner);
