"""Spectre.Console's widgets, drawn with Rich exactly as Spectre draws them: FigletText, Panel (its header on the
top border, without the spaces Rich puts round a title), a left-justified Rule, Table and Grid with Spectre's
borders and styles, Columns, and a live view that crops from the top as Spectre's does."""

from __future__ import annotations

import re
from typing import Any, Iterable, Sequence

from rich import box
from rich.console import Console, ConsoleOptions, Group, RenderableType, RenderResult
from rich.measure import Measurement
from rich.segment import Segment
from rich.style import Style
from rich.table import Table
from rich.text import Text

from .figlet_font import GLYPHS, HEIGHT


def markup(text: str) -> Text:
    """new Markup(text)."""
    return Text.from_markup(text)


def rows(*items: RenderableType) -> Group:
    """new Rows(...)."""
    return Group(*items)


# FigletText -----------------------------------------------------------------------------------------


def _glyph_width(ch: str) -> int:
    return len(GLYPHS[ch][0])


def _word_width(word: str) -> int:
    return sum(_glyph_width(c) for c in word if c in GLYPHS)


def figlet_rows(text: str, max_width: int) -> list[list[str]]:
    """How Spectre's FigletText breaks text into rows: whole words (and the spaces between) while they fit,
    a word on a row of its own when it doesn't, and a word wider than the row character by character."""
    result: list[list[str]] = []
    line: list[str] = []
    total = 0
    for word in re.findall(r"\S+|\s+", text):
        chars = [c for c in word if c in GLYPHS]
        width = _word_width(word)
        if width + total < max_width:
            line.extend(chars)
            total += width
        elif width < max_width:
            result.append(line)
            line = list(chars)
            total = width
        else:
            for c in chars:
                w = _glyph_width(c)
                if total + w > max_width:
                    result.append(line)
                    line = []
                    total = 0
                line.append(c)
                total += w
    if line:
        result.append(line)
    return result


def figlet_lines(text: str, max_width: int) -> list[str]:
    out = []
    for row in figlet_rows(text, max_width):
        for k in range(HEIGHT):
            out.append("".join(GLYPHS[c][k] for c in row))
    return out


class FigletText:
    """new FigletText(text).Color(color): large text in FIGlet's standard font."""

    def __init__(self, text: str, style: str = ""):
        self.text = text
        self.style = style

    def __rich_console__(self, console: Console, options: ConsoleOptions) -> RenderResult:
        style = console.get_style(self.style) if self.style else Style()
        for line in figlet_lines(self.text, options.max_width):
            yield Segment(line, style)
            yield Segment.line()

    def __rich_measure__(self, console: Console, options: ConsoleOptions) -> Measurement:
        width = _word_width(self.text)
        return Measurement(min(width, options.max_width), min(width, options.max_width))


# Panel ----------------------------------------------------------------------------------------------


class Panel:
    """new Panel(content).Header(header).Border(...).BorderColor(...)[.Expand()]: a box with one space either side
    of its content, and the header (markup, in the border's colour) straight after the top-left corner."""

    def __init__(
        self,
        renderable: RenderableType,
        header: str | None = None,
        border: box.Box = box.ROUNDED,
        border_style: str = "",
        expand: bool = False,
    ):
        self.renderable = renderable
        self.header = header
        self.box = border
        self.border_style = border_style
        self.expand = expand

    def __rich_console__(self, console: Console, options: ConsoleOptions) -> RenderResult:
        max_width = options.max_width
        if self.expand:
            width = max_width
        else:
            width = min(max_width, Measurement.get(console, options.update(width=max_width - 4), self.renderable).maximum + 4)
        inner = max(0, width - 4)
        border = console.get_style(self.border_style) if self.border_style else Style()
        b = self.box
        lines = console.render_lines(self.renderable, options.update(width=inner, height=None), pad=True)

        # The top border, with the header.
        yield Segment(b.top_left, border)
        used = 1
        if self.header is not None and width > 4:
            title = Text.from_markup(self.header)
            title.plain = title.plain.replace("\n", " ")
            title.stylize_before(border)
            title.truncate(max(0, width - 3), overflow="ellipsis")
            yield Segment(b.top, border)
            used += 1
            for seg in title.render(console):
                yield seg
            used += title.cell_len
        yield Segment(b.top * max(0, width - used - 1), border)
        yield Segment(b.top_right, border)
        yield Segment.line()

        for line in lines:
            yield Segment(b.mid_left, border)
            yield Segment(" ")
            yield from line
            yield Segment(" ")
            yield Segment(b.mid_right, border)
            yield Segment.line()

        yield Segment(b.bottom_left + b.bottom * max(0, width - 2) + b.bottom_right, border)
        yield Segment.line()

    def __rich_measure__(self, console: Console, options: ConsoleOptions) -> Measurement:
        if self.expand:
            return Measurement(options.max_width, options.max_width)
        m = Measurement.get(console, options.update(width=options.max_width - 4), self.renderable)
        return Measurement(min(m.minimum + 4, options.max_width), min(m.maximum + 4, options.max_width))


# Rule -----------------------------------------------------------------------------------------------


class Rule:
    """new Rule(title).LeftJustified(): "── title ──────…" across the width."""

    def __init__(self, title: str):
        self.title = title

    def __rich_console__(self, console: Console, options: ConsoleOptions) -> RenderResult:
        width = options.max_width
        title = Text.from_markup(self.title)
        title.plain = title.plain.replace("\n", " ")
        if width < 6 + title.cell_len:
            title.truncate(max(0, width - 6), overflow="ellipsis")
        yield Segment("── ")
        yield from title.render(console)
        yield Segment(" " + "─" * max(0, width - 4 - title.cell_len))
        yield Segment.line()


# Tables ---------------------------------------------------------------------------------------------


def _spectre_ratio_reduce(total: int, ratios: list[int], maximums: list[int], values: list[int]) -> list[int]:
    """Spectre's Ratio.Reduce, which Rich's table code is otherwise the same as: Spectre divides in integers
    before rounding, so a narrow window shares out the cuts to columns a little differently. Installed into
    rich.table, so tables squeeze into a narrow window the way the C# app's do."""
    ratios = [ratio if mx else 0 for ratio, mx in zip(ratios, maximums)]
    total_ratio = sum(ratios)
    if total_ratio <= 0:
        return values[:]
    total_remaining = total
    result = []
    for ratio, maximum, value in zip(ratios, maximums, values):
        if ratio != 0 and total_ratio > 0:
            distributed = min(maximum, (ratio * total_remaining) // total_ratio)
            result.append(value - distributed)
            total_remaining -= distributed
            total_ratio -= ratio
        else:
            result.append(value)
    return result


import rich.table as _rich_table  # noqa: E402

# Only if Rich still has it: otherwise tables use Rich's own, which differs only in narrow windows.
if hasattr(_rich_table, "ratio_reduce"):
    _rich_table.ratio_reduce = _spectre_ratio_reduce  # type: ignore[attr-defined]


def table(
    headers: Sequence[str],
    title: str | None = None,
    border: box.Box = box.ROUNDED,
    border_style: str | None = "grey35",
    no_wrap: bool = False,
    show_header: bool = True,
) -> Table:
    """new Table().Border(...).BorderColor(...)[.Title(...)].AddColumn(...): the headers plain and at the top of
    their cells (Rich puts a header that wraps at the bottom), the title above in silver. The headers are the
    table's first row, ended by the line under it. ``no_wrap``: every column NoWrap."""
    t = Table(
        title=title,
        title_style="silver",
        box=border,
        safe_box=False,
        border_style=border_style or "",
        show_header=False,
        show_edge=True,
        pad_edge=True,
        padding=(0, 1),
        expand=False,
        highlight=False,
    )
    for _ in headers:
        t.add_column(no_wrap=no_wrap, overflow="fold" if no_wrap else "ellipsis")
    if show_header:
        # A header that doesn't fit wraps at spaces, and a word too long for it ends in an ellipsis.
        t.add_row(*(Text(h, no_wrap=False, overflow="ellipsis") for h in headers), end_section=True)
    return t


def cell(text: str, style: str = "", fold: bool = False) -> Text:
    """A table cell of plain text. ``fold``: in a NoWrap column, which Spectre still breaks when the table can't
    fit the window, at spaces and then anywhere."""
    if fold:
        return Text(text, style=style, no_wrap=False, overflow="fold")
    return Text(text, style=style)


def simple_table() -> Table:
    """new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35).HideHeaders(): two columns, no lines."""
    return table(["", ""], border=box.SIMPLE, show_header=False)


def grid(no_wrap_first: bool = False) -> Table:
    """new Grid().AddColumn().AddColumn(): two spaces between the columns."""
    g = Table.grid(padding=(0, 2, 0, 0), pad_edge=False)
    g.add_column(no_wrap=no_wrap_first)
    g.add_column()
    return g


class columns:
    """new Columns(items) { Expand = true }: the items side by side when they fit (each column as wide as its
    widest item, one space between), otherwise in fewer columns, down to one under the other, as Spectre's
    Columns decides; the spare width is shared out."""

    def __init__(self, *items: RenderableType):
        self.items = list(items)

    def _count(self, console: Console, options: ConsoleOptions) -> int:
        max_width = options.max_width
        widths_of = [Measurement.get(console, options, item).maximum for item in self.items]
        count = len(self.items)
        padding = 1
        while count > 1:
            widths: dict[int, int] = {}
            index = 0
            exceeded = False
            for w in widths_of:
                widths[index] = max(widths.get(index, 0), w)
                if sum(widths.values()) + padding * (len(widths) - 1) > max_width:
                    count = len(widths) - 1
                    exceeded = True
                    break
                index = (index + 1) % count
            if not exceeded:
                break
        return max(1, count)

    def __rich_console__(self, console: Console, options: ConsoleOptions) -> RenderResult:
        count = self._count(console, options)
        t = Table.grid(padding=(0, 1, 0, 0), pad_edge=False, expand=True)
        for _ in range(count):
            t.add_column(no_wrap=True)
        for i in range(0, len(self.items), count):
            row = self.items[i : i + count]
            t.add_row(*row, *([""] * (count - len(row))))
        yield t


# Live -----------------------------------------------------------------------------------------------


class CropTop:
    """Spectre's live display with VerticalOverflow.Crop and VerticalOverflowCropping.Top: when the frame is taller
    than the window, its top is cut off, so the latest lines stay in view."""

    def __init__(self, renderable: RenderableType):
        self.renderable = renderable

    def __rich_console__(self, console: Console, options: ConsoleOptions) -> RenderResult:
        lines = console.render_lines(self.renderable, options.update(height=None), pad=False)
        height = console.size.height
        if height > 0 and len(lines) > height:
            lines = lines[len(lines) - height :]
        for i, line in enumerate(lines):
            yield from line
            if i < len(lines) - 1:
                yield Segment.line()


def plain_print(console: Console, text: str) -> None:
    """AnsiConsole.WriteLine(text): plain text, no markup."""
    console.print(text, markup=False, highlight=False, emoji=False)


def write(console: Console, renderable: Any) -> None:
    """AnsiConsole.Write(renderable)."""
    console.print(renderable)
