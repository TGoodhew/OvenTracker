using System.ComponentModel;
using System.Text.Json;
using OvenTracker.Analysis;
using OvenTracker.Ovens;
using OvenTracker.Session;
using OvenTracker.Ui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

public sealed class AnalyzeSettings : SharedSettings
{
    [CommandArgument(0, "<FOLDERS>")]
    [Description("One or more runs' folders (each with readings.csv and session.json), or readings.csv files. Several runs are analysed together: runs of the same setpoints at different thermostat adjustments measure how far the adjustment moves the oven.")]
    public string[] Paths { get; set; } = [];

    [CommandOption("--precision <DEG>")]
    [Description("Re-run with a different precision target, in the oven's unit.")]
    public double? Precision { get; set; }

    [CommandOption("--tolerance <DEG>")]
    [Description("Judge against a different tolerance, in the oven's unit.")]
    public double? Tolerance { get; set; }

    [CommandOption("--min-cycles <N>")]
    public int? MinCycles { get; set; }

    public override ValidationResult Validate()
    {
        if (base.Validate() is { Successful: false } adjustment) return adjustment;
        if (Paths.Length == 0) return ValidationResult.Error("Give at least one run's folder.");
        foreach (var p in Paths)
            if (!File.Exists(p) && !Directory.Exists(p)) return ValidationResult.Error($"No such file or folder: {p}");
        return ValidationResult.Success();
    }
}

/// <summary>
/// Re-runs the analysis over saved runs, for example with a different precision or tolerance, or
/// several runs together.
/// </summary>
public sealed class AnalyzeCommand : Command<AnalyzeSettings>
{
    /// <summary>One saved run: its folder, its session and its readings.</summary>
    public sealed record SavedRun(string Folder, string Csv, SessionInfo Info);

    public static SavedRun? Load(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!;
        var csv = Directory.Exists(path) ? Path.Combine(path, "readings.csv") : path;
        if (!File.Exists(csv)) return null;
        var sessionPath = Path.Combine(dir, "session.json");
        var info = File.Exists(sessionPath)
            ? JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(sessionPath), Json.Options)!
            : new SessionInfo(RunCommand.Version, File.GetCreationTime(csv), "unknown meter", "?", TempUnit.Fahrenheit, 15 / 1.8, new AnalysisOptions(), []);
        return new SavedRun(dir, csv, info);
    }

    /// <summary>
    /// The runs' results, together. With several runs, each point carries its run's adjustment, and the
    /// latest run's session (and adjustment) stands for the whole.
    /// </summary>
    /// <param name="adjustmentC">
    /// From --adjustment: for a single run, the adjustment it had (over what it recorded); for several, the
    /// adjustment of those that recorded none.
    /// </param>
    public static (SessionInfo Info, IReadOnlyList<PointResult> Results) Combine(IReadOnlyList<SavedRun> runs, Func<SessionInfo, AnalysisOptions> options, double? adjustmentC)
    {
        if (runs.Count == 1)
        {
            var only = runs[0].Info with { AdjustmentC = adjustmentC ?? runs[0].Info.AdjustmentC };
            return (only, Replay.Analyze(runs[0].Csv, options(only)));
        }

        var ordered = runs.OrderBy(r => r.Info.Started).ToList();
        var results = new List<PointResult>();
        foreach (var run in ordered)
        {
            var adj = run.Info.AdjustmentC ?? adjustmentC ?? 0;
            foreach (var r in Replay.Analyze(run.Csv, options(run.Info)))
                results.Add(r with { Point = r.Point with { Index = results.Count + 1, AdjustmentC = adj } });
        }
        var latest = ordered[^1].Info;
        return (latest with { AdjustmentC = latest.AdjustmentC ?? adjustmentC ?? 0, Plan = results.Select(r => r.Point).ToList() }, results);
    }

    public override int Execute(CommandContext context, AnalyzeSettings s, CancellationToken cancellationToken)
    {
        var runs = new List<SavedRun>();
        foreach (var p in s.Paths)
        {
            if (Load(p) is not { } run)
            {
                AnsiConsole.MarkupLine($"[red]No readings.csv in {Markup.Escape(p)}.[/]");
                return 1;
            }
            runs.Add(run);
        }

        var unit = runs.OrderBy(r => r.Info.Started).Last().Info.Unit;
        var f = new UnitFormatter(unit);
        AnalysisOptions Options(SessionInfo info)
        {
            var o = info.Options;
            if (s.Precision is { } p) o = o with { PrecisionC = f.DeltaFromDisplay(p) };
            if (s.MinCycles is { } mc) o = o with { MinCycles = mc };
            return o;
        }

        var (info, results) = Combine(runs, Options, s.AdjustmentC(unit));
        info = info with
        {
            Options = Options(info),
            ToleranceC = s.Tolerance is { } t ? f.DeltaFromDisplay(t) : info.ToleranceC,
            Oven = OvenProfiles.Find(s.Oven) ?? info.Oven,
            Adjust = s.BuildOptions(unit, info.Adjust, OvenProfiles.Find(s.Oven) ?? info.Oven),
            Sensor = s.ResolveSensor(info.Sensor, unit), // e.g. --probe 80bk-a on a run made without it
        };

        var report = new Report(info, results);
        ConsoleReport.Full(report);
        var latest = runs.OrderBy(r => r.Info.Started).Last().Folder;
        var output = Path.Combine(latest, runs.Count == 1 ? "report-reanalysed.md" : "report-combined.md");
        File.WriteAllText(output, report.Markdown());
        AnsiConsole.MarkupLine($"\nWrote {Markup.Escape(output)}.");
        return 0;
    }
}
