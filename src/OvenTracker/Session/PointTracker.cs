using OvenTracker.Analysis;
using OvenTracker.Meter;
using OvenTracker.Ui;
using Spectre.Console;

namespace OvenTracker.Session;

public enum PointOutcome { Next, Quit }

/// <summary>Why a setpoint's tracking is paused.</summary>
public enum PauseReason
{
    /// <summary>The meter's battery is low: its readings can't be trusted. Swap or charge it.</summary>
    LowBattery,

    /// <summary>A reading changed faster than an oven can: the meter or the probe is failing.</summary>
    ImpossibleReading,
}

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
    AutoPowerOff? autoPowerOff = null,
    double? probeMaxC = null,
    bool hasBattery = false)
{
    /// <summary>
    /// Faster than this, a reading can't be the oven: 10 °C (18 °F) a second, where an oven's probe moves at most
    /// about 1 °C a second, door open or not, and a meter with a failing battery jumps by 50–90 °C (#51).
    /// </summary>
    public const double MaxRateCPerSecond = 10;

    /// <summary>Plausible readings in a row before tracking carries on by itself after a pause, once the meter has been off and come back.</summary>
    public const int ResumeAfterReadings = 10;

    /// <summary>Plausible readings in a row before tracking carries on by itself when nobody can press a key.</summary>
    public const int ResumeAfterReadingsWithoutKeys = 60;

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
        // Paused for a battery swap (or a failing meter): readings are recorded outside the setpoint and not analysed,
        // and the setpoint carries on afterwards with its cycles so far. The analyzer sees a gap (see SetpointAnalyzer.GapSeconds).
        PauseReason? paused = null;
        var ignoreLowBattery = false;
        var offDuringPause = false;
        var plausible = 0;
        (double T, double C)? lastOk = null;
        double? pauseJumpC = null;
        var batteryReadsAtResume = -1L; // a low battery pauses again only once the battery has been read since carrying on

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
                var jump = r is { Status: ReadingStatus.Ok, Celsius: { } rc } && lastOk is { } prev
                    && Math.Abs(rc - prev.C) / Math.Max(0.5, elapsed - prev.T) > MaxRateCPerSecond;
                if (paused is null && jump)
                {
                    pauseJumpC = r.Celsius!.Value - lastOk!.Value.C;
                    Pause(PauseReason.ImpossibleReading, elapsed);
                }
                if (paused is not null)
                {
                    // Not part of the setpoint: recorded as an idle reading, so replay leaves it out too.
                    recorder.Reading(r, null, null);
                    lastStatus = r.Status;
                    plausible = r.Status == ReadingStatus.Ok && !jump ? plausible + 1 : 0;
                    if (r is { Status: ReadingStatus.Ok, Celsius: { } pc0 }) lastOk = (elapsed, pc0);
                    continue;
                }
                recorder.Reading(r, point, elapsed);
                lastStatus = r.Status;
                if (r is { Status: ReadingStatus.Ok, Celsius: { } c })
                {
                    lastOk = (elapsed, c);
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

            // A meter that has gone quiet (often its auto power-off): say so, beep once, and mark the gap. First, so that
            // carrying on after a pause, below, has the last word in the same pass.
            switch (silence.Update(poller.SilentFor))
            {
                case SilenceChange.WentQuiet:
                    recorder.Event(lastTime, point, Elapsed(), "meter-silent");
                    ConsoleCaps.Bell();
                    break;
                case SilenceChange.CameBack:
                    recorder.Event(lastTime, point, Elapsed(), "meter-back");
                    if (paused is not null) poller.RequestBattery(); // back from a swap: check the new battery now
                    notice = paused is not null
                        ? "The meter is answering again; checking it before carrying on."
                        : "The meter is answering again; tracking carries on.";
                    break;
            }

            // A battery meter running low: pause for a swap, rather than measure with readings that can't be trusted.
            if (paused is null && hasBattery && !ignoreLowBattery && poller.LowBattery == true && poller.BatteryReads > batteryReadsAtResume)
                Pause(PauseReason.LowBattery, Elapsed());
            if (paused is not null)
            {
                if (poller.SilentFor >= SilenceWatch.DefaultThreshold || !poller.IsConnected) offDuringPause = true;
                // Back by itself: the meter was switched off and on (a new battery), and its readings make sense again;
                // or nobody can press a key, and they have for a minute.
                var batteryOk = poller.LowBattery != true || ignoreLowBattery;
                if (batteryOk && (offDuringPause && plausible >= ResumeAfterReadings || !canReadKeys && plausible >= ResumeAfterReadingsWithoutKeys))
                    Resume(ignoreLow: false);
            }
            alert = paused is { } why ? PauseMessage(why, hasBattery, pauseJumpC, format, canReadKeys)
                : silence.Silent ? SilenceWatch.Message(poller.SilentFor, autoPowerOff)
                : heading is { State: HeadingState.NotHeading } h ? NotHeadingMessage(h, format)
                : probeMaxC is { } max && analyzer.LatestC is { } latest && latest > max ? OverRatingMessage(latest, max, format) : null;

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
                // While paused: Enter carries on (the battery is swapped, the meter fixed); C carries on regardless of a low battery.
                if (paused is not null && key is '\r' or 'C')
                {
                    Resume(ignoreLow: key == 'C');
                    continue;
                }
                switch (key)
                {
                    case 'B' when paused is null:
                        // A battery swap you choose: before it runs out, or to charge it.
                        Pause(PauseReason.LowBattery, Elapsed());
                        break;
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

        // The setpoint's clock at the last reading, which runs on while paused (the analyzer's stands still).
        double Elapsed() => start is { } s0 ? (lastTime - s0).TotalSeconds : 0;

        void Pause(PauseReason why, double at)
        {
            paused = why;
            offDuringPause = false;
            plausible = 0;
            recorder.Event(lastTime, point, at, $"pause:{why}");
            poller.Enqueue(m => m.Beep());
            ConsoleCaps.Bell();
            // The simulator "swaps" its battery by itself on any pause (its wild readings come from a flat battery too),
            // so a simulated run carries on.
            if (fast && hasBattery) poller.Enqueue(m => (m as IOvenSimulation)?.SwapBattery());
        }

        void Resume(bool ignoreLow)
        {
            paused = null;
            pauseJumpC = null;
            ignoreLowBattery |= ignoreLow;
            lastOk = null; // the first reading after the pause isn't compared across it
            batteryReadsAtResume = poller.BatteryReads;
            poller.RequestBattery();
            recorder.Event(lastTime, point, Elapsed(), "pause-end");
            notice = "Carrying on with this setpoint: the cycles so far are kept, and the one the pause broke is left out.";
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
                                   (canReadKeys ? "   [grey]keys: P preheated · B battery swap · N N finish · R R restart · S S skip · Q Q quit[/]" : ""));
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

    /// <summary>What to say while tracking is paused.</summary>
    public static string PauseMessage(PauseReason why, bool hasBattery, double? jumpC, UnitFormatter f, bool canReadKeys)
    {
        var carryOn = canReadKeys
            ? "Tracking carries on by itself once the meter has been off and is back with sensible readings, or press Enter."
            : "Tracking carries on by itself once the meter's readings make sense again.";
        return why switch
        {
            PauseReason.LowBattery =>
                "PAUSED for a battery swap: the meter's battery is low, and a low battery gives false readings. Switch the meter off, " +
                "fit a fresh battery (or a charged one), and switch it back on at temperature. " + carryOn +
                " The cycles so far are kept." + (canReadKeys ? " C carries on without a swap." : ""),
            _ =>
                $"PAUSED: a reading jumped {(jumpC is { } j ? f.D(j) : "")} in a second, faster than an oven can change, so the meter " +
                (hasBattery ? "is failing, most likely its battery. Switch it off, fit a fresh battery, and switch it back on. " : "or the probe is failing. Check the probe and its connection. ") +
                carryOn + " The cycles so far are kept.",
        };
    }

    /// <summary>What to say when the probe reads above its rating.</summary>
    public static string OverRatingMessage(double readingC, double maxC, UnitFormatter f) =>
        $"The probe reads {f.T(readingC, 0)}, above its rated {f.T(maxC, 0)}: it may be damaged, and readings this hot can't be trusted. Lower the setting.";

    /// <summary>What to say when the oven isn't heading for its new setting.</summary>
    public static string NotHeadingMessage(HeadingCheck h, UnitFormatter f) =>
        $"The oven isn't {(h.Heating ? "heating up" : "cooling down")} towards {f.Setpoint(h.SetpointC)}: it's still near {f.T(h.FromC, 0)}. " +
        "Did you change the setting? If not, change it now and press R twice to restart this setpoint.";

    delegate Spectre.Console.Rendering.IRenderable IRenderableFactory();

    static int SafeWidth() => AnsiConsole.Profile.Width > 0 ? AnsiConsole.Profile.Width : 120;
    static int SafeHeight() => AnsiConsole.Profile.Height > 0 ? AnsiConsole.Profile.Height : 40;
}
