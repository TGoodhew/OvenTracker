"""An oven that heats in steps, and one that really runs cold (#49), as the C# app's PreheatTests."""

from __future__ import annotations

from oventracker.analysis.setpoint_analyzer import AnalysisOptions, CompletionReason, PointPhase, SetpointAnalyzer, UnitKind

SETPOINT = 176.67  # 350 °F


def run(c, seconds: float) -> SetpointAnalyzer:
    a = SetpointAnalyzer(SETPOINT, AnalysisOptions())
    t = 0.0
    while t <= seconds and a.phase != PointPhase.Complete:
        a.add(t, c(t))
        t += 1
    return a


def cycle(t: float, lo: float, hi: float, period: float) -> float:
    x = t % period / period
    return lo + (hi - lo) * 2 * x if x < 0.5 else hi - (hi - lo) * 2 * (x - 0.5)


def stepped_preheat(t: float) -> float:
    """Heats from 25 °C in steps (a 2-minute climb of 25 °C, then a 1-minute dip of 8 °C) to the setting, then cycles
    172–185 °C every 12 minutes."""
    stepped, tt = 25.0, t
    while tt > 0 and stepped < 172:
        climb = min(tt, 120)
        stepped += 25 * climb / 120
        tt -= climb
        if tt <= 0 or stepped >= 172:
            break
        dip = min(tt, 60)
        stepped -= 8 * dip / 60
        tt -= dip
    return stepped if stepped < 172 else cycle(tt, 172, 185, 720)


def test_an_oven_heating_in_steps_isnt_taken_for_cycling():
    m = run(stepped_preheat, 4 * 3600).metrics()
    assert m.time_to_setpoint_minutes > 15, f"reached after {m.time_to_setpoint_minutes:.1f} min"
    assert 11 <= m.cycle_minutes <= 13
    assert 176 <= m.mean_c <= 180


def test_an_oven_that_runs_cold_is_still_measured_from_its_first_peak():
    def cold(t: float) -> float:
        return 25 + 145 * t / 900 if t < 900 else cycle(t - 900 + 360, 160, 170, 720)

    m = run(cold, 4 * 3600).metrics()
    assert m.reason == CompletionReason.Converged
    assert m.kind == UnitKind.Cycle
    assert 15 <= m.time_to_setpoint_minutes <= 17
    assert 164 <= m.mean_c <= 166
    assert m.discarded_units == 0


def test_the_margin_has_a_floor():
    a = SetpointAnalyzer(SETPOINT, AnalysisOptions())
    assert a.arrival_margin_c == SetpointAnalyzer.MIN_ARRIVAL_MARGIN_C == 5
