using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using OvenTracker.Analysis;
using OvenTracker.Meter;
using OvenTracker.Ovens;
using OvenTracker.Session;
using OvenTracker.Ui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

public class RunSettings : SharedSettings
{
    [CommandOption("--meter <DRIVER>")]
    [Description("Which kind of meter: u1253b (the default), 34401a (with a type K thermocouple), 121gw (EEVblog, Bluetooth LE), ut60bt (UNI-T, Bluetooth LE), or simulated. [bold]oventracker meters[/] lists them and what each can see.")]
    public string? Meter { get; set; }

    [CommandOption("-r|--resource <WHERE>")]
    [Description("Where the meter is, in the driver's terms: COM5 for the U1253B (found automatically if left out), GPIB0::22::INSTR or COM3 for the 34401A, ble:<address> for the 121GW or UT60BT. Remembered.")]
    public string? Resource { get; set; }

    [CommandOption("--meter-option <KEY=VALUE>")]
    [Description("A setting for the meter's driver, e.g. cj=22C (the cold junction's temperature) or nplc=100 for the 34401A. Repeat it for more than one. Remembered for that meter.")]
    public string[]? MeterOptions { get; set; }

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
    [Description("Longest to spend at one setpoint. Default 120, or longer for an oven known to cycle slowly (an hour plus six cycles: 180 for the GE PHS925).")]
    public double? MaxMinutes { get; set; }

    [CommandOption("--interval <SEC>")]
    [Description("Seconds between readings.")]
    [DefaultValue(1.0)]
    public double Interval { get; set; } = 1;

    [CommandOption("-o|--out <DIR>")]
    [Description("Folder for the readings and report. Default: OvenTracker/<date time> in your Documents folder (on Linux, ~/Documents, or ~ if there is none).")]
    public string? Out { get; set; }

    [CommandOption("--resume <FOLDER>")]
    [Description("Carry on a run saved earlier: measure the setpoints it didn't finish, adding to the same folder and report. If the oven is still hot, the app waits for it to cool first.")]
    public string? Resume { get; set; }

    [CommandOption("--ambient <TEMP>")]
    [Description("Room temperature, in the oven's unit. Normally the probe's first reading with the oven cold; give it if the oven is hot when you start.")]
    public double? Ambient { get; set; }

    [CommandOption("--sim-hot <TEMP>", IsHidden = true)]
    [Description("With --simulate, start as if the oven had just been used at this temperature, in the oven's unit.")]
    public double? SimHot { get; set; }

    [CommandOption("--remind <MINUTES>")]
    [Description("While waiting for the oven to be changed (or turned off), beep again every this many minutes. 0 for no repeats.")]
    [DefaultValue(1.0)]
    public double Remind { get; set; } = 1;

    [CommandOption("--sim-forget <N>", IsHidden = true)]
    [Description("With --simulate, leave the simulated oven at its old setting for setpoint N, as if you'd forgotten to change it.")]
    public int? SimForget { get; set; }

    [CommandOption("--sim-apo <MINUTES>", IsHidden = true)]
    [Description("With --simulate, give the simulated meter an auto power-off after this many minutes (it wakes 2 minutes later).")]
    public double? SimApo { get; set; }

    [CommandOption("--sim-battery <MINUTES>", IsHidden = true)]
    [Description("With --simulate, make the simulated meter's battery run down over this many minutes, its readings going wrong near the end; the app pauses for a swap.")]
    public double? SimBattery { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Don't ask: start at once and begin each setpoint straight away (mainly for --simulate).")]
    public bool Yes { get; set; }

    public override ValidationResult Validate()
    {
        if (base.Validate() is { Successful: false } adjustment) return adjustment;
        if (Meter is not null && MeterDrivers.Find(Meter) is null) return ValidationResult.Error($"--meter must be one of: {MeterDrivers.Names}.");
        if (Simulate && Meter is not null && MeterDrivers.Find(Meter)?.IsSimulation != true) return ValidationResult.Error("--simulate can't be used with another --meter.");
        if ((Resource ?? Port) is { } where && ResourceSpec.Parse(where) is null)
            return ValidationResult.Error($"--resource \"{where}\" isn't one the app understands. Examples: COM5, COM5:9600,E,7,2 (with serial settings), /dev/ttyUSB0, GPIB0::22::INSTR, ble:C4:BE:84:12:34:56.");
        if (MeterDrivers.ParseOptions(MeterOptions) is null) return ValidationResult.Error("--meter-option takes key=value, e.g. --meter-option cj=22C.");
        if (Port is not null && Resource is not null && !string.Equals(Port, Resource, StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Error("Give the meter's location once, with --resource.");
        if (Unit is not null && Temp.ParseUnit(Unit) is null) return ValidationResult.Error("--unit must be F or C.");
        if (Interval is < 0.5 or > 30) return ValidationResult.Error("--interval must be between 0.5 and 30 seconds.");
        if (MinCycles is < 2 or > 20) return ValidationResult.Error("--min-cycles must be between 2 and 20.");
        if (MaxMinutes is < 10 or > 600) return ValidationResult.Error("--max-minutes must be between 10 and 600.");
        if (Remind is < 0 or > 60) return ValidationResult.Error("--remind must be between 0 and 60 minutes.");
        if (Precision is <= 0) return ValidationResult.Error("--precision must be positive.");
        if (Tolerance is <= 0) return ValidationResult.Error("--tolerance must be positive.");
        if (Modes is not null && RunCommand.ParseModes(Modes) is null) return ValidationResult.Error("--modes must be bake, convection, or both.");
        if (Resume is not null && (!Directory.Exists(Resume) || !File.Exists(Path.Combine(Resume, "session.json")) || !File.Exists(Path.Combine(Resume, "readings.csv"))))
            return ValidationResult.Error($"--resume needs a run's folder, with session.json and readings.csv: {Resume}");
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
        // The oven: the one asked for, the saved run's, the one used last time, or the generic one.
        var resumedRun = s.Resume is { } r0 ? AnalyzeCommand.Load(r0) : null;
        var resumedOven = resumedRun?.Info.Oven;
        var ovenProfile = OvenProfiles.Find(s.Oven) ?? resumedOven ?? OvenProfiles.Find(appSettings.LastOven) ?? OvenProfiles.Default;
        var driver = ChooseDriver(s, appSettings);
        var resource = s.Resource ?? s.Port;
        // The driver's own settings: those given; when resuming, the run's own (a cold junction, say, shouldn't change
        // between sittings unless asked); otherwise the ones used with this meter last time.
        var givenOptions = MeterDrivers.ParseOptions(s.MeterOptions)!;
        var meterOptions = givenOptions.Count > 0 ? givenOptions
            : resumedRun?.Info.MeterOptions is { Count: > 0 } runOptions ? runOptions
            : appSettings.MeterOptionsFor(driver.Name);
        string? forgotten = null;
        if (driver.CheckOptions(meterOptions) is { } badOption)
        {
            if (givenOptions.Count > 0)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(badOption)}[/]");
                return 1;
            }
            // Remembered from an earlier version of the driver (the 34401A's rtd=, say): not worth stopping a run over.
            forgotten = $"The settings remembered for this meter no longer apply, so they're forgotten: {badOption}";
            meterOptions = new Dictionary<string, string>();
        }
        AnsiConsole.Write(new FigletText("OvenTracker").Color(Color.Orange1));
        AnsiConsole.MarkupLine($"[grey]v{Version} · oven temperature accuracy · {Markup.Escape(driver.Description)}[/]");
        AnsiConsole.WriteLine();
        if (forgotten is not null) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(forgotten)}[/]");

        // 1. The meter.
        ProbeResult? found = null;
        await AnsiConsole.Status().StartAsync("Looking for the meter…", async sc =>
        {
            found = await Task.Run(() => driver.Open(new MeterRequest(resource, appSettings.LastResourceFor(driver.Name), s.Interval,
                where => sc.Status($"Trying {Markup.Escape(where)}…"), meterOptions)), ct);
        });
        if (found?.Meter is not { } meter || found.Identity is not { } identity)
        {
            AnsiConsole.MarkupLine(resource is not null
                ? $"[red]No meter answered on {Markup.Escape(resource)}[/]: {Markup.Escape(found?.Error ?? "")}"
                : $"[red]No meter answered[/] (looking for a {Markup.Escape(driver.Description)}).");
            // Not when the error just shown already says it (a driver may put its help in the error).
            if (driver.NotFoundHelp.Length > 0 && !(found?.Error ?? "").Contains(driver.NotFoundHelp, StringComparison.Ordinal))
                AnsiConsole.MarkupLine(Markup.Escape(driver.NotFoundHelp));
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
            appSettings.RememberMeterOptions(driver.Name, meterOptions);
            appSettings.Save();
            AnsiConsole.MarkupLine($"[green]Connected[/] to {Markup.Escape(identity.ToString())} on {Markup.Escape(found.Resource)}.");
            foreach (var note in found.Notes ?? []) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(note)}[/]");
            if (driver.HasBattery && BatteryNote(meter.ReadBatteryPercent()) is { } batteryNote) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(batteryNote)}[/]");

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
        }
        var oven = meter as IOvenSimulation;
        if (s.SimApo is { } simApo) oven?.SetAutoPowerOff(TimeSpan.FromMinutes(simApo));
        if (s.SimBattery is { } simBattery) oven?.SetBattery(TimeSpan.FromMinutes(simBattery));
        // Prompts need a terminal; without one (output to a file or pipe), the run goes on as with --yes.
        var interactive = ConsoleCaps.Live;
        if (!interactive && MissingWithoutTerminal(s) is { } missing)
        {
            AnsiConsole.MarkupLine($"[red]The output isn't a terminal, so the app can't ask: give {Markup.Escape(missing)}.[/]");
            meter.Dispose();
            return 1;
        }
        if (!await CheckAutoPowerOffAsync(driver, meter, identity, appSettings, s.Yes || !interactive, ct)) { meter.Dispose(); return 0; }
        var first = meter.Read();
        if (first.Status == ReadingStatus.Overload)
            AnsiConsole.MarkupLine("[yellow]The meter reads OL: the probe isn't connected (or is broken).[/]");
        else if (first.Celsius is { } c0 && !driver.IsSimulation)
            AnsiConsole.MarkupLine($"The probe reads {Markup.Escape(new UnitFormatter(Temp.ParseUnit(s.Unit) ?? TempUnit.Celsius).T(c0))} now.");
        AnsiConsole.WriteLine();

        // 2. What to test: asked, or the saved run's when resuming.
        var resumed = s.Resume is { } resumeFolder ? AnalyzeCommand.Load(resumeFolder) : null;
        TempUnit unit;
        IReadOnlyList<OvenMode> modes;
        IReadOnlyList<TestPoint> plan;
        AnalysisOptions options;
        double toleranceC;
        if (resumed is not null)
        {
            unit = resumed.Info.Unit;
            plan = resumed.Info.Plan;
            modes = plan.Select(p => p.Mode).Distinct().ToList();
            options = resumed.Info.Options;
            toleranceC = resumed.Info.ToleranceC;
        }
        else
        {
            unit = Temp.ParseUnit(s.Unit) ?? await new SelectionPrompt<TempUnit>()
                .Title("What unit does your [orange1]oven[/] use?")
                .AddChoices(TempUnit.Fahrenheit, TempUnit.Celsius)
                .UseConverter(u => u == TempUnit.Fahrenheit ? "°F" : "°C")
                .ShowAsync(AnsiConsole.Console, ct);
            var fu = new UnitFormatter(unit);

            modes = s.Modes is not null ? ParseModes(s.Modes)! : await new MultiSelectionPrompt<OvenMode>()
                .Title("Which modes do you want to test?")
                .AddChoices(OvenMode.Bake, OvenMode.Convection)
                .Select(OvenMode.Bake).Select(OvenMode.Convection)
                .UseConverter(Temp.ModeName)
                .ShowAsync(AnsiConsole.Console, ct);

            var defaultSetpoints = unit == TempUnit.Fahrenheit ? "350, 400, 450" : "175, 200, 230";
            var setpoints = s.Setpoints is not null ? ParseNumbers(s.Setpoints)! : ParseNumbers(await new TextPrompt<string>($"Setpoints to test, in {fu.Symbol}?")
                .DefaultValue(defaultSetpoints)
                .Validate(t => ParseNumbers(t) is null ? ValidationResult.Error("Numbers separated by commas, please.") : ValidationResult.Success())
                .ShowAsync(AnsiConsole.Console, ct))!;

            var precisionC = s.Precision is { } p ? fu.DeltaFromDisplay(p) : unit == TempUnit.Fahrenheit ? 2 / 1.8 : 1.0;
            toleranceC = s.Tolerance is { } t ? fu.DeltaFromDisplay(t) : unit == TempUnit.Fahrenheit ? 15 / 1.8 : 8.0;
            options = new AnalysisOptions { PrecisionC = precisionC, MinCycles = s.MinCycles, MaxPointMinutes = s.MaxMinutes ?? ovenProfile.DefaultMaxMinutes };
            plan = BuildPlan(modes, setpoints.Select(fu.FromDisplay).ToList(), s.Grouped);
        }
        var f = new UnitFormatter(unit);

        // The oven's thermostat adjustment now, so the report can say what to set it to.
        var adjustmentC = s.AdjustmentC(unit) ?? resumed?.Info.AdjustmentC;
        if (adjustmentC is null && !driver.IsSimulation && !s.Yes && interactive)
        {
            if (!ovenProfile.IsGeneric && ovenProfile.HowToRead is { Count: > 0 } read)
                AnsiConsole.MarkupLine($"[grey]To see it on the {Markup.Escape(ovenProfile.Name)}: {Markup.Escape(string.Join(" ", read.Select((step, i) => $"{i + 1}. {step}")))}[/]");
            var answer = await new TextPrompt<string>($"What is the oven's thermostat adjustment set to now, in {f.Symbol}? [grey](0 if it has never been changed; Enter if you don't know)[/]")
                .AllowEmpty()
                .Validate(t => t.Trim().Length == 0 || double.TryParse(t, System.Globalization.NumberStyles.Float, Inv, out _)
                    ? ValidationResult.Success() : ValidationResult.Error("A number such as -10, or Enter."))
                .ShowAsync(AnsiConsole.Console, ct);
            if (double.TryParse(answer, System.Globalization.NumberStyles.Float, Inv, out var given)) adjustmentC = f.DeltaFromDisplay(given);
            AnsiConsole.MarkupLine(adjustmentC is { } a
                ? $"Thermostat adjustment: [bold]{Markup.Escape(new Report(new SessionInfo("", DateTimeOffset.Now, "", "", unit, 0, new AnalysisOptions(), []), []).Adjustment(a))}[/]."
                : "Thermostat adjustment: [bold]not known[/] (the report will say how much to change it by, rather than what to set it to).");
        }
        if (adjustmentC is { } simAdjust) oven?.SetAdjustment(simAdjust); // the simulated oven obeys --adjustment too
        if (s.SimHot is { } simHot && oven is not null)
        {
            oven.PretendUsed(f.FromDisplay(simHot));
            first = meter.Read(); // the simulated oven is warm now, as a real one would be when the app starts
        }

        // Room temperature, for knowing when the oven is cold again: given, saved, or the first reading if the oven was cold.
        var ambientC = s.Ambient is { } amb ? f.FromDisplay(amb) : resumed?.Info.AmbientC ?? (first.Celsius is < 45 and > -10 ? first.Celsius : null);

        // Characterise: phase 1's points are measured at the current adjustment (0 when it isn't known: the gain needs only the change).
        var characterising = s is CharacteriseSettings || resumed?.Info.Characterise is not null;
        if (resumed is null && s is CharacteriseSettings) plan = [.. plan.Select(p => p with { AdjustmentC = adjustmentC ?? 0 })];

        // When resuming, everything the earlier sittings measured, with the plan's adjustment for each. The setpoints
        // that finished (by themselves or by choice) are done; the others (interrupted or skipped) are measured again,
        // and until they are, their earlier result stays in the results, as analyze shows it.
        var earlier = resumed is null ? [] : Replay.Analyze(resumed.Csv, options, plan)
            .Select(r => r with { Point = plan.FirstOrDefault(p => p.Index == r.Point.Index) ?? r.Point })
            .ToList();
        var done = earlier
            .Where(r => r.Metrics.Reason is CompletionReason.Converged or CompletionReason.TimedOut or CompletionReason.Manual)
            .ToList();
        var todo = plan.Where(p => done.All(d => d.Point.Index != p.Index)).ToList();
        if (resumed is not null && todo.Count == 0 && resumed.Info.Characterise is not { ChangedToC: null })
        {
            AnsiConsole.MarkupLine($"[green]Every setpoint in {Markup.Escape(resumed.Folder)} is already done.[/] [bold]oventracker analyze[/] re-reads it.");
            meter.Dispose();
            return 0;
        }

        // 3. Explain, then go.
        AnsiConsole.Write(new Panel(new Markup(
                "[bold]Before you start[/]\n" +
                "• Put the thermocouple tip in the [bold]centre of the oven[/], on the middle shelf, clear of the racks and walls (clip the lead to a rack so the tip hangs free). Run the lead out under the door seal; the meter stays outside.\n" +
                "• Check the thermocouple's insulation is rated for your highest setpoint.\n" +
                "• Leave the door shut while a setpoint is being measured. If it gets opened, press [bold]R[/] twice to restart that setpoint.\n" +
                "• When the oven beeps or shows it is preheated, press [bold]P[/]: the report shows how true that signal was.\n" +
                (modes.Contains(OvenMode.Convection) ? ConvectionNote(ovenProfile, f) : "") +
                "\n[bold]How long it takes[/]\n" +
                $"The app finds when the oven has settled, then measures whole thermostat cycles until the mean is known to ±{Markup.Escape(f.Span(options.PrecisionC))} (at least {options.MinCycles} cycles). " +
                "It learns your oven's cycle as it goes and shows the time left. As a rough guide, the first setpoint from cold takes about an hour and each one after it 20–45 min.")).
            Border(BoxBorder.Rounded).BorderColor(Color.Grey35));

        if (resumed is not null)
            AnsiConsole.MarkupLine($"[bold]Carrying on[/] {Markup.Escape(resumed.Folder)}: {plan.Count - todo.Count} of {plan.Count} setpoints done.");
        var planTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35).AddColumn("#").AddColumn("Mode").AddColumn("Setting");
        if (resumed is not null) planTable.AddColumn("");
        foreach (var tp in plan)
        {
            var row = new List<string> { tp.Index.ToString(Inv), Temp.ModeName(tp.Mode), Markup.Escape(f.Setpoint(tp.SetpointC)) };
            if (resumed is not null) row.Add(todo.Contains(tp) ? "to do" : "[green]done[/]");
            planTable.AddRow(row.ToArray());
        }
        AnsiConsole.Write(planTable);
        if (!s.Grouped && modes.Count > 1)
            AnsiConsole.MarkupLine("[grey]The modes alternate at each setpoint, so the oven only ever heats up between them and bake and convection are compared under the same conditions.[/]");

        // The probe: the one given, or what the meter (or the saved run) says. Warn about setpoints near its rating.
        var sensor = s.ResolveSensor(resumed?.Info.Sensor ?? config?.Sensor, unit);
        if (ProbeRangeWarning(sensor, todo.Select(p => p.SetpointC), f) is { } rangeWarning)
            AnsiConsole.Write(new Panel(new Markup(rangeWarning.Text)).Header(rangeWarning.Over ? "[red]Probe rating[/]" : "[yellow]Probe rating[/]")
                .Border(BoxBorder.Rounded).BorderColor(rangeWarning.Over ? Color.Red : Color.Yellow));

        // A warm oven at the start of a new session: the first setpoint is meant to start from cold.
        var startsWarmC = resumed is null && first.Celsius is { } warmC && todo.Count > 0
                          && warmC > ResetWaiter.TargetFor(plan, todo[0], ambientC ?? AssumedAmbientC) ? warmC : (double?)null;
        if (startsWarmC is { } warm)
        {
            var coldC = ResetWaiter.TargetFor(plan, todo[0], ambientC ?? AssumedAmbientC);
            AnsiConsole.Write(new Panel(new Markup(
                    $"[bold]The oven is warm: {Markup.Escape(f.T(warm, 0))}.[/] The first setpoint is meant to start from cold " +
                    $"(below {Markup.Escape(f.T(coldC, 0))}), so that its time to temperature and first peak are like-for-like.\n" +
                    "On the next screen, press [bold]W[/] to let it cool first: the app shows it cooling and, after a few minutes, predicts how long it will take. " +
                    "Or start anyway, and the report marks the first setpoint [italic]started warm[/] (its mean is unaffected).\n" +
                    (ambientC is null
                        ? $"[grey]Room temperature is assumed to be {Markup.Escape(f.T(AssumedAmbientC, 0))}, since the oven isn't cold. If it isn't, give it with --ambient.[/]"
                        : $"[grey]Room temperature: {Markup.Escape(f.T(ambientC.Value, 0))}.[/]")))
                .Header("[yellow]Warm oven[/]").Border(BoxBorder.Rounded).BorderColor(Color.Yellow));
        }

        if (!s.Yes && interactive && !await new ConfirmationPrompt("Ready to start?").ShowAsync(AnsiConsole.Console, ct)) return 0;

        var startedAt = DateTimeOffset.Now;
        string outDir;
        SessionInfo info;
        if (resumed is not null)
        {
            outDir = resumed.Folder;
            info = resumed.Info with
            {
                MeterOptions = meterOptions.Count > 0 ? new Dictionary<string, string>(meterOptions) : resumed.Info.MeterOptions,
                AdjustmentC = adjustmentC,
                Adjust = s.BuildOptions(unit, resumed.Info.Adjust, ovenProfile),
                Oven = ovenProfile,
                AmbientC = ambientC,
                Sensor = sensor,
                Sittings = [.. resumed.Info.Sittings ?? [resumed.Info.Started], startedAt],
            };
        }
        else
        {
            outDir = s.Out ?? Path.Combine(RunsFolder(), DateTime.Now.ToString("yyyy-MM-dd HHmm", Inv));
            info = new SessionInfo(Version, startedAt, $"{identity.Manufacturer} {identity.Model}", meter.Location, unit, toleranceC, options, plan,
                driver.Name, identity, sensor, characterising ? adjustmentC ?? 0 : adjustmentC, s.BuildOptions(unit, null, ovenProfile), ambientC, [startedAt], ovenProfile,
                s is CharacteriseSettings cs ? new CharacteriseInfo(f.DeltaFromDisplay(cs.Step), adjustmentC ?? 0) : null,
                meterOptions.Count > 0 ? new Dictionary<string, string>(meterOptions) : null);
        }
        if (!driver.IsSimulation)
        {
            appSettings.LastOven = ovenProfile.Key;
            appSettings.Save();
        }
        using var recorder = new RunRecorder(outDir, info, append: resumed is not null);
        // A simulation's readings carry its own, faster clock: its events take the time of the latest reading, so
        // readings.csv stays in time order within a sitting. (A resumed simulation's clock starts again from the PC's.)
        if (driver.IsSimulation) recorder.Clock = () => recorder.LastReadingTime ?? DateTimeOffset.UtcNow;
        if (resumed is not null)
        {
            recorder.Note(recorder.Now, "resume");
            // What this sitting used, in the readings: session.json has room for one set, and a resumed run may have been given others.
            if (meterOptions.Count > 0)
                recorder.Note(recorder.Now, "meter-options:" + string.Join(";", meterOptions.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")));
        }
        using var poller = new MeterPoller(meter, TimeSpan.FromSeconds(s.Interval), driver.IsSimulation ? s.Speed : 1, connected: true);
        poller.Start();

        var results = new List<PointResult>(earlier);
        var meterLabel = driver.IsSimulation ? "simulated meter" : $"{identity.Model} on {meter.Location}";
        var tracker = new PointTracker(poller, recorder, f, options, toleranceC, meterLabel, driver.NotTemperatureHelp, plan.Count, driver.IsSimulation, driver.AutoPowerOff, sensor?.MaxC, driver.HasBattery);
        var waiter = new ResetWaiter(poller, recorder, f, ambientC ?? AssumedAmbientC, ambientC is null, driver.IsSimulation, driver.AutoPowerOff);
        var afterReset = resumed is not null; // the first setpoint of a resumed run follows a break
        var stopped = false;
        Exception? failure = null;
        var prompt = new ChangePrompt(poller, recorder, f, TimeSpan.FromMinutes(s.Remind));
        var ask = ChangePrompt.ShouldAsk(s.Yes, driver.IsSimulation, !Console.IsInputRedirected);
        string? lastDone = null; // what was just finished, for the prompt

        // results.json and report.md as they stand: after each setpoint, and at the end.
        Report SaveResults()
        {
            results = [.. results.OrderBy(r => r.Point.Index)];
            var now = new Report(info, results);
            recorder.WriteResults(info, results, now.Markdown());
            return now;
        }
        PointResult? previous = null; // the setpoint just measured in this sitting, if the oven went straight on from it
        var firstPrompt = true;

        // Characterise, phase 2: change the adjustment (towards correct, by at least --step), then the lowest and
        // highest setpoints again in each mode, highest first so the oven cools only once. False if it can't go on.
        async Task<bool> PlanPhase2Async()
        {
            if (info.Characterise is not { ChangedToC: null } ch) return false;
            var measured = results.Where(r => r.Metrics.OffsetC is not null).ToList();
            if (measured.Count == 0) return false;
            var adjust = new Report(info, results).AdjustOptions;
            var from = info.AdjustmentC ?? 0;
            var wanted = new Report(info, results).Advice().FirstOrDefault()?.ChangeC ?? 0;
            var sign = Math.Abs(wanted) >= adjust.StepC / 2 ? Math.Sign(wanted) : measured.Average(r => r.Metrics.OffsetC!.Value) > 0 ? -1 : 1;
            var size = Math.Round(Math.Max(ch.StepC, Math.Abs(wanted)) / adjust.StepC) * adjust.StepC;
            var to = Math.Clamp(from + sign * size, -adjust.RangeC, adjust.RangeC);
            if (Math.Abs(to - from) < 2 * adjust.StepC) to = Math.Clamp(from - sign * size, -adjust.RangeC, adjust.RangeC); // no room that way
            var report = new Report(info, results);
            var toText = report.Adjustment(to);

            if (ask)
            {
                var steps = ovenProfile.HowToChange is { Count: > 0 } h && !ovenProfile.IsGeneric
                    ? $" To set it on the {ovenProfile.Name}: {string.Join(" ", h.Select((x, i) => $"{i + 1}. {x}"))}"
                    : "";
                var doneText = $"Phase 1 is done. Now change the oven's thermostat adjustment from {report.Adjustment(from)} to {toText}, " +
                               $"and leave it there for phase 2.{steps}";
                if (!await prompt.AskToSetAdjustmentAsync(doneText, toText, ct))
                {
                    AnsiConsole.MarkupLine("[yellow]Stopped before phase 2. Carry on later with --resume: it asks for the change again.[/]");
                    return false;
                }
            }
            else if (!driver.IsSimulation)
            {
                AnsiConsole.MarkupLine($"[yellow]Phase 1 is done. Change the adjustment to {Markup.Escape(toText)}, then carry on with --resume.[/]");
                return false;
            }
            oven?.SetAdjustment(to); // the simulated oven

            var next = plan.Max(p => p.Index) + 1;
            var phase2 = new List<TestPoint>();
            var sps = plan.Select(p => p.SetpointC).Distinct().Order().ToList();
            foreach (var sp in new[] { sps[^1], sps[0] }.Distinct())
                foreach (var mode in modes)
                    phase2.Add(new TestPoint(next++, mode, sp, to));
            plan = [.. plan, .. phase2];
            info = info with { Plan = plan, AdjustmentC = to, Characterise = ch with { ChangedToC = to, ChangedAt = DateTimeOffset.Now } };
            recorder.UpdateSession(info);
            recorder.Note(recorder.Now, $"adjustment-changed:{to.ToString("F3", Inv)}");
            todo.AddRange(phase2);
            previous = null;
            lastDone = $"The adjustment is {toText} now. Phase 2: {phase2.Count} setpoints.";
            return true;
        }

        try
        {
            for (var w = 0; w < 50 && poller.LastCelsius is null; w++) await Task.Delay(100, ct); // a first reading, to know how hot the oven is
            var ti = 0;
            while (true)
            {
            for (; ti < todo.Count; ti++)
            {
                var point = todo[ti];
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule($"[orange1]{point.Index} of {plan.Count}: {Temp.ModeName(point.Mode)} {Markup.Escape(f.Setpoint(point.SetpointC))}[/]").LeftJustified());
                var label = $"{Temp.ModeName(point.Mode)} {f.Setpoint(point.SetpointC)}";
                var targetC = ResetWaiter.TargetFor(plan, point, ambientC ?? AssumedAmbientC);
                ChangeChoice choice;
                var hotAtPrompt = false; // how the oven was when asked, before anyone started it for this setpoint
                while (true)
                {
                    // Too hot to start from? Going straight on from a setpoint, the oven cycles around it, so allow half its swing.
                    var hot = poller.LastCelsius is { } nowC && nowC > targetC + (previous?.Metrics.SwingC ?? 0) / 2;
                    hotAtPrompt = hot;
                    var waitOffer = hot ? $"wait for it to cool below {f.T(targetC, 0)} first" : null;
                    if (hot && afterReset && s.Yes) choice = ChangeChoice.Wait;   // resumed with --yes: cool first, by itself
                    else if (!ask) choice = ChangeChoice.Start;                   // the simulator with --yes, or no keyboard
                    else
                    {
                        var heading = hot && (afterReset || firstPrompt)
                            ? $"THE OVEN IS {(afterReset ? "STILL HOT" : "WARM")} ({f.T(poller.LastCelsius!.Value, 0)}). PRESS W TO LET IT COOL, OR SET IT TO"
                            : firstPrompt ? "SET THE OVEN TO" : "CHANGE THE OVEN TO";
                        choice = await prompt.AskAsync(heading, Temp.ModeName(point.Mode), point.SetpointC, lastDone, waitOffer, ct);
                    }
                    if (choice != ChangeChoice.Wait) break;

                    if (!s.Yes) AnsiConsole.MarkupLine("[grey]Turn the oven off and leave the door shut.[/]");
                    oven?.SetOven(point.Mode, null);
                    DrainIdle(poller, recorder);
                    var outcome = await waiter.RunAsync(new ResetTarget(label, targetC), autoCarryOn: s.Yes, ct);
                    if (outcome == ResetOutcome.Quit) { stopped = true; break; }
                    afterReset = true;
                    previous = null; // the oven has cooled since
                    lastDone = $"The oven is ready for {label}.";
                }
                if (stopped || choice == ChangeChoice.Finish) { stopped = true; break; }
                if (choice == ChangeChoice.Skip) { lastDone = $"{label} skipped."; previous = null; continue; }
                firstPrompt = false;

                if (s.SimForget != point.Index) oven?.SetOven(point.Mode, point.SetpointC);
                var idle = DrainIdle(poller, recorder);
                if (afterReset) recorder.Note(recorder.Now, "reset");
                // The first setpoint of a new session, started without letting a warm oven cool. Judged by the oven when it
                // was asked, not now: by now it may be heating for this setpoint.
                var startedWarm = resumed is null && !afterReset && point.Index == plan[0].Index && hotAtPrompt;
                if (startedWarm) recorder.Note(recorder.Now, "started-warm");
                // Where the oven starts from: the setpoint just measured, if it went straight on from it, or its last two minutes.
                var from = previous?.Metrics.MeanC ?? StartingFrom(idle, poller.LastCelsius);
                var (result, pointOutcome) = await tracker.RunAsync(point, ct, from, previous?.Metrics.SwingC);
                result = result with { AfterReset = afterReset, StartedWarm = startedWarm };
                afterReset = false;
                results.RemoveAll(r => r.Point.Index == point.Index);
                results.Add(result);
                // Saved as each setpoint finishes, so a crash, a closed window or a second Ctrl+C loses at most the
                // setpoint in progress, not the report (#43). A save that fails (a full disk) mustn't stop the measuring.
                try { SaveResults(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AnsiConsole.MarkupLine($"[yellow]Couldn't save the results so far: {Markup.Escape(ex.Message)}. The readings are still being recorded.[/]");
                }
                poller.Enqueue(m => m.Beep());
                ConsoleCaps.Bell();
                ConsoleReport.PointSummary(new Report(info, results), result);
                lastDone = Done(new Report(info, results), result);
                previous = result.Metrics.MeanC is not null ? result : null;
                if (pointOutcome == PointOutcome.Quit) { stopped = true; break; }
            }
            // Characterise: with phase 1 done, change the adjustment and add phase 2.
            if (stopped || !characterising || !await PlanPhase2Async()) break;
            }

            // Finished (or stopped): ask for the oven to be turned off, with the same reminders.
            if (ask && !driver.IsSimulation && results.Count > 0 && !ct.IsCancellationRequested)
                await prompt.AskTurnOffAsync(stopped ? "The session is stopped." : "All the setpoints are done.", ct);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Stopped.[/]");
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the setpoints already measured are written up below.
            failure = ex;
            AnsiConsole.MarkupLine($"[red]The session stopped with an error: {Markup.Escape(ex.Message)}[/]");
        }
        finally
        {
            // Each step on its own: a meter or link failing as it closes must not stop the results being written.
            try { oven?.SetOven(OvenMode.Bake, null); } catch { }
            try { poller.Stop(); } catch { }
            try { DrainIdle(poller, recorder); } catch { }
            try { meter.Dispose(); } catch { }
        }

        var report = SaveResults();
        AnsiConsole.WriteLine();
        if (results.Count > 0) ConsoleReport.Full(report);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Saved to [link]{Markup.Escape(outDir)}[/]: report.md, results.json and readings.csv.");
        if (info.Characterise is { ChangedToC: not null })
            SaveCharacterisation(info, results, outDir, ovenProfile, save: !driver.IsSimulation);
        var left = plan.Count(p => !results.Any(r => r.Point.Index == p.Index && r.Metrics.Reason is CompletionReason.Converged or CompletionReason.TimedOut or CompletionReason.Manual));
        if (left > 0)
            AnsiConsole.MarkupLine($"{left} setpoint{(left == 1 ? "" : "s")} still to do. To carry on later, even after using the oven: [bold]oventracker run --resume \"{Markup.Escape(outDir)}\"[/]");
        AnsiConsole.MarkupLine("[grey]Turn the oven off.[/]");
        if (failure is not null)
        {
            AnsiConsole.MarkupLine($"[red]Stopped by an error ({Markup.Escape(failure.GetType().Name)}: {Markup.Escape(failure.Message)}). The setpoints before it are saved.[/]");
            return 1;
        }
        return 0;
    }

    /// <summary>Why a simulation's gains aren't saved.</summary>
    public const string SimulationNotSaved = "A simulation: the gains aren't saved to any oven's profile.";

    /// <summary>
    /// Writes characterisation.md and .json beside the run, shows them, and saves the measured gains to the oven's
    /// profile: not for the generic profile (it stands for every oven), and not for a simulation.
    /// </summary>
    /// <param name="save">Save the gains to the oven's profile.</param>
    /// <param name="ovenFolder">Where oven profiles are saved; the usual folder when null (tests give their own).</param>
    /// <param name="notSavedBecause">When not saving, why, for the message. A simulation's reason if not given.</param>
    public static Characterisation SaveCharacterisation(SessionInfo info, IReadOnlyList<PointResult> results, string folder, OvenProfile oven, bool save,
        string? ovenFolder = null, string? notSavedBecause = null)
    {
        var ch = Characterisation.From(info, results);
        var md = ch.Markdown(info);
        File.WriteAllText(Path.Combine(folder, "characterisation.md"), md);
        File.WriteAllText(Path.Combine(folder, "characterisation.json"), System.Text.Json.JsonSerializer.Serialize(ch, Json.Options));
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[orange1]Characterisation[/]").LeftJustified());
        AnsiConsole.WriteLine(md);
        if (!ch.MeasuredAny)
            AnsiConsole.MarkupLine("[yellow]No gain could be measured: no setpoint was finished at both adjustments.[/]");
        else if (!save)
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(notSavedBecause ?? SimulationNotSaved)}[/]");
        else if (oven.IsGeneric)
            AnsiConsole.MarkupLine("[yellow]The gains aren't saved: the generic profile stands for every oven. Describe yours (docs/adding-an-oven.md), " +
                                   "then save them with [bold]oventracker analyze \"" + Markup.Escape(folder) + "\" --oven <yours> --save-gains[/].[/]");
        else
        {
            var gains = new Dictionary<OvenMode, MeasuredGain>(oven.Gains ?? new Dictionary<OvenMode, MeasuredGain>());
            foreach (var (mode, g) in ch.MeasuredGains(DateTimeOffset.Now)) gains[mode] = g;
            var path = OvenProfiles.Save(oven with { Gains = gains }, ovenFolder);
            AnsiConsole.MarkupLine($"[green]Saved the measured gains to the {Markup.Escape(oven.Name)}'s profile[/]: {Markup.Escape(path)}. Later runs use them.");
        }
        return ch;
    }

    /// <summary>Without a terminal nothing can be asked, so a new run needs these given. Null if nothing is missing.</summary>
    static string? MissingWithoutTerminal(RunSettings s)
    {
        if (s.Resume is not null) return null;
        var missing = new List<string>();
        if (s.Unit is null) missing.Add("--unit");
        if (s.Modes is null) missing.Add("--modes");
        if (s.Setpoints is null) missing.Add("--setpoints");
        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    /// <summary>
    /// Warns, before a run, about a meter that will switch itself off mid-setpoint: when the meter says its
    /// auto power-off is on, or when its driver says it has one and the meter can't say (unless you've
    /// said it's off for this meter before). Returns false if you chose to stop.
    /// </summary>
    static async Task<bool> CheckAutoPowerOffAsync(IMeterDriver driver, ITemperatureMeter meter, MeterIdentity identity, AppSettings settings, bool yes, CancellationToken ct)
    {
        var key = $"{driver.Name}:{identity.Serial}";
        AutoPowerOffSetting Read()
        {
            try { return meter.ReadAutoPowerOff(); } catch { return AutoPowerOffSetting.Unknown; }
        }
        var setting = Read();
        while (true)
        {
            var warning = AutoPowerOffAdvice.For(driver.AutoPowerOff, setting, settings.IsAutoPowerOffTurnedOff(key));
            if (warning is null)
            {
                if (setting.State == AutoPowerOffState.Off) AnsiConsole.MarkupLine("[grey]Auto power-off is off.[/]");
                else if (driver.AutoPowerOff is not null && settings.IsAutoPowerOffTurnedOff(key))
                    AnsiConsole.MarkupLine("[grey]Auto power-off: you said it's turned off on this meter.[/]");
                return true;
            }

            var body = new List<string> { $"[bold]{Markup.Escape(warning.Headline)}[/]" };
            if (warning.Steps.Count > 0)
            {
                body.Add("");
                body.Add("To turn it off:");
                body.AddRange(warning.Steps.Select((step, i) => $"  {i + 1}. {Markup.Escape(step)}"));
            }
            if (warning.Source is { } source) body.Add($"[grey]({Markup.Escape(source)})[/]");
            AnsiConsole.Write(new Panel(new Markup(string.Join("\n", body)))
                .Header(warning.Kind == AutoPowerOffWarningKind.On ? "[red]Auto power-off is on[/]" : "[yellow]Auto power-off[/]")
                .Border(BoxBorder.Rounded).BorderColor(warning.Kind == AutoPowerOffWarningKind.On ? Color.Red : Color.Yellow));
            if (yes || driver.IsSimulation)
            {
                AnsiConsole.MarkupLine("[grey]If the meter switches itself off anyway, the live display says so and tracking carries on once it's woken.[/]");
                return true;
            }

            const string turnedOff = "I've turned it off: don't remind me again for this meter";
            const string checkAgain = "I've changed it: check again";
            const string carryOn = "Carry on anyway (the display will say if the meter switches off)";
            const string stop = "Stop";
            var choices = setting.State == AutoPowerOffState.Unknown ? new[] { turnedOff, carryOn, stop } : new[] { checkAgain, carryOn, stop };
            var choice = await new SelectionPrompt<string>().Title("Auto power-off:").AddChoices(choices).ShowAsync(AnsiConsole.Console, ct);
            AnsiConsole.MarkupLine($"Auto power-off: [bold]{Markup.Escape(choice)}[/]");
            switch (choice)
            {
                case turnedOff:
                    settings.RememberAutoPowerOffTurnedOff(key);
                    settings.Save();
                    return true;
                case checkAgain:
                    setting = Read();
                    continue;
                case stop:
                    return false;
                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// How far above a setpoint the oven goes: its first peak and the thermostat's swing (25 °F). A setpoint
    /// within this of the probe's maximum may take the probe past its rating.
    /// </summary>
    public const double PeakAboveSetpointC = 25 / 1.8;

    /// <summary>A warning, in Spectre markup, if any setpoint is above the probe's rating or within its peak of it; null if none.</summary>
    public static (string Text, bool Over)? ProbeRangeWarning(Sensor? sensor, IEnumerable<double> setpointsC, UnitFormatter f)
    {
        if (sensor?.MaxC is not { } max) return null;
        var sps = setpointsC.Distinct().Order().ToList();
        var over = sps.Where(sp => sp > max).ToList();
        var near = sps.Where(sp => sp <= max && sp + PeakAboveSetpointC > max).ToList();
        if (over.Count == 0 && near.Count == 0) return null;
        var lines = new List<string> { $"The {Markup.Escape(sensor.Name)} is rated to {Markup.Escape(f.T(max, 0))}." };
        if (over.Count > 0)
            lines.Add($"[red]{Markup.Escape(string.Join(", ", over.Select(f.Setpoint)))} {(over.Count == 1 ? "is" : "are")} above that: the probe may be damaged, and its readings can't be trusted.[/] Leave {(over.Count == 1 ? "it" : "them")} out, or use a probe rated higher.");
        if (near.Count > 0)
            lines.Add($"At {Markup.Escape(string.Join(", ", near.Select(f.Setpoint)))}, the oven's first peak and thermostat swing (about {Markup.Escape(f.Span(PeakAboveSetpointC, 0))}) may take the probe past it. The app will say if a reading does.");
        return (string.Join("\n", lines), over.Count > 0);
    }

    /// <summary>The "Before you start" line about convection conversion: the oven's own, with how to turn it off, or a general one.</summary>
    public static string ConvectionNote(OvenProfile oven, UnitFormatter f)
    {
        if (oven.ConvectionConversionC is { } conversion)
        {
            var off = oven.HowToTurnOffConversion is { Count: > 0 } steps
                ? $" To turn it off: {string.Join(" ", steps.Select((step, i) => $"{i + 1}. {step}"))}"
                : "";
            return $"• The {Markup.Escape(oven.Name)} lowers convection settings by about {Markup.Escape(f.Span(conversion, 0))} by itself. Turn that off for the test, or enter the temperature it actually targets.{Markup.Escape(off)}\n";
        }
        return "• If your oven lowers convection temperatures automatically (e.g. by 25 °F), turn that off, or enter the temperature it actually targets.\n";
    }

    /// <summary>Room temperature to assume when it wasn't measured or given: 70 °F.</summary>
    public const double AssumedAmbientC = 21;

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

    /// <summary>Below this, a battery meter's charge is mentioned before a run: a long session will probably need a swap.</summary>
    public const double BatteryNotePercent = 60;

    /// <summary>What to say on connecting about a battery that won't last a long session, or null.</summary>
    public static string? BatteryNote(double? percent) => percent is { } p && p < BatteryNotePercent
        ? $"The meter's battery is at {p.ToString("F0", Inv)} %, and a long session will probably need a fresh one. " +
          "When it runs low the app pauses, so you can swap it, then carries on with the same setpoint."
        : null;

    /// <summary>The driver to use: the one asked for, or the one that answered last time, or the default.</summary>
    public static IMeterDriver ChooseDriver(RunSettings s, AppSettings settings)
    {
        if (s.Simulate) return MeterDrivers.Find(SimulatedDriver.DriverName)!;
        if (MeterDrivers.Find(s.Meter) is { } asked) return asked;
        if (MeterDrivers.Find(settings.LastMeter) is { IsSimulation: false } last) return last;
        return MeterDrivers.Find(MeterDrivers.DefaultName)!;
    }

    /// <summary>Records the readings that came in between setpoints, and returns them.</summary>
    static List<MeterReading> DrainIdle(MeterPoller poller, RunRecorder recorder)
    {
        var list = new List<MeterReading>();
        while (poller.Readings.TryRead(out var r))
        {
            recorder.Reading(r, null, null);
            list.Add(r);
        }
        return list;
    }

    /// <summary>
    /// Where the oven was before a setpoint started: the mean of the last two minutes of readings
    /// between setpoints (an oven cycling on its thermostat swings either side of it), or the latest.
    /// </summary>
    public static double? StartingFrom(IReadOnlyList<MeterReading> idle, double? latestC)
    {
        var good = idle.Where(r => r is { Status: ReadingStatus.Ok, Celsius: not null }).ToList();
        if (good.Count == 0) return latestC;
        var from = good[^1].Time - TimeSpan.FromMinutes(2);
        return good.Where(r => r.Time >= from).Average(r => r.Celsius!.Value);
    }

    /// <summary>One line on a finished setpoint, for the prompt.</summary>
    static string Done(Report report, PointResult r)
    {
        var f = report.Format;
        var name = $"{Temp.ModeName(r.Point.Mode)} {f.Setpoint(r.Point.SetpointC)}";
        return r.Metrics.OffsetC is { } off
            ? $"{name} is done: {f.T(r.Metrics.MeanC!.Value)} ({f.D(off)}), {Report.VerdictText(SessionSummary.Judge(r.Metrics, report.Info.ToleranceC))}."
            : $"{name} is done, without a result.";
    }
}
