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
        .WithExample("run", "--meter", "u1253b", "--resource", "COM5")
        .WithExample("run", "--simulate", "--speed", "120");
    config.AddCommand<OvensCommand>("ovens")
        .WithDescription("List the ovens the app knows (built in, and described in files), or show one in full.")
        .WithExample("ovens", "--show", "ge-phs925");
    config.AddCommand<MetersCommand>("meters")
        .WithAlias("ports")
        .WithDescription("List the meters each kind of meter's driver can see, and which one answers.")
        .WithExample("meters", "--probe");
    config.AddCommand<AnalyzeCommand>("analyze")
        .WithDescription("Re-run the analysis over saved runs, alone or together.")
        .WithExample("analyze", "\"Documents\\OvenTracker\\2026-11-20 1400\"", "--tolerance", "10")
        .WithExample("analyze", "\"Documents\\OvenTracker\\2026-11-20 1400\"", "\"Documents\\OvenTracker\\2026-11-22 0900\"", "--favour", "350");
});

return await app.RunAsync(args, cts.Token);
