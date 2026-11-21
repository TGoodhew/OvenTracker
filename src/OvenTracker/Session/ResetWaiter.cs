using System.Globalization;
using OvenTracker.Analysis;
using OvenTracker.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace OvenTracker.Session;

public enum ResetOutcome
{
    /// <summary>The oven reached the target.</summary>
    Ready,

    /// <summary>The user chose to carry on before it did.</summary>
    CarryOn,

    /// <summary>The user stopped the session.</summary>
    Quit,
}

/// <summary>A temperature the oven has to get down to, and what it's for.</summary>
public sealed record ResetTarget(string Label, double TargetC);

/// <summary>
/// Watches the oven between sittings, or between setpoints when it needs to cool first: shows its
/// temperature, predicts when it will be ready for the next setpoint and when it will be cold, and
/// says when it gets there.
/// </summary>
public sealed class ResetWaiter(MeterPoller poller, RunRecorder recorder, UnitFormatter f, double ambientC, bool ambientAssumed, bool fast,
    Meter.AutoPowerOff? autoPowerOff = null)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The temperature a setpoint should start from: just above the setpoint before it in the plan, or near room temperature for the first.</summary>
    public static double TargetFor(IReadOnlyList<TestPoint> plan, TestPoint point, double ambientC)
    {
        var i = plan.ToList().FindIndex(p => p.Index == point.Index);
        return i > 0 ? plan[i - 1].SetpointC + Margin : ambientC + Margin;
    }

    /// <summary>How far above the previous setpoint (or room temperature) still counts as ready: 10 °F.</summary>
    public const double Margin = 10 / 1.8;

    /// <param name="next">What the oven has to cool to for the next setpoint: the run waits for this one.</param>
    /// <param name="autoCarryOn">Carry on by itself when ready (--yes), rather than waiting for a key.</param>
    public async Task<ResetOutcome> RunAsync(ResetTarget next, bool autoCarryOn, CancellationToken ct)
    {
        var model = new CoolingModel(ambientC);
        var cold = new ResetTarget("Start again from cold", ambientC + Margin);
        DateTimeOffset? start = null;
        DateTimeOffset now = recorder.Now; // then each reading's time
        var ready = false;
        var outcome = ResetOutcome.Ready;
        var canReadKeys = !Console.IsInputRedirected;
        autoCarryOn |= !canReadKeys; // nobody can press C
        string? notice = null;

        recorder.Note(now, "reset-start");
        IRenderable Render() => View(model, next, cold, ready, start is { } s ? now - s : TimeSpan.Zero, notice, autoCarryOn);

        if (!ConsoleCaps.Live)
        {
            outcome = await RunPlainAsync();
            AnsiConsole.WriteLine();
            recorder.Note(now, $"reset-end:{outcome}");
            return outcome;
        }

        if (canReadKeys) Console.Write("\u001b[H\u001b[2J");
        await AnsiConsole.Live(Render())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async ctx =>
            {
                while (true)
                {
                    while (poller.Readings.TryRead(out var r))
                    {
                        recorder.Reading(r, null, null);
                        now = r.Time;
                        start ??= r.Time;
                        if (r is { Status: Meter.ReadingStatus.Ok, Celsius: { } c }) model.Add((r.Time - start.Value).TotalSeconds, c);
                    }

                    if (!ready && model.CurrentC is { } current && current <= next.TargetC)
                    {
                        ready = true;
                        poller.Enqueue(m => m.Beep());
                        ConsoleCaps.Bell();
                        if (autoCarryOn) break;
                    }

                    if (ct.IsCancellationRequested) { outcome = ResetOutcome.Quit; break; }
                    var done = false;
                    while (canReadKeys && Console.KeyAvailable)
                    {
                        var key = char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar);
                        if (key is 'C' or '\r') { outcome = ready ? ResetOutcome.Ready : ResetOutcome.CarryOn; done = true; }
                        else if (key == 'Q') { outcome = ResetOutcome.Quit; done = true; }
                        else notice = "Keys: C (or Enter) carry on with the next setpoint · Q stop here (the run is saved; carry on later with --resume)";
                    }
                    if (done) break;
                    ctx.UpdateTarget(Render());
                    try { await Task.Delay(fast ? 100 : 500, ct); } catch (OperationCanceledException) { }
                }
                ctx.UpdateTarget(Render());
            });

        AnsiConsole.WriteLine();
        recorder.Note(now, $"reset-end:{outcome}");
        return outcome;

        // Without a live display: a line each minute, and when the oven's state changes.
        async Task<ResetOutcome> RunPlainAsync()
        {
            AnsiConsole.MarkupLine($"[bold]Oven reset:[/] waiting until the oven is below {Markup.Escape(f.T(next.TargetC, 0))} for {Markup.Escape(next.Label)}." +
                                   (canReadKeys ? " [grey]C carry on now · Q stop here[/]" : ""));
            var lastLine = DateTimeOffset.MinValue;
            var lastState = (CoolingState?)null;
            while (true)
            {
                while (poller.Readings.TryRead(out var r))
                {
                    recorder.Reading(r, null, null);
                    now = r.Time;
                    start ??= r.Time;
                    if (r is { Status: Meter.ReadingStatus.Ok, Celsius: { } c }) model.Add((r.Time - start.Value).TotalSeconds, c);
                }
                if (model.CurrentC is { } current && current <= next.TargetC)
                {
                    poller.Enqueue(m => m.Beep());
                    ConsoleCaps.Bell();
                    AnsiConsole.WriteLine($"The oven is ready for {next.Label} ({f.T(current, 0)}).");
                    if (autoCarryOn) return ResetOutcome.Ready;
                    ready = true;
                }
                if (ct.IsCancellationRequested) return ResetOutcome.Quit;
                while (canReadKeys && Console.KeyAvailable)
                {
                    var key = char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar);
                    if (key is 'C' or '\r') return ready ? ResetOutcome.Ready : ResetOutcome.CarryOn;
                    if (key == 'Q') return ResetOutcome.Quit;
                }
                if (model.CurrentC is { } shown && (DateTimeOffset.UtcNow - lastLine >= TimeSpan.FromMinutes(1) || model.State != lastState))
                {
                    var eta = model.TimeUntil(next.TargetC) is { } e ? $"ready in about {Minutes(e.TotalMinutes)}" : "learning how fast it cools";
                    var coldEta = model.TimeUntil(cold.TargetC) is { } ce ? $"; cold in about {Minutes(ce.TotalMinutes)}" : "";
                    var silent = poller.SilentFor >= SilenceWatch.DefaultThreshold ? $"  {SilenceWatch.Message(poller.SilentFor, autoPowerOff)}" : "";
                    var battery = BatteryText.Plain(poller.BatteryPercent) is { } b ? $" Meter {b}." : "";
                    AnsiConsole.WriteLine($"{f.T(shown, 0)}, {model.State.ToString().ToLowerInvariant()}: {eta}{coldEta}.{battery}{silent}");
                    lastLine = DateTimeOffset.UtcNow;
                    lastState = model.State;
                }
                try { await Task.Delay(fast ? 100 : 500, ct); } catch (OperationCanceledException) { }
            }
        }
    }

    IRenderable View(CoolingModel m, ResetTarget next, ResetTarget cold, bool ready, TimeSpan watched, string? notice, bool autoCarryOn)
    {
        var current = m.CurrentC is { } c ? $"[bold]{f.T(c)}[/]" : "[grey]waiting for a reading…[/]";
        var state = ready
            ? "[green]Ready.[/]"
            : m.State switch
            {
                CoolingState.Learning => "[grey]Learning how fast this oven cools (a few minutes)…[/]",
                CoolingState.Cooling => $"[cyan]Cooling[/] [grey](time constant {Minutes(m.TauSeconds!.Value / 60)})[/]",
                CoolingState.Heating => "[yellow]Getting hotter: the oven is in use, or hasn't been turned off. Predictions resume once it cools.[/]",
                CoolingState.DoorOpen => "[yellow]Dropping fast: the door is open. Shut it and the prediction carries on.[/]",
                _ => "",
            };

        var targets = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .AddColumn("Ready to").AddColumn("Below").AddColumn("Time").AddColumn("At");
        // From cold is worth showing only when it's a different target (it's the same for the first setpoint).
        foreach (var t in Math.Abs(cold.TargetC - next.TargetC) < 1 ? [next] : new[] { next, cold })
        {
            var eta = m.TimeUntil(t.TargetC);
            var when = eta is { } e ? (e == TimeSpan.Zero ? "[green]now[/]" : e.TotalMinutes < 1 ? "under a minute" : $"about {Minutes(e.TotalMinutes)}") : "[grey]—[/]";
            var at = eta is { } e2 && e2 > TimeSpan.Zero ? DateTime.Now.Add(e2).ToString("t", CultureInfo.CurrentCulture) : "";
            targets.AddRow(Markup.Escape(t.Label), Markup.Escape(f.T(t.TargetC, 0)), when, at);
        }

        var samples = m.Samples.Select(s => new Sample(s.T, s.C)).ToList();
        var width = Math.Max(40, (AnsiConsole.Profile.Width > 0 ? AnsiConsole.Profile.Width : 100) - 4);
        IRenderable chart = samples.Count < 2
            ? new Markup("[grey]The chart starts with the first readings.[/]")
            : new Markup(BraillePlot.Markup(samples, samples[0].T, Math.Max(samples[^1].T, samples[0].T + 60), next.TargetC, 0, f, width, 10));

        var rows = new List<IRenderable>
        {
            new Panel(new Rows(
                    new Markup($"[bold orange1]OVEN RESET[/]   [grey]watching for {Minutes(watched.TotalMinutes)}[/]" +
                               (BatteryText.Markup(poller.BatteryPercent) is { } battery ? $"   [grey]meter battery[/] {battery}" : "")),
                    new Markup($"{current}   {state}"),
                    new Markup(ready
                        ? autoCarryOn ? "Carrying on with the next setpoint." : $"[bold]The oven is ready for {Markup.Escape(next.Label)}.[/] Press [bold]C[/] to carry on."
                        : $"Waiting until the oven is below {Markup.Escape(f.T(next.TargetC, 0))} for [bold]{Markup.Escape(next.Label)}[/]. Leave the door shut.")))
                .Border(BoxBorder.Rounded).BorderColor(Color.Orange1).Expand(),
            targets,
            new Panel(chart).Header("[grey]oven temperature while cooling · the target dotted[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey35).Expand(),
            new Markup("[grey]Keys:[/] [bold]C[/] carry on now   [bold]Q[/] stop here (saved; carry on later with [bold]--resume[/])"
                       + (ambientAssumed ? $"   [grey]room temperature assumed {Markup.Escape(f.T(m.AmbientC, 0))} (--ambient to set it)[/]" : "")),
        };
        if (notice is not null) rows.Add(new Markup($"[yellow]{Markup.Escape(notice)}[/]"));
        if (poller.SilentFor >= SilenceWatch.DefaultThreshold)
            rows.Insert(1, new Markup($"[bold red]{Markup.Escape(SilenceWatch.Message(poller.SilentFor, autoPowerOff))}[/]"));
        return new Rows(rows);
    }

    static string Minutes(double minutes) => minutes switch
    {
        < 1 => "under a minute",
        < 90 => $"{minutes.ToString("F0", Inv)} min",
        _ => $"{Math.Floor(minutes / 60).ToString("F0", Inv)} h {(minutes % 60).ToString("F0", Inv)} min",
    };
}
