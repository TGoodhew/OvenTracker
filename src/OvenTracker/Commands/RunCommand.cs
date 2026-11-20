using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using OvenTracker.Analysis;
using OvenTracker.Meter;
using OvenTracker.Session;
using OvenTracker.Ui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

public sealed class RunSettings : CommandSettings
{
    [CommandOption("-p|--port <PORT>")]
    [Description("Serial port of the U1177A (e.g. COM5). Found automatically if left out.")]
    public string? Port { get; set; }

    [CommandOption("--simulate")]
    [Description("Use a simulated oven and meter instead of the real ones.")]
    public bool Simulate { get; set; }

    [CommandOption("--speed <X>")]
    [Description("With --simulate, how many times faster than real time to run.")]
    [DefaultValue(60.0)]
    public double Speed { get; set; } = 60;

    [CommandOption("-u|--unit <F|C>")]
    [Description("The unit your oven's dial or display uses.")]
    public string? Unit { get; set; }

    [CommandOption("-m|--modes <LIST>")]
    [Description("Modes to test: bake, convection, or bake,convection.")]
    public string? Modes { get; set; }

    [CommandOption("-t|--setpoints <LIST>")]
    [Description("Setpoints to test, in the oven's unit, e.g. 350,400,450.")]
    public string? Setpoints { get; set; }

    [CommandOption("--grouped")]
    [Description("Do every setpoint in bake, then every one in convection (the default alternates the modes at each setpoint, so the oven never has to cool down).")]
    public bool Grouped { get; set; }

    [CommandOption("--precision <DEG>")]
    [Description("How closely to pin down each mean (95 % interval half-width), in the oven's unit. Default 2 °F / 1 °C.")]
    public double? Precision { get; set; }

    [CommandOption("--tolerance <DEG>")]
    [Description("How far from the setting still counts as accurate, in the oven's unit. Default 15 °F / 8 °C.")]
    public double? Tolerance { get; set; }

    [CommandOption("--min-cycles <N>")]
    [Description("Fewest full thermostat cycles a result is based on.")]
    [DefaultValue(4)]
    public int MinCycles { get; set; } = 4;

    [CommandOption("--max-minutes <MIN>")]
    [Description("Longest to spend at one setpoint.")]
    [DefaultValue(120.0)]
    public double MaxMinutes { get; set; } = 120;

    [CommandOption("--interval <SEC>")]
    [Description("Seconds between readings.")]
    [DefaultValue(1.0)]
    public double Interval { get; set; } = 1;

    [CommandOption("-o|--out <DIR>")]
    [Description("Folder for the readings and report. Default: Documents\\OvenTracker\\<date time>.")]
    public string? Out { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Don't ask: start at once and begin each setpoint straight away (mainly for --simulate).")]
    public bool Yes { get; set; }

    public override ValidationResult Validate()
    {
        if (Unit is not null && Temp.ParseUnit(Unit) is null) return ValidationResult.Error("--unit must be F or C.");
        if (Interval is < 0.5 or > 30) return ValidationResult.Error("--interval must be between 0.5 and 30 seconds.");
        if (MinCycles is < 2 or > 20) return ValidationResult.Error("--min-cycles must be between 2 and 20.");
        if (MaxMinutes is < 10 or > 600) return ValidationResult.Error("--max-minutes must be between 10 and 600.");
        if (Precision is <= 0) return ValidationResult.Error("--precision must be positive.");
        if (Tolerance is <= 0) return ValidationResult.Error("--tolerance must be positive.");
        if (Modes is not null && RunCommand.ParseModes(Modes) is null) return ValidationResult.Error("--modes must be bake, convection, or both.");
        if (Setpoints is not null && RunCommand.ParseNumbers(Setpoints) is null) return ValidationResult.Error("--setpoints must be numbers, e.g. 350,400,450.");
        return ValidationResult.Success();
    }
}

public sealed class RunCommand : AsyncCommand<RunSettings>
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Version =>
        typeof(RunCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static IReadOnlyList<OvenMode>? ParseModes(string text)
    {
        var list = new List<OvenMode>();
        foreach (var p in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var s = p.Trim().ToLowerInvariant();
            if ("bake".StartsWith(s, StringComparison.Ordinal)) list.Add(OvenMode.Bake);
            else if ("convection".StartsWith(s, StringComparison.Ordinal) || s == "conv") list.Add(OvenMode.Convection);
            else if (s == "both") list.AddRange([OvenMode.Bake, OvenMode.Convection]);
            else return null;
        }
        return list.Count == 0 ? null : list.Distinct().ToList();
    }

    public static IReadOnlyList<double>? ParseNumbers(string text)
    {
        var list = new List<double>();
        foreach (var p in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(p, NumberStyles.Float, Inv, out var v) || v <= 0) return null;
            list.Add(v);
        }
        return list.Count == 0 ? null : list.Distinct().Order().ToList();
    }

    public static IReadOnlyList<TestPoint> BuildPlan(IReadOnlyList<OvenMode> modes, IReadOnlyList<double> setpointsC, bool grouped)
    {
        var order = grouped
            ? modes.SelectMany(m => setpointsC.Select(s => (m, s)))
            : setpointsC.SelectMany(s => modes.Select(m => (m, s)));
        return order.Select((p, i) => new TestPoint(i + 1, p.m, p.s)).ToList();
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RunSettings s, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        AnsiConsole.Write(new FigletText("OvenTracker").Color(Color.Orange1));
        AnsiConsole.MarkupLine($"[grey]v{Version} · oven accuracy with a Keysight U1253B and U1177A Bluetooth adapter[/]");
        AnsiConsole.WriteLine();

        // 1. The meter.
        ITemperatureMeter meter;
        SimulatedOven? oven = null;
        MeterIdentity identity;
        if (s.Simulate)
        {
            oven = new SimulatedOven(Environment.TickCount);
            meter = new SimulatedMeter(oven, s.Interval);
            identity = meter.Connect();
            AnsiConsole.MarkupLine($"[yellow]Simulation[/]: a made-up oven, running {s.Speed.ToString("0.#", Inv)}× real time.");
        }
        else
        {
            var settings = AppSettings.Load();
            ProbeResult? found = null;
            await AnsiConsole.Status().StartAsync("Looking for the meter…", async sc =>
            {
                found = await Task.Run(() => s.Port is not null
                    ? PortFinder.Probe(s.Port, keepOpen: true)
                    : PortFinder.Find(settings.LastPort, includeAll: false, port => sc.Status($"Trying {port}… (a Bluetooth port can take a few seconds)"), keepOpen: true), ct);
            });
            if (found?.Meter is null)
            {
                AnsiConsole.MarkupLine(s.Port is not null
                    ? $"[red]No meter answered on {Markup.Escape(s.Port)}[/]: {Markup.Escape(found?.Error ?? "")}"
                    : "[red]No U1253B answered on any Bluetooth serial port.[/]");
                AnsiConsole.MarkupLine("Check the meter is on, the U1177A is clipped on and switched on (its light blinks), and that it is paired in Windows Bluetooth settings. " +
                                       "[bold]oventracker ports[/] lists the serial ports; give one with [bold]--port COMn[/].");
                return 1;
            }
            settings.LastPort = found.Port;
            settings.Save();
            meter = found.Meter;
            identity = found.Identity!;
            AnsiConsole.MarkupLine($"[green]Connected[/] to {Markup.Escape(identity.ToString())} on {Markup.Escape(found.Port)}.");

            var config = meter.ReadConfig();
            if (!config.IsTemperature)
            {
                await AnsiConsole.Status().StartAsync($"The meter is on {Markup.Escape(config.Raw)}. Turn the dial to temperature (°C/°F) with the thermocouple plugged in…", async _ =>
                {
                    while (!config.IsTemperature && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(1500, ct);
                        config = await Task.Run(meter.ReadConfig, ct);
                    }
                });
            }
            AnsiConsole.MarkupLine($"Meter mode {Markup.Escape(config.Raw)}" + (meter.ReadBatteryPercent() is { } b ? $", battery {b.ToString("F0", Inv)} %." : "."));
            var first = meter.Read();
            if (first.Status == ReadingStatus.Overload)
                AnsiConsole.MarkupLine("[yellow]The meter reads OL: the thermocouple isn't connected (or is broken).[/]");
            else if (first.Celsius is { } c0)
                AnsiConsole.MarkupLine($"Thermocouple reads {c0.ToString("F1", Inv)} °C now.");
        }
        AnsiConsole.WriteLine();

        // 2. What to test.
        var unit = Temp.ParseUnit(s.Unit) ?? await new SelectionPrompt<TempUnit>()
            .Title("What unit does your [orange1]oven[/] use?")
            .AddChoices(TempUnit.Fahrenheit, TempUnit.Celsius)
            .UseConverter(u => u == TempUnit.Fahrenheit ? "°F" : "°C")
            .ShowAsync(AnsiConsole.Console, ct);
        var f = new UnitFormatter(unit);

        var modes = s.Modes is not null ? ParseModes(s.Modes)! : await new MultiSelectionPrompt<OvenMode>()
            .Title("Which modes do you want to test?")
            .AddChoices(OvenMode.Bake, OvenMode.Convection)
            .Select(OvenMode.Bake).Select(OvenMode.Convection)
            .UseConverter(Temp.ModeName)
            .ShowAsync(AnsiConsole.Console, ct);

        var defaultSetpoints = unit == TempUnit.Fahrenheit ? "350, 400, 450" : "175, 200, 230";
        var setpoints = s.Setpoints is not null ? ParseNumbers(s.Setpoints)! : ParseNumbers(await new TextPrompt<string>($"Setpoints to test, in {f.Symbol}?")
            .DefaultValue(defaultSetpoints)
            .Validate(t => ParseNumbers(t) is null ? ValidationResult.Error("Numbers separated by commas, please.") : ValidationResult.Success())
            .ShowAsync(AnsiConsole.Console, ct))!;

        var precisionC = s.Precision is { } p ? f.DeltaFromDisplay(p) : unit == TempUnit.Fahrenheit ? 2 / 1.8 : 1.0;
        var toleranceC = s.Tolerance is { } t ? f.DeltaFromDisplay(t) : unit == TempUnit.Fahrenheit ? 15 / 1.8 : 8.0;
        var options = new AnalysisOptions { PrecisionC = precisionC, MinCycles = s.MinCycles, MaxPointMinutes = s.MaxMinutes };
        var plan = BuildPlan(modes, setpoints.Select(f.FromDisplay).ToList(), s.Grouped);

        // 3. Explain, then go.
        AnsiConsole.Write(new Panel(new Markup(
                "[bold]Before you start[/]\n" +
                "• Put the thermocouple tip in the [bold]centre of the oven[/], on the middle shelf, clear of the racks and walls (clip the lead to a rack so the tip hangs free). Run the lead out under the door seal; the meter stays outside.\n" +
                "• Check the thermocouple's insulation is rated for your highest setpoint.\n" +
                "• Leave the door shut while a setpoint is being measured. If it gets opened, press [bold]R[/] twice to restart that setpoint.\n" +
                "• When the oven beeps or shows it is preheated, press [bold]P[/]: the report shows how true that signal was.\n" +
                (modes.Contains(OvenMode.Convection) ? "• If your oven lowers convection temperatures automatically (e.g. by 25 °F), turn that off, or enter the temperature it actually targets.\n" : "") +
                "\n[bold]How long it takes[/]\n" +
                $"The app finds when the oven has settled, then measures whole thermostat cycles until the mean is known to ±{Markup.Escape(f.Span(precisionC))} (at least {options.MinCycles} cycles). " +
                "It learns your oven's cycle as it goes and shows the time left. As a rough guide, the first setpoint from cold takes about an hour and each one after it 20–45 min.")).
            Border(BoxBorder.Rounded).BorderColor(Color.Grey35));

        var planTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35).AddColumn("#").AddColumn("Mode").AddColumn("Setting");
        foreach (var tp in plan) planTable.AddRow(tp.Index.ToString(Inv), Temp.ModeName(tp.Mode), Markup.Escape(f.Setpoint(tp.SetpointC)));
        AnsiConsole.Write(planTable);
        if (!s.Grouped && modes.Count > 1)
            AnsiConsole.MarkupLine("[grey]The modes alternate at each setpoint, so the oven only ever heats up between them and bake and convection are compared under the same conditions.[/]");

        if (!s.Yes && !await new ConfirmationPrompt("Ready to start?").ShowAsync(AnsiConsole.Console, ct)) return 0;

        var outDir = s.Out ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OvenTracker", DateTime.Now.ToString("yyyy-MM-dd HHmm", Inv));
        var info = new SessionInfo(Version, DateTimeOffset.Now, $"{identity.Manufacturer} {identity.Model}", meter.Location, unit, toleranceC, options, plan);
        using var recorder = new RunRecorder(outDir, info);
        using var poller = new MeterPoller(meter, TimeSpan.FromSeconds(s.Interval), s.Simulate ? s.Speed : 1, connected: true);
        poller.Start();

        var results = new List<PointResult>();
        var meterLabel = s.Simulate ? "simulated U1253B" : $"{identity.Model} on {meter.Location}";
        var tracker = new PointTracker(poller, recorder, f, options, toleranceC, meterLabel, plan.Count, s.Simulate);
        try
        {
            foreach (var point in plan)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule($"[orange1]{point.Index} of {plan.Count}: {Temp.ModeName(point.Mode)} {Markup.Escape(f.Setpoint(point.SetpointC))}[/]").LeftJustified());
                var choice = s.Yes ? "Start tracking" : await new SelectionPrompt<string>()
                    .Title($"Set the oven to [bold]{Temp.ModeName(point.Mode).ToUpperInvariant()} {Markup.Escape(f.Setpoint(point.SetpointC))}[/] and start it, then:")
                    .AddChoices("Start tracking", "Skip this setpoint", "Finish the session")
                    .ShowAsync(AnsiConsole.Console, ct);
                if (choice == "Finish the session") break;
                if (choice == "Skip this setpoint") continue;

                oven?.Set(point.Mode, point.SetpointC);
                DrainIdle(poller, recorder);
                var (result, outcome) = await tracker.RunAsync(point, ct);
                results.Add(result);
                poller.Enqueue(m => m.Beep());
                AnsiConsole.Write("\a");
                ConsoleReport.PointSummary(new Report(info, results), result);
                if (outcome == PointOutcome.Quit) break;
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Stopped.[/]");
        }
        finally
        {
            oven?.Set(OvenMode.Bake, null);
            poller.Stop();
            DrainIdle(poller, recorder);
            meter.Dispose();
        }

        var report = new Report(info, results);
        recorder.WriteResults(info, results, report.Markdown());
        AnsiConsole.WriteLine();
        if (results.Count > 0) ConsoleReport.Full(report);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Saved to [link]{Markup.Escape(outDir)}[/]: report.md, results.json and readings.csv.");
        AnsiConsole.MarkupLine("[grey]Turn the oven off.[/]");
        return 0;
    }

    static void DrainIdle(MeterPoller poller, RunRecorder recorder)
    {
        while (poller.Readings.TryRead(out var r)) recorder.Reading(r, null, null);
    }
}
