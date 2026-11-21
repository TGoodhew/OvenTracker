using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OvenTracker.Analysis;
using OvenTracker.Meter;

namespace OvenTracker.Session;

/// <param name="Meter">The meter's make and model, for people.</param>
/// <param name="Location">Where it was, e.g. COM5.</param>
/// <param name="Driver">The <see cref="IMeterDriver.Name"/> that read it. Null in runs saved before drivers.</param>
/// <param name="Identity">What the meter said it was.</param>
/// <param name="Sensor">The temperature sensor the meter was set up for, when it said.</param>
/// <param name="AdjustmentC">The oven's thermostat adjustment (offset) setting during the run, if given.</param>
/// <param name="Adjust">How to work out the best adjustment: the oven's steps and range, and the objective.</param>
/// <param name="AmbientC">Room temperature: the probe's first reading, when the oven was cold, or as given.</param>
/// <param name="Sittings">When each sitting started: the first, then each resume.</param>
/// <param name="Oven">The oven's profile, as it was for this run.</param>
/// <param name="Characterise">For a characterise run: its step, and the adjustment phase 2 moved to.</param>
public sealed record SessionInfo(
    string AppVersion,
    DateTimeOffset Started,
    string Meter,
    string Location,
    TempUnit Unit,
    double ToleranceC,
    AnalysisOptions Options,
    IReadOnlyList<TestPoint> Plan,
    string? Driver = null,
    MeterIdentity? Identity = null,
    Sensor? Sensor = null,
    double? AdjustmentC = null,
    AdjustmentOptions? Adjust = null,
    double? AmbientC = null,
    IReadOnlyList<DateTimeOffset>? Sittings = null,
    Ovens.OvenProfile? Oven = null,
    CharacteriseInfo? Characterise = null);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Writes every reading to <c>readings.csv</c> as it arrives (flushed each line, so nothing is lost
/// if the PC sleeps or the app is closed), with the events that the analysis needs to be replayed.
/// </summary>
public sealed class RunRecorder : IDisposable
{
    public const string Header = "time_utc,point,mode,setpoint_c,elapsed_s,status,temp_c,raw,event";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly StreamWriter csv;

    /// <param name="append">Carry on a run saved earlier: add to its readings.csv instead of starting a new one.</param>
    public RunRecorder(string directory, SessionInfo info, bool append = false)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(info, Json.Options));
        var path = Path.Combine(directory, "readings.csv");
        var fresh = !append || !File.Exists(path) || new FileInfo(path).Length == 0;
        csv = new StreamWriter(path, append: !fresh) { AutoFlush = true };
        if (fresh) csv.WriteLine(Header);
    }

    /// <summary>
    /// Something that happened outside any setpoint: "resume" (a new sitting began), "reset" (the
    /// next setpoint follows a wait for the oven to cool), "reset-start" and "reset-end".
    /// </summary>
    public void Note(DateTimeOffset time, string name) =>
        csv.WriteLine(string.Join(',', time.UtcDateTime.ToString("O", Inv), "0", "", "", "", "Event", "", "", Clean(name)));

    /// <summary>
    /// Text for a CSV field: commas become semicolons, and quotes and line breaks are dropped, so every row keeps
    /// its nine fields. Used for <c>raw</c> and for event names (a <c>prompt:</c> event carries the prompt's words).
    /// </summary>
    public static string Clean(string text) =>
        text.Replace(",", ";").Replace("\"", "").Replace("\r", "").Replace("\n", " ");

    /// <summary>
    /// A number as written to readings.csv: the shortest text that reads back as exactly the same double ("R"), so
    /// re-analysing a recording gives exactly what the live run did.
    /// </summary>
    public static string Number(double v) => v.ToString("R", Inv);

    public string Directory { get; }
    public string CsvPath => Path.Combine(Directory, "readings.csv");

    /// <summary>
    /// The time for an event: the PC's clock, or for a simulation the simulated clock (set <see cref="Clock"/>), so
    /// events and readings in readings.csv are on the same clock and in order.
    /// </summary>
    public DateTimeOffset Now => Clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public Func<DateTimeOffset>? Clock { get; set; }

    /// <summary>The time of the last reading written.</summary>
    public DateTimeOffset? LastReadingTime { get; private set; }

    public void Reading(MeterReading r, TestPoint? point, double? elapsed)
    {
        LastReadingTime = r.Time;
        csv.WriteLine(string.Join(',',
            r.Time.UtcDateTime.ToString("O", Inv),
            point?.Index.ToString(Inv) ?? "0",
            point?.Mode.ToString() ?? "",
            point is null ? "" : Number(point.SetpointC),
            elapsed is { } e ? Number(e) : "",
            r.Status.ToString(),
            r.Celsius is { } c ? Number(c) : "",
            Clean(r.Raw),
            ""));
    }

    public void Event(DateTimeOffset time, TestPoint point, double elapsed, string name) =>
        csv.WriteLine(string.Join(',',
            time.UtcDateTime.ToString("O", Inv), point.Index.ToString(Inv), point.Mode.ToString(),
            Number(point.SetpointC), Number(elapsed), "Event", "", "", Clean(name)));

    /// <summary>Rewrites session.json, e.g. when characterise adds its second phase to the plan.</summary>
    public void UpdateSession(SessionInfo info) =>
        File.WriteAllText(Path.Combine(Directory, "session.json"), JsonSerializer.Serialize(info, Json.Options));

    public void WriteResults(SessionInfo info, IReadOnlyList<PointResult> results, string markdown)
    {
        File.WriteAllText(Path.Combine(Directory, "results.json"), ResultsJson(info, results));
        File.WriteAllText(Path.Combine(Directory, "report.md"), markdown);
    }

    /// <summary>results.json: the session and each setpoint's result (spec/results.schema.json).</summary>
    public static string ResultsJson(SessionInfo info, IReadOnlyList<PointResult> results) =>
        JsonSerializer.Serialize(new { session = info, results }, Json.Options);

    public void Dispose() => csv.Dispose();
}

/// <summary>Re-runs the analysis over a saved <c>readings.csv</c>, exactly as it ran live.</summary>
public static class Replay
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <param name="plan">
    /// The run's plan, when its session.json has one: a setpoint then takes the plan's exact value rather than
    /// readings.csv's, which older recordings rounded to three decimals.
    /// </param>
    public static IReadOnlyList<PointResult> Analyze(string csvPath, AnalysisOptions options, IReadOnlyList<TestPoint>? plan = null)
    {
        var results = new List<PointResult>();
        SetpointAnalyzer? analyzer = null;
        TestPoint? point = null;
        var afterReset = false;   // the setpoint being replayed came after a break
        var resetPending = false; // a break has happened since the last setpoint began
        bool startedWarm = false, warmPending = false;

        void Close(CompletionReason ifOpen)
        {
            if (analyzer is null || point is null) return;
            analyzer.Finish(ifOpen);
            results.Add(new PointResult(point, analyzer.Metrics(), afterReset, startedWarm));
            analyzer = null;
            point = null;
        }

        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 9 || !int.TryParse(f[1], Inv, out var index)) continue;
            if (index == 0)
            {
                if (f[5] == "Event" && f[8] is "resume" or "reset") resetPending = true;
                if (f[5] == "Event" && f[8] == "started-warm") warmPending = true;
                continue;
            }
            var mode = Enum.Parse<OvenMode>(f[2]);
            var sp = double.Parse(f[3], NumberStyles.Float, Inv);
            if (plan?.FirstOrDefault(p => p.Index == index) is { } planned && Math.Abs(planned.SetpointC - sp) < 0.001) sp = planned.SetpointC;
            double.TryParse(f[4], NumberStyles.Float, Inv, out var elapsed);

            if (f[5] == "Event")
            {
                var ev = f[8];
                if (ev is "start" or "restart")
                {
                    if (ev == "start")
                    {
                        Close(CompletionReason.Aborted);
                        // A setpoint measured again in a later sitting replaces the earlier attempt.
                        results.RemoveAll(r => r.Point.Index == index);
                        afterReset = resetPending;
                        resetPending = false;
                        startedWarm = warmPending;
                        warmPending = false;
                    }
                    point = new TestPoint(index, mode, sp);
                    analyzer = new SetpointAnalyzer(sp, options);
                }
                else if (ev == "preheat-signal") analyzer?.MarkPreheatSignal();
                else if (ev.StartsWith("end:", StringComparison.Ordinal) && analyzer is not null)
                {
                    // Why the measuring stopped, as recorded. If this analysis (with other options, say) hadn't finished by
                    // itself by then, the recorded reason still says why the readings end here; PointMetrics.MetPrecision
                    // says whether this analysis's target was met.
                    Close(Enum.Parse<CompletionReason>(ev[4..]));
                }
                continue;
            }

            if (analyzer is null || point?.Index != index || f[5] != nameof(ReadingStatus.Ok)) continue;
            if (double.TryParse(f[6], NumberStyles.Float, Inv, out var c)) analyzer.Add(elapsed, c);
        }
        Close(CompletionReason.Aborted);
        return results;
    }
}
