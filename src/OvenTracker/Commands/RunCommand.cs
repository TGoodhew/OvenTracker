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

public sealed class RunSettings : AdjustmentSettings
{
    [CommandOption("--meter <DRIVER>")]
    [Description("Which kind of meter: u1253b (the default) or simulated. [bold]oventracker meters[/] lists them and what each can see.")]
    public string? Meter { get; set; }

    [CommandOption("-r|--resource <WHERE>")]
    [Description("Where the meter is, in the driver's terms (e.g. COM5 for the U1253B). Found automatically if left out.")]
    public string? Resource { get; set; }

    [CommandOption("-p|--port <PORT>", IsHidden = true)]
    [Description("The same as --resource: the name it had when there was only a serial meter.")]
    public string? Port { get; set; }

    [CommandOption("--simulate")]
    [Description("Use a simulated oven and meter instead of the real ones: the same as --meter simulated.")]
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
    [Description("Folder for the readings and report. Default: OvenTracker/<date time> in your Documents folder (on Linux, ~/Documents, or ~ if there is none).")]
    public string? Out { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Don't ask: start at once and begin each setpoint straight away (mainly for --simulate).")]
    public bool Yes { get; set; }

    public override ValidationResult Validate()
    {
        if (base.Validate() is { Successful: false } adjustment) return adjustment;
        if (Meter is not null && MeterDrivers.Find(Meter) is null) return ValidationResult.Error($"--meter must be one of: {MeterDrivers.Names}.");
        if (Simulate && Meter is not null && MeterDrivers.Find(Meter)?.IsSimulation != true) return ValidationResult.Error("--simulate can't be used with another --meter.");
        if (Port is not null && Resource is not null && !string.Equals(Port, Resource, StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Error("Give the meter's location once, with --resource.");
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
        var appSettings = AppSettings.Load();
        var driver = ChooseDriver(s, appSettings);
        var resource = s.Resource ?? s.Port;
        AnsiConsole.Write(new FigletText("OvenTracker").Color(Color.Orange1));
        AnsiConsole.MarkupLine($"[grey]v{Version} · oven temperature accuracy · {Markup.Escape(driver.Description)}[/]");
        AnsiConsole.WriteLine();

        // 1. The meter.
        ProbeResult? found = null;
        await AnsiConsole.Status().StartAsync("Looking for the meter…", async sc =>
        {
            found = await Task.Run(() => driver.Open(new MeterRequest(resource, appSettings.LastResourceFor(driver.Name), s.Interval,
                where => sc.Status($"Trying {Markup.Escape(where)}…"))), ct);
        });
        if (found?.Meter is not { } meter || found.Identity is not { } identity)
        {
            AnsiConsole.MarkupLine(resource is not null
                ? $"[red]No meter answered on {Markup.Escape(resource)}[/]: {Markup.Escape(found?.Error ?? "")}"
                : $"[red]No meter answered[/] (looking for a {Markup.Escape(driver.Description)}).");
            if (driver.NotFoundHelp.Length > 0) AnsiConsole.MarkupLine(Markup.Escape(driver.NotFoundHelp));
            AnsiConsole.MarkupLine($"[bold]oventracker meters[/] lists what each kind of meter can see. Give the meter's location with [bold]--resource[/] " +
                                   $"({Markup.Escape(driver.ResourceHelp)}), or another kind of meter with [bold]--meter[/] ({Markup.Escape(MeterDrivers.Names)}).");
            return 1;
        }

        MeterConfig? config;
        if (driver.IsSimulation)
        {
            config = meter.ReadConfig();
            AnsiConsole.MarkupLine($"[yellow]Simulation[/]: a made-up oven, running {s.Speed.ToString("0.#", Inv)}× real time.");
        }
        else
        {
            appSettings.Remember(driver.Name, found.Resource);
            appSettings.Save();
            AnsiConsole.MarkupLine($"[green]Connected[/] to {Markup.Escape(identity.ToString())} on {Markup.Escape(found.Resource)}.");

            config = meter.ReadConfig();
            if (config is { IsTemperature: false })
            {
                await AnsiConsole.Status().StartAsync($"The meter is on {Markup.Escape(config.Description)}. {Markup.Escape(driver.NotTemperatureHelp)}", async _ =>
                {
                    while (config is { IsTemperature: false } && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(1500, ct);
                        config = await Task.Run(meter.ReadConfig, ct);
                    }
                });
            }
            var about = new List<string>();
            if (config is not null) about.Add($"Meter set to {Markup.Escape(config.Description)}");
            if (meter.ReadBatteryPercent() is { } b) about.Add($"battery {b.ToString("F0", Inv)} %");
            if (about.Count > 0) AnsiConsole.MarkupLine(string.Join(", ", about) + ".");
            var first = meter.Read();
            if (first.Status == ReadingStatus.Overload)
                AnsiConsole.MarkupLine("[yellow]The meter reads OL: the probe isn't connected (or is broken).[/]");
            else if (first.Celsius is { } c0)
                AnsiConsole.MarkupLine($"The probe reads {c0.ToString("F1", Inv)} °C now.");
        }
        var oven = meter as IOvenSimulation;
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

        // The oven's thermostat adjustment now, so the report can say what to set it to.
        var adjustmentC = s.AdjustmentC(unit);
        if (adjustmentC is null && !driver.IsSimulation && !s.Yes)
        {
            var answer = await new TextPrompt<string>($"What is the oven's thermostat adjustment set to now, in {f.Symbol}? [grey](0 if it has never been changed; Enter if you don't know)[/]")
                .AllowEmpty()
                .Validate(t => t.Trim().Length == 0 || double.TryParse(t, System.Globalization.NumberStyles.Float, Inv, out _)
                    ? ValidationResult.Success() : ValidationResult.Error("A number such as -10, or Enter."))
                .ShowAsync(AnsiConsole.Console, ct);
            if (double.TryParse(answer, System.Globalization.NumberStyles.Float, Inv, out var given)) adjustmentC = f.DeltaFromDisplay(given);
        }

        if (adjustmentC is { } simAdjust) oven?.SetAdjustment(simAdjust); // the simulated oven obeys --adjustment too

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

        var outDir = s.Out ?? Path.Combine(RunsFolder(), DateTime.Now.ToString("yyyy-MM-dd HHmm", Inv));
        var info = new SessionInfo(Version, DateTimeOffset.Now, $"{identity.Manufacturer} {identity.Model}", meter.Location, unit, toleranceC, options, plan,
            driver.Name, identity, config?.Sensor, adjustmentC, s.BuildOptions(unit));
        using var recorder = new RunRecorder(outDir, info);
        using var poller = new MeterPoller(meter, TimeSpan.FromSeconds(s.Interval), driver.IsSimulation ? s.Speed : 1, connected: true);
        poller.Start();

        var results = new List<PointResult>();
        var meterLabel = driver.IsSimulation ? "simulated meter" : $"{identity.Model} on {meter.Location}";
        var tracker = new PointTracker(poller, recorder, f, options, toleranceC, meterLabel, driver.NotTemperatureHelp, plan.Count, driver.IsSimulation);
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

                oven?.SetOven(point.Mode, point.SetpointC);
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
            oven?.SetOven(OvenMode.Bake, null);
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

    /// <summary>
    /// Where runs are saved by default: Documents\OvenTracker on Windows; on Linux and macOS,
    /// ~/Documents/OvenTracker, or ~/OvenTracker where there is no Documents folder.
    /// </summary>
    public static string RunsFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var standard = Path.Combine(home, "Documents");
            docs = Directory.Exists(standard) ? standard : string.IsNullOrEmpty(docs) ? home : docs;
        }
        return Path.Combine(docs, "OvenTracker");
    }

    /// <summary>The driver to use: the one asked for, or the one that answered last time, or the default.</summary>
    public static IMeterDriver ChooseDriver(RunSettings s, AppSettings settings)
    {
        if (s.Simulate) return MeterDrivers.Find(SimulatedDriver.DriverName)!;
        if (MeterDrivers.Find(s.Meter) is { } asked) return asked;
        if (MeterDrivers.Find(settings.LastMeter) is { IsSimulation: false } last) return last;
        return MeterDrivers.Find(MeterDrivers.DefaultName)!;
    }

    static void DrainIdle(MeterPoller poller, RunRecorder recorder)
    {
        while (poller.Readings.TryRead(out var r)) recorder.Reading(r, null, null);
    }
}
