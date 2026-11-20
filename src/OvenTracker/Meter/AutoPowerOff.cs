namespace OvenTracker.Meter;

/// <summary>
/// A meter that switches itself off after a while: a driver's metadata about its kind of meter
/// (<see cref="IMeterDriver.AutoPowerOff"/>). A setpoint takes far longer than most meters' timeout,
/// so the app warns before a run unless the meter says its auto power-off is off.
/// </summary>
/// <param name="DefaultTimeout">How long the meter waits before switching off, as it comes from the factory.</param>
/// <param name="HowToTurnOff">The steps to turn it off, one per item, in plain text. Shown before a run.</param>
/// <param name="Source">Where the steps come from, e.g. "U1253B User's and Service Guide, pp. 110 and 126–127".</param>
/// <param name="RemoteReadsKeepItOn">True if reading the meter remotely restarts its countdown, so it can't switch off mid-run and needs no warning.</param>
/// <param name="HowToWake">What to do when it has switched itself off, e.g. "press any button on the meter".</param>
public sealed record AutoPowerOff(
    TimeSpan DefaultTimeout,
    IReadOnlyList<string> HowToTurnOff,
    string? Source = null,
    bool RemoteReadsKeepItOn = false,
    string HowToWake = "press any button on the meter");

public enum AutoPowerOffState { Unknown, Off, On }

/// <summary>What a meter says its auto power-off is set to, when it can say (<see cref="ITemperatureMeter.ReadAutoPowerOff"/>).</summary>
public sealed record AutoPowerOffSetting(AutoPowerOffState State, TimeSpan? Timeout = null)
{
    public static readonly AutoPowerOffSetting Unknown = new(AutoPowerOffState.Unknown);
    public static readonly AutoPowerOffSetting Off = new(AutoPowerOffState.Off);
    public static AutoPowerOffSetting On(TimeSpan timeout) => new(AutoPowerOffState.On, timeout);
}

public enum AutoPowerOffWarningKind
{
    /// <summary>The meter says auto power-off is on: it will switch off mid-run.</summary>
    On,

    /// <summary>The meter can't say, and it's on by default: a reminder.</summary>
    Reminder,
}

/// <param name="Headline">One sentence: what will happen.</param>
/// <param name="Steps">How to turn it off, from the driver.</param>
public sealed record AutoPowerOffWarning(AutoPowerOffWarningKind Kind, string Headline, IReadOnlyList<string> Steps, string? Source);

public static class AutoPowerOffAdvice
{
    /// <summary>
    /// Whether to warn before a run, and what to say. No warning when the meter has no auto power-off,
    /// when reading it keeps it on, when it says auto power-off is off, or when the user has said so for
    /// this meter before (<paramref name="confirmedOff"/>) and the meter can't say otherwise.
    /// </summary>
    public static AutoPowerOffWarning? For(AutoPowerOff? metadata, AutoPowerOffSetting setting, bool confirmedOff = false)
    {
        var steps = metadata?.HowToTurnOff ?? [];
        var source = metadata?.Source;
        if (setting.State == AutoPowerOffState.On)
        {
            var after = setting.Timeout ?? metadata?.DefaultTimeout;
            return new(AutoPowerOffWarningKind.On,
                $"Auto power-off is on{(after is { } t ? $", set to {Minutes(t)}" : "")}: the meter will switch itself off in the middle of a setpoint.",
                steps, source);
        }
        if (metadata is null || metadata.RemoteReadsKeepItOn || setting.State == AutoPowerOffState.Off || confirmedOff) return null;
        return new(AutoPowerOffWarningKind.Reminder,
            $"This meter switches itself off after {Minutes(metadata.DefaultTimeout)} by default, and reading it remotely doesn't keep it on. " +
            "A setpoint takes far longer, so turn auto power-off off before starting.",
            steps, source);
    }

    static string Minutes(TimeSpan t) => t.TotalMinutes == 1 ? "1 minute" : $"{t.TotalMinutes:0} minutes";
}
