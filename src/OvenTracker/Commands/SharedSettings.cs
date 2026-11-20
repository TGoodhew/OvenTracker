using System.ComponentModel;
using OvenTracker.Analysis;
using OvenTracker.Meter;
using OvenTracker.Ovens;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

/// <summary>The options shared by <c>run</c> and <c>analyze</c>: the oven's thermostat adjustment, and the probe.</summary>
public abstract class SharedSettings : CommandSettings
{
    [CommandOption("--oven <KEY>")]
    [Description("The oven, so the app knows its adjustment's steps and range, how to read and change it, its convection conversion and how slowly it cycles: generic, ge-phs925, or one described in a file (oventracker ovens lists them). Remembered.")]
    public string? Oven { get; set; }

    [CommandOption("--probe <NAME>")]
    [Description("The probe in use, so the report uses its tolerance and range: type-k (standard limits, the default for a type K meter), type-k-special, or 80bk-a (Fluke 80BK-A).")]
    public string? Probe { get; set; }

    [CommandOption("--probe-tolerance <SPEC>")]
    [Description("A probe's tolerance from its datasheet: the greater of degrees and a percentage, e.g. 2.2C,2% or 4F,2% (or 0.3C+0.5% for the two added). Without C or F, in the oven's unit.")]
    public string? ProbeTolerance { get; set; }

    [CommandOption("--probe-max <TEMP>")]
    [Description("The highest temperature the probe is rated for, e.g. 260C or 500F. The app warns before a run if a setpoint would take it past that.")]
    public string? ProbeMax { get; set; }

    [CommandOption("--adjustment <DEG>")]
    [Description("The oven's thermostat adjustment (offset) setting during the run, in the oven's unit, e.g. -10. Recorded, so the app can say what to set it to, and can measure how far the adjustment moves the oven from runs at different settings.")]
    public double? Adjustment { get; set; }

    [CommandOption("--objective <AIM>")]
    [Description("What the recommended adjustment makes smallest: balanced (the overall error, the default) or minimax (the worst setpoint's error).")]
    public string? Objective { get; set; }

    [CommandOption("--favour <TEMP>")]
    [Description("Make the oven right at this temperature, in the oven's unit (e.g. the one you bake at most), rather than across all of them.")]
    public double? Favour { get; set; }

    [CommandOption("--adjust-step <DEG>")]
    [Description("The oven's adjustment step, in its unit. Default 1.")]
    public double? AdjustStep { get; set; }

    [CommandOption("--adjust-range <DEG>")]
    [Description("How far the oven's adjustment goes either way, in its unit. Default 35 °F / 19 °C (the GE PHS925 goes to ±35 °F).")]
    public double? AdjustRange { get; set; }

    [CommandOption("--adjust-model <MODEL>")]
    [Description("How the adjustment is assumed to act until runs at different settings measure it: shift (the same at every setting, the default) or gain (scaled by the oven's own error slope).")]
    public string? AdjustModel { get; set; }

    [CommandOption("--separate-adjustments")]
    [Description("Bake and convection each have their own adjustment (most ovens have one for both).")]
    public bool SeparateAdjustments { get; set; }

    public static AdjustObjective? ParseObjective(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "balanced" or "least-squares" => AdjustObjective.Balanced,
        "minimax" or "worst" => AdjustObjective.Minimax,
        _ => null,
    };

    public static AdjustModel? ParseModel(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "shift" => Analysis.AdjustModel.Shift,
        "gain" => Analysis.AdjustModel.Gain,
        _ => null,
    };

    public override ValidationResult Validate()
    {
        if (Objective is not null && ParseObjective(Objective) is null) return ValidationResult.Error("--objective must be balanced or minimax.");
        if (AdjustModel is not null && ParseModel(AdjustModel) is null) return ValidationResult.Error("--adjust-model must be shift or gain.");
        if (AdjustStep is <= 0) return ValidationResult.Error("--adjust-step must be positive.");
        if (AdjustRange is <= 0) return ValidationResult.Error("--adjust-range must be positive.");
        if (Favour is <= 0) return ValidationResult.Error("--favour must be a temperature, e.g. 350.");
        if (Oven is not null && OvenProfiles.Find(Oven) is null) return ValidationResult.Error($"--oven must be one of: {OvenProfiles.Keys()}. To add yours, see docs/adding-an-oven.md.");
        if (Probe is not null && Probes.Find(Probe) is null) return ValidationResult.Error($"--probe must be one of: {Probes.Keys}. For another, give --probe-tolerance (and --probe-max).");
        if (ProbeTolerance is not null && Probes.ParseTolerance(ProbeTolerance, TempUnit.Celsius) is null)
            return ValidationResult.Error("--probe-tolerance must look like 2.2C,2% (the greater of ±2.2 °C and ±2 %) or 0.3C+0.5% (the two added).");
        if (ProbeMax is not null && Probes.ParseTemperature(ProbeMax, TempUnit.Celsius) is null) return ValidationResult.Error("--probe-max must be a temperature, e.g. 260C or 500F.");
        return ValidationResult.Success();
    }

    /// <summary>The adjustment options: the ones given here, over <paramref name="saved"/> (from a run), over the defaults for the unit.</summary>
    /// <param name="oven">The oven: its step, range, sharing and measured gains are the defaults when nothing is saved.</param>
    public AdjustmentOptions BuildOptions(TempUnit unit, AdjustmentOptions? saved = null, OvenProfile? oven = null)
    {
        var f = new UnitFormatter(unit);
        var o = saved ?? AdjustmentOptions.For(oven ?? OvenProfiles.Default, unit);
        // A newly chosen oven brings its own measured gains, even to options saved with a run.
        if (saved is not null && Oven is not null && oven is not null) o = o with { KnownGains = AdjustmentOptions.For(oven, unit).KnownGains };
        if (AdjustStep is { } step) o = o with { StepC = f.DeltaFromDisplay(step) };
        if (AdjustRange is { } range) o = o with { RangeC = f.DeltaFromDisplay(range) };
        if (ParseObjective(Objective) is { } objective) o = o with { Objective = objective, FavourC = Favour is null ? null : o.FavourC };
        if (Favour is { } favour) o = o with { FavourC = f.FromDisplay(favour) };
        if (ParseModel(AdjustModel) is { } model) o = o with { Model = model };
        if (SeparateAdjustments) o = o with { SharedAcrossModes = false };
        return o;
    }

    /// <summary>Whether any probe option was given.</summary>
    public bool GivesProbe => Probe is not null || ProbeTolerance is not null || ProbeMax is not null;

    /// <summary>The sensor for the report: the probe given, over what the meter (or the saved run) says.</summary>
    public Sensor? ResolveSensor(Sensor? reported, TempUnit unit) =>
        GivesProbe ? Probes.Resolve(reported, Probe, ProbeTolerance, ProbeMax, unit) : reported;

    /// <summary>The adjustment given, in °C.</summary>
    public double? AdjustmentC(TempUnit unit) => Adjustment is { } a ? new UnitFormatter(unit).DeltaFromDisplay(a) : null;
}
