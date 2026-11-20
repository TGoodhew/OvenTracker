using System.ComponentModel;
using OvenTracker.Meter;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

public sealed class PortsSettings : CommandSettings
{
    [CommandOption("--probe")]
    [Description("Ask each outgoing Bluetooth port for *IDN? to see which one is the meter.")]
    public bool Probe { get; set; }

    [CommandOption("--all")]
    [Description("With --probe, ask every serial port, not just Bluetooth ones.")]
    public bool All { get; set; }
}

public sealed class PortsCommand : Command<PortsSettings>
{
    public override int Execute(CommandContext context, PortsSettings s, CancellationToken cancellationToken)
    {
        var ports = PortFinder.List();
        if (ports.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No serial ports.[/] Pair the U1177A in Windows Bluetooth settings: it adds a \"Standard Serial over Bluetooth link\" port.");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Port").AddColumn("Device").AddColumn("Bluetooth");
        if (s.Probe) table.AddColumn("Answers *IDN?");
        foreach (var p in ports)
        {
            var bt = p.IsBluetoothOutgoing ? $"[green]outgoing[/] {p.BluetoothAddress}" : p.IsBluetooth ? "[grey]incoming (not usable)[/]" : "";
            var row = new List<string> { p.Port, Markup.Escape(p.Description), bt };
            if (s.Probe)
            {
                if (p.IsBluetoothOutgoing || s.All)
                {
                    ProbeResult r = null!;
                    AnsiConsole.Status().Start($"Asking {p.Port}…", _ => r = PortFinder.Probe(p.Port));
                    row.Add(r.Identity is { } id ? $"[green]{Markup.Escape(id.ToString())}[/]" : $"[grey]{Markup.Escape(r.Error ?? "")}[/]");
                }
                else row.Add("[grey]not asked[/]");
            }
            table.AddRow(row.ToArray());
        }
        AnsiConsole.Write(table);
        return 0;
    }
}
