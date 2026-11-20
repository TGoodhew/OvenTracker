using OvenTracker.Analysis;
using OvenTracker.Meter;
using OvenTracker.Ui;
using Spectre.Console;

namespace OvenTracker.Session;

public enum PointOutcome { Next, Quit }

/// <summary>
/// Tracks one setpoint until the analyzer says it has enough, or the user stops it: on the live display in a
/// terminal, or with a progress line each minute when the output isn't one (a file, a pipe, no console).
/// </summary>
public sealed class PointTracker(
    MeterPoller poller,
    RunRecorder recorder,
    UnitFormatter format,
    AnalysisOptions options,
    double toleranceC,
    string meterLabel,
    string notTemperatureHint,
    int pointCount,
    bool fast,
    AutoPowerOff? autoPowerOff = null)
{
    /// <param name="startingFromC">Where the oven was before this setpoint, so the app can check it heads for the new setting.</param>
    /// <param name="swingC">The thermostat's swing at the setpoint before, if measured.</param>
    public async Task<(PointResult Result, PointOutcome Outcome)> RunAsync(TestPoint point, CancellationToken ct, double? startingFromC = null, double? swingC = null)
    {
        var heading = startingFromC is { } from ? new HeadingCheck(from, point.SetpointC, swingC) : null;
        var analyzer = new SetpointAnalyzer(point.SetpointC, options);
        DateTimeOffset? start = null;
        var started = false;
        DateTimeOffset lastTime = DateTimeOffset.UtcNow;
        ReadingStatus? lastStatus = null;
        string? notice = null;
        char? armed = null; // keys that need pressing twice
        var view = ChartView.Auto;
        var outcome = PointOutcome.Next;
        var canReadKeys = !Console.IsInputRedirected;
        var silence = new SilenceWatch();
        string? alert = null;

        IRenderableFactory render = () => Dashboard.Render(new DashboardModel(
            point, pointCount, analyzer, format, poller, meterLabel, notTemperatureHint, toleranceC, view, lastStatus, notice,
            SafeWidth(), SafeHeight(), alert));

        // One pass: take the new readings, watch for a quiet meter or an oven not heading for the setting,
        // and handle keys. True once the setpoint is done. The live display and plain output share it.
        bool Tick()
        {
            while (poller.Readings.TryRead(out var r))
            {
                if (start is null)
                {
                    start = r.Time;
                    // Replay starts a setpoint here; after R the "restart" event has already done that.
                    if (!started) recorder.Event(r.Time, point, 0, "start");
                    started = true;
                }
                lastTime = r.Time;
                var elapsed = (r.Time - start.Value).TotalSeconds;
                recorder.Reading(r, point, elapsed);
                lastStatus = r.Status;
                if (r is { Status: ReadingStatus.Ok, Celsius: { } c })
                {
                    analyzer.Add(elapsed, c);
                    // Is the oven heading for the new setting? If not, it probably wasn't changed.
                    if (heading is not null)
                    {
                        var was = heading.State;
                        var now = heading.Update(elapsed, c);
                        if (now == HeadingState.NotHeading && was != HeadingState.NotHeading)
                        {
                            recorder.Event(r.Time, point, elapsed, "not-heading");
                            poller.Enqueue(m => m.Beep());
                            ConsoleCaps.Bell();
                        }
                        else if (now == HeadingState.OnItsWay && was == HeadingState.NotHeading)
                        {
                            recorder.Event(r.Time, point, elapsed, "heading");
                            notice = "The oven is heading for the setting now.";
                        }
                    }
                }
                if (analyzer.Phase == PointPhase.Complete) return true;
            }

            // A meter that has gone quiet (often its auto power-off): say so, beep once, and mark the gap.
            switch (silence.Update(poller.SilentFor))
            {
                case SilenceChange.WentQuiet:
                    recorder.Event(lastTime, point, analyzer.Now, "meter-silent");
                    ConsoleCaps.Bell();
                    break;
                case SilenceChange.CameBack:
                    recorder.Event(lastTime, point, analyzer.Now, "meter-back");
                    notice = "The meter is answering again; tracking carries on.";
                    break;
            }
            alert = silence.Silent ? SilenceWatch.Message(poller.SilentFor, autoPowerOff)
                : heading is { State: HeadingState.NotHeading } h ? NotHeadingMessage(h, format) : null;

            if (ct.IsCancellationRequested)
            {
                analyzer.Finish(CompletionReason.Aborted);
                outcome = PointOutcome.Quit;
                return true;
            }

            while (canReadKeys && Console.KeyAvailable)
            {
                var key = char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar);
                var confirmed = armed == key;
                armed = null;
                switch (key)
                {
                    case 'P':
                        analyzer.MarkPreheatSignal();
                        recorder.Event(lastTime, point, analyzer.Now, "preheat-signal");
                        notice = $"Preheat signal noted at {Dashboard.Clock(analyzer.Now)}" +
                                 (analyzer.SmoothedC is { } pc ? $", oven at {format.T(pc)}." : ".");
                        break;
                    case 'Z':
                        view = (ChartView)(((int)view + 1) % 3);
                        break;
                    case 'N' or 'R' or 'S' or 'Q' when !confirmed:
                        armed = key;
                        notice = key switch
                        {
                            'N' => "Press N again to finish this setpoint now with the data so far.",
                            'R' => "Press R again to throw this setpoint's data away and start it again.",
                            'S' => "Press S again to skip this setpoint.",
                            _ => "Press Q again to stop the session and write the report.",
                        };
                        break;
                    case 'N':
                        analyzer.Finish(CompletionReason.Manual);
                        break;
                    case 'S':
                        analyzer.Finish(CompletionReason.Skipped);
                        break;
                    case 'Q':
                        analyzer.Finish(CompletionReason.Aborted);
                        outcome = PointOutcome.Quit;
                        break;
                    case 'R':
                        recorder.Event(lastTime, point, analyzer.Now, "restart");
                        analyzer = new SetpointAnalyzer(point.SetpointC, options);
                        // Check again from where the oven is now (e.g. after changing the setting).
                        if (heading is not null) heading = new HeadingCheck(poller.LastCelsius ?? heading.FromC, point.SetpointC, swingC);
                        start = null;
                        notice = "Restarted: close the door and leave it; the clock starts again from the next reading.";
                        break;
                }
            }
            return analyzer.Phase == PointPhase.Complete;
        }

        var delay = TimeSpan.FromMilliseconds(fast ? 100 : 250);
        var live = ConsoleCaps.Live;
        if (live)
        {
            try
            {
                // Start the live display at the top of a clear screen, so it never has to scroll.
                if (canReadKeys) Console.Write("\u001b[H\u001b[2J"); // the screen only: earlier output stays in the scrollback
                await AnsiConsole.Live(render())
                    .AutoClear(false)
                    .Overflow(VerticalOverflow.Crop)
                    .Cropping(VerticalOverflowCropping.Top)
                    .StartAsync(async ctx =>
                    {
                        while (!Tick())
                        {
                            ctx.UpdateTarget(render());
                            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
                        }
                        ctx.UpdateTarget(render());
                    });
                AnsiConsole.WriteLine();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // The console can't draw the live display after all: carry on, with plain lines.
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[yellow]The live display stopped ({Markup.Escape(ex.Message)}); carrying on with progress lines.[/]");
                live = false;
            }
        }
        if (!live && analyzer.Phase != PointPhase.Complete) await RunPlainAsync();

        recorder.Event(lastTime, point, analyzer.Now, $"end:{analyzer.Reason}");
        return (new PointResult(point, analyzer.Metrics()), outcome);

        // Without a live display (output to a file or pipe, or no console): a line now and then instead.
        async Task RunPlainAsync()
        {
            AnsiConsole.MarkupLine($"[bold]{point.Index} of {pointCount}: {Markup.Escape(Temp.ModeName(point.Mode))} {Markup.Escape(format.Setpoint(point.SetpointC))}[/]" +
                                   (canReadKeys ? "   [grey]keys: P preheated · N N finish · R R restart · S S skip · Q Q quit[/]" : ""));
            var lastLine = double.NegativeInfinity;
            var lastPhase = analyzer.Phase;
            string? lastAlert = null, lastNotice = null;
            while (!Tick())
            {
                var due = analyzer.Now - lastLine >= PlainEvery.TotalSeconds || analyzer.Phase != lastPhase;
                if (alert != lastAlert)
                {
                    if (alert is not null) AnsiConsole.MarkupLine($"[red]{Markup.Escape(alert)}[/]");
                    lastAlert = alert;
                }
                if (notice != lastNotice)
                {
                    if (notice is not null) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(notice)}[/]");
                    lastNotice = notice;
                }
                if (due && analyzer.LatestC is not null)
                {
                    AnsiConsole.WriteLine(PlainLine(analyzer, format, poller.BatteryPercent));
                    lastLine = analyzer.Now;
                    lastPhase = analyzer.Phase;
                }
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
            }
            AnsiConsole.WriteLine(PlainLine(analyzer, format, poller.BatteryPercent));
        }
    }

    /// <summary>How often a progress line is written without the live display: each minute of the setpoint.</summary>
    public static readonly TimeSpan PlainEvery = TimeSpan.FromMinutes(1);

    /// <summary>One progress line for plain output: time, temperature, phase, mean so far and time left.</summary>
    public static string PlainLine(SetpointAnalyzer a, UnitFormatter f, double? batteryPercent = null)
    {
        var parts = new List<string> { Dashboard.Clock(a.Now) };
        if (a.LatestC is { } c) parts.Add($"{f.T(c)} ({f.D(c - a.SetpointC)})");
        parts.Add(Dashboard.PhaseText(a));
        if (a.CurrentEstimate is { } e) parts.Add($"mean so far {f.T(e.MeanC)}" + (e.HalfWidthC is { } hw ? $" ±{f.Span(hw)}" : ""));
        if (a.Phase != PointPhase.Complete) parts.Add($"time left {Dashboard.Remaining(a.EstimateRemaining())}");
        if (BatteryText.Plain(batteryPercent) is { } battery) parts.Add(battery);
        return string.Join("  ·  ", parts);
    }

    /// <summary>What to say when the oven isn't heading for its new setting.</summary>
    public static string NotHeadingMessage(HeadingCheck h, UnitFormatter f) =>
        $"The oven isn't {(h.Heating ? "heating up" : "cooling down")} towards {f.Setpoint(h.SetpointC)}: it's still near {f.T(h.FromC, 0)}. " +
        "Did you change the setting? If not, change it now and press R twice to restart this setpoint.";

    delegate Spectre.Console.Rendering.IRenderable IRenderableFactory();

    static int SafeWidth() => AnsiConsole.Profile.Width > 0 ? AnsiConsole.Profile.Width : 120;
    static int SafeHeight() => AnsiConsole.Profile.Height > 0 ? AnsiConsole.Profile.Height : 40;
}
