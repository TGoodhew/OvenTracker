using OvenTracker.Analysis;

namespace OvenTracker.Tests;

public class AdjustmentAdvisorTests
{
    static double T(double f) => (f - 32) / 1.8;   // a temperature in °F, as °C
    static double D(double f) => f / 1.8;          // a difference in °F, as °C
    static double InF(double dc) => Math.Round(dc * 1.8, 3);

    static readonly AdjustmentOptions Ge = AdjustmentOptions.For(TempUnit.Fahrenheit); // 1 °F steps, ±35 °F

    static AdjustPoint Bake(double setF, double offsetF, double adjustF = 0, double hwF = 0.8) =>
        new(OvenMode.Bake, T(setF), D(offsetF), D(hwF), D(adjustF));

    static AdjustPoint Conv(double setF, double offsetF, double adjustF = 0) =>
        new(OvenMode.Convection, T(setF), D(offsetF), D(0.8), D(adjustF));

    static AdjustmentAdvice One(IReadOnlyList<AdjustPoint> points, AdjustmentOptions? options = null, double? currentF = 0) =>
        Assert.Single(AdjustmentAdvisor.Advise(points, options ?? Ge, currentF is { } c ? D(c) : null));

    [Fact]
    public void AConstantErrorIsCorrectedExactlyEverywhere()
    {
        var a = One([Bake(325, 10), Bake(350, 10.4), Bake(400, 9.6)]);
        Assert.Equal(-10, InF(a.ChangeC));
        Assert.Equal(-10, InF(a.NewSettingC!.Value));
        Assert.All(a.Residuals, r => Assert.InRange(InF(r.AfterC), -0.5, 0.5));
        Assert.Empty(a.DialCorrections);
        Assert.Null(a.TooFewSetpoints);
        Assert.False(a.Curved);
        Assert.False(a.GainMeasured);
    }

    [Fact]
    public void TheIssuesExampleSplitsTheErrorAndSaysWhatToSetWhereItFallsShort()
    {
        // +2 °F at 350 °F but +15 °F at 425 °F: no single adjustment fixes both.
        var a = One([Bake(350, 2), Bake(425, 15)]);
        Assert.Equal(-8, InF(a.ChangeC)); // −8.5 ideal; −8 and −9 are equally good, so the smaller change
        Assert.Equal([-6, 7], a.Residuals.Select(r => InF(r.AfterC)));
        // Still 6–7 °F out at each, so the app says what to set: 356 °F for 350, 418 °F for 425.
        Assert.Equal([356, 418], a.DialCorrections.Select(d => Math.Round(d.SetC * 1.8 + 32)));
        // Two setpoints with different errors: a third in between would show the shape.
        Assert.NotNull(a.TooFewSetpoints);
        Assert.Equal(387.5, Math.Round(a.AddSetpointsC.Single() * 1.8 + 32, 1));
    }

    [Fact]
    public void MinimaxKeepsTheWorstSetpointAsCloseAsItCan()
    {
        var a = One([Bake(325, 0), Bake(375, 4), Bake(450, 16)], Ge with { Objective = AdjustObjective.Minimax });
        Assert.Equal(-8, InF(a.ChangeC)); // the worst errors become −8 and +8
        Assert.Equal(8, InF(a.WorstAfterC));

        var balanced = One([Bake(325, 0), Bake(375, 4), Bake(450, 16)]);
        Assert.Equal(-7, InF(balanced.ChangeC)); // the mean error is 6.67
        Assert.True(balanced.WorstAfterC > a.WorstAfterC);
    }

    [Fact]
    public void FavouringATemperatureMakesItRightThere()
    {
        var a = One([Bake(350, 2), Bake(425, 15)], Ge with { FavourC = T(350) });
        Assert.Equal(-2, InF(a.ChangeC));
        Assert.Equal([0, 13], a.Residuals.Select(r => InF(r.AfterC)));
        Assert.Equal(412, Math.Round(a.DialCorrections.Single().SetC * 1.8 + 32));
    }

    [Fact]
    public void TheAdjustmentStaysWithinTheOvensRange()
    {
        var big = One([Bake(350, 50), Bake(400, 50)]);
        Assert.True(big.Clamped);
        Assert.Equal(-35, InF(big.ChangeC));

        // Already at −20: only 15 more is possible.
        var already = One([Bake(350, 30, adjustF: -20), Bake(400, 30, adjustF: -20)], currentF: -20);
        Assert.True(already.Clamped);
        Assert.Equal(-15, InF(already.ChangeC));
        Assert.Equal(-35, InF(already.NewSettingC!.Value));
    }

    [Fact]
    public void RunsAtTwoAdjustmentsMeasureHowFarTheAdjustmentMovesTheOven()
    {
        // At 0 the oven ran 10 °F hot; at −10 it ran 1 °F hot: each degree moved it 0.9 °F.
        var a = One([Bake(350, 10, 0), Bake(400, 10, 0), Bake(350, 1, -10), Bake(400, 1, -10)], currentF: -10);
        Assert.True(a.GainMeasured);
        Assert.Equal(0.9, a.Gain, 3);
        Assert.NotNull(a.GainHalfWidthC);
        Assert.Equal(-1, InF(a.ChangeC)); // 1 °F hot at −10 now: one more step
        Assert.Equal(-11, InF(a.NewSettingC!.Value));
        Assert.All(a.Residuals, r => Assert.InRange(InF(r.AfterC), -0.2, 0.2));
    }

    [Fact]
    public void TheGainModelUsesTheOvensOwnSlope()
    {
        // Measured = 1.1 × set − 35 °F: an adjustment of k moves it 1.1 k.
        var pts = new[] { 300.0, 400, 500 }.Select(s => Bake(s, 0.1 * s - 35)).ToList();
        var a = One(pts, Ge with { Model = AdjustModel.Gain });
        Assert.False(a.GainMeasured);
        Assert.Equal(1.1, a.Gain, 3);
    }

    [Fact]
    public void OneAdjustmentForBothModesSplitsTheirErrors()
    {
        var shared = One([Bake(350, 8), Bake(400, 8), Conv(350, -9), Conv(400, -9)]);
        Assert.Null(shared.Mode);
        Assert.InRange(InF(shared.ChangeC), 0, 1);
        Assert.NotEmpty(shared.DialCorrections); // 8–9 °F left either way

        var separate = AdjustmentAdvisor.Advise([Bake(350, 8), Bake(400, 8), Conv(350, -9), Conv(400, -9)], Ge with { SharedAcrossModes = false }, 0);
        Assert.Equal([-8, 9], separate.Select(a => InF(a.ChangeC)));
        Assert.Equal([OvenMode.Bake, OvenMode.Convection], separate.Select(a => a.Mode!.Value));
    }

    [Fact]
    public void OneSetpointCantShowTheErrorsShape()
    {
        var a = One([Bake(350, 6)]);
        Assert.Equal(-6, InF(a.ChangeC));
        Assert.NotNull(a.TooFewSetpoints);
        Assert.Equal([300, 400], a.AddSetpointsC.Select(c => Math.Round(c * 1.8 + 32)));
    }

    [Fact]
    public void AnErrorThatBendsIsCalledCurved()
    {
        var a = One([Bake(300, 0), Bake(375, 12), Bake(450, 0)]);
        Assert.True(a.Curved);
    }

    [Fact]
    public void WithoutTheCurrentAdjustmentItSaysHowMuchToChange()
    {
        var a = One([Bake(350, 10), Bake(400, 10)], currentF: null);
        Assert.Null(a.CurrentC);
        Assert.Null(a.NewSettingC);
        Assert.Equal(-10, InF(a.ChangeC));
    }

    [Fact]
    public void CelsiusOvensUseWholeDegreeSteps()
    {
        var a = Assert.Single(AdjustmentAdvisor.Advise([new AdjustPoint(OvenMode.Bake, 180, 4.4, 0.5, 0)], AdjustmentOptions.For(TempUnit.Celsius), 0));
        Assert.Equal(-4, a.ChangeC, 6);
    }
}
