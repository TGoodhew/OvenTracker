"""Oven profiles (Ovens/OvenProfile.cs): the built-in ones, and any described in a JSON file beside the runs."""

from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, field
from typing import Any

from .jsonio import DateTimeOffset, dumps, opt_float, opt_str_list, parse_enum
from .units import OvenMode

GENERIC_KEY = "generic"


@dataclass(frozen=True)
class MeasuredGain:
    """A gain measured on an oven by characterise: how far one degree of adjustment moves it."""

    gain: float
    half_width: float | None
    measured_on: DateTimeOffset

    def to_json(self) -> dict[str, Any]:
        return {"gain": self.gain, "halfWidth": self.half_width, "measuredOn": self.measured_on}

    @staticmethod
    def from_json(d: dict[str, Any]) -> "MeasuredGain":
        on = d.get("measuredOn")
        return MeasuredGain(
            float(d.get("gain", 0)),
            opt_float(d.get("halfWidth")),
            DateTimeOffset.parse(on) if isinstance(on, str) else DateTimeOffset(0, 0),
        )


@dataclass(frozen=True)
class OvenProfile:
    """What the app knows about a kind of oven: its thermostat adjustment, convection conversion, cycle and gains."""

    key: str
    name: str
    adjust_step_c: float
    adjust_range_c: float
    shared_adjustment: bool = True
    how_to_read: list[str] | None = None
    how_to_change: list[str] | None = None
    convection_conversion_c: float | None = None
    how_to_turn_off_conversion: list[str] | None = None
    typical_cycle_minutes: float | None = None
    gains: dict[OvenMode, MeasuredGain] | None = field(default=None)
    notes: list[str] | None = None
    source: str | None = None

    @property
    def is_generic(self) -> bool:
        return self.key == GENERIC_KEY

    @property
    def default_max_minutes(self) -> float:
        """Two hours, or, when the oven's cycle is known, an hour plus six cycles, if longer."""
        cycle = self.typical_cycle_minutes
        return max(120.0, 60 + 6 * cycle) if cycle is not None else 120.0

    def to_json(self) -> dict[str, Any]:
        return {
            "key": self.key,
            "name": self.name,
            "adjustStepC": self.adjust_step_c,
            "adjustRangeC": self.adjust_range_c,
            "sharedAdjustment": self.shared_adjustment,
            "howToRead": self.how_to_read,
            "howToChange": self.how_to_change,
            "convectionConversionC": self.convection_conversion_c,
            "howToTurnOffConversion": self.how_to_turn_off_conversion,
            "typicalCycleMinutes": self.typical_cycle_minutes,
            "gains": self.gains,
            "notes": self.notes,
            "source": self.source,
            "isGeneric": self.is_generic,
            "defaultMaxMinutes": self.default_max_minutes,
        }

    @staticmethod
    def from_json(d: dict[str, Any] | None) -> "OvenProfile | None":
        if d is None:
            return None
        gains = d.get("gains")
        return OvenProfile(
            key=d.get("key"),  # type: ignore[arg-type]
            name=d.get("name"),  # type: ignore[arg-type]
            adjust_step_c=float(d.get("adjustStepC", 0)),
            adjust_range_c=float(d.get("adjustRangeC", 0)),
            shared_adjustment=bool(d.get("sharedAdjustment", True)),
            how_to_read=opt_str_list(d.get("howToRead")),
            how_to_change=opt_str_list(d.get("howToChange")),
            convection_conversion_c=opt_float(d.get("convectionConversionC")),
            how_to_turn_off_conversion=opt_str_list(d.get("howToTurnOffConversion")),
            typical_cycle_minutes=opt_float(d.get("typicalCycleMinutes")),
            gains=None
            if gains is None
            else {parse_enum(OvenMode, k): MeasuredGain.from_json(v) for k, v in gains.items()},
            notes=opt_str_list(d.get("notes")),
            source=d.get("source"),
        )


GENERIC = OvenProfile(
    GENERIC_KEY,
    "an oven",
    1 / 1.8,
    35 / 1.8,
    True,
    how_to_read=[
        "Look in the oven's manual for its temperature offset (or calibration, or thermostat adjustment) setting, and how to see what it's set to."
    ],
    how_to_change=[
        "Look in the oven's manual for its temperature offset (or calibration, or thermostat adjustment) setting, and how to change it."
    ],
    notes=[
        "Most electronic ovens have an adjustment of up to ±30 or ±35 °F in 1° steps. Describe yours with --oven (docs/adding-an-oven.md) and the app uses its own."
    ],
)

GE_PHS925 = OvenProfile(
    "ge-phs925",
    "GE Profile PHS925ST1SS",
    1 / 1.8,
    35 / 1.8,
    True,
    how_to_read=[
        "Touch BROIL HI/LO and BAKE at the same time until the display shows SF.",
        "Touch BAKE. The display shows the adjustment: positive is hotter, negative cooler.",
        "Touch START to leave without changing it.",
    ],
    how_to_change=[
        "Touch BROIL HI/LO and BAKE at the same time until the display shows SF.",
        "Touch BAKE. The display shows the adjustment set now.",
        "Touch BAKE again to switch between positive (hotter) and negative (cooler).",
        "Type the amount on the number pads, e.g. 1 then 0 for 10.",
        "Check the display shows the right sign, then touch START.",
    ],
    convection_conversion_c=25 / 1.8,
    how_to_turn_off_conversion=[
        "Touch BAKE and BROIL HI/LO at the same time until the display shows SF.",
        "Touch CONVECTION COOK until the display shows CON OFF.",
        "Touch START.",
    ],
    typical_cycle_minutes=20.0,
    notes=[
        "The adjustment affects baking and roasting, not broil or self-clean, and is kept through a power cut.",
        "The manual doesn't say whether convection has an adjustment of its own; the app assumes it shares bake's.",
    ],
    source="GE PHS925ST1SS owner's manual, pp. 15, 17 and 26; the display's sign confirmed on the oven, and its ~20-minute cycle measured, 2026-11-20.",
)

BUILT_IN: list[OvenProfile] = [GENERIC, GE_PHS925]
DEFAULT = GENERIC


def _documents_folder() -> str:
    """Environment.SpecialFolder.MyDocuments (on Windows, wherever Documents has been moved to)."""
    if sys.platform == "win32":
        try:
            import ctypes
            import uuid
            from ctypes import wintypes

            class GUID(ctypes.Structure):
                _fields_ = [
                    ("Data1", wintypes.DWORD),
                    ("Data2", wintypes.WORD),
                    ("Data3", wintypes.WORD),
                    ("Data4", wintypes.BYTE * 8),
                ]

            u = uuid.UUID("{FDD39AD0-238F-46AF-ADB4-6C85480369C7}")  # FOLDERID_Documents
            guid = GUID(u.fields[0], u.fields[1], u.fields[2], (wintypes.BYTE * 8).from_buffer_copy(u.bytes[8:]))
            path = ctypes.c_wchar_p()
            shell32 = ctypes.windll.shell32  # type: ignore[attr-defined]
            if shell32.SHGetKnownFolderPath(ctypes.byref(guid), 0, None, ctypes.byref(path)) == 0:
                result = path.value or ""
                ctypes.windll.ole32.CoTaskMemFree(path)  # type: ignore[attr-defined]
                if result:
                    return result
        except Exception:
            pass
        return os.path.join(os.path.expanduser("~"), "Documents")
    home = os.path.expanduser("~")
    standard = os.path.join(home, "Documents")
    return standard if os.path.isdir(standard) else home


def runs_folder() -> str:
    """Where runs are saved by default: Documents/OvenTracker (RunCommand.RunsFolder)."""
    return os.path.join(_documents_folder(), "OvenTracker")


def folder() -> str:
    """Where oven files live: OvenTracker/ovens beside the runs."""
    return os.path.join(runs_folder(), "ovens")


def _json_files(directory: str) -> list[str]:
    if not os.path.isdir(directory):
        return []
    return [
        os.path.join(directory, n)
        for n in sorted(os.listdir(directory))
        if n.lower().endswith(".json") and os.path.isfile(os.path.join(directory, n))
    ]


def _load_file(path: str) -> OvenProfile | None:
    try:
        with open(path, encoding="utf-8-sig") as fh:
            p = OvenProfile.from_json(json.load(fh))
        return p if p is not None and isinstance(p.key, str) and len(p.key) > 0 else None
    except Exception:
        return None


def load_folder(directory: str) -> list[OvenProfile]:
    return [p for p in (_load_file(f) for f in _json_files(directory)) if p is not None]


def all_profiles(directory: str | None = None) -> list[OvenProfile]:
    """The built-in profiles, with any oven file of the same key in its place, then the other oven files."""
    files = load_folder(directory if directory is not None else folder())
    result = [next((f for f in files if f.key == b.key), b) for b in BUILT_IN]
    result.extend(f for f in files if all(b.key != f.key for b in BUILT_IN))
    return result


def find(key: str | None, directory: str | None = None) -> OvenProfile | None:
    wanted = None if key is None else key.strip()
    for p in all_profiles(directory):
        if wanted is not None and p.key.lower() == wanted.lower():
            return p
    return None


def keys(directory: str | None = None) -> str:
    return ", ".join(p.key for p in all_profiles(directory))


def unreadable(directory: str | None = None) -> list[str]:
    """Files in the oven folder that couldn't be read as a profile."""
    return [f for f in _json_files(directory if directory is not None else folder()) if _load_file(f) is None]


def to_json_text(profile: OvenProfile) -> str:
    return dumps(profile)
