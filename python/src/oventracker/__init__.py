"""OvenTracker in Python: the whole app, with the same commands, screens, report and results as the C# app (the
reference implementation; see spec/ in the repository)."""

from __future__ import annotations

try:
    from importlib.metadata import PackageNotFoundError, version as _version

    try:
        __version__ = _version("oventracker")
    except PackageNotFoundError:
        __version__ = "1.0.0"
except ImportError:  # pragma: no cover
    __version__ = "1.0.0"

__all__ = ["__version__"]
