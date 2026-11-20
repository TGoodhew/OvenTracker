using System.Text.Json;

namespace OvenTracker.Session;

/// <summary>Remembers the meter that answered last time, and where, so the next run finds it straight away.</summary>
public sealed class AppSettings
{
    /// <summary>The driver that answered last time, e.g. "u1253b".</summary>
    public string? LastMeter { get; set; }

    /// <summary>Where it answered, e.g. "COM5".</summary>
    public string? LastResource { get; set; }

    /// <summary>Settings saved before there were drivers only have this: the serial port of the one meter there was.</summary>
    public string? LastPort { get; set; }

    /// <summary>The oven used last time (its profile's key).</summary>
    public string? LastOven { get; set; }

    const string DriverBeforeDrivers = "u1253b";

    /// <summary>Where the given driver's meter was last time, if it was that driver.</summary>
    public string? LastResourceFor(string driver) =>
        LastMeter is null
            ? driver == DriverBeforeDrivers ? LastPort : null
            : string.Equals(LastMeter, driver, StringComparison.OrdinalIgnoreCase) ? LastResource : null;

    /// <summary>Meters ("driver:serial") whose auto power-off you've said is turned off, so the app stops reminding you.</summary>
    public List<string> AutoPowerOffTurnedOff { get; set; } = [];

    public bool IsAutoPowerOffTurnedOff(string meterKey) => AutoPowerOffTurnedOff.Contains(meterKey, StringComparer.OrdinalIgnoreCase);

    public void RememberAutoPowerOffTurnedOff(string meterKey)
    {
        if (!IsAutoPowerOffTurnedOff(meterKey)) AutoPowerOffTurnedOff.Add(meterKey);
    }

    public void Remember(string driver, string resource)
    {
        LastMeter = driver;
        LastResource = resource;
        LastPort = null;
    }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OvenTracker", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        }
        catch { /* not worth failing a run over */ }
    }
}
