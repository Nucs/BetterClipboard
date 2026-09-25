"""Render BetterClipboard's app icon (multi-size .ico + PNGs) with Pillow.

Design: a rounded square with an indigo -> violet -> fuchsia diagonal gradient and a soft top
highlight, carrying a white clipboard whose three "text" lines reuse the gradient. Everything is drawn
at 4x and downsampled with LANCZOS so edges stay smooth at every size; the small sizes (16-32 px) are
rendered separately with a simplified glyph (thicker strokes, two lines) so they stay legible in the
tray instead of turning into mush.

Usage:  python tools/make_icon.py            (writes into src/BetterClipboard.App/Assets)
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "BetterClipboard.App" / "Assets"

STOPS = [(0.0, (99, 102, 241)), (0.55, (139, 92, 246)), (1.0, (217, 70, 239))]  # indigo-500, violet-500, fuchsia-500


def lerp(a, b, t):
    """Linear interpolation between two RGB tuples."""
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def gradient_color(t):
    """Color at position t (0..1) along the multi-stop gradient."""
    for (p0, c0), (p1, c1) in zip(STOPS, STOPS[1:]):
        if t <= p1:
            return lerp(c0, c1, (t - p0) / (p1 - p0))
    return STOPS[-1][1]


def diagonal_gradient(size):
    """RGBA image filled with the diagonal (top-left -> bottom-right) gradient."""
    small = 64  # compute on a small grid and upscale: gradients have no detail to lose
    img = Image.new("RGBA", (small, small))
    px = img.load()
    for y in range(small):
        for x in range(small):
            px[x, y] = gradient_color((x + y) / (2 * (small - 1))) + (255,)
    return img.resize((size, size), Image.BICUBIC)


def rounded_mask(size, radius, box=None):
    """L-mode mask with a rounded rectangle (optionally inside box)."""
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box or (0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def render(size, simple=False):
    """Render the icon at `size` px (supersampled 4x)."""
    s = size * 4
    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Tile: gradient rounded square with a subtle top highlight for depth.
    margin = round(s * 0.04)
    tile_box = (margin, margin, s - margin, s - margin)
    tile_radius = round(s * 0.23)
    grad = diagonal_gradient(s)
    tile_mask = rounded_mask(s, tile_radius, tile_box)
    canvas.paste(grad, (0, 0), tile_mask)

    highlight = Image.new("RGBA", (s, s), (255, 255, 255, 0))
    hd = ImageDraw.Draw(highlight)
    hd.ellipse((-s * 0.3, -s * 0.75, s * 1.3, s * 0.42), fill=(255, 255, 255, 38))
    canvas.alpha_composite(Image.composite(highlight, Image.new("RGBA", (s, s)), tile_mask))

    # Clipboard board with a soft drop shadow.
    bw, bh = s * (0.50 if not simple else 0.56), s * (0.58 if not simple else 0.62)
    bx0 = (s - bw) / 2
    by0 = s * (0.25 if not simple else 0.22)
    board = (bx0, by0, bx0 + bw, by0 + bh)
    shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        (board[0], board[1] + s * 0.025, board[2], board[3] + s * 0.025), radius=s * 0.07, fill=(30, 10, 80, 90))
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(s * 0.025)))
    d = ImageDraw.Draw(canvas)
    d.rounded_rectangle(board, radius=s * 0.07, fill=(255, 255, 255, 255))

    # Clip: a pill straddling the board's top edge, with a hole.
    cw, ch = bw * 0.46, s * (0.11 if not simple else 0.12)
    cx0 = (s - cw) / 2
    cy0 = by0 - ch * 0.55
    d.rounded_rectangle((cx0, cy0, cx0 + cw, cy0 + ch), radius=ch / 2, fill=(236, 233, 254, 255), outline=(255, 255, 255, 255), width=round(s * 0.012))
    hole_w, hole_h = cw * 0.34, ch * 0.34
    hx0, hy0 = (s - hole_w) / 2, cy0 + ch * 0.22
    d.rounded_rectangle((hx0, hy0, hx0 + hole_w, hy0 + hole_h), radius=hole_h / 2, fill=STOPS[1][1] + (255,))

    # "Text" lines in gradient color.
    lines = [0.78, 0.62, 0.70] if not simple else [0.74, 0.52]
    line_h = s * (0.045 if not simple else 0.075)
    gap = s * (0.075 if not simple else 0.12)
    ly = by0 + bh * (0.30 if not simple else 0.32)
    for i, frac in enumerate(lines):
        lx0 = bx0 + bw * 0.16
        lx1 = lx0 + (bw * 0.68) * frac
        color = gradient_color(0.15 + i * 0.35) + (255,)
        d.rounded_rectangle((lx0, ly, lx1, ly + line_h), radius=line_h / 2, fill=color)
        ly += gap

    return canvas.resize((size, size), Image.LANCZOS)


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    images = [render(sz, simple=sz <= 32) for sz in sizes]
    # Pillow writes one ICO entry per image when append_images is used with explicit sizes.
    images[-1].save(ASSETS / "BetterClipboard.ico", format="ICO", sizes=[(sz, sz) for sz in sizes],
                    append_images=images[:-1])
    render(256).save(ASSETS / "AppIcon.png")
    render(64).save(ASSETS / "AppIcon64.png")
    render(32, simple=True).save(ASSETS / "AppIcon32.png")
    print("wrote", ", ".join(p.name for p in sorted(ASSETS.iterdir())))


if __name__ == "__main__":
    main()
