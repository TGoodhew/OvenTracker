"""What a meter is (Meter/Meter.cs): its readings, what it is set to, and the calls the app makes on it."""

from __future__ import annotations

import enum
from dataclasses import dataclass
from datetime import datetime
from typing import TYPE_CHECKING, Protocol, runtime_checkable

from ..probes import Sensor
from ..session.recording import MeterIdentity
from ..units import OvenMode, TempUnit

if TYPE_CHECKING:
    from datetime import timedelta

    from .auto_power_off import AutoPowerOffSetting

__all__ = [
    "ReadingStatus",
    "MeterReading",
    "MeterConfig",
    "MeterIdentity",
    "Sensor",
    "TemperatureMeter",
    "OvenSimulation",
    "MeterException",
]


class ReadingStatus(enum.IntEnum):
    Ok = 0
    Overload = 1
    NotTemperature = 2
    Error = 3


@dataclass(frozen=True)
class MeterReading:
    """One reading. ``time`` is a UTC datetime; ``celsius`` is set when it is a valid temperature; ``raw`` is
    exactly what the meter sent, for the recording."""

    time: datetime
    status: ReadingStatus
    celsius: float | None
    raw: str


@dataclass(frozen=True)
class MeterConfig:
    """What the meter is set to measure, as far as it can say."""

    description: str
    is_temperature: bool
    unit: TempUnit | None = None
    sensor: Sensor | None = None


class MeterException(Exception):
    """Something the meter or its link did wrong, in words for people."""


class TemperatureMeter:
    """A thermometer the app can poll. Calls are synchronous and come from one thread. Only ``location``,
    ``connect``, ``disconnect`` and ``read`` are required: a meter that can't report a battery, beep, say what it
    is set to or report its auto power-off leaves the rest as they are, and the app does without."""

    @property
    def location(self) -> str:
        raise NotImplementedError

    def connect(self) -> MeterIdentity:
        raise NotImplementedError

    def disconnect(self) -> None:
        raise NotImplementedError

    def read(self) -> MeterReading:
        raise NotImplementedError

    def read_config(self) -> MeterConfig | None:
        """Asks the meter what it is set to measure. None if it can't say."""
        return None

    @property
    def last_config(self) -> MeterConfig | None:
        """What the meter last said it was set to, without asking again where it keeps one."""
        return self.read_config()

    def read_battery_percent(self) -> float | None:
        return None

    def read_auto_power_off(self) -> "AutoPowerOffSetting":
        """What the meter's auto power-off is set to, if it can say: unknown by default."""
        from .auto_power_off import AutoPowerOffSetting

        return AutoPowerOffSetting.UNKNOWN

    def beep(self) -> None:
        """Makes the meter beep when a setpoint is done, if it can."""

    def dispose(self) -> None:
        self.disconnect()

    def __enter__(self) -> "TemperatureMeter":
        return self

    def __exit__(self, *exc: object) -> None:
        self.dispose()


@runtime_checkable
class OvenSimulation(Protocol):
    """A meter that drives a simulated oven, so the app can tell it what the oven is set to."""

    def set_oven(self, mode: OvenMode, setpoint_c: float | None) -> None: ...

    def set_adjustment(self, adjustment_c: float) -> None: ...

    def pretend_used(self, setpoint_c: float) -> None: ...

    def set_auto_power_off(self, after: "timedelta | None") -> None: ...
