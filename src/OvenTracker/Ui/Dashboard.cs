using System.Globalization;
using OvenTracker.Analysis;
using OvenTracker.Meter;
using OvenTracker.Session;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace OvenTracker.Ui;

public sealed record DashboardModel(
    TestPoint Point,
    int PointCount,
    SetpointAnalyzer Analyzer,
    UnitFormatter Format,
    MeterPoller Poller,
    string MeterLabel,
    double ToleranceC,
    ChartView View,
    ReadingStatus? LastStatus,
    string? Notice,
    int Width,
    int Height);

public enum ChartView { Auto, Whole, Recent }

public static class Dashboard
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const int TableRows = 6;

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Remaining(Eta? eta) => eta switch
    {
        null => "learning your oven…",
        { Seconds: < 30 } => "under a minute",
        { Rough: true } e => $"≈ {Math.Ceiling(e.Seconds / 60).ToString("F0", Inv)} min (rough)",
        var e => $"≈ {Math.Ceiling(e.Seconds / 60).ToString("F0", Inv)} min",
    };

    public static string PhaseText(SetpointAnalyzer a) => a.Phase switch
    {
        PointPhase.Heating => a.Cooling ? "Cooling to the setting" : "Heating to the setting",
        PointPhase.Settling => "Settling (letting the first cycle pass)",
        PointPhase.Measuring when a.Kind == UnitKind.Block =>
            $"Measuring: block {a.Units.Count + 1} of ~{a.RequiredUnits} (no thermostat cycle seen)",
        PointPhase.Measuring => $"Measuring: cycle {a.Units.Count + 1} of ~{a.RequiredUnits}",
        _ => "Complete",
    };

    public static IRenderable Render(DashboardModel m)
    {
        var a = m.Analyzer;
        var f = m.Format;
        var now = a.Now;

        // Header: where we are and what the oven is doing.
        var current = a.LatestC is { } c
            ? $"[bold]{f.T(c)}[/]  {OffsetMarkup(c - a.SetpointC, m.ToleranceC, f)} vs setting"
            : "[grey]waiting for a reading…[/]";
        var status = m.LastStatus switch
        {
            ReadingStatus.Overload => "   [red]Probe open (OL): check the thermocouple is plugged in[/]",
            ReadingStatus.NotTemperature => "   [red]Meter is not on temperature: turn the dial to °C/°F[/]",
            ReadingStatus.Error => "   [red]Meter sent something unexpected[/]",
            _ => "",
        };
        var header = new Rows(
            new Markup($"[bold orange1]{Temp.ModeName(m.Point.Mode).ToUpperInvariant()} {Markup.Escape(f.Setpoint(m.Point.SetpointC))}[/]" +
                       $"   [grey]setpoint {m.Point.Index} of {m.PointCount}   elapsed {Clock(now)}[/]"),
            new Markup($"{current}{status}"),
            new Markup($"[cyan]{Markup.Escape(PhaseText(a))}[/]" + (a.Phase == PointPhase.Complete ? "" : $"   time left at this setting: [bold]{Markup.Escape(Remaining(a.EstimateRemaining()))}[/]")));

        // Chart.
        var plotWidth = Math.Max(40, m.Width - 4);
        // The frame keeps one height and stays inside the window, or the live display scrolls instead of
        // redrawing in place: header 5 + chart borders 4 + two tables of TableRows + 2 + notice 1 + keys.
        var keysText = "Keys: P oven says preheated   N finish this setpoint now   R restart it (door opened)   S skip   Z chart view   Q quit";
        var keyLines = (int)Math.Ceiling(keysText.Length / (double)Math.Max(20, m.Width));
        var plotRows = Math.Clamp(m.Height - 1 - (5 + 4 + TableRows + 2 + 1 + keyLines) - 1, 3, 24);
        var t1 = Math.Max(now, 60);
        var settledFrom = a.TurningPoints.Count > 0 ? Math.Max(0, a.TurningPoints[0].T - 180) : 0;
        var (t0, viewName) = m.View switch
        {
            ChartView.Recent => (Math.Max(0, t1 - 20 * 60), "last 20 minutes"),
            ChartView.Whole => (0.0, "whole setpoint"),
            _ when settledFrom > 0 && t1 - settledFrom > 120 => (settledFrom, "since it reached temperature"),
            _ => (0.0, "whole setpoint"),
        };
        var plot = new Panel(new Markup(BraillePlot.Markup(a.Series.Items, t0, t1, a.SetpointC, m.ToleranceC, f, plotWidth, plotRows)))
            .Header($"[grey]{viewName} (Z changes)  ·  [deepskyblue1]oven[/]  [yellow]setting[/]  [grey30]±tolerance[/][/]")
            .Border(BoxBorder.Rounded).BorderColor(Color.Grey35).Expand();

        // Statistics so far.
        var stats = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35).HideHeaders().AddColumn("").AddColumn("");
        var e = a.CurrentEstimate;
        var unitName = a.Kind == UnitKind.Block ? "Blocks" : "Cycles";
        if (e is not null)
        {
            stats.AddRow("Mean so far", $"[bold]{f.T(e.MeanC)}[/]  {OffsetMarkup(e.MeanC - a.SetpointC, m.ToleranceC, f)}");
            stats.AddRow("±95 %", e.HalfWidthC is { } hw
                ? $"{f.Span(hw)}  [grey](target {f.Span(a.Options.PrecisionC)})[/]"
                : "[grey]needs two cycles[/]");
            var swing = a.Units.Average(u => u.SwingC);
            stats.AddRow("Swing", $"{f.Span(swing)} peak to peak");
        }
        else
        {
            stats.AddRow("Mean so far", "[grey]after the first full cycle[/]");
        }
        stats.AddRow(unitName, $"{a.Units.Count} measured" + (a.DiscardedUnits > 0 ? $", {a.DiscardedUnits} dropped (still warming)" : ""));
        if (a.ExpectedUnitSeconds() is { } us && a.Kind != UnitKind.Block)
            stats.AddRow("Cycle length", $"{(us / 60).ToString("F1", Inv)} min");
        if (a.Metrics() is { TimeToSetpointMinutes: { } reach })
            stats.AddRow("Reached setting", $"after {reach.ToString("F1", Inv)} min");

        // Meter and link.
        var p = m.Poller;
        var link = p.State switch
        {
            LinkState.Connected => "[green]connected[/]",
            LinkState.Reconnecting => "[yellow]link lost, reconnecting…[/]",
            LinkState.Connecting => "[yellow]connecting…[/]",
            _ => "[grey]stopped[/]",
        };
        var meter = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35).HideHeaders().AddColumn("").AddColumn("");
        meter.AddRow("Meter", Markup.Escape(m.MeterLabel));
        meter.AddRow("Link", link + (p.Reconnects > 0 ? $" [grey]({p.Reconnects} drop{(p.Reconnects == 1 ? "" : "s")})[/]" : ""));
        if (p.BatteryPercent is { } b)
            meter.AddRow("Battery", b < 20 ? $"[red]{b.ToString("F0", Inv)} %[/]" : $"{b.ToString("F0", Inv)} %");
        meter.AddRow("Readings", $"{a.Series.Count.ToString("N0", Inv)}");
        meter.AddRow("Detecting", $"turns of {f.Span(a.Hysteresis)} or more");
        if (p.LastError is { } err && p.LastErrorAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(2))
            meter.AddRow("Last error", $"[yellow]{Markup.Escape(err)}[/]");

        while (stats.Rows.Count < TableRows) stats.AddRow("", "");
        while (meter.Rows.Count < TableRows) meter.AddRow("", "");
        var bottom = new Columns(stats, meter) { Expand = true };
        var keys = new Markup("[grey]Keys:[/] [bold]P[/] oven says preheated   [bold]N[/] finish this setpoint now   " +
                              "[bold]R[/] restart it (door opened)   [bold]S[/] skip   [bold]Z[/] chart view   [bold]Q[/] quit");
        var parts = new List<IRenderable> { new Panel(header).Border(BoxBorder.Rounded).BorderColor(Color.Orange1).Expand(), plot, bottom };
        parts.Add(m.Notice is null ? new Text(" ") : new Markup($"[yellow]{Markup.Escape(m.Notice)}[/]"));
        parts.Add(keys);
        return new Rows(parts);
    }

    public static string OffsetMarkup(double offsetC, double toleranceC, UnitFormatter f)
    {
        var color = Math.Abs(offsetC) <= toleranceC / 2 ? "green" : Math.Abs(offsetC) <= toleranceC ? "yellow" : "red";
        return $"[{color}]({Markup.Escape(f.D(offsetC))})[/]";
    }
}
