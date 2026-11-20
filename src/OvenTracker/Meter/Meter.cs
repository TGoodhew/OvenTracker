namespace OvenTracker.Meter;

public enum ReadingStatus { Ok, Overload, NotTemperature, Error }

/// <param name="Celsius">The reading in °C, when it is a valid temperature.</param>
/// <param name="Raw">Exactly what the meter sent.</param>
public sealed record MeterReading(DateTimeOffset Time, ReadingStatus Status, double? Celsius, string Raw);

/// <param name="Raw">The CONF? reply, e.g. <c>"TEMP:K CEL"</c>.</param>
public sealed record MeterConfig(string Raw, string Function, bool IsTemperature, TempUnit? Unit);

public sealed record MeterIdentity(string Manufacturer, string Model, string Serial, string Firmware)
{
    public override string ToString() => $"{Manufacturer} {Model} (s/n {Serial}, fw {Firmware})";
}

/// <summary>A thermometer the app can poll. Calls are synchronous and come from one thread.</summary>
public interface ITemperatureMeter : IDisposable
{
    /// <summary>Where the meter is, e.g. "COM5 (Bluetooth)".</summary>
    string Location { get; }

    MeterIdentity Connect();
    void Disconnect();
    MeterConfig ReadConfig();
    MeterReading Read();
    double? ReadBatteryPercent();
    void Beep();
}

public sealed class MeterException(string message, Exception? inner = null) : Exception(message, inner);
