using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OvenTracker.Commands;

public sealed class CharacteriseSettings : RunSettings
{
    [CommandOption("--step <DEG>")]
    [Description("The least change of adjustment to test, in the oven's unit. Bigger measures the gain better. Default 10. The app goes the way that brings the oven towards correct, and further if the best adjustment is further.")]
    [DefaultValue(10.0)]
    public double Step { get; set; } = 10;

    public override ValidationResult Validate()
    {
        if (base.Validate() is { Successful: false } result) return result;
        if (Step is < 2 or > 100) return ValidationResult.Error("--step must be between 2 and 100 degrees.");
        return ValidationResult.Success();
    }
}

/// <summary>
/// Measures how an oven's adjustment moves it, in one session: every setpoint at the current adjustment,
/// then (after you change it) the lowest and highest again, then each mode's gain, its error's shape, and
/// the best adjustment with those gains. The gains are saved to the oven's profile. See #27 and
/// docs/oven-control-model.md.
/// </summary>
public sealed class CharacteriseCommand : AsyncCommand<CharacteriseSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, CharacteriseSettings settings, CancellationToken cancellationToken) =>
        new RunCommand().ExecuteAsync(context, settings, cancellationToken);
}
