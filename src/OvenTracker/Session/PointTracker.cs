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
    bool fast)
{
    public async Task<(PointResult Result, PointOutcome Outcome)> RunAsync(TestPoint point, CancellationToken ct)
    {
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

        IRenderableFactory render = () => Dashboard.Render(new DashboardModel(
            point, pointCount, analyzer, format, poller, meterLabel, notTemperatureHint, toleranceC, view, lastStatus, notice,
            SafeWidth(), SafeHeight()));

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
                        if (r is { Status: ReadingStatus.Ok, Celsius: { } c }) analyzer.Add(elapsed, c);
                        if (analyzer.Phase == PointPhase.Complete) break;
                    }

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

    delegate Spectre.Console.Rendering.IRenderable IRenderableFactory();

    static int SafeWidth() => AnsiConsole.Profile.Width > 0 ? AnsiConsole.Profile.Width : 120;
    static int SafeHeight() => AnsiConsole.Profile.Height > 0 ? AnsiConsole.Profile.Height : 40;
}
