using System.Globalization;
using OvenTracker.Meter;

namespace OvenTracker.Session;

public enum SilenceChange { None, WentQuiet, CameBack }

/// <summary>
/// Notices when the meter stops sending readings mid-run (often its auto power-off) and when it starts
/// again, so the live display can say so and the recording can mark the gap.
/// </summary>
public sealed class SilenceWatch(TimeSpan threshold)
{
    /// <summary>Half a minute without a reading: far longer than the interval between readings, short enough to act on.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(30);

    public SilenceWatch() : this(DefaultThreshold) { }

    public bool Silent { get; private set; }

    public SilenceChange Update(TimeSpan silentFor)
    {
        if (!Silent && silentFor >= threshold) { Silent = true; return SilenceChange.WentQuiet; }
        if (Silent && silentFor < threshold) { Silent = false; return SilenceChange.CameBack; }
        return SilenceChange.None;
    }

    /// <summary>What to tell the user while the meter is quiet: about auto power-off when the meter has one.</summary>
    public static string Message(TimeSpan silentFor, AutoPowerOff? autoPowerOff)
    {
        var how = silentFor.TotalMinutes >= 2
            ? $"{Math.Floor(silentFor.TotalMinutes).ToString("F0", CultureInfo.InvariantCulture)} min"
            : $"{Math.Floor(silentFor.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture)} s";
        return autoPowerOff is { } apo
            ? $"No reading for {how}. The meter may have switched itself off (auto power-off): {apo.HowToWake} to wake it. Tracking carries on when readings come back."
            : $"No reading for {how}. The meter has stopped answering: check it's on and still connected. Tracking carries on when readings come back.";
    }
}
