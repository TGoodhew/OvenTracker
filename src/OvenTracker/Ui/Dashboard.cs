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
    string NotTemperatureHint,
    double ToleranceC,
    ChartView View,
    ReadingStatus? LastStatus,
    string? Notice,
    int Width,
    int Height,
    string? Alert = null);

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

    /// <summary>The smallest and largest the chart can be, in rows of text.</summary>
    const int MinPlotRows = 3, MaxPlotRows = 24;

    /// <summary>
    /// The frame, sized to the window. The live display redraws in place only while the frame fits, so the chart
    /// gets what's left once the rest is drawn: the frame is drawn with the smallest chart and measured (the tables
    /// stack, and long lines wrap, in a narrow window), and the chart is grown by the rows to spare. The chart's
    /// height is the only part that changes with it, so the result fits exactly. If even the smallest chart
    /// doesn't fit, the frame is compact: one table, with the meter and its link in a single row.
    /// </summary>
    public static IRenderable Render(DashboardModel m)
    {
        var available = m.Height - 2; // a line to spare, and one for the cursor
        foreach (var compact in new[] { false, true })
        {
            var smallest = Render(m, MinPlotRows, compact);
            var spare = available - HeightOf(smallest, m.Width);
            if (spare >= 0) return spare == 0 ? smallest : Render(m, Math.Min(MaxPlotRows, MinPlotRows + spare), compact);
            if (compact) return smallest; // a very small window: as small as it gets
        }
        throw new InvalidOperationException(); // not reached
    }

    /// <summary>How many lines a renderable takes at a width.</summary>
    public static int HeightOf(IRenderable r, int width)
    {
        var w = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(w),
        });
        console.Profile.Width = width;
        console.Write(r);
        var s = w.ToString().Replace("\r\n", "\n");
        return s.Length == 0 ? 0 : s.Count(ch => ch == '\n') + (s.EndsWith('\n') ? 0 : 1);
    }

    /// <param name="compact">One table instead of two, for a small window.</param>
    static IRenderable Render(DashboardModel m, int plotRows, bool compact)
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
            ReadingStatus.Overload => "   [red]Probe open (OL): check the probe is plugged in[/]",
            ReadingStatus.NotTemperature => $"   [red]The meter is not measuring temperature.{(m.NotTemperatureHint.Length > 0 ? " " + Markup.Escape(m.NotTemperatureHint) : "")}[/]",
            ReadingStatus.Error => "   [red]Meter sent something unexpected[/]",
            _ => "",
        };
        var header = new Rows(
            new Markup($"[bold orange1]{Temp.ModeName(m.Point.Mode).ToUpperInvariant()} {Markup.Escape(f.Setpoint(m.Point.SetpointC))}[/]" +
                       $"   [grey]setpoint {m.Point.Index} of {m.PointCount}   elapsed {Clock(now)}[/]"),
            new Markup(m.Alert is { } alert ? $"{current}   [bold red]{Markup.Escape(alert)}[/]" : $"{current}{status}"),
            new Markup($"[cyan]{Markup.Escape(PhaseText(a))}[/]" + (a.Phase == PointPhase.Complete ? "" : $"   time left at this setting: [bold]{Markup.Escape(Remaining(a.EstimateRemaining()))}[/]")));

        // Chart (its height from Render(m), which sizes the frame to the window).
        var plotWidth = Math.Max(40, m.Width - 4);
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
        var drops = p.Reconnects > 0 ? $" [grey]({p.Reconnects} drop{(p.Reconnects == 1 ? "" : "s")})[/]" : "";
        var battery = BatteryText.Markup(p.BatteryPercent);
        var lastError = p.LastError is { } err && p.LastErrorAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(2)
            ? $"[yellow]{Markup.Escape(err)}[/]" : null;
        var meter = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35).HideHeaders().AddColumn("").AddColumn("");
        meter.AddRow("Meter", Markup.Escape(m.MeterLabel));
        meter.AddRow("Link", link + drops);
        if (battery is not null) meter.AddRow("Battery", battery);
        meter.AddRow("Readings", $"{a.Series.Count.ToString("N0", Inv)}");
        meter.AddRow("Detecting", $"turns of {f.Span(a.Hysteresis)} or more");
        if (lastError is not null) meter.AddRow("Last error", lastError);

        IRenderable bottom;
        if (compact)
        {
            // The meter, its link (with any drops) and battery in one row, and the last error while there is one:
            // what matters while watching, without the meter table.
            stats.AddRow("Meter", $"{Markup.Escape(m.MeterLabel)}, {link}{drops}" + (battery is not null ? $", battery {battery}" : ""));
            if (lastError is not null) stats.AddRow("Last error", lastError);
            while (stats.Rows.Count < TableRows) stats.AddRow("", "");
            bottom = stats.Border(TableBorder.None); // no blank border lines
        }
        else
        {
            while (stats.Rows.Count < TableRows) stats.AddRow("", "");
            while (meter.Rows.Count < TableRows) meter.AddRow("", "");
            bottom = new Columns(stats, meter) { Expand = true };
        }
        var keys = new Markup(compact
            ? "[grey]Keys:[/] [bold]P[/] preheated  [bold]N[/] finish  [bold]R[/] restart  [bold]S[/] skip  [bold]Z[/] view  [bold]Q[/] quit"
            : "[grey]Keys:[/] [bold]P[/] oven says preheated   [bold]N[/] finish this setpoint now   " +
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
