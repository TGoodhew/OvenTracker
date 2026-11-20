namespace OvenTracker.Meter;

/// <summary>What the app asks a driver for when it wants a meter.</summary>
/// <param name="Resource">Where to look, as the user gave it (e.g. COM5). Null to search.</param>
/// <param name="Preferred">Where a meter answered last time, tried first when searching.</param>
/// <param name="IntervalSeconds">How often the app will read the meter.</param>
/// <param name="Progress">Told each place the driver tries, for the status line.</param>
/// <param name="Options">The driver's own settings from --meter-option (key=value), already checked by <see cref="IMeterDriver.CheckOptions"/>.</param>
public sealed record MeterRequest(string? Resource, string? Preferred, double IntervalSeconds, Action<string>? Progress = null,
    IReadOnlyDictionary<string, string>? Options = null);

/// <summary>A setting a driver takes through <c>--meter-option key=value</c>, for the help text.</summary>
/// <param name="Values">The values it takes, for people, e.g. "pt100, pt100-b".</param>
public sealed record MeterOption(string Key, string Values, string Description);

/// <summary>The outcome of asking one place for a meter.</summary>
/// <param name="Meter">The connected meter, when the caller asked to keep it open.</param>
public sealed record ProbeResult(string Resource, MeterIdentity? Identity, string? Error, ITemperatureMeter? Meter = null);

/// <summary>Something a driver can see, for the <c>meters</c> command.</summary>
/// <param name="Note">Anything worth knowing about it, e.g. "outgoing Bluetooth".</param>
/// <param name="Usable">Whether it is worth asking: a driver may list places it won't probe by default.</param>
/// <param name="Transport">How it's connected, for people: "Serial", "Bluetooth", "GPIB", "USB", "LAN".</param>
public sealed record MeterCandidate(string Resource, string Description, string? Note, bool Usable, string Transport = "Serial");

/// <summary>
/// Knows one kind of meter: how to find it, how to connect, and what to tell people when it
/// can't. Add a meter by implementing this and listing it in <see cref="MeterDrivers"/>.
/// Text that a driver returns is plain, not Spectre markup.
/// </summary>
public interface IMeterDriver
{
    /// <summary>What <c>--meter</c> takes, e.g. "u1253b". Lower case, no spaces.</summary>
    string Name { get; }

    /// <summary>For people, e.g. "Keysight U1253B through a U1177A Bluetooth adapter".</summary>
    string Description { get; }

    /// <summary>
    /// What <c>--resource</c> takes for this driver, e.g. "a serial port such as COM5". A driver that can be
    /// reached more than one way lists each (see <see cref="ResourceSpec"/> for the forms).
    /// </summary>
    string ResourceHelp { get; }

    /// <summary>A simulation: the app runs faster than real time and tells it the oven's setting.</summary>
    bool IsSimulation => false;

    /// <summary>
    /// Whether this kind of meter switches itself off after a while, with the steps to turn that off.
    /// Null (the default) for a meter that doesn't, such as a bench DMM: it then gets no warning.
    /// </summary>
    AutoPowerOff? AutoPowerOff => null;

    /// <summary>The settings this driver takes through --meter-option. None by default.</summary>
    IReadOnlyList<MeterOption> Options => [];

    /// <summary>What's wrong with the --meter-option settings given, or null if they're all right. Keys are lower case.</summary>
    string? CheckOptions(IReadOnlyDictionary<string, string> options) =>
        options.Count == 0 ? null : $"The {Name} meter takes no --meter-option.";

    /// <summary>What to check when no meter answered.</summary>
    string NotFoundHelp { get; }

    /// <summary>What to do when the meter is not measuring temperature.</summary>
    string NotTemperatureHelp { get; }

    /// <summary>
    /// Connects to the meter at <see cref="MeterRequest.Resource"/>, or finds one: the preferred
    /// place first, then wherever the driver usually looks. Null if none answered.
    /// </summary>
    ProbeResult? Open(MeterRequest request);

    /// <summary>The places this driver can see a meter might be. Cheap: nothing is sent to them.</summary>
    IReadOnlyList<MeterCandidate> Discover();

    /// <summary>Asks one place whether a meter of this kind is there, and closes it again.</summary>
    ProbeResult Probe(string resource);
}

/// <summary>The meters the app knows.</summary>
public static class MeterDrivers
{
    public const string DefaultName = U1253BDriver.DriverName;

    public static IReadOnlyList<IMeterDriver> All { get; } = [new U1253BDriver(), new Hp34401ADriver(), new SimulatedDriver()];

    public static IMeterDriver? Find(string? name) =>
        All.FirstOrDefault(d => string.Equals(d.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Names => string.Join(", ", All.Select(d => d.Name));

    /// <summary>Reads --meter-option values ("rtd=pt100-b", or several separated by commas). Null if one isn't key=value.</summary>
    public static IReadOnlyDictionary<string, string>? ParseOptions(IEnumerable<string>? given)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in (given ?? []).SelectMany(g => g.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var eq = item.IndexOf('=');
            if (eq <= 0 || eq == item.Length - 1) return null;
            options[item[..eq].Trim().ToLowerInvariant()] = item[(eq + 1)..].Trim();
        }
        return options;
    }
}
