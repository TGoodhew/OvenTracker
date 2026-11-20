using System.ComponentModel;
using System.Text.Json;
using OvenTracker.Analysis;
using OvenTracker.Session;
using OvenTracker.Ui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

public sealed class AnalyzeSettings : CommandSettings
{
    [CommandArgument(0, "<FOLDER>")]
    [Description("A run's folder (with readings.csv and session.json), or a readings.csv.")]
    public string Path { get; set; } = "";

    [CommandOption("--precision <DEG>")]
    [Description("Re-run with a different precision target, in the oven's unit.")]
    public double? Precision { get; set; }

    [CommandOption("--tolerance <DEG>")]
    [Description("Judge against a different tolerance, in the oven's unit.")]
    public double? Tolerance { get; set; }

    [CommandOption("--min-cycles <N>")]
    public int? MinCycles { get; set; }

    public override ValidationResult Validate() =>
        File.Exists(Path) || Directory.Exists(Path) ? ValidationResult.Success() : ValidationResult.Error($"No such file or folder: {Path}");
}

/// <summary>Re-runs the analysis over a saved run, for example with a different precision or tolerance.</summary>
public sealed class AnalyzeCommand : Command<AnalyzeSettings>
{
    public override int Execute(CommandContext context, AnalyzeSettings s, CancellationToken cancellationToken)
    {
        var dir = Directory.Exists(s.Path) ? s.Path : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(s.Path))!;
        var csv = Directory.Exists(s.Path) ? System.IO.Path.Combine(s.Path, "readings.csv") : s.Path;
        if (!File.Exists(csv))
        {
            AnsiConsole.MarkupLine($"[red]No readings.csv in {Markup.Escape(dir)}.[/]");
            return 1;
        }

        var sessionPath = System.IO.Path.Combine(dir, "session.json");
        var info = File.Exists(sessionPath)
            ? JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(sessionPath), Json.Options)!
            : new SessionInfo(RunCommand.Version, File.GetCreationTime(csv), "U1253B", "?", TempUnit.Fahrenheit, 15 / 1.8, new AnalysisOptions(), []);
        var f = new UnitFormatter(info.Unit);
        var options = info.Options;
        if (s.Precision is { } p) options = options with { PrecisionC = f.DeltaFromDisplay(p) };
        if (s.MinCycles is { } mc) options = options with { MinCycles = mc };
        info = info with { Options = options, ToleranceC = s.Tolerance is { } t ? f.DeltaFromDisplay(t) : info.ToleranceC };

        var results = Replay.Analyze(csv, options);
        var report = new Report(info, results);
        ConsoleReport.Full(report);
        var output = System.IO.Path.Combine(dir, "report-reanalysed.md");
        File.WriteAllText(output, report.Markdown());
        AnsiConsole.MarkupLine($"\nWrote {Markup.Escape(output)}.");
        return 0;
    }
}
