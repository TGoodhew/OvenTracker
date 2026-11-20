using System.Text.Json;
using OvenTracker.Analysis;
using OvenTracker.Commands;
using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

public class ProbeTests
{
    static readonly UnitFormatter F = new(TempUnit.Fahrenheit);
    static double T(double f) => (f - 32) / 1.8;

    [Fact]
    public void TheFluke80BKAIsTwoPercentAndRatedTo260C()
    {
        var p = Probes.Find("80BK-A")!.Sensor;
        Assert.Equal(260, p.MaxC);
        Assert.Equal(2.2, p.ToleranceC(100), 6);               // the fixed part below 110 °C
        Assert.Equal(8.36, p.ToleranceC(T(450)) * 1.8, 2);     // 2 % of 232 °C = ±4.6 °C = ±8.4 °F at 450 °F
        Assert.False(p.IsGenericType);
        Assert.Contains("fluke.com", Probes.Find("80bk-a")!.Source); // every built-in probe cites its source
    }

    [Fact]
    public void TypeKGradesFollowASTME230()
    {
        Assert.Equal(4.0, Probes.Find("type-k")!.Sensor.ToleranceC(T(450)) * 1.8, 1);          // ±2.2 °C
        Assert.Equal(1.98, Probes.Find("type-k-special")!.Sensor.ToleranceC(T(450)) * 1.8, 2); // ±1.1 °C: 0.4 % of 232 °C is less
        Assert.True(Probes.Find("type-k")!.Sensor.IsGenericType);
        Assert.Null(Probes.Find("80pk-1")); // not added: Fluke's own pages don't give its accuracy
    }

    [Theory]
    [InlineData("2.2C,2%", 2.2, 0.02, false)]
    [InlineData("4F,2%", 2.2222, 0.02, false)]
    [InlineData(" 0.3c + 0.5 % ", 0.3, 0.005, true)]
    [InlineData("2.2,2%", 1.2222, 0.02, false)] // no unit: the oven's (°F here)
    public void ToleranceSpecsAreRead(string text, double limitC, double fraction, bool adds)
    {
        var t = Probes.ParseTolerance(text, TempUnit.Fahrenheit)!.Value;
        Assert.Equal(limitC, t.LimitC, 3);
        Assert.Equal(fraction, t.Fraction, 6);
        Assert.Equal(adds, t.Adds);
    }

    [Theory]
    [InlineData("2.2")]
    [InlineData("2%")]
    [InlineData("two,2%")]
    public void BadToleranceSpecsAreRejected(string text) => Assert.Null(Probes.ParseTolerance(text, TempUnit.Celsius));

    [Fact]
    public void TemperaturesAreReadInEitherUnit()
    {
        Assert.Equal(260, Probes.ParseTemperature("260C", TempUnit.Fahrenheit)!.Value, 6);
        Assert.Equal(260, Probes.ParseTemperature("500F", TempUnit.Celsius)!.Value, 6);
        Assert.Equal(260, Probes.ParseTemperature("500", TempUnit.Fahrenheit)!.Value, 6);
        Assert.Null(Probes.ParseTemperature("hot", TempUnit.Celsius));
    }

    [Fact]
    public void TheProbeGivenWinsOverWhatTheMeterSays()
    {
        var reported = Sensor.Thermocouple("K");
        Assert.Equal("Fluke 80BK-A type K probe", new RunSettings { Probe = "80bk-a" }.ResolveSensor(reported, TempUnit.Fahrenheit)!.Name);
        Assert.Same(reported, new RunSettings().ResolveSensor(reported, TempUnit.Fahrenheit)); // nothing given: the meter's

        var custom = new AnalyzeSettings { ProbeTolerance = "1C,1%", ProbeMax = "300C" }.ResolveSensor(reported, TempUnit.Fahrenheit)!;
        Assert.Equal(1, custom.LimitC, 6);
        Assert.Equal(0.01, custom.LimitFraction, 6);
        Assert.Equal(300, custom.MaxC);
        Assert.Contains("custom", custom.Name);
    }

    [Fact]
    public void SetpointsNearOrPastTheProbesRatingAreWarnedAbout()
    {
        var probe = Probes.Find("80bk-a")!.Sensor;
        Assert.Null(RunCommand.ProbeRangeWarning(probe, [T(350), T(400), T(450)], F)); // 450 + 25 = 475 °F, under 500 °F

        var near = RunCommand.ProbeRangeWarning(probe, [T(350), T(480)], F)!.Value;
        Assert.False(near.Over);
        Assert.Contains("480 °F", near.Text);
        Assert.Contains("rated to 500 °F", near.Text);

        var over = RunCommand.ProbeRangeWarning(probe, [T(525)], F)!.Value;
        Assert.True(over.Over);
        Assert.Contains("525 °F", over.Text);

        Assert.Null(RunCommand.ProbeRangeWarning(Sensor.Thermocouple("K"), [T(525)], F)); // no rating known
    }

    [Fact]
    public void TheReportNamesTheProbeAndItsTolerance()
    {
        var plan = new[] { new TestPoint(1, OvenMode.Bake, T(450)) };
        var info = new SessionInfo("test", DateTimeOffset.Now, "sim", "sim", TempUnit.Fahrenheit, 8, new AnalysisOptions(), plan, Sensor: Probes.Find("80bk-a")!.Sensor);
        var text = new Report(info, []).SensorAccuracy(T(450));
        Assert.Contains("the Fluke 80BK-A type K probe is good to the greater of ±4.0 °F and ±2 % of the temperature", text);
        Assert.Contains("±8.4 °F at 450 °F", text);
    }

    [Fact]
    public void TheReportSaysHowCloseTheReadingsCameToTheRating()
    {
        var plan = new[] { new TestPoint(1, OvenMode.Bake, T(450)) };
        var info = new SessionInfo("test", DateTimeOffset.Now, "sim", "sim", TempUnit.Fahrenheit, 8, new AnalysisOptions(), plan, Sensor: Probes.Find("80bk-a")!.Sensor);
        PointMetrics Peaking(double maxF) => new(T(450), CompletionReason.Converged, UnitKind.Cycle, 4, 0, T(452), 0.5, 1, 0, 10, T(440), T(maxF), 20, 20, 30, 0, 30, 60, 90, null, null, 1);

        Assert.Null(new Report(info, [new PointResult(plan[0], Peaking(470))]).ProbeRange());            // 30 °F under
        Assert.Contains("close to that", new Report(info, [new PointResult(plan[0], Peaking(490))]).ProbeRange());
        Assert.Contains("can't be trusted", new Report(info, [new PointResult(plan[0], Peaking(505))]).ProbeRange());
    }

    [Fact]
    public void TheProbeIsKeptInTheSession()
    {
        var info = new SessionInfo("test", DateTimeOffset.Now, "sim", "sim", TempUnit.Fahrenheit, 8, new AnalysisOptions(), [], Sensor: Probes.Find("80bk-a")!.Sensor);
        var back = JsonSerializer.Deserialize<SessionInfo>(JsonSerializer.Serialize(info, Json.Options), Json.Options)!;
        Assert.Equal(info.Sensor, back.Sensor);
    }

    [Fact]
    public void ReadingsAboveTheRatingRaiseAnAlert() =>
        Assert.Contains("above its rated 500 °F", PointTracker.OverRatingMessage(T(505), 260, F));
}
