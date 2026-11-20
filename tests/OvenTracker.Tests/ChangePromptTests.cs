using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

public class ChangePromptTests
{
    [Theory]
    [InlineData(false, false, true, true)]   // a real meter: ask
    [InlineData(true, false, true, true)]    // a real meter with --yes: still ask, since nothing else changes the oven
    [InlineData(false, true, true, true)]    // the simulator: ask
    [InlineData(true, true, true, false)]    // the simulator with --yes: carry on by itself
    [InlineData(false, false, false, false)] // no keyboard: can't ask
    public void ItStopsToAskUnlessNothingNeedsChanging(bool yes, bool simulation, bool canReadKeys, bool asks) =>
        Assert.Equal(asks, ChangePrompt.ShouldAsk(yes, simulation, canReadKeys));

    [Fact]
    public void RemindersComeEveryIntervalUntilAnswered()
    {
        var minute = TimeSpan.FromMinutes(1);
        Assert.Equal(0, ChangePrompt.RemindersDue(TimeSpan.FromSeconds(59), minute));
        Assert.Equal(1, ChangePrompt.RemindersDue(TimeSpan.FromSeconds(61), minute));
        Assert.Equal(3, ChangePrompt.RemindersDue(TimeSpan.FromMinutes(3.5), minute));
        Assert.Equal(0, ChangePrompt.RemindersDue(TimeSpan.FromMinutes(30), TimeSpan.Zero)); // --remind 0
    }

    [Fact]
    public void TheOvenStartsFromTheMeanOfItsLastTwoMinutes()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        // Five minutes cycling between 170 and 190 °C, after an hour at something else.
        var idle = new List<MeterReading> { new(t0, ReadingStatus.Ok, 100, "") };
        for (var s = 3600; s <= 3900; s++) idle.Add(new(t0.AddSeconds(s), ReadingStatus.Ok, 180 + 10 * Math.Sin(2 * Math.PI * s / 60), "")); // a 1-minute cycle
        idle.Add(new(t0.AddSeconds(3901), ReadingStatus.Overload, null, "OL"));
        Assert.InRange(RunCommand.StartingFrom(idle, 999)!.Value, 178, 182);

        Assert.Equal(150, RunCommand.StartingFrom([], 150));
        Assert.Null(RunCommand.StartingFrom([], null));
    }

    [Fact]
    public void TheWarningSaysWhatToDo()
    {
        var h = new HeadingCheck(176.67, 204.44);
        var text = PointTracker.NotHeadingMessage(h, new UnitFormatter(TempUnit.Fahrenheit));
        Assert.Contains("isn't heating up towards 400 °F", text);
        Assert.Contains("still near 350 °F", text);
        Assert.Contains("press R twice", text);
        Assert.Contains("cooling down", PointTracker.NotHeadingMessage(new HeadingCheck(232.22, 204.44), new UnitFormatter(TempUnit.Fahrenheit)));
    }
}
