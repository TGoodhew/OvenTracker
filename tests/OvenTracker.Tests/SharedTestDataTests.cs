using OvenTracker.Commands;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace OvenTracker.Tests;

/// <summary>
/// Replays every case in testdata/cases (spec/testdata.md) and checks the analysis gives exactly the expected
/// files. The C# app is the reference: the Python port is checked against the same files, within the
/// tolerances in the spec. After a deliberate change to the analysis or the report, run testdata/regenerate.ps1.
/// </summary>
[Collection(nameof(ConsoleCollection))]
public class SharedTestDataTests
{
    public static string TestData
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "testdata", "cases"))) return Path.Combine(dir.FullName, "testdata");
            throw new DirectoryNotFoundException("testdata/cases not found above " + AppContext.BaseDirectory);
        }
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var d in Directory.GetDirectories(Path.Combine(TestData, "cases")).Order(StringComparer.Ordinal)) data.Add(Path.GetFileName(d));
        return data;
    }

    /// <summary>A case's arguments to analyze, one per line; "runs/…" is relative to testdata.</summary>
    public static string[] Arguments(string caseName) =>
        File.ReadAllLines(Path.Combine(TestData, "cases", caseName, "args.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.StartsWith("runs/", StringComparison.Ordinal) ? Path.Combine(TestData, l.Replace('/', Path.DirectorySeparatorChar)) : l)
            .ToArray();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheAnalysisGivesTheExpectedFiles(string caseName)
    {
        var expected = Path.Combine(TestData, "cases", caseName, "expected");
        var output = Path.Combine(Path.GetTempPath(), "OvenTrackerCase-" + Guid.NewGuid().ToString("N"));
        var before = AnsiConsole.Console;
        AnsiConsole.Console = new TestConsole();
        try
        {
            var code = await new CommandApp<AnalyzeCommand>().RunAsync([.. Arguments(caseName), "--out", output]);
            Assert.Equal(0, code);
            var files = Directory.GetFiles(expected).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            Assert.NotEmpty(files);
            Assert.Equal(files, Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList());
            foreach (var name in files)
                Assert.Equal(Normalize(File.ReadAllText(Path.Combine(expected, name!))), Normalize(File.ReadAllText(Path.Combine(output, name!))));
        }
        finally
        {
            AnsiConsole.Console = before;
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n");
}
