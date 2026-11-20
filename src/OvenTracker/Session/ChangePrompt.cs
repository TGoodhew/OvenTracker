using System.Globalization;
using OvenTracker.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace OvenTracker.Session;

public enum ChangeChoice { Start, Skip, Finish, Wait }

/// <summary>
/// Stops between setpoints and asks for the oven to be changed to the next setting, in large text, with
/// the oven's temperature live. Until a key is pressed it reminds (the meter beeps and the PC's bell
/// rings) every <c>remindEvery</c>, so a change isn't missed when nobody is at the oven.
/// </summary>
public sealed class ChangePrompt(MeterPoller poller, RunRecorder recorder, UnitFormatter f, TimeSpan remindEvery)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Whether to stop and ask. With a real meter, always, even with --yes: nothing else changes the
    /// oven. Only the simulator carries on by itself with --yes. Without a keyboard, it can't ask.
    /// </summary>
    public static bool ShouldAsk(bool yes, bool simulation, bool canReadKeys) => canReadKeys && !(yes && simulation);

    /// <summary>How many reminders are due after waiting this long (none if reminders are off).</summary>
    public static int RemindersDue(TimeSpan waited, TimeSpan every) => every <= TimeSpan.Zero ? 0 : (int)(waited.Ticks / every.Ticks);

    /// <param name="heading">"SET THE OVEN TO" or "CHANGE THE OVEN TO".</param>
    /// <param name="done">What was just finished, e.g. "Bake 350 °F is done: +7.9 °F, Accurate."</param>
    /// <param name="waitOffer">If the oven should cool first, what W offers.</param>
    public async Task<ChangeChoice> AskAsync(string heading, string modeName, double setpointC, string? done, string? waitOffer, CancellationToken ct)
    {
        var keys = "[bold]Enter[/] it's set and started: track it   [bold]S[/] skip this setpoint   [bold]F[/] finish the session" +
                   (waitOffer is null ? "" : $"   [bold]W[/] {Markup.Escape(waitOffer)}");
        var temp = $"{Math.Round(f.ToDisplay(setpointC)).ToString("0", Inv)} {f.Symbol}";
        // The mode and the temperature on lines of their own, so neither wraps in a narrow window.
        var result = await WaitForKeyAsync($"{heading} {modeName.ToUpperInvariant()} {temp}", heading, [modeName.ToUpperInvariant(), temp], done, keys,
            key => key switch
            {
                '\r' or '\n' or ' ' => ChangeChoice.Start,
                'S' => ChangeChoice.Skip,
                'F' or 'Q' => ChangeChoice.Finish,
                'W' when waitOffer is not null => ChangeChoice.Wait,
                _ => (ChangeChoice?)null,
            }, ct);
        return result ?? ChangeChoice.Finish;
    }

    /// <summary>At the end of a session: asks for the oven to be turned off, reminding until Enter.</summary>
    public async Task AskTurnOffAsync(string? done, CancellationToken ct) =>
        await WaitForKeyAsync("TURN THE OVEN OFF", "TURN THE OVEN", ["OFF"], done, "[bold]Enter[/] it's off",
            key => key is '\r' or '\n' or ' ' ? true : (bool?)null, ct);

    async Task<T?> WaitForKeyAsync<T>(string note, string heading, IReadOnlyList<string> big, string? done, string keys, Func<char, T?> choose, CancellationToken ct) where T : struct
    {
        var started = DateTimeOffset.UtcNow;
        var reminded = 0;
        T? choice = null;
        recorder.Note(started, $"prompt:{note}");

        IRenderable Render()
        {
            var waited = DateTimeOffset.UtcNow - started;
            var rows = new List<IRenderable>();
            if (done is not null) rows.Add(new Markup($"[green]{Markup.Escape(done)}[/]"));
            rows.Add(new Markup($"[bold]{Markup.Escape(heading)}[/]"));
            foreach (var line in big) rows.Add(new FigletText(line.Replace("°", "")).Color(Color.Orange1));
            rows.Add(new Markup((poller.LastCelsius is { } c ? $"The oven is at [bold]{Markup.Escape(f.T(c, 0))}[/] now.   " : "") +
                                $"[grey]Waiting {Markup.Escape(Waited(waited))}" +
                                (remindEvery > TimeSpan.Zero ? $"; a reminder beeps every {Markup.Escape(Waited(remindEvery))}.[/]" : ".[/]")));
            rows.Add(new Markup(keys));
            return new Panel(new Rows(rows)).Border(BoxBorder.Double).BorderColor(Color.Orange1).Expand();
        }

        if (!ConsoleCaps.Live)
        {
            // Output to a file or pipe, with a keyboard still there: say it in plain lines, and wait.
            if (done is not null) AnsiConsole.WriteLine(done);
            AnsiConsole.MarkupLine($"[bold]>>> {Markup.Escape(heading)} {Markup.Escape(string.Join(" ", big))}[/]");
            AnsiConsole.MarkupLine(keys);
            while (!ct.IsCancellationRequested && choice is null)
            {
                while (Console.KeyAvailable && choice is null)
                    choice = choose(char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar));
                if (choice is not null) break;
                var due = RemindersDue(DateTimeOffset.UtcNow - started, remindEvery);
                if (due > reminded)
                {
                    reminded = due;
                    poller.Enqueue(m => m.Beep());
                    ConsoleCaps.Bell();
                    AnsiConsole.MarkupLine($"[yellow]Still waiting ({Markup.Escape(Waited(DateTimeOffset.UtcNow - started))}): {Markup.Escape(heading)} {Markup.Escape(string.Join(" ", big))}[/]");
                }
                try { await Task.Delay(250, ct); } catch (OperationCanceledException) { }
            }
            recorder.Note(DateTimeOffset.UtcNow, $"answered:{choice}:{(DateTimeOffset.UtcNow - started).TotalSeconds.ToString("F0", Inv)}s");
            return choice;
        }

        Console.Write("\u001b[H\u001b[2J");
        await AnsiConsole.Live(Render()).AutoClear(false).StartAsync(async ctx =>
        {
            while (!ct.IsCancellationRequested && choice is null)
            {
                while (Console.KeyAvailable && choice is null)
                    choice = choose(char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar));
                if (choice is not null) break;

                var due = RemindersDue(DateTimeOffset.UtcNow - started, remindEvery);
                if (due > reminded)
                {
                    reminded = due;
                    poller.Enqueue(m => m.Beep());
                    ConsoleCaps.Bell();
                }
                ctx.UpdateTarget(Render());
                try { await Task.Delay(250, ct); } catch (OperationCanceledException) { }
            }
            ctx.UpdateTarget(Render());
        });
        recorder.Note(DateTimeOffset.UtcNow, $"answered:{choice}:{(DateTimeOffset.UtcNow - started).TotalSeconds.ToString("F0", Inv)}s");
        return choice;
    }

    static string Waited(TimeSpan t) => t.TotalMinutes >= 1
        ? $"{Math.Floor(t.TotalMinutes).ToString("F0", Inv)} min"
        : $"{Math.Floor(t.TotalSeconds).ToString("F0", Inv)} s";
}
