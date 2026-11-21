"""The meter drivers (Meter/MeterDriver.cs): how the app asks for a meter, and the ones it knows."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Iterable

from .auto_power_off import AutoPowerOff
from .base import MeterIdentity, TemperatureMeter


@dataclass(frozen=True)
class MeterRequest:
    """What the app asks a driver for when it wants a meter.

    ``resource``: where to look, as the user gave it (None to search). ``preferred``: where a meter answered last
    time. ``progress``: told each place the driver tries, for the status line. ``options``: the driver's own
    settings from --meter-option, already checked by ``check_options``."""

    resource: str | None
    preferred: str | None
    interval_seconds: float
    progress: Callable[[str], None] | None = None
    options: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class MeterOption:
    key: str
    values: str
    description: str


@dataclass(frozen=True)
class ProbeResult:
    """The outcome of asking one place for a meter. ``meter`` is the connected meter, when asked to keep it open.
    ``notes``: anything worth telling people on connecting, e.g. a setting that was assumed. Plain text."""

    resource: str
    identity: MeterIdentity | None
    error: str | None
    meter: TemperatureMeter | None = None
    notes: list[str] | None = None


@dataclass(frozen=True)
class MeterCandidate:
    """Something a driver can see, for the meters command."""

    resource: str
    description: str
    note: str | None
    usable: bool
    transport: str = "Serial"


class MeterDriver:
    """Knows one kind of meter: how to find it, how to connect, and what to tell people when it can't. Text that
    a driver returns is plain, not markup."""

    name: str = ""
    description: str = ""
    resource_help: str = ""
    is_simulation: bool = False
    #: Whether this kind of meter runs on a battery. When it runs low mid-setpoint, the app pauses for a battery swap
    #: and carries on with the same setpoint. False (the default) for a mains-powered bench meter.
    has_battery: bool = False
    #: Not in the Python version yet: the C# app has it.
    not_ported: bool = False

    @property
    def auto_power_off(self) -> AutoPowerOff | None:
        return None

    @property
    def options(self) -> list[MeterOption]:
        return []

    def check_options(self, options: dict[str, str]) -> str | None:
        return None if not options else f"The {self.name} meter takes no --meter-option."

    @property
    def not_found_help(self) -> str:
        return ""

    @property
    def not_temperature_help(self) -> str:
        return ""

    def open(self, request: MeterRequest) -> ProbeResult | None:
        raise NotImplementedError

    def discover(self) -> list[MeterCandidate]:
        return []

    def probe(self, resource: str) -> ProbeResult:
        raise NotImplementedError


class NotPortedDriver(MeterDriver):
    """A meter the C# app has and this one doesn't yet. Its name is known, so --meter can say so clearly."""

    not_ported = True

    def __init__(self, name: str, description: str, resource_help: str):
        self.name = name
        self.description = description
        self.resource_help = resource_help

    @property
    def not_ported_message(self) -> str:
        return (
            f"The {self.name} meter ({self.description}) isn't in the Python version yet: use the C# app "
            "(oventracker) for it, or another kind of meter with --meter (u1253b, 34401a or simulated)."
        )

    @property
    def not_found_help(self) -> str:
        return "It isn't in the Python version yet: the C# app (oventracker) has it."

    def check_options(self, options: dict[str, str]) -> str | None:
        return None

    def open(self, request: MeterRequest) -> ProbeResult | None:
        return ProbeResult(request.resource or "", None, self.not_ported_message)

    def probe(self, resource: str) -> ProbeResult:
        return ProbeResult(resource, None, "not in the Python version yet")


DEFAULT_NAME = "u1253b"

_ALL: list[MeterDriver] | None = None


def all_drivers() -> list[MeterDriver]:
    """The meters the app knows, in the C# app's order."""
    global _ALL
    if _ALL is None:
        from .eevblog121gw import Eevblog121GwDriver
        from .hp34401a import Hp34401ADriver
        from .simulated import SimulatedDriver
        from .u1253b import U1253BDriver
        from .ut60bt import Ut60btDriver

        _ALL = [U1253BDriver(), Hp34401ADriver(), Eevblog121GwDriver(), Ut60btDriver(), SimulatedDriver()]
    return _ALL


def find(name: str | None) -> MeterDriver | None:
    if name is None:
        return None
    wanted = name.strip().lower()
    return next((d for d in all_drivers() if d.name.lower() == wanted), None)


def names() -> str:
    return ", ".join(d.name for d in all_drivers())


def parse_options(given: Iterable[str] | None) -> dict[str, str] | None:
    """Reads --meter-option values ("cj=22C", or several separated by commas). None if one isn't key=value."""
    options: dict[str, str] = {}
    for g in given or []:
        for item in (p.strip() for p in g.split(",")):
            if not item:
                continue
            eq = item.find("=")
            if eq <= 0 or eq == len(item) - 1:
                return None
            options[item[:eq].strip().lower()] = item[eq + 1 :].strip()
    return options
