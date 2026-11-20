using System.ComponentModel;
using OvenTracker.Meter;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

public sealed class MetersSettings : CommandSettings
{
    [CommandOption("--probe")]
    [Description("Ask each place a meter is likely to be (for the U1253B, each outgoing Bluetooth port) what it is.")]
    public bool Probe { get; set; }

    [CommandOption("--all")]
    [Description("With --probe, ask every place, not just the likely ones (for the U1253B, every serial port).")]
    public bool All { get; set; }

    [CommandOption("--meter <DRIVER>")]
    [Description("Only look for this kind of meter.")]
    public string? Meter { get; set; }

    public override ValidationResult Validate() =>
        Meter is not null && MeterDrivers.Find(Meter) is null
            ? ValidationResult.Error($"--meter must be one of: {MeterDrivers.Names}.")
            : ValidationResult.Success();
}

/// <summary>Lists the meters each driver can see, and optionally asks each one what it is.</summary>
public sealed class MetersCommand : Command<MetersSettings>
{
    public override int Execute(CommandContext context, MetersSettings s, CancellationToken cancellationToken)
    {
        var drivers = s.Meter is not null ? [MeterDrivers.Find(s.Meter)!] : MeterDrivers.All;
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Meter").AddColumn("Where").AddColumn("Connection").AddColumn("Device").AddColumn("Note");
        if (s.Probe) table.AddColumn("Answers");
        var nothing = new List<IMeterDriver>();

        foreach (var driver in drivers)
        {
            var candidates = driver.Discover();
            if (candidates.Count == 0)
            {
                nothing.Add(driver);
                continue;
            }
            foreach (var c in candidates)
            {
                var row = new List<string>
                {
                    Markup.Escape(driver.Name), Markup.Escape(c.Resource), Markup.Escape(c.Transport), Markup.Escape(c.Description),
                    c.Note is null ? "" : c.Usable ? $"[green]{Markup.Escape(c.Note)}[/]" : $"[grey]{Markup.Escape(c.Note)}[/]",
                };
                if (s.Probe)
                {
                    if (c.Usable || s.All)
                    {
                        ProbeResult r = null!;
                        AnsiConsole.Status().Start($"Asking {Markup.Escape(c.Resource)}…", _ => r = driver.Probe(c.Resource));
                        row.Add(r.Identity is { } id
                            ? r.Error is null ? $"[green]{Markup.Escape(id.ToString())}[/]" : $"[yellow]{Markup.Escape(id.ToString())}: {Markup.Escape(r.Error)}[/]"
                            : $"[grey]{Markup.Escape(r.Error ?? "")}[/]");
                    }
                    else row.Add("[grey]not asked (--all asks it)[/]");
                }
                table.AddRow(row.ToArray());
            }
        }

        if (table.Rows.Count > 0) AnsiConsole.Write(table);
        foreach (var d in nothing)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(d.Name)}[/]: nothing found ({Markup.Escape(d.Description)}). {Markup.Escape(d.NotFoundHelp)}");
        AnsiConsole.MarkupLine("[grey]Use one with [bold]oventracker run --meter <meter> --resource <where>[/].[/]");
        return nothing.Count == drivers.Count ? 1 : 0;
    }
}
