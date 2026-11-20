using System.Text.Json;
using OvenTracker.Session;

namespace OvenTracker.Ovens;

/// <summary>A gain measured on an oven by <c>characterise</c>: how far one degree of adjustment moves it.</summary>
/// <param name="HalfWidthC">Its uncertainty (per degree), roughly 95 %.</param>
public sealed record MeasuredGain(double Gain, double? HalfWidth, DateTimeOffset MeasuredOn);

/// <summary>
/// What the app knows about a kind of oven: its thermostat adjustment (step, range, whether bake and
/// convection share it, how to read and change it), any automatic convection conversion, how slowly it
/// cycles, and gains measured on it. Like a meter driver's metadata, but for the oven, and loadable from a
/// JSON file so anyone can describe their own oven without code. docs/adding-an-oven.md explains each field.
/// </summary>
/// <param name="Key">What <c>--oven</c> takes, e.g. "ge-phs925". Lower case, no spaces.</param>
/// <param name="Name">For people, e.g. "GE Profile PHS925ST1SS".</param>
/// <param name="AdjustStepC">The adjustment's step (1 °F = 0.5556 °C).</param>
/// <param name="AdjustRangeC">How far the adjustment goes either way.</param>
/// <param name="SharedAdjustment">One adjustment for bake and convection, as on most ovens.</param>
/// <param name="HowToRead">Steps to see the adjustment set now, one per item.</param>
/// <param name="HowToChange">Steps to set it, one per item.</param>
/// <param name="ConvectionConversionC">How much the oven quietly lowers a convection setting, if it does (25 °F = 13.9 °C).</param>
/// <param name="HowToTurnOffConversion">Steps to turn that conversion off.</param>
/// <param name="TypicalCycleMinutes">How often its thermostat cycles, roughly, if known: it sets the default time per setpoint.</param>
/// <param name="Gains">Gains measured by <c>characterise</c>, per mode.</param>
/// <param name="Notes">Anything else worth showing, e.g. what the adjustment doesn't affect.</param>
/// <param name="Source">Where the facts come from (manual pages, measurements).</param>
public sealed record OvenProfile(
    string Key,
    string Name,
    double AdjustStepC,
    double AdjustRangeC,
    bool SharedAdjustment = true,
    IReadOnlyList<string>? HowToRead = null,
    IReadOnlyList<string>? HowToChange = null,
    double? ConvectionConversionC = null,
    IReadOnlyList<string>? HowToTurnOffConversion = null,
    double? TypicalCycleMinutes = null,
    IReadOnlyDictionary<OvenMode, MeasuredGain>? Gains = null,
    IReadOnlyList<string>? Notes = null,
    string? Source = null)
{
    /// <summary>The generic profile, for an oven the app knows nothing about.</summary>
    public bool IsGeneric => Key == OvenProfiles.GenericKey;

    /// <summary>
    /// The default time limit per setpoint: two hours, or, when the oven's cycle is known, an hour to heat
    /// and settle plus six cycles (enough for the four the analysis needs and a dropped one or two), if longer.
    /// </summary>
    public double DefaultMaxMinutes => TypicalCycleMinutes is { } cycle ? Math.Max(120, 60 + 6 * cycle) : 120;
}

/// <summary>The ovens the app knows: built in, and any described in a JSON file beside the runs.</summary>
public static class OvenProfiles
{
    public const string GenericKey = "generic";

    static readonly OvenProfile Generic = new(
        GenericKey, "an oven", 1 / 1.8, 35 / 1.8, true,
        HowToRead: ["Look in the oven's manual for its temperature offset (or calibration, or thermostat adjustment) setting, and how to see what it's set to."],
        HowToChange: ["Look in the oven's manual for its temperature offset (or calibration, or thermostat adjustment) setting, and how to change it."],
        Notes: ["Most electronic ovens have an adjustment of up to ±30 or ±35 °F in 1° steps. Describe yours with --oven (docs/adding-an-oven.md) and the app uses its own."]);

    static readonly OvenProfile GePhs925 = new(
        "ge-phs925", "GE Profile PHS925ST1SS", 1 / 1.8, 35 / 1.8, true,
        HowToRead:
        [
            "Touch BROIL HI/LO and BAKE at the same time until the display shows SF.",
            "Touch BAKE. The display shows the adjustment: positive is hotter, negative cooler.",
            "Touch START to leave without changing it.",
        ],
        HowToChange:
        [
            "Touch BROIL HI/LO and BAKE at the same time until the display shows SF.",
            "Touch BAKE. The display shows the adjustment set now.",
            "Touch BAKE again to switch between positive (hotter) and negative (cooler).",
            "Type the amount on the number pads, e.g. 1 then 0 for 10.",
            "Check the display shows the right sign, then touch START.",
        ],
        ConvectionConversionC: 25 / 1.8,
        HowToTurnOffConversion:
        [
            "Touch BAKE and BROIL HI/LO at the same time until the display shows SF.",
            "Touch CONVECTION COOK until the display shows CON OFF.",
            "Touch START.",
        ],
        TypicalCycleMinutes: 20,
        Notes:
        [
            "The adjustment affects baking and roasting, not broil or self-clean, and is kept through a power cut.",
            "The manual doesn't say whether convection has an adjustment of its own; the app assumes it shares bake's.",
        ],
        Source: "GE PHS925ST1SS owner's manual, pp. 15, 17 and 26; the display's sign confirmed on the oven, and its ~20-minute cycle measured, 2026-11-20.");

    static readonly IReadOnlyList<OvenProfile> BuiltIn = [Generic, GePhs925];

    /// <summary>Where oven files live: OvenTracker/ovens beside the runs (Documents/OvenTracker/ovens).</summary>
    public static string Folder => Path.Combine(Commands.RunCommand.RunsFolder(), "ovens");

    /// <summary>The built-in profiles, with any oven file of the same key in its place, then the other oven files.</summary>
    public static IReadOnlyList<OvenProfile> All(string? folder = null)
    {
        var files = LoadFolder(folder ?? Folder);
        var all = BuiltIn.Select(b => files.FirstOrDefault(f => f.Key == b.Key) ?? b).ToList();
        all.AddRange(files.Where(f => BuiltIn.All(b => b.Key != f.Key)));
        return all;
    }

    public static OvenProfile? Find(string? key, string? folder = null) =>
        All(folder).FirstOrDefault(p => string.Equals(p.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Keys(string? folder = null) => string.Join(", ", All(folder).Select(p => p.Key));

    public static OvenProfile Default => Generic;

    /// <summary>Files in the oven folder that couldn't be read as a profile.</summary>
    public static IReadOnlyList<string> Unreadable(string? folder = null)
    {
        var dir = folder ?? Folder;
        if (!Directory.Exists(dir)) return [];
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<OvenProfile>(File.ReadAllText(file), Json.Options) is not { Key.Length: > 0 }) bad.Add(file);
            }
            catch { bad.Add(file); }
        }
        return bad;
    }

    static List<OvenProfile> LoadFolder(string folder)
    {
        var list = new List<OvenProfile>();
        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<OvenProfile>(File.ReadAllText(file), Json.Options) is { Key.Length: > 0 } p) list.Add(p);
            }
            catch { /* a file that isn't a profile is skipped; `ovens` says which */ }
        }
        return list;
    }

    /// <summary>Writes a profile to the oven folder (e.g. with gains measured by characterise), and returns the file.</summary>
    public static string Save(OvenProfile profile, string? folder = null)
    {
        var dir = folder ?? Folder;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{profile.Key}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, Json.Options));
        return path;
    }
}
