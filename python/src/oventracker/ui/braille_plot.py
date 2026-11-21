"""A line chart drawn with Unicode braille characters (Ui/BraillePlot.cs): each character cell is a 2 × 4 grid of
dots. Temperature in blue, the setpoint dotted in yellow, the tolerance band faint."""

from __future__ import annotations

from typing import Sequence

from rich.markup import escape

from ..netfmt import clamp, fixed
from ..units import UnitFormatter

# Dot bit for (x, y) within a cell, x 0..1, y 0..3 from the top.
_BITS = ((0x01, 0x02, 0x04, 0x40), (0x08, 0x10, 0x20, 0x80))


def markup(
    samples: Sequence[object],
    t0: float,
    t1: float,
    setpoint_c: float,
    tolerance_c: float,
    f: UnitFormatter,
    width_chars: int,
    height_rows: int,
) -> str:
    """The chart as markup. ``samples`` have ``t`` (seconds) and ``c`` (°C), or are (t, c) pairs."""
    label_width = 7
    cols = max(10, width_chars - label_width - 1)
    rows = max(3, height_rows)
    pw, ph = cols * 2, rows * 4

    def tc(s: object) -> tuple[float, float]:
        if isinstance(s, tuple):
            return s[0], s[1]
        return s.t, s.c  # type: ignore[attr-defined]

    lo, hi = setpoint_c - tolerance_c, setpoint_c + tolerance_c
    visible = [p for p in (tc(s) for s in samples) if t0 <= p[0] <= t1]
    for _, c in visible:
        lo = min(lo, c)
        hi = max(hi, c)
    pad = max(1.0, (hi - lo) * 0.05)
    lo -= pad
    hi += pad
    if t1 <= t0:
        t1 = t0 + 60

    def y_of(c: float) -> int:
        return int(clamp(round((hi - c) / (hi - lo) * (ph - 1)), 0, ph - 1))

    temp = [[0] * rows for _ in range(cols)]
    sets = [[0] * rows for _ in range(cols)]
    band = [[0] * rows for _ in range(cols)]

    def dot(layer: list[list[int]], x: int, y: int) -> None:
        layer[x // 2][y // 4] |= _BITS[x % 2][y % 4]

    sy, by0, by1 = y_of(setpoint_c), y_of(setpoint_c + tolerance_c), y_of(setpoint_c - tolerance_c)
    for x in range(pw):
        if x % 2 == 0:
            dot(sets, x, sy)
        if x % 6 == 0:
            dot(band, x, by0)
            dot(band, x, by1)

    # Per pixel column, draw from the lowest to the highest reading in it, joined to the column before.
    prev_y: int | None = None
    i = 0
    x = 0
    while x < pw and i < len(visible):
        col_end = t0 + (x + 1) * (t1 - t0) / pw
        y_min: int | None = None
        y_max: int | None = None
        while i < len(visible) and (visible[i][0] <= col_end or x == pw - 1):
            y = y_of(visible[i][1])
            y_min = y if y_min is None else min(y_min, y)
            y_max = y if y_max is None else max(y_max, y)
            i += 1
        if y_min is not None and y_max is not None:
            a, b = y_min, y_max
            if prev_y is not None:
                a, b = min(a, prev_y), max(b, prev_y)
            for y in range(a, b + 1):
                dot(temp, x, y)
            prev_y = (y_min + y_max) // 2
        x += 1

    out: list[str] = []
    for r in range(rows):
        if r == 0 or r == rows - 1 or r == rows // 2:
            label = fixed(f.to_display(hi - (r * 4 + 1.5) / (ph - 1) * (hi - lo)), 0).rjust(label_width - 1) + " "
        else:
            label = " " * label_width
        out.append("[grey]" + label + "┤[/]")
        style: str | None = None
        run: list[str] = []
        for c in range(cols):
            if temp[c][r] != 0:
                cell_style, bits = "deepskyblue1", temp[c][r] | sets[c][r]
            elif sets[c][r] != 0:
                cell_style, bits = "yellow", sets[c][r]
            elif band[c][r] != 0:
                cell_style, bits = "grey30", band[c][r]
            else:
                cell_style, bits = None, 0
            if cell_style != style:
                _flush(out, run, style)
                style = cell_style
            run.append(" " if bits == 0 else chr(0x2800 + bits))
        _flush(out, run, style)
        out.append("\n")
    left = f"{fixed(t0 / 60, 0)} min"
    right = f"{fixed(t1 / 60, 0)} min"
    out.append("[grey]" + " " * label_width + "└" + "─" * cols + "[/]\n")
    out.append("[grey]" + " " * (label_width + 1) + left + " " * max(1, cols - len(left) - len(right)) + right + "[/]")
    return "".join(out)


def _flush(out: list[str], run: list[str], style: str | None) -> None:
    if not run:
        return
    text = escape("".join(run))
    out.append(text if style is None else f"[{style}]{text}[/]")
    run.clear()
