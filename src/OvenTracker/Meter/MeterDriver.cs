namespace OvenTracker.Meter;

/// <summary>What the app asks a driver for when it wants a meter.</summary>
/// <param name="Resource">Where to look, as the user gave it (e.g. COM5). Null to search.</param>
/// <param name="Preferred">Where a meter answered last time, tried first when searching.</param>
/// <param name="IntervalSeconds">How often the app will read the meter.</param>
/// <param name="Progress">Told each place the driver tries, for the status line.</param>
public sealed record MeterRequest(string? Resource, string? Preferred, double IntervalSeconds, Action<string>? Progress = null);

/// <summary>The outcome of asking one place for a meter.</summary>
/// <param name="Meter">The connected meter, when the caller asked to keep it open.</param>
public sealed record ProbeResult(string Resource, MeterIdentity? Identity, string? Error, ITemperatureMeter? Meter = null);

/// <summary>Something a driver can see, for the <c>meters</c> command.</summary>
/// <param name="Note">Anything worth knowing about it, e.g. "outgoing Bluetooth".</param>
/// <param name="Usable">Whether it is worth asking: a driver may list places it won't probe by default.</param>
public sealed record MeterCandidate(string Resource, string Description, string? Note, bool Usable);

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

    /// <summary>What <c>--resource</c> takes for this driver, e.g. "a serial port such as COM5".</summary>
    string ResourceHelp { get; }

    /// <summary>A simulation: the app runs faster than real time and tells it the oven's setting.</summary>
    bool IsSimulation => false;

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

    public static IReadOnlyList<IMeterDriver> All { get; } = [new U1253BDriver(), new SimulatedDriver()];

    public static IMeterDriver? Find(string? name) =>
        All.FirstOrDefault(d => string.Equals(d.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Names => string.Join(", ", All.Select(d => d.Name));
}
