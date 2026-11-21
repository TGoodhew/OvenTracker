"""Remembers the meter that answered last time, and where, so the next run finds it straight away
(Session/AppSettings.cs). The same file as the C# app's: OvenTracker/settings.json in the user's application data
folder (%APPDATA% on Windows, ~/.config on Linux), with the same property names."""

from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, field

from ..netfmt import json_string

_DRIVER_BEFORE_DRIVERS = "u1253b"


def _app_data() -> str:
    """Environment.SpecialFolder.ApplicationData."""
    if sys.platform == "win32":
        return os.environ.get("APPDATA") or os.path.join(os.path.expanduser("~"), "AppData", "Roaming")
    return os.environ.get("XDG_CONFIG_HOME") or os.path.join(os.path.expanduser("~"), ".config")


def file_path() -> str:
    return os.path.join(_app_data(), "OvenTracker", "settings.json")


@dataclass
class AppSettings:
    #: The driver that answered last time, e.g. "u1253b".
    last_meter: str | None = None
    #: Where it answered, e.g. "COM5".
    last_resource: str | None = None
    #: Settings saved before there were drivers only have this: the serial port of the one meter there was.
    last_port: str | None = None
    #: The oven used last time (its profile's key).
    last_oven: str | None = None
    #: Meters ("driver:serial") whose auto power-off you've said is turned off.
    auto_power_off_turned_off: list[str] = field(default_factory=list)
    #: Each driver's --meter-option settings, as last used.
    meter_options: dict[str, dict[str, str]] = field(default_factory=dict)

    def last_resource_for(self, driver: str) -> str | None:
        """Where the given driver's meter was last time, if it was that driver."""
        if self.last_meter is None:
            return self.last_port if driver == _DRIVER_BEFORE_DRIVERS else None
        return self.last_resource if self.last_meter.lower() == driver.lower() else None

    def is_auto_power_off_turned_off(self, meter_key: str) -> bool:
        return any(k.lower() == meter_key.lower() for k in self.auto_power_off_turned_off)

    def remember_auto_power_off_turned_off(self, meter_key: str) -> None:
        if not self.is_auto_power_off_turned_off(meter_key):
            self.auto_power_off_turned_off.append(meter_key)

    def meter_options_for(self, driver: str) -> dict[str, str]:
        return dict(self.meter_options.get(driver, {}))

    def remember_meter_options(self, driver: str, options: dict[str, str]) -> None:
        if not options:
            self.meter_options.pop(driver, None)
        else:
            self.meter_options[driver] = dict(options)

    def remember(self, driver: str, resource: str) -> None:
        self.last_meter = driver
        self.last_resource = resource
        self.last_port = None

    @staticmethod
    def load(path: str | None = None) -> "AppSettings":
        path = path or file_path()
        try:
            if not os.path.isfile(path):
                return AppSettings()
            with open(path, encoding="utf-8-sig") as fh:
                d = json.load(fh)
            if not isinstance(d, dict):
                return AppSettings()

            def s(key: str) -> str | None:
                v = d.get(key)
                return v if isinstance(v, str) else None

            options = d.get("MeterOptions") or {}
            return AppSettings(
                last_meter=s("LastMeter"),
                last_resource=s("LastResource"),
                last_port=s("LastPort"),
                last_oven=s("LastOven"),
                auto_power_off_turned_off=[str(x) for x in (d.get("AutoPowerOffTurnedOff") or [])],
                meter_options={str(k): {str(a): str(b) for a, b in (v or {}).items()} for k, v in options.items()},
            )
        except Exception:
            return AppSettings()

    def to_json(self) -> str:
        """As System.Text.Json writes it by default: compact, PascalCase, nulls left out."""
        parts = []
        for key, value in (
            ("LastMeter", self.last_meter),
            ("LastResource", self.last_resource),
            ("LastPort", self.last_port),
            ("LastOven", self.last_oven),
        ):
            if value is not None:
                parts.append(f"{json_string(key)}:{json_string(value)}")
        parts.append('"AutoPowerOffTurnedOff":[' + ",".join(json_string(k) for k in self.auto_power_off_turned_off) + "]")
        options = ",".join(
            f"{json_string(d)}:{{" + ",".join(f"{json_string(k)}:{json_string(v)}" for k, v in o.items()) + "}"
            for d, o in self.meter_options.items()
        )
        parts.append('"MeterOptions":{' + options + "}")
        return "{" + ",".join(parts) + "}"

    def save(self, path: str | None = None) -> None:
        path = path or file_path()
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write(self.to_json())
        except Exception:
            pass  # not worth failing a run over
