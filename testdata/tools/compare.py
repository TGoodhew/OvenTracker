#!/usr/bin/env python3
"""Compares two analyze output folders within the tolerances in spec/testdata.md.

    python testdata/tools/compare.py EXPECTED_DIR ACTUAL_DIR

Exits 0 when they match, and 1 with a list of the differences. Importable too:
``compare_dirs(expected, actual) -> list[str]`` (empty when they match). Standard library only.

The rules:

- The same set of files.
- JSON files: the same keys and structure. Strings, booleans and nulls are exactly equal, and so are integers
  (1 and 1.0 are the same number: C# writes an integer-valued double as 1). Other numbers are equal within
  1e-6 absolute or 1e-9 relative, whichever is looser.
- Markdown (and any other text) files: equal line by line once line endings are normalised. A line may differ
  only if its text other than numbers is identical, each number that differs is one unit apart in its last
  shown digit (a value rounded either side of a boundary), and the JSON files matched.
"""

from __future__ import annotations

import json
import math
import os
import re
import sys
from decimal import Decimal
from typing import Any

ABS_TOL = 1e-6
REL_TOL = 1e-9

__all__ = ["compare_dirs", "compare_json", "compare_text", "main"]


# JSON ----------------------------------------------------------------------------------------------


class _Int(int):
    """An integer as written in the file (no point, no exponent)."""


def _load_json(path: str) -> Any:
    with open(path, encoding="utf-8-sig") as fh:
        return json.load(fh, parse_int=_Int)


def _is_number(v: Any) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _numbers_equal(a: float | int, b: float | int) -> bool:
    if isinstance(a, _Int) and isinstance(b, _Int):
        return int(a) == int(b)
    fa, fb = float(a), float(b)
    if fa == fb:
        return True
    if math.isnan(fa) or math.isnan(fb) or math.isinf(fa) or math.isinf(fb):
        return False
    return abs(fa - fb) <= max(ABS_TOL, REL_TOL * max(abs(fa), abs(fb)))


def compare_json(expected: Any, actual: Any, path: str = "$") -> list[str]:
    """The differences between two parsed JSON documents, each as 'path: what'."""
    diffs: list[str] = []
    if isinstance(expected, dict) and isinstance(actual, dict):
        for k in expected:
            if k not in actual:
                diffs.append(f"{path}.{k}: missing (expected {_show(expected[k])})")
        for k in actual:
            if k not in expected:
                diffs.append(f"{path}.{k}: not expected (got {_show(actual[k])})")
        for k in expected:
            if k in actual:
                diffs.extend(compare_json(expected[k], actual[k], f"{path}.{k}"))
    elif isinstance(expected, list) and isinstance(actual, list):
        if len(expected) != len(actual):
            diffs.append(f"{path}: {len(expected)} items expected, {len(actual)} found")
        for i, (e, a) in enumerate(zip(expected, actual)):
            diffs.extend(compare_json(e, a, f"{path}[{i}]"))
    elif _is_number(expected) and _is_number(actual):
        if not _numbers_equal(expected, actual):
            diffs.append(f"{path}: expected {_show(expected)}, got {_show(actual)}")
    elif type(expected) is not type(actual) or expected != actual:
        # Strings, booleans, nulls, and a number against anything else.
        diffs.append(f"{path}: expected {_show(expected)}, got {_show(actual)}")
    return diffs


def _show(v: Any) -> str:
    text = json.dumps(v, ensure_ascii=False)
    return text if len(text) <= 80 else text[:77] + "..."


# Text ----------------------------------------------------------------------------------------------

# A number as the report shows it, with its sign (+, − U+2212, ±, or a hyphen not inside a word or date).
_NUMBER = re.compile(r"(?<![\w.])[+\-−±]?\d+(?:\.\d+)?|\d+(?:\.\d+)?")


def _split(line: str) -> tuple[list[str], list[str]]:
    """The line's text between numbers, and its numbers."""
    texts: list[str] = []
    numbers: list[str] = []
    pos = 0
    for m in _NUMBER.finditer(line):
        texts.append(line[pos : m.start()])
        numbers.append(m.group(0))
        pos = m.end()
    texts.append(line[pos:])
    return texts, numbers


def _value(token: str) -> tuple[Decimal, int]:
    """A shown number's value and how many decimals it shows."""
    sign = -1 if token[0] in "-−" else 1
    digits = token.lstrip("+-−±")
    decimals = len(digits.split(".")[1]) if "." in digits else 0
    return Decimal(digits) * sign, decimals


def _one_unit_apart(a: str, b: str) -> bool:
    va, da = _value(a)
    vb, db = _value(b)
    if da != db:
        return False
    return abs(va - vb) == Decimal(1).scaleb(-da)


def compare_text(expected: str, actual: str, allow_last_digit: bool = True) -> list[str]:
    """The differences between two text files, line by line once line endings are normalised."""
    e_lines = expected.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    a_lines = actual.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    diffs: list[str] = []
    if len(e_lines) != len(a_lines):
        diffs.append(f"{len(e_lines)} lines expected, {len(a_lines)} found")
    for i, (e, a) in enumerate(zip(e_lines, a_lines), start=1):
        if e == a:
            continue
        et, en = _split(e)
        at, an = _split(a)
        if allow_last_digit and et == at and len(en) == len(an):
            bad = [(x, y) for x, y in zip(en, an) if x != y and not _one_unit_apart(x, y)]
            if not bad:
                continue
        diffs.append(f"line {i}:\n    expected: {e}\n    actual:   {a}")
    return diffs


def _read_text(path: str) -> str:
    with open(path, encoding="utf-8-sig", newline="") as fh:
        return fh.read()


# Folders -------------------------------------------------------------------------------------------


def compare_dirs(expected: str, actual: str) -> list[str]:
    """The differences between two output folders, as readable lines; empty when they match."""
    diffs: list[str] = []
    if not os.path.isdir(expected):
        return [f"no such folder: {expected}"]
    if not os.path.isdir(actual):
        return [f"no such folder: {actual}"]
    e_files = sorted(n for n in os.listdir(expected) if os.path.isfile(os.path.join(expected, n)))
    a_files = sorted(n for n in os.listdir(actual) if os.path.isfile(os.path.join(actual, n)))
    for n in e_files:
        if n not in a_files:
            diffs.append(f"{n}: missing")
    for n in a_files:
        if n not in e_files:
            diffs.append(f"{n}: not expected")
    common = [n for n in e_files if n in a_files]

    json_ok = True
    for n in common:
        if n.lower().endswith(".json"):
            try:
                d = compare_json(_load_json(os.path.join(expected, n)), _load_json(os.path.join(actual, n)))
            except ValueError as ex:
                d = [f"not JSON: {ex}"]
            if d:
                json_ok = False
                diffs.extend(f"{n}: {x}" for x in d)
    for n in common:
        if not n.lower().endswith(".json"):
            d = compare_text(_read_text(os.path.join(expected, n)), _read_text(os.path.join(actual, n)), allow_last_digit=json_ok)
            diffs.extend(f"{n}: {x}" for x in d)
    return diffs


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 2:
        print("usage: python testdata/tools/compare.py EXPECTED_DIR ACTUAL_DIR", file=sys.stderr)
        return 2
    diffs = compare_dirs(args[0], args[1])
    if diffs:
        print(f"{len(diffs)} difference{'s' if len(diffs) != 1 else ''} between {args[0]} and {args[1]}:")
        for d in diffs:
            print(f"- {d}")
        return 1
    print(f"{args[1]} matches {args[0]}.")
    return 0


if __name__ == "__main__":
    for _stream in (sys.stdout, sys.stderr):
        try:
            _stream.reconfigure(errors="replace")  # type: ignore[union-attr]
        except Exception:
            pass
    sys.exit(main())
