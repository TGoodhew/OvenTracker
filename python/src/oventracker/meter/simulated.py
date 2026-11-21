"""A simulated oven and meter (Meter/SimulatedMeter.cs), so the whole app can be tried without either.

Its random numbers differ from the C# app's (spec/testdata.md): the shared test data uses recorded runs."""

from __future__ import annotations

import math
import random
import time
from datetime import datetime, timedelta, timezone

from ..netfmt import fixed, round_digits
from ..probes import Sensor
from ..units import OvenMode, TempUnit
from .auto_power_off import AutoPowerOff, AutoPowerOffSetting
from .base import MeterConfig, MeterIdentity, MeterReading, ReadingStatus, TemperatureMeter
from .driver import MeterCandidate, MeterDriver, MeterRequest, ProbeResult


class SimulatedOven:
    """A small physical model of an electric oven with an on/off thermostat. Air, walls and the heating element
    are separate thermal masses, and the thermostat's own sensor lags the air: together they give a realistic
    overshoot, a slow heat-soak drift and a regular cycle. Each mode has its own deliberate error."""

    def __init__(self, seed: int = 1):
        self._rng = random.Random(seed)
        self.ambient_c = 22.0
        self.air_c = 22.0
        self.wall_c = 22.0
        self.sensor_c = 22.0
        self.element = 0.0
        self.heater_on = False
        self.mode = OvenMode.Bake
        self.setpoint_c: float | None = None
        #: The thermostat adjustment (offset): it moves where the thermostat holds by this much at every setting.
        self.adjustment_c = 0.0
        #: An open door lets the hot air out many times faster.
        self.door_open = False

    @staticmethod
    def bias_c(mode: OvenMode, setpoint_c: float) -> float:
        """The thermostat's error at a setpoint: bake runs hot, more so at the top; convection runs cool."""
        return 4.0 + 0.04 * (setpoint_c - 177) if mode == OvenMode.Bake else -5.0 + 0.01 * (setpoint_c - 177)

    def set(self, mode: OvenMode, setpoint_c: float | None) -> None:
        self.mode = mode
        self.setpoint_c = setpoint_c

    def step(self, seconds: float) -> None:
        bake = self.mode == OvenMode.Bake
        coupling = 0.03 if bake else 0.06  # the fan stirs the air into the walls and racks
        hysteresis = 2.5 if bake else 1.5
        sensor_lag = 25.0 if bake else 20.0
        heater_rate, air_loss, wall_mass, wall_loss, element_lag = 1.6, 0.0015, 12.0, 0.0035, 60.0

        done = 0.0
        while done < seconds:
            dt = min(0.5, seconds - done)
            if self.setpoint_c is not None:
                sp = self.setpoint_c
                target = sp + self.bias_c(self.mode, sp) + self.adjustment_c
                if self.sensor_c < target - hysteresis:
                    self.heater_on = True
                elif self.sensor_c > target + hysteresis:
                    self.heater_on = False
            else:
                self.heater_on = False

            self.element += ((1.0 if self.heater_on else 0.0) - self.element) * dt / element_lag
            to_wall = coupling * (self.air_c - self.wall_c)
            self.air_c += (
                heater_rate * self.element - to_wall - (25 if self.door_open else 1) * air_loss * (self.air_c - self.ambient_c)
            ) * dt
            self.wall_c += (to_wall - wall_loss * (self.wall_c - self.ambient_c)) / wall_mass * dt
            self.sensor_c += (self.air_c - self.sensor_c) * dt / sensor_lag
            done += 0.5

    def probe_c(self) -> float:
        """What a thermocouple in the middle of the oven reads: the air, plus a little noise."""
        return self.air_c + self._gaussian() * 0.08

    def _gaussian(self) -> float:
        u1 = 1.0 - self._rng.random()
        u2 = self._rng.random()
        return math.sqrt(-2 * math.log(u1)) * math.cos(2 * math.pi * u2)


_CONFIG = MeterConfig("type K, °C (simulated)", True, TempUnit.Celsius, Sensor.thermocouple("K"))


class SimulatedMeter(TemperatureMeter):
    """A pretend thermocouple meter reading a SimulatedOven, on a simulated clock."""

    def __init__(self, oven: SimulatedOven, interval_seconds: float):
        self.oven = oven
        self._interval = interval_seconds
        now = datetime.now(timezone.utc)
        self._clock = now
        self._awake_since = now
        self._off_since: datetime | None = None
        #: Auto power-off, as --sim-apo sets it: off (None) by default.
        self.auto_power_off_after: timedelta | None = None
        #: How long the simulated meter stays off before "someone presses a button".
        self.wakes_after = timedelta(minutes=2)

    @property
    def location(self) -> str:
        return "simulator"

    def connect(self) -> MeterIdentity:
        return MeterIdentity("OvenTracker", "simulated meter", "SIM00001", "0.00")

    def disconnect(self) -> None:
        pass

    def read_config(self) -> MeterConfig:
        return _CONFIG

    @property
    def last_config(self) -> MeterConfig:
        return _CONFIG

    def read(self) -> MeterReading:
        self.oven.step(self._interval)
        self._clock = self._clock + timedelta(seconds=self._interval)
        apo = self.auto_power_off_after
        if self._off_since is None and apo is not None and self._clock - self._awake_since >= apo:
            self._off_since = self._clock
        if self._off_since is not None:
            if self._clock - self._off_since < self.wakes_after:
                raise TimeoutError("The simulated meter has switched itself off.")
            self._off_since = None  # someone pressed a button
            self._awake_since = self._clock
        v = round_digits(self.oven.probe_c(), 1)
        return MeterReading(self._clock, ReadingStatus.Ok, v, fixed(v, 1))

    def read_battery_percent(self) -> float | None:
        return 81.4

    def read_auto_power_off(self) -> AutoPowerOffSetting:
        t = self.auto_power_off_after
        return AutoPowerOffSetting.on(t) if t is not None else AutoPowerOffSetting.OFF

    def set_oven(self, mode: OvenMode, setpoint_c: float | None) -> None:
        self.oven.set(mode, setpoint_c)

    def set_adjustment(self, adjustment_c: float) -> None:
        self.oven.adjustment_c = adjustment_c

    def set_auto_power_off(self, after: timedelta | None) -> None:
        self.auto_power_off_after = after

    def pretend_used(self, setpoint_c: float) -> None:
        self.oven.set(OvenMode.Bake, setpoint_c)
        self.oven.step(3600)
        self.oven.set(OvenMode.Bake, None)
        self.oven.step(120)  # turned off a couple of minutes ago

    def dispose(self) -> None:
        pass


DRIVER_NAME = "simulated"


class SimulatedDriver(MeterDriver):
    """A made-up oven and meter, so the whole app can be tried without either."""

    name = DRIVER_NAME
    description = "a simulated oven and meter"
    resource_help = "nothing (there is only one)"
    is_simulation = True

    _APO = AutoPowerOff(
        timedelta(minutes=10), ["Leave out --sim-apo."], how_to_wake="wait: the simulated meter wakes by itself after 2 minutes"
    )

    @property
    def auto_power_off(self) -> AutoPowerOff:
        """The simulated meter can switch itself off (--sim-apo), and says whether it will."""
        return self._APO

    def open(self, request: MeterRequest) -> ProbeResult:
        meter = SimulatedMeter(SimulatedOven(time.monotonic_ns() & 0x7FFFFFFF), request.interval_seconds)
        return ProbeResult(meter.location, meter.connect(), None, meter)

    def discover(self) -> list[MeterCandidate]:
        return [MeterCandidate("simulator", "Simulated oven and meter", None, True, "Simulated")]

    def probe(self, resource: str) -> ProbeResult:
        return ProbeResult(resource, SimulatedMeter(SimulatedOven(), 1).connect(), None)
