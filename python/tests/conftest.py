"""Shared paths for the tests: the repository's testdata/, wherever pytest is run from."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

PYTHON_DIR = Path(__file__).resolve().parents[1]
REPO = PYTHON_DIR.parent
TESTDATA = REPO / "testdata"
SRC = PYTHON_DIR / "src"

# Run from a clone without installing: the package is in python/src.
if str(SRC) not in sys.path:
    try:
        import oventracker  # noqa: F401
    except ImportError:
        sys.path.insert(0, str(SRC))


def load_compare():
    """testdata/tools/compare.py, imported from its file."""
    path = TESTDATA / "tools" / "compare.py"
    spec = importlib.util.spec_from_file_location("testdata_compare", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules.setdefault("testdata_compare", module)
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="session")
def compare():
    return load_compare()
