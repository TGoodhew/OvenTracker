"""meters (and its alias ports): lists the meters each driver can see, and optionally asks each one what it is
(Commands/MetersCommand.cs)."""

from __future__ import annotations

from dataclasses import dataclass

from ..meter import driver as drivers
from ..meter.driver import MeterDriver, ProbeResult
from .names import APP_NAME


@dataclass
class MetersSettings:
    probe: bool = False
    all: bool = False
    meter: str | None = None

    def validate(self) -> str | None:
        if self.meter is not None and drivers.find(self.meter) is None:
            return f"--meter must be one of: {drivers.names()}."
        return None


def execute(s: MetersSettings) -> int:
    from rich.text import Text

    from ..ui import console as ui
    from ..ui.widgets import table

    c = ui.get()
    error = s.validate()
    if error is not None:
        c.print(f"[red]Error:[/] {ui.escape(error)}")
        return -1
    chosen = drivers.find(s.meter) if s.meter is not None else None
    driver_list: list[MeterDriver] = [chosen] if chosen is not None else drivers.all_drivers()
    t = table(["Meter", "Where", "Connection", "Device", "Note"] + (["Answers"] if s.probe else []), border_style=None)
    rows = 0
    nothing: list[MeterDriver] = []

    for d in driver_list:
        if d.not_ported:
            nothing.append(d)
            continue
        if ui.live():  # the 121GW's scan takes a few seconds
            with c.status(f"Looking for {ui.escape(d.name)} meters…", spinner="dots"):
                candidates = d.discover()
        else:
            candidates = d.discover()
        if not candidates:
            nothing.append(d)
            continue
        for cand in candidates:
            note = (
                Text("")
                if cand.note is None
                else Text(cand.note, style="green")
                if cand.usable
                else Text(cand.note, style="grey")
            )
            row = [Text(d.name), Text(cand.resource), Text(cand.transport), Text(cand.description), note]
            if s.probe:
                if cand.usable or s.all:
                    result: ProbeResult
                    if ui.live():
                        with c.status(f"Asking {ui.escape(cand.resource)}…", spinner="dots"):
                            result = d.probe(cand.resource)
                    else:
                        c.print(f"Asking {ui.escape(cand.resource)}…")
                        result = d.probe(cand.resource)
                    if result.identity is not None:
                        row.append(
                            Text(str(result.identity), style="green")
                            if result.error is None
                            else Text(f"{result.identity}: {result.error}", style="yellow")
                        )
                    else:
                        row.append(Text(result.error or "", style="grey"))
                else:
                    row.append(Text.from_markup("[grey]not asked (--all asks it)[/]"))
            t.add_row(*row)
            rows += 1

    if rows > 0:
        c.print(t)
    for d in nothing:
        if d.not_ported:
            c.print(f"[yellow]{ui.escape(d.name)}[/]: not in the Python version yet ({ui.escape(d.description)}). The C# app (oventracker) has it.")
        else:
            c.print(f"[yellow]{ui.escape(d.name)}[/]: nothing found ({ui.escape(d.description)}). {ui.escape(d.not_found_help)}")
    c.print(f"[grey]Use one with [bold]{APP_NAME} run --meter <meter> --resource <where>[/].[/]")
    return 1 if len(nothing) == len(driver_list) else 0
