using OvenTracker.Analysis;
using OvenTracker.Session;
using Spectre.Console;

namespace OvenTracker.Ui;

public static class ConsoleReport
{
    static string VerdictColor(Verdict v) => v switch
    {
        Verdict.Accurate => "green",
        Verdict.Borderline => "yellow",
        Verdict.OutOfTolerance => "red",
        _ => "grey",
    };

    public static void PointSummary(Report report, PointResult r)
    {
        var f = report.Format;
        var m = r.Metrics;
        var v = SessionSummary.Judge(m, report.Info.ToleranceC);
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Result[/]", m.MeanC is { } mean
            ? $"[bold]{f.T(mean)}[/] ±{f.Span(m.HalfWidthC ?? 0)}  {Dashboard.OffsetMarkup(m.OffsetC!.Value, report.Info.ToleranceC, f)}  [{VerdictColor(v)}]{Report.VerdictText(v)}[/]"
            : "[grey]not enough steady data for a result[/]");
        if (m.SwingC is { } sw) grid.AddRow("[grey]Swing[/]", $"{f.Span(sw)} peak to peak" + (m.CycleMinutes is { } cm ? $", {cm:F1} min cycle" : ""));
        grid.AddRow("[grey]Time[/]", $"{Report.Minutes(m.TotalMinutes)} ({Markup.Escape(report.ReasonText(m))})");
        if (m.Reason == CompletionReason.TimedOut && !m.MetPrecision)
            grid.AddRow("", "[yellow]The mean did not settle within the time limit; treat this one with care.[/]");
        AnsiConsole.Write(new Panel(grid)
            .Header($"[bold]{Temp.ModeName(r.Point.Mode)} {Markup.Escape(f.Setpoint(r.Point.SetpointC))}[/]")
            .Border(BoxBorder.Rounded).BorderColor(Color.Orange1));
    }

    public static void Full(Report report)
    {
        AnsiConsole.Write(new Rule("[orange1]Results[/]").LeftJustified());
        // The full row is too wide for most windows, so the console shows it as two tables.
        int[] accuracy = [0, 1, 2, 3, 4, 5, 6, 8], behaviour = [0, 1, 9, 10, 7, 11, 12];
        var rows = report.PointRows().ToList();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35).Title("Accuracy");
        foreach (var i in accuracy) table.AddColumn(new TableColumn(Report.PointColumns[i]).NoWrap());
        foreach (var (_, v, cells) in rows)
        {
            var row = accuracy.Select(i => Markup.Escape(cells[i])).ToArray();
            row[5] = $"[{VerdictColor(v)}]{row[5]}[/]";
            table.AddRow(row);
        }
        AnsiConsole.Write(table);
        var how = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35).Title("Getting there");
        foreach (var i in behaviour) how.AddColumn(new TableColumn(Report.PointColumns[i]).NoWrap());
        foreach (var (_, _, cells) in rows) how.AddRow(behaviour.Select(i => Markup.Escape(cells[i])).ToArray());
        AnsiConsole.Write(how);

        var comparison = report.ComparisonRows().ToList();
        if (comparison.Count > 0)
        {
            var t = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35).Title("Bake and convection");
            foreach (var c in Report.ComparisonColumns) t.AddColumn(c);
            foreach (var r in comparison) t.AddRow(r.Select(Markup.Escape).ToArray());
            AnsiConsole.Write(t);
        }

        foreach (var line in report.Findings()) AnsiConsole.MarkupLine($"[bold]•[/] {Markup.Escape(line)}");
        foreach (var line in report.Durations()) AnsiConsole.MarkupLine($"[grey]•[/] {Markup.Escape(line)}");
    }
}
