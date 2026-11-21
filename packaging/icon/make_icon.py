"""Draws OvenTracker's icon: an oven with its door window glowing, and a thermometer beside it, on a rounded orange
square. One description of the shapes gives both the SVG source (oventracker.svg) and the bitmaps: PNGs at the
sizes Linux desktops use, and a multi-size oventracker.ico for Windows (the program and its installer).

Small sizes (32 px and under) leave out the knobs and the handle, so the oven and thermometer still read at 16 px.

Run from anywhere, with Pillow installed:  python packaging/icon/make_icon.py
It rewrites the files beside it; they are committed, so building the app doesn't need Pillow."""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
CANVAS = 256

BACKGROUND = "#D9480F"
OVEN = "#F1F3F5"
PANEL_LINE = "#ADB5BD"
KNOB = "#495057"
WINDOW = "#212529"
GLOW = "#FF922B"
HANDLE = "#868E96"
OUTLINE = "#212529"
TUBE = "#FFFFFF"
MERCURY = "#E03131"


def shapes(detailed: bool) -> list[tuple]:
    """The icon on a 256-unit canvas, back to front: ("rect", x0, y0, x1, y1, radius, fill) or
    ("circle", cx, cy, r, fill)."""
    s: list[tuple] = [
        ("rect", 8, 8, 248, 248, 48, BACKGROUND),
        # The oven.
        ("rect", 26, 50, 186, 214, 18, OVEN),
        ("rect", 26, 90, 186, 96, 0, PANEL_LINE),
        # The door window, glowing.
        ("rect", 44, 112, 168, 196, 12, WINDOW),
        ("rect", 54, 122, 158, 186, 7, GLOW),
    ]
    if detailed:
        s += [
            ("circle", 56, 70, 10, KNOB),
            ("circle", 86, 70, 10, KNOB),
            ("rect", 120, 62, 166, 78, 4, WINDOW),
            ("rect", 56, 101, 156, 107, 3, HANDLE),
        ]
    # The thermometer: outline, tube, then the red column and bulb.
    s += [
        ("rect", 194, 44, 238, 196, 22, OUTLINE),
        ("circle", 216, 198, 32, OUTLINE),
        ("rect", 202, 52, 230, 196, 14, TUBE),
        ("circle", 216, 198, 24, MERCURY),
        ("rect", 209, 100, 223, 196, 7, MERCURY),
    ]
    return s


def svg() -> str:
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {CANVAS} {CANVAS}" width="{CANVAS}" height="{CANVAS}">']
    for shape in shapes(detailed=True):
        if shape[0] == "rect":
            _, x0, y0, x1, y1, r, fill = shape
            parts.append(f'  <rect x="{x0}" y="{y0}" width="{x1 - x0}" height="{y1 - y0}" rx="{r}" fill="{fill}"/>')
        else:
            _, cx, cy, r, fill = shape
            parts.append(f'  <circle cx="{cx}" cy="{cy}" r="{r}" fill="{fill}"/>')
    parts.append("</svg>")
    return "\n".join(parts) + "\n"


def bitmap(size: int) -> Image.Image:
    """The icon at ``size`` pixels, drawn at four times that and scaled down, for smooth edges."""
    scale = 4 * size / CANVAS
    big = Image.new("RGBA", (4 * size, 4 * size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(big)
    for shape in shapes(detailed=size > 32):
        if shape[0] == "rect":
            _, x0, y0, x1, y1, r, fill = shape
            box = (x0 * scale, y0 * scale, x1 * scale - 1, y1 * scale - 1)
            if r:
                draw.rounded_rectangle(box, radius=r * scale, fill=fill)
            else:
                draw.rectangle(box, fill=fill)
        else:
            _, cx, cy, r, fill = shape
            draw.ellipse(((cx - r) * scale, (cy - r) * scale, (cx + r) * scale - 1, (cy + r) * scale - 1), fill=fill)
    return big.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    (HERE / "oventracker.svg").write_text(svg(), encoding="utf-8", newline="\n")
    for size in (16, 32, 48, 64, 128, 256, 512):
        bitmap(size).save(HERE / f"oventracker-{size}.png", optimize=True)
    ico_sizes = (16, 20, 24, 32, 40, 48, 64, 128, 256)
    images = [bitmap(s) for s in ico_sizes]
    images[-1].save(HERE / "oventracker.ico", sizes=[(s, s) for s in ico_sizes], append_images=images[:-1])
    print(f"Wrote oventracker.svg, {len((16, 32, 48, 64, 128, 256, 512))} PNGs and oventracker.ico ({len(ico_sizes)} sizes) in {HERE}")


if __name__ == "__main__":
    main()
