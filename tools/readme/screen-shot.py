"""Finish a whole-screen picture of a window for the Microsoft Store listing: round the window's corners.

The Store wants screenshots of 1366 x 768 pixels or more, so its pictures are whole screens (a desktop, a document,
BetterClipboard's panel or Settings over it), not the cut-out windows the README shows. They are taken in a
claude-desktops Windows desktop, which has no GPU: DWM draws every window's corners square there and puts a
half-transparent border band around it (2 px at 150 %), which no real Windows 11 PC shows.

This tool removes both with a second capture of the same screen without the window, and changes nothing else:

1. Inside the window's rounded shape (its frame bounds without the band, corners cut round), the pixels come from
   the capture with the window.
2. In the rest of the window's frame bounds (the band and what the square corners covered), they come from the
   capture without it: whatever really lies behind the window there (wallpaper, a document, the taskbar).
3. Everywhere else the picture is the capture with the window, untouched.

A flyout or popup that the app draws over the window (the image preview, a menu) is a window of its own and may
lie across the first window's edge. Name its visible rectangle with --keep: inside it the capture with the windows
always wins, so the rounding never cuts into it. XAML draws a popup's own corners and shadow, so they need no help.

Usage:
  python tools/readme/screen-shot.py <with.png> <without.png> <picture.png>
      --window LEFT TOP WIDTH HEIGHT [--window ...] [--keep LEFT TOP WIDTH HEIGHT]...
      [--trim 2] [--radius 12]

Both captures and the rectangles come from tools/readme/capture-screen.ps1 (one run with the window showing, one
after it was closed; nothing else on the screen may change in between, though a clock that moved on does no harm:
only the window's own rectangle is taken from the second capture). The defaults fit captures at 150 % scaling; at
another scale use round(1.5 * scale) for --trim and 8 * scale for --radius.

The result is an opaque PNG of the captures' size, which is what the Store takes.
"""
import argparse
import sys

from PIL import Image, ImageDraw

# The corner masks are drawn this many times larger, then scaled down: smooth arcs instead of stair steps.
SUPERSAMPLE = 4


def rounded_mask(width: int, height: int, radius: float) -> Image.Image:
    """Returns an anti-aliased mask of a rounded rectangle that fills `width` x `height`.

    Args:
        width: Mask width in pixels.
        height: Mask height in pixels.
        radius: Corner radius in pixels.

    Returns:
        An "L" image: 255 inside the shape, 0 in the cut corners, in between along the arcs.
    """
    large = Image.new("L", (width * SUPERSAMPLE, height * SUPERSAMPLE), 0)
    ImageDraw.Draw(large).rounded_rectangle(
        (0, 0, width * SUPERSAMPLE - 1, height * SUPERSAMPLE - 1), radius=radius * SUPERSAMPLE, fill=255)
    return large.resize((width, height), Image.LANCZOS)


def clip(rect, size):
    """Cuts a rectangle to the picture.

    Args:
        rect: (left, top, width, height) in pixels; may reach outside the picture (a maximized window's frame does).
        size: The picture's (width, height).

    Returns:
        (left, top, right, bottom) inside the picture, or None when nothing of the rectangle is inside it.
    """
    left, top, width, height = rect
    box = (max(0, left), max(0, top), min(size[0], left + width), min(size[1], top + height))
    return box if box[0] < box[2] and box[1] < box[3] else None


def finish(shown: Image.Image, hidden: Image.Image, windows, keep, trim: int, radius: float) -> Image.Image:
    """Builds the picture: `shown` with each window's band and square corners replaced by what lies behind them.

    Args:
        shown: The capture with the windows.
        hidden: The capture of the same screen without them; must have the same size.
        windows: The windows' frame bounds, (left, top, width, height) each.
        keep: Rectangles, (left, top, width, height) each, where `shown` always wins (popups over a window).
        trim: Width of DWM's border band inside the frame bounds, in pixels.
        radius: Corner radius of the window's visible shape, in pixels.

    Returns:
        The finished picture, opaque ("RGB").

    Raises:
        ValueError: When the two captures differ in size, or a window is too small to hold its band twice.
    """
    if shown.size != hidden.size:
        raise ValueError(f"the captures differ in size: {shown.size} and {hidden.size}")
    shown = shown.convert("RGB")
    hidden = hidden.convert("RGB")
    result = shown.copy()

    for left, top, width, height in windows:
        if width <= 2 * trim or height <= 2 * trim:
            raise ValueError(f"window {width}x{height} is too small for a {trim}-pixel band")
        # The window's frame bounds, as a patch of its own: the rounded shape from `shown`, the rest from `hidden`.
        # Built off-picture and pasted, so a frame that reaches beyond the screen (negative origin) needs no care here.
        mask = Image.new("L", (width, height), 0)
        mask.paste(rounded_mask(width - 2 * trim, height - 2 * trim, radius), (trim, trim))
        patch_shown = Image.new("RGB", (width, height))
        patch_hidden = Image.new("RGB", (width, height))
        patch_shown.paste(shown, (-left, -top))
        patch_hidden.paste(hidden, (-left, -top))
        result.paste(Image.composite(patch_shown, patch_hidden, mask), (left, top))

    for rect in keep:
        box = clip(rect, shown.size)
        if box:
            result.paste(shown.crop(box), box[:2])
    return result


def main() -> None:
    """Parses the command line, builds the picture and writes it; exits with 2 on input that cannot be used."""
    parser = argparse.ArgumentParser(description="Round a window's corners in a whole-screen capture (Store listing pictures).")
    parser.add_argument("shown", help="capture with the window (tools/readme/capture-screen.ps1)")
    parser.add_argument("hidden", help="capture of the same screen without it")
    parser.add_argument("output", help="the PNG to write")
    parser.add_argument("--window", nargs=4, type=int, action="append", required=True,
                        metavar=("LEFT", "TOP", "WIDTH", "HEIGHT"), help="a window's frame bounds; may be repeated")
    parser.add_argument("--keep", nargs=4, type=int, action="append", default=[],
                        metavar=("LEFT", "TOP", "WIDTH", "HEIGHT"),
                        help="a rectangle where the capture with the windows always wins (a popup over a window)")
    parser.add_argument("--trim", type=int, default=2, help="DWM's border band in pixels (default 2, for 150 %% scaling)")
    parser.add_argument("--radius", type=float, default=12, help="corner radius in pixels (default 12, for 150 %% scaling)")
    arguments = parser.parse_args()

    try:
        picture = finish(Image.open(arguments.shown), Image.open(arguments.hidden),
                         [tuple(window) for window in arguments.window], [tuple(rect) for rect in arguments.keep],
                         arguments.trim, arguments.radius)
        picture.save(arguments.output, optimize=True)
    except (OSError, ValueError) as error:
        print(error)
        sys.exit(2)
    print(f"{arguments.output}: {picture.size[0]}x{picture.size[1]}")


if __name__ == "__main__":
    main()
