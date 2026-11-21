"""Number formatting and rounding as .NET 11 does them. The expected strings were checked against the runtime."""

from __future__ import annotations

import pytest

from oventracker import netfmt
from oventracker.units import TempUnit, UnitFormatter


@pytest.mark.parametrize(
    "value, f0, f1, f2",
    [
        (0.5, "0", "0.5", "0.50"),
        (1.5, "2", "1.5", "1.50"),
        (2.5, "2", "2.5", "2.50"),
        (0.25, "0", "0.2", "0.25"),
        (0.125, "0", "0.1", "0.12"),
        (0.375, "0", "0.4", "0.38"),
        (-2.5, "-2", "-2.5", "-2.50"),
        (1.45, "1", "1.4", "1.45"),
        (2.675, "3", "2.7", "2.67"),
        (0.05, "0", "0.1", "0.05"),
        (0.15, "0", "0.1", "0.15"),
        (1.005, "1", "1.0", "1.00"),
        (-0.04, "-0", "-0.0", "-0.04"),
        (-0.0, "-0", "-0.0", "-0.00"),
        (1e15, "1000000000000000", "1000000000000000.0", "1000000000000000.00"),
    ],
)
def test_standard_fixed_format_is_exact_and_half_even(value, f0, f1, f2):
    assert netfmt.fixed(value, 0) == f0
    assert netfmt.fixed(value, 1) == f1
    assert netfmt.fixed(value, 2) == f2


@pytest.mark.parametrize(
    "value, zero, one",
    [
        (0.5, "1", "0.5"),
        (1.5, "2", "1.5"),
        (2.5, "3", "2.5"),
        (0.25, "0", "0.3"),  # half away from zero
        (0.125, "0", "0.1"),
        (0.375, "0", "0.4"),
        (-2.5, "-3", "-2.5"),
        (1.45, "1", "1.5"),  # 15 significant digits first: 1.45000000000000, then half up
        (0.0075 * 100, "1", "0.8"),
        (0.15, "0", "0.2"),
        (1.005, "1", "1"),
        (-0.04, "-0", "-0"),
        (-0.0, "-0", "-0"),
        (1e-5, "0", "0"),
        (123456789012345678.0, "123456789012346000", "123456789012346000"),
    ],
)
def test_custom_format_rounds_15_digits_then_half_away(value, zero, one):
    assert netfmt.custom(value, 0) == zero
    assert netfmt.custom(value, 1) == one


def test_custom_format_with_three_places():
    assert netfmt.custom(0.0075 * 100, 3) == "0.75"
    assert netfmt.custom(0.02 * 100, 3) == "2"
    assert netfmt.custom(0.004 * 100, 3) == "0.4"


@pytest.mark.parametrize(
    "value, text",
    [
        (0.5, "0.5"),
        (1.0, "1"),
        (22.0, "22"),
        (-0.0, "-0"),
        (0.0, "0"),
        (1e15, "1000000000000000"),
        (1e16, "10000000000000000"),
        (9.9e16, "99000000000000000"),
        (1e17, "1E+17"),
        (123456789012345678.0, "1.2345678901234568E+17"),
        (1234567890123456.7, "1234567890123456.8"),
        (1e-5, "1E-05"),
        (1.5e-5, "1.5E-05"),
        (1e-7, "1E-07"),
        (0.0001, "0.0001"),
        (0.00012, "0.00012"),
        (5e-324, "5E-324"),
        (1.7976931348623157e308, "1.7976931348623157E+308"),
        (1e21, "1E+21"),
        (8.333333333333334, "8.333333333333334"),
        (-5.555555555555555, "-5.555555555555555"),
        (4, "4"),
    ],
)
def test_json_numbers(value, text):
    assert netfmt.json_number(value) == text


def test_json_strings_escape_as_the_default_encoder_does():
    assert netfmt.json_string("a'b\"c+d<°−é&>`\\\n\t/") == '"a\\u0027b\\u0022c\\u002Bd\\u003C\\u00B0\\u2212\\u00E9\\u0026\\u003E\\u0060\\\\\\n\\t/"'
    assert netfmt.json_string("\x01x\x7f\U0001F600") == '"\\u0001x\\u007F\\uD83D\\uDE00"'


def test_math_round():
    assert netfmt.round_even(2.5) == 2
    assert netfmt.round_even(3.5) == 4
    assert str(netfmt.round_even(-0.4)) == "-0.0"
    # Math.Round(x, digits) rounds the exact value: 2.675 is 2.67499999..., though 2.675 * 100 is 267.5.
    assert netfmt.round_digits(2.675, 2) == 2.67
    assert netfmt.round_digits(0.125, 2) == 0.12
    assert netfmt.round_digits(176.66666666666666, 2) == 176.67


def test_round_away_from_zero():
    assert netfmt.round_away(2.5) == 3
    assert netfmt.round_away(-2.5) == -3
    assert netfmt.round_away(2.4999999999999996) == 2
    assert str(netfmt.round_away(-0.3)) == "-0.0"


def test_floor_and_ceiling_keep_the_sign_of_zero():
    assert str(netfmt.net_ceiling(-0.3)) == "-0.0"
    assert str(netfmt.net_floor(0.3)) == "0.0"
    assert netfmt.net_floor(-0.3) == -1
    assert netfmt.net_ceiling(1.2) == 2


def test_clamp_refuses_a_minimum_over_the_maximum():
    assert netfmt.clamp(5, 0, 3) == 3
    with pytest.raises(ValueError):
        netfmt.clamp(1, 3, 0)


def test_unit_formatter():
    c = UnitFormatter(TempUnit.Celsius)
    f = UnitFormatter(TempUnit.Fahrenheit)
    assert c.D(0.04) == "±0.0 °C"
    assert c.D(-0.05000000000000001) == "−0.1 °C"
    assert c.D(4.4) == "+4.4 °C"
    assert f.D(-10 / 1.8, 0) == "−10 °F"
    assert f.setpoint(176.66666666666666) == "350 °F"
    assert f.setpoint(176.667) == "350 °F"
    assert c.setpoint(180.25) == "180.2 °C"  # Math.Round(180.25, 1): an exact tie, to even
    assert c.setpoint(180.35) == "180.3 °C"
    assert f.T(176.66666666666666) == "350.0 °F"
    assert c.span(-1.25) == "1.2 °C"
