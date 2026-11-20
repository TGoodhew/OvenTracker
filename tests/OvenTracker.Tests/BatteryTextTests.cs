using OvenTracker.Analysis;
using OvenTracker.Session;
using OvenTracker.Ui;
using Spectre.Console;

namespace OvenTracker.Tests;

public class BatteryTextTests
{
    [Fact]
    public void AMeterWithoutABatteryShowsNothing()
    {
        Assert.Null(BatteryText.Markup(null));
        Assert.Null(BatteryText.Plain(null));
    }

    [Fact]
    public void ABatteryIsShownAndALowOneStandsOut()
    {
        Assert.Equal("64 %", BatteryText.Markup(64));
        Assert.Equal("battery 64 %", BatteryText.Plain(64));

        var low = BatteryText.Markup(15)!;
        Assert.Contains("[red]", low);
        Assert.Contains("15 % (low)", low);
        _ = new Markup(low); // valid markup
        Assert.Equal("battery 15 % (low)", BatteryText.Plain(15));

        Assert.False(BatteryText.IsLow(20));
        Assert.True(BatteryText.IsLow(19.9));
    }

    [Fact]
    public void ProgressLinesIncludeTheBatteryWhenTheMeterHasOne()
    {
        var a = new SetpointAnalyzer(176.67);
        for (var t = 0; t < 120; t++) a.Add(t, 100 + t * 0.2);
        var f = new UnitFormatter(TempUnit.Fahrenheit);
        Assert.EndsWith("battery 15 % (low)", PointTracker.PlainLine(a, f, 15));
        Assert.DoesNotContain("battery", PointTracker.PlainLine(a, f, null));
    }
}
