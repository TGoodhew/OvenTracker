using Spectre.Console;

namespace OvenTracker.Ui;

/// <summary>What the console can do: whether the live displays and prompts can be used.</summary>
public static class ConsoleCaps
{
    /// <summary>
    /// The live displays (and Spectre's prompts) need a terminal: not output redirected to a file or a pipe,
    /// and not a console Spectre can't drive. Otherwise the app writes plain progress lines.
    /// </summary>
    public static bool Live => AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;

    /// <summary>Whether keys can be read: input from a keyboard, not a file or a pipe.</summary>
    public static bool Keys => !Console.IsInputRedirected;

    /// <summary>Rings the PC's bell, where there's a terminal to ring it (a bell in a log file helps nobody).</summary>
    public static void Bell()
    {
        if (!Console.IsOutputRedirected) Console.Write("\a");
    }
}
