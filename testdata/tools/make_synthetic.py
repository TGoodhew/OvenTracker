"""Makes the synthetic recordings in testdata/runs (spec/testdata.md). Deterministic: run it again and the files are the same.

synthetic-no-cycle      An oven that heats and then holds steady with no visible thermostat cycle (as some
                        well-damped or PID-controlled ovens do), ended without an end event (the app closed).
synthetic-manual-finish sim-bake-c cut short 60 % of the way through its first setpoint, where the user
                        finished it by hand (end:Manual), and the rest of the run was abandoned.

Usage: python testdata/tools/make_synthetic.py
"""
import json
import math
import os
from datetime import datetime, timedelta, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNS = os.path.join(ROOT, "runs")

OPTIONS = {
    "precisionC": 1.1111111111111112, "minCycles": 4, "minBlocks": 5, "blockMinutes": 3, "minHysteresisC": 1,
    "smoothingSeconds": 5, "maxPointMinutes": 120, "noCycleMinutes": 15, "flatWindowMinutes": 10,
    "flatSlopeCPerMinute": 0.15, "driftHorizonMinutes": 30, "defaultCycleMinutes": 6,
}
HEADER = "time_utc,point,mode,setpoint_c,elapsed_s,status,temp_c,raw,event"


def stamp(t: datetime) -> str:
    return t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond:06d}0Z"


def write(name: str, session: dict, lines: list[str]) -> None:
    folder = os.path.join(RUNS, name)
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, "session.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(session, f, indent=2)
        f.write("\n")
    with open(os.path.join(folder, "readings.csv"), "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER + "\n")
        for line in lines:
            f.write(line + "\n")


def no_cycle() -> None:
    sp = (350 - 32) / 1.8
    start = datetime(2026, 1, 10, 18, 0, 0, tzinfo=timezone.utc)
    session = {
        "appVersion": "1.0.0", "started": "2026-01-10T10:00:00-08:00", "meter": "synthetic", "location": "none",
        "unit": "Fahrenheit", "toleranceC": 15 / 1.8, "options": OPTIONS,
        "plan": [{"index": 1, "mode": "Bake", "setpointC": sp}],
        "driver": "synthetic", "adjustmentC": 0, "ambientC": 21.0,
    }
    lines = [f"{stamp(start)},1,Bake,{sp:.3f},0.0,Event,,,start"]
    seed = 12345
    for i in range(0, 75 * 60):  # 75 minutes, one reading a second
        t = start + timedelta(seconds=i)
        # Heats with a first-order response to 2.5 °C above the setpoint, then holds it with ±0.06 °C of noise.
        c = 21 + (sp + 2.5 - 21) * (1 - math.exp(-i / 420))
        seed = (seed * 1103515245 + 12345) % 2**31
        c += (seed / 2**31 - 0.5) * 0.12
        lines.append(f"{stamp(t)},1,Bake,{sp:.3f},{i:.1f},Ok,{c:.2f},{c:+.5E},")
    write("synthetic-no-cycle", session, lines)


def manual_finish() -> None:
    src = os.path.join(RUNS, "sim-bake-c")
    with open(os.path.join(src, "session.json"), encoding="utf-8") as f:
        session = json.load(f)
    with open(os.path.join(src, "readings.csv"), encoding="utf-8") as f:
        rows = [line.rstrip("\n") for line in f][1:]
    # Cut at 60 % of the first setpoint's time: part-way through measuring it.
    first = [float(r.split(",")[4]) for r in rows if r.split(",")[1] == "1" and r.split(",")[5] == "Ok"]
    at = first[-1] * 0.6
    out, cut = [], None
    for row in rows:
        f = row.split(",")
        if f[1] == "1" and f[5] == "Ok" and float(f[4] or 0) >= at:
            cut = f
            break
        if f[1] not in ("0", "1"):
            break
        if f[5] == "Event" and f[8].startswith("end:"):
            break
        out.append(row)
    assert cut is not None, "sim-bake-c has no first setpoint"
    out.append(",".join([cut[0], "1", cut[2], cut[3], cut[4], "Event", "", "", "end:Manual"]))
    write("synthetic-manual-finish", session, out)


if __name__ == "__main__":
    no_cycle()
    manual_finish()
    print("ok")
