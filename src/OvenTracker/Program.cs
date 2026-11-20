using System.Text;
using OvenTracker.Commands;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The first Ctrl+C stops the session cleanly and still writes the report; a second one ends the app.
    if (cts.IsCancellationRequested) return;
    e.Cancel = true;
    cts.Cancel();
};

var app = new CommandApp<RunCommand>();
app.Configure(config =>
{
    config.SetApplicationName("oventracker");
    config.SetApplicationVersion(RunCommand.Version);
    config.AddCommand<RunCommand>("run")
        .WithDescription("Measure the oven at each setpoint, in bake and convection (the default).")
        .WithExample("run")
        .WithExample("run", "--unit", "F", "--modes", "bake,convection", "--setpoints", "350,400,450")
        .WithExample("run", "--simulate", "--speed", "120");
    config.AddCommand<PortsCommand>("ports")
        .WithDescription("List serial ports, and find which one is the U1177A.")
        .WithExample("ports", "--probe");
    config.AddCommand<AnalyzeCommand>("analyze")
        .WithDescription("Re-run the analysis over a saved run.")
        .WithExample("analyze", "\"Documents\\OvenTracker\\2026-11-20 1400\"", "--tolerance", "10");
});

return await app.RunAsync(args, cts.Token);
