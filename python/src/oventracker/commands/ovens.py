"""ovens: lists the ovens the app knows (built in, and described in files), or shows one in full
(Commands/OvensCommand.cs)."""

from __future__ import annotations

from dataclasses import dataclass

from .. import ovens
from ..netfmt import fixed
from ..ovens import OvenProfile
from ..units import TempUnit, UnitFormatter, mode_name, parse_unit
from .names import APP_NAME


@dataclass
class OvensSettings:
    show: str | None = None
    unit: str | None = None

    def validate(self) -> str | None:
        if self.unit is not None and parse_unit(self.unit) is None:
            return "--unit must be F or C."
        if self.show is not None and ovens.find(self.show) is None:
            return f'No oven "{self.show}". Known: {ovens.keys()}.'
        return None


def execute(s: OvensSettings) -> int:
    from rich.text import Text

    from ..ui import console as ui
    from ..ui.widgets import table

    c = ui.get()
    error = s.validate()
    if error is not None:
        c.print(f"[red]Error:[/] {ui.escape(error)}")
        return -1
    f = UnitFormatter(parse_unit(s.unit) or TempUnit.Fahrenheit)
    if s.show is not None:
        _show(ovens.find(s.show), f)  # type: ignore[arg-type]
        return 0

    t = table(["--oven", "Oven", "Adjustment", "Cycle", "Convection conversion", "Measured gain"], border_style=None)
    for p in ovens.all_profiles():
        t.add_row(
            Text(p.key),
            Text(p.name),
            Text(f"±{f.span(p.adjust_range_c, 0)} in {f.span(p.adjust_step_c, 0)} steps{', shared' if p.shared_adjustment else ', per mode'}"),
            Text(f"{fixed(p.typical_cycle_minutes, 0)} min") if p.typical_cycle_minutes is not None else Text.from_markup("[grey]—[/]"),
            Text(f.D(-p.convection_conversion_c, 0)) if p.convection_conversion_c is not None else Text.from_markup("[grey]—[/]"),
            Text(", ".join(f"{mode_name(k)} {fixed(v.gain, 2)}" for k, v in p.gains.items()))
            if p.gains
            else Text.from_markup("[grey]assumed 1:1[/]"),
        )
    c.print(t)
    c.print(
        f"[grey]Your own ovens: a JSON file each in {ui.escape(ovens.folder())} (docs/adding-an-oven.md). "
        f"{APP_NAME} ovens --show <key> for the details.[/]"
    )
    for bad in ovens.unreadable():
        c.print(f"[yellow]Couldn't read {ui.escape(bad)} as an oven profile.[/]")
    return 0


def _show(p: OvenProfile, f: UnitFormatter) -> None:
    from rich.text import Text

    from ..ui import console as ui
    from ..ui.widgets import Panel, grid

    g = grid(no_wrap_first=True)

    def row(label: str, value: Text) -> None:
        g.add_row(Text(label, style="grey"), value)

    def steps(items: list[str] | None) -> Text:
        if items:
            return Text("\n".join(f"{i + 1}. {x}" for i, x in enumerate(items)))
        return Text.from_markup("[grey]—[/]")

    row("--oven", Text(p.key))
    row(
        "Adjustment",
        Text(
            f"±{f.span(p.adjust_range_c, 0)} in {f.span(p.adjust_step_c, 0)} steps, "
            f"{'one for bake and convection' if p.shared_adjustment else 'one for each mode'}"
        ),
    )
    row("To read it", steps(p.how_to_read))
    row("To change it", steps(p.how_to_change))
    row(
        "Convection conversion",
        Text(f"lowers convection settings by about {f.span(p.convection_conversion_c, 0)}")
        if p.convection_conversion_c is not None
        else Text.from_markup("[grey]none known[/]"),
    )
    if p.how_to_turn_off_conversion:
        row("To turn it off", steps(p.how_to_turn_off_conversion))
    row(
        "Thermostat cycle",
        Text(f"about {fixed(p.typical_cycle_minutes, 0)} min, so up to {fixed(p.default_max_minutes, 0)} min per setpoint")
        if p.typical_cycle_minutes is not None
        else Text.from_markup("[grey]unknown: up to 120 min per setpoint[/]"),
    )
    if p.gains:
        lines = []
        for k, v in p.gains.items():
            hw = f" ±{fixed(v.half_width, 2)}" if v.half_width is not None else ""
            lines.append(f"{mode_name(k)}: {fixed(v.gain, 2)}{hw} ({v.measured_on.to_local():%Y-%m-%d})")
        row("Measured gain", Text("\n".join(lines)))
    else:
        row("Measured gain", Text.from_markup(f"[grey]not measured: assumed 1:1 ({APP_NAME} characterise measures it)[/]"))
    if p.notes:
        row("Notes", Text("\n".join(p.notes)))
    if p.source is not None:
        row("Source", Text(p.source))
    ui.get().print(Panel(g, header=f"[bold]{ui.escape(p.name)}[/]"))
