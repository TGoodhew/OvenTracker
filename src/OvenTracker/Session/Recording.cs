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
    AdjustmentOptions? Adjust = null);

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

    public RunRecorder(string directory, SessionInfo info)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(info, Json.Options));
        csv = new StreamWriter(Path.Combine(directory, "readings.csv"), append: false) { AutoFlush = true };
        csv.WriteLine(Header);
    }

    public string Directory { get; }
    public string CsvPath => Path.Combine(Directory, "readings.csv");

    public void Reading(MeterReading r, TestPoint? point, double? elapsed) =>
        csv.WriteLine(string.Join(',',
            r.Time.UtcDateTime.ToString("O", Inv),
            point?.Index.ToString(Inv) ?? "0",
            point?.Mode.ToString() ?? "",
            point is null ? "" : point.SetpointC.ToString("F3", Inv),
            elapsed?.ToString("F1", Inv) ?? "",
            r.Status.ToString(),
            r.Celsius?.ToString("F2", Inv) ?? "",
            r.Raw.Replace(",", ";").Replace("\"", ""),
            ""));

    public void Event(DateTimeOffset time, TestPoint point, double elapsed, string name) =>
        csv.WriteLine(string.Join(',',
            time.UtcDateTime.ToString("O", Inv), point.Index.ToString(Inv), point.Mode.ToString(),
            point.SetpointC.ToString("F3", Inv), elapsed.ToString("F1", Inv), "Event", "", "", name));

    public void WriteResults(SessionInfo info, IReadOnlyList<PointResult> results, string markdown)
    {
        File.WriteAllText(Path.Combine(Directory, "results.json"), JsonSerializer.Serialize(new { session = info, results }, Json.Options));
        File.WriteAllText(Path.Combine(Directory, "report.md"), markdown);
    }

    public void Dispose() => csv.Dispose();
}

/// <summary>Re-runs the analysis over a saved <c>readings.csv</c>, exactly as it ran live.</summary>
public static class Replay
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<PointResult> Analyze(string csvPath, AnalysisOptions options)
    {
        var results = new List<PointResult>();
        SetpointAnalyzer? analyzer = null;
        TestPoint? point = null;

        void Close(CompletionReason ifOpen)
        {
            if (analyzer is null || point is null) return;
            analyzer.Finish(ifOpen);
            results.Add(new PointResult(point, analyzer.Metrics()));
            analyzer = null;
            point = null;
        }

        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 9 || !int.TryParse(f[1], Inv, out var index) || index == 0) continue;
            var mode = Enum.Parse<OvenMode>(f[2]);
            var sp = double.Parse(f[3], Inv);
            double.TryParse(f[4], NumberStyles.Float, Inv, out var elapsed);

            if (f[5] == "Event")
            {
                var ev = f[8];
                if (ev is "start" or "restart")
                {
                    if (ev == "start") Close(CompletionReason.Aborted);
                    point = new TestPoint(index, mode, sp);
                    analyzer = new SetpointAnalyzer(sp, options);
                }
                else if (ev == "preheat-signal") analyzer?.MarkPreheatSignal();
                else if (ev.StartsWith("end:", StringComparison.Ordinal) && analyzer is not null)
                {
                    var reason = Enum.Parse<CompletionReason>(ev[4..]);
                    // Converged and TimedOut come back by themselves; the others were the user's choice.
                    Close(reason is CompletionReason.Converged or CompletionReason.TimedOut ? CompletionReason.Manual : reason);
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
