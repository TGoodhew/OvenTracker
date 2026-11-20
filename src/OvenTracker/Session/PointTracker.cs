using OvenTracker.Analysis;
using OvenTracker.Meter;
using OvenTracker.Ui;
using Spectre.Console;

namespace OvenTracker.Session;

public enum PointOutcome { Next, Quit }

/// <summary>Runs the live display for one setpoint until the analyzer says it has enough, or the user stops it.</summary>
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

        // Start the live display at the top of a clear screen, so it never has to scroll.
        if (canReadKeys) Console.Write("\u001b[H\u001b[2J"); // the screen only: earlier output stays in the scrollback
        await AnsiConsole.Live(render())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                while (true)
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
                                    Console.Write("\a");
                                }
                                else if (now == HeadingState.OnItsWay && was == HeadingState.NotHeading)
                                {
                                    recorder.Event(r.Time, point, elapsed, "heading");
                                    notice = "The oven is heading for the setting now.";
                                }
                            }
                        }
                        if (analyzer.Phase == PointPhase.Complete) break;
                    }

                    // A meter that has gone quiet (often its auto power-off): say so, beep once, and mark the gap.
                    switch (silence.Update(poller.SilentFor))
                    {
                        case SilenceChange.WentQuiet:
                            recorder.Event(lastTime, point, analyzer.Now, "meter-silent");
                            Console.Write("\a");
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
                        break;
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

                    if (analyzer.Phase == PointPhase.Complete) break;
                    ctx.UpdateTarget(render());
                    try { await Task.Delay(fast ? 100 : 250, ct); } catch (OperationCanceledException) { }
                }
                ctx.UpdateTarget(render());
            });

        AnsiConsole.WriteLine();
        recorder.Event(lastTime, point, analyzer.Now, $"end:{analyzer.Reason}");
        return (new PointResult(point, analyzer.Metrics()), outcome);
    }

    /// <summary>What to say when the oven isn't heading for its new setting.</summary>
    public static string NotHeadingMessage(HeadingCheck h, UnitFormatter f) =>
        $"The oven isn't {(h.Heating ? "heating up" : "cooling down")} towards {f.Setpoint(h.SetpointC)}: it's still near {f.T(h.FromC, 0)}. " +
        "Did you change the setting? If not, change it now and press R twice to restart this setpoint.";

    delegate Spectre.Console.Rendering.IRenderable IRenderableFactory();

    static int SafeWidth() => AnsiConsole.Profile.Width > 0 ? AnsiConsole.Profile.Width : 120;
    static int SafeHeight() => AnsiConsole.Profile.Height > 0 ? AnsiConsole.Profile.Height : 40;
}
