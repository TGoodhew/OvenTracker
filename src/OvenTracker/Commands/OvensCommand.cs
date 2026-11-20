using System.ComponentModel;
using System.Globalization;
using OvenTracker.Ovens;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

public sealed class OvensSettings : CommandSettings
{
    [CommandOption("--show <KEY>")]
    [Description("Show everything the app knows about one oven: how to read and change its adjustment, its convection conversion, measured gains and sources.")]
    public string? Show { get; set; }

    [CommandOption("-u|--unit <F|C>")]
    [Description("The unit to show temperatures in. Default °F.")]
    public string? Unit { get; set; }

    public override ValidationResult Validate()
    {
        if (Unit is not null && Temp.ParseUnit(Unit) is null) return ValidationResult.Error("--unit must be F or C.");
        if (Show is not null && OvenProfiles.Find(Show) is null) return ValidationResult.Error($"No oven \"{Show}\". Known: {OvenProfiles.Keys()}.");
        return ValidationResult.Success();
    }
}

/// <summary>Lists the ovens the app knows (built in, and described in files), or shows one in full.</summary>
public sealed class OvensCommand : Command<OvensSettings>
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public override int Execute(CommandContext context, OvensSettings s, CancellationToken cancellationToken)
    {
        var f = new UnitFormatter(Temp.ParseUnit(s.Unit) ?? TempUnit.Fahrenheit);
        if (s.Show is not null)
        {
            Show(OvenProfiles.Find(s.Show)!, f);
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("--oven").AddColumn("Oven").AddColumn("Adjustment").AddColumn("Cycle").AddColumn("Convection conversion").AddColumn("Measured gain");
        foreach (var p in OvenProfiles.All())
            table.AddRow(
                Markup.Escape(p.Key), Markup.Escape(p.Name),
                Markup.Escape($"±{f.Span(p.AdjustRangeC, 0)} in {f.Span(p.AdjustStepC, 0)} steps{(p.SharedAdjustment ? ", shared" : ", per mode")}"),
                p.TypicalCycleMinutes is { } c ? $"{c.ToString("0", Inv)} min" : "[grey]—[/]",
                p.ConvectionConversionC is { } cc ? Markup.Escape(f.D(-cc, 0)) : "[grey]—[/]",
                p.Gains is { Count: > 0 } g ? Markup.Escape(string.Join(", ", g.Select(kv => $"{Temp.ModeName(kv.Key)} {kv.Value.Gain.ToString("F2", Inv)}"))) : "[grey]assumed 1:1[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Your own ovens: a JSON file each in {Markup.Escape(OvenProfiles.Folder)} (docs/adding-an-oven.md). " +
                               "oventracker ovens --show <key> for the details.[/]");
        foreach (var bad in OvenProfiles.Unreadable())
            AnsiConsole.MarkupLine($"[yellow]Couldn't read {Markup.Escape(bad)} as an oven profile.[/]");
        return 0;
    }

    static void Show(OvenProfile p, UnitFormatter f)
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        void Row(string label, string value) => grid.AddRow($"[grey]{Markup.Escape(label)}[/]", value);
        string Steps(IReadOnlyList<string>? steps) => steps is { Count: > 0 } ? Markup.Escape(string.Join("\n", steps.Select((x, i) => $"{i + 1}. {x}"))) : "[grey]—[/]";

        Row("--oven", Markup.Escape(p.Key));
        Row("Adjustment", Markup.Escape($"±{f.Span(p.AdjustRangeC, 0)} in {f.Span(p.AdjustStepC, 0)} steps, {(p.SharedAdjustment ? "one for bake and convection" : "one for each mode")}"));
        Row("To read it", Steps(p.HowToRead));
        Row("To change it", Steps(p.HowToChange));
        Row("Convection conversion", p.ConvectionConversionC is { } cc ? Markup.Escape($"lowers convection settings by about {f.Span(cc, 0)}") : "[grey]none known[/]");
        if (p.HowToTurnOffConversion is { Count: > 0 }) Row("To turn it off", Steps(p.HowToTurnOffConversion));
        Row("Thermostat cycle", p.TypicalCycleMinutes is { } c ? $"about {c.ToString("0", Inv)} min, so up to {p.DefaultMaxMinutes.ToString("0", Inv)} min per setpoint" : "[grey]unknown: up to 120 min per setpoint[/]");
        Row("Measured gain", p.Gains is { Count: > 0 } g
            ? Markup.Escape(string.Join("\n", g.Select(kv => $"{Temp.ModeName(kv.Key)}: {kv.Value.Gain.ToString("F2", Inv)}{(kv.Value.HalfWidth is { } hw ? $" ±{hw.ToString("F2", Inv)}" : "")} ({kv.Value.MeasuredOn.ToLocalTime():yyyy-MM-dd})")))
            : "[grey]not measured: assumed 1:1 (oventracker characterise measures it)[/]");
        if (p.Notes is { Count: > 0 } notes) Row("Notes", Markup.Escape(string.Join("\n", notes)));
        if (p.Source is not null) Row("Source", Markup.Escape(p.Source));
        AnsiConsole.Write(new Panel(grid).Header($"[bold]{Markup.Escape(p.Name)}[/]").Border(BoxBorder.Rounded));
    }
}
