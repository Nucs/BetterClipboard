"""Draw the flags of BetterClipboard's interface languages as small SVG files.

The flags sit next to each language's names in the first-start language picker and in Settings › Language. They are
embedded in BetterClipboard.Core.dll with the language catalogs (src/BetterClipboard.Core/Localization/Flags), so a
release is one build for every language and nothing is read from disk at run time.

Design rules, so a list of flags lines up and reads well at 36 × 24 DIPs:
- every flag is drawn at 3:2 (900 × 600), whatever its official proportions (the US flag is 19:10); the stripes and the
  canton keep their official fractions of the height and the width;
- rounded corners are part of the drawing (a clip path): WinUI does not clip an Image to its parent's CornerRadius;
- a faint dark outline is drawn on top, so the white of a flag does not melt into a light window;
- stars are explicit polygons, with no <use>, <text> or CSS, the subset Direct2D's SVG renderer (WinUI's
  SvgImageSource) is known to draw.

The drawings are BetterClipboard's own (geometry from the flags' official specifications), so they bring no third-party
licence with them.

Usage:  python tools/make_flags.py            (writes src/BetterClipboard.Core/Localization/Flags/*.svg)
"""

import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FLAGS = ROOT / "src" / "BetterClipboard.Core" / "Localization" / "Flags"

WIDTH, HEIGHT = 900, 600
CORNER = 48  # about 2 DIPs at 36 x 24, the corner radius of WinUI's small controls
POINT_RATIO = 0.381966  # inner / outer radius of a regular five-pointed star (cos 72° / cos 36°)


def star(cx: float, cy: float, radius: float, angle: float = -90.0) -> str:
    """Path data of a five-pointed star whose first point is at `angle` degrees (-90 = straight up, SVG's y grows down).

    Args:
        cx: The star's centre, x.
        cy: The star's centre, y.
        radius: The distance from the centre to each outer point.
        angle: The direction of the first point, in degrees.

    Returns:
        SVG path data ("M…L…Z") for the ten corners.
    """
    points = []
    for i in range(10):
        r = radius if i % 2 == 0 else radius * POINT_RATIO
        a = math.radians(angle + i * 36)
        points.append(f"{cx + r * math.cos(a):.2f},{cy + r * math.sin(a):.2f}")
    return "M" + "L".join(points) + "Z"


def united_states() -> str:
    """13 stripes, a canton of 7 stripes' height and 2/5 of the width, 50 stars in 9 alternating rows of 6 and 5.

    Returns:
        The flag's SVG elements, without the document around them.
    """
    stripe = HEIGHT / 13
    parts = [f'<rect width="{WIDTH}" height="{HEIGHT}" fill="#FFFFFF"/>']
    for i in range(0, 13, 2):
        parts.append(f'<rect y="{i * stripe:.3f}" width="{WIDTH}" height="{stripe:.3f}" fill="#B22234"/>')
    canton_w, canton_h = WIDTH * 0.4, stripe * 7
    parts.append(f'<rect width="{canton_w:.3f}" height="{canton_h:.3f}" fill="#3C3B6E"/>')
    # The official grid: 12 columns and 10 rows across the canton; star diameter 0.0616 of the flag's height.
    column, row = canton_w / 12, canton_h / 10
    radius = HEIGHT * 0.0616 / 2
    stars = []
    for r in range(9):
        columns = range(1, 12, 2) if r % 2 == 0 else range(2, 11, 2)
        for c in columns:
            stars.append(star(c * column, (r + 1) * row, radius))
    parts.append(f'<path fill="#FFFFFF" d="{"".join(stars)}"/>')
    return "".join(parts)


def china() -> str:
    """Red field; a large star and four small ones turned toward it, on the official 30 x 20 grid (unit = width / 30).

    Returns:
        The flag's SVG elements, without the document around them.
    """
    unit = WIDTH / 30
    big = (5 * unit, 5 * unit)
    parts = [f'<rect width="{WIDTH}" height="{HEIGHT}" fill="#EE1C25"/>']
    stars = [star(big[0], big[1], 3 * unit)]
    for x, y in ((10, 2), (12, 4), (12, 7), (10, 9)):
        cx, cy = x * unit, y * unit
        toward_big = math.degrees(math.atan2(big[1] - cy, big[0] - cx))
        stars.append(star(cx, cy, unit, toward_big))
    parts.append(f'<path fill="#FFFF00" d="{"".join(stars)}"/>')
    return "".join(parts)


def document(body: str) -> str:
    """Wraps a flag's drawing: 3:2 canvas, rounded-corner clip, faint outline on top.

    Args:
        body: The flag's SVG elements (united_states() or china()).

    Returns:
        A complete SVG document, ending with a newline.
    """
    outline = (f'<rect x="3" y="3" width="{WIDTH - 6}" height="{HEIGHT - 6}" rx="{CORNER - 3}" ry="{CORNER - 3}" '
               f'fill="none" stroke="#000000" stroke-opacity="0.18" stroke-width="6"/>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{HEIGHT}" viewBox="0 0 {WIDTH} {HEIGHT}">'
            f'<defs><clipPath id="corners"><rect width="{WIDTH}" height="{HEIGHT}" rx="{CORNER}" ry="{CORNER}"/></clipPath></defs>'
            f'<g clip-path="url(#corners)">{body}</g>{outline}</svg>\n')


def main() -> None:
    """Writes every flag a shipped language names in its catalog's "$language" header."""
    FLAGS.mkdir(parents=True, exist_ok=True)
    for code, draw in (("us", united_states), ("cn", china)):
        path = FLAGS / f"{code}.svg"
        path.write_bytes(document(draw()).encode("utf-8"))
        print(f"{path.relative_to(ROOT)}: {path.stat().st_size} bytes")


if __name__ == "__main__":
    main()
