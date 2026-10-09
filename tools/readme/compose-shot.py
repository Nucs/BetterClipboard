"""Build a README photo of a window with a flyout that leaves it (the update dialog), on the wallpaper.

tools/readme/finish-shot.py finishes a capture of one window. A flyout that is larger than its window is a window
of its own, so its photo is a piece of the screen: the wallpaper, the window, and the flyout over both. In a VM
without a GPU that piece shows two things real Windows 11 hardware does not: DWM draws the window's corners
square, and puts a half-transparent border band around it (2 px at 150 %). This tool removes both, with a second
capture of the same screen without the windows:

1. Inside the window's rounded shape (its frame bounds without the band, corners cut round), the pixels come from
   the capture with the windows. Outside it they come from the capture without them, so the band and the square
   corners become wallpaper.
2. Inside each popup window's rectangle, the pixels always come from the capture with the windows. XAML draws a
   flyout's rounded corners and shadow itself, into a transparent window, so they are right as they are.
3. The photo is cut to what is visible (the window, and each popup's body without its shadow's room) plus a
   margin, and its own corners are cut round and transparent, like the other photos.

Usage:
  python tools/readme/compose-shot.py <with.png> <without.png> <photo.png>
      --window LEFT TOP WIDTH HEIGHT [--popup LEFT TOP WIDTH HEIGHT]...
      [--trim 2] [--radius 12] [--margin 32]

Both captures and the rectangles come from tools/readme/capture-screen.ps1 (one run with the flyout open, one
after closing the flyout and the window; nothing else on the screen may change in between). The defaults fit
captures at 150 % scaling; at another scale use round(1.5 * scale) for --trim and 8 * scale for --radius.
"""
import argparse

from PIL import Image, ImageChops, ImageDraw

# The corner masks are drawn this many times larger, then scaled down: smooth arcs instead of stair steps.
SUPERSAMPLE = 4

# A popup pixel counts as the popup's body when its red, green and blue together are lighter than the wallpaper
# by more than this. Capture noise stays below it (two captures of an unchanged screen are identical).
LIGHTER_BY = 12


def rounded_mask(width: int, height: int, radius: float) -> Image.Image:
    """Returns an anti-aliased mask of a rounded rectangle that fills `width` x `height`.

    Args:
        width: Mask width in pixels.
        height: Mask height in pixels.
        radius: Corner radius in pixels.

    Returns:
        An "L" image: 255 inside the shape, 0 outside, in-between values along the arcs.
    """
    large = Image.new("L", (width * SUPERSAMPLE, height * SUPERSAMPLE), 0)
    ImageDraw.Draw(large).rounded_rectangle(
        (0, 0, width * SUPERSAMPLE - 1, height * SUPERSAMPLE - 1), radius=radius * SUPERSAMPLE, fill=255)
    return large.resize((width, height), Image.LANCZOS)


def visible_box(shown: Image.Image, hidden: Image.Image, box: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    """Returns the part of `box` that the popup's body covers: the popup without its shadow margin.

    A popup window is larger than the bubble it shows (room for the shadow, most of it below), so its rectangle
    alone would make the photo's margins uneven.

    The body is told from the shadow by direction, not by amount: a shadow only darkens the wallpaper, while a
    dark-theme bubble on a dark wallpaper is lighter than it in at least one color. (An amount cannot tell them
    apart: on bright wallpaper the shadow changes a pixel more than the body does on dark wallpaper.) So this
    works for a bubble that is lighter than some of the wallpaper along each of its edges; a dark bubble on a
    wallpaper that is lighter everywhere gets the fallback below.

    Args:
        shown: The capture with the windows (RGB).
        hidden: The capture without them (RGB), the same size.
        box: The popup window's rectangle as (left, top, right, bottom).

    Returns:
        The bounding box of the lighter pixels in screen coordinates, or `box` itself when no pixel is lighter
        (the margins then include the shadow's room).
    """
    # subtract() stops at zero, so only pixels where the capture with the windows is lighter are left.
    red, green, blue = ImageChops.subtract(shown.crop(box), hidden.crop(box)).split()
    # The adds saturate at 255, which is far above the threshold, so nothing is lost.
    total = ImageChops.add(ImageChops.add(red, green), blue)
    found = total.point(lambda value: 255 if value > LIGHTER_BY else 0).getbbox()
    if found is None:
        return box
    return (box[0] + found[0], box[1] + found[1], box[0] + found[2], box[1] + found[3])


def compose(shown_path: str, hidden_path: str, target: str, window: tuple[int, int, int, int],
            popups: list[tuple[int, int, int, int]], trim: int, radius: float, margin: int) -> tuple[int, int]:
    """Writes `target`: the window with round corners and its popups on the wallpaper, cut to what is visible.

    Args:
        shown_path: The screen capture with the window and its popups.
        hidden_path: The capture of the same screen without them.
        target: The PNG file to write (RGBA, optimized); an existing file is replaced.
        window: The window's frame bounds as (left, top, width, height), DWM's border band included.
        popups: Each popup window's bounds as (left, top, width, height); may be empty.
        trim: Width of DWM's border band in pixels, cut from every edge of the window.
        radius: Corner radius in pixels, for the window and for the photo itself.
        margin: Pixels of wallpaper to keep around the visible shapes (less where the screen ends).

    Returns:
        The photo's width and height in pixels.

    Raises:
        OSError: A capture cannot be read, or the target cannot be written.
        ValueError: The captures differ in size, a rectangle lies outside them, or `trim` leaves nothing of
            the window.
    """
    shown = Image.open(shown_path).convert("RGB")
    hidden = Image.open(hidden_path).convert("RGB")
    if shown.size != hidden.size:
        raise ValueError(f"The captures differ in size: {shown.size} and {hidden.size}.")
    screen = (0, 0, shown.width, shown.height)

    def as_box(rect: tuple[int, int, int, int], what: str) -> tuple[int, int, int, int]:
        """Turns (left, top, width, height) into (left, top, right, bottom), refusing one outside the screen."""
        left, top, width, height = rect
        box = (left, top, left + width, top + height)
        if width <= 0 or height <= 0 or left < 0 or top < 0 or box[2] > screen[2] or box[3] > screen[3]:
            raise ValueError(f"The {what} rectangle {rect} is not inside the {screen[2]}x{screen[3]} capture.")
        return box

    outer = as_box(window, "window")
    inner = (outer[0] + trim, outer[1] + trim, outer[2] - trim, outer[3] - trim)
    if inner[2] <= inner[0] or inner[3] <= inner[1]:
        raise ValueError(f"Trimming {trim} px per side leaves nothing of the window {window}.")
    popup_boxes = [as_box(popup, "popup") for popup in popups]

    # Where the capture with the windows is kept: the window's rounded shape, and every popup's whole rectangle
    # (pasted after the shape, so a popup over the window's edge keeps its own pixels there).
    keep = Image.new("L", shown.size, 0)
    keep.paste(rounded_mask(inner[2] - inner[0], inner[3] - inner[1], radius), (inner[0], inner[1]))
    draw = ImageDraw.Draw(keep)
    for box in popup_boxes:
        draw.rectangle((box[0], box[1], box[2] - 1, box[3] - 1), fill=255)
    result = Image.composite(shown, hidden, keep)

    # The cut: everything visible, plus the margin, inside the screen.
    left, top, right, bottom = inner
    for box in popup_boxes:
        seen = visible_box(shown, hidden, box)
        left, top = min(left, seen[0]), min(top, seen[1])
        right, bottom = max(right, seen[2]), max(bottom, seen[3])
    cut = (max(left - margin, 0), max(top - margin, 0), min(right + margin, screen[2]), min(bottom + margin, screen[3]))
    photo = result.crop(cut).convert("RGBA")
    photo.putalpha(rounded_mask(photo.width, photo.height, radius))
    photo.save(target, optimize=True)
    return photo.size


def main() -> None:
    """Parses the command line, composes one photo and prints its size."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("shown", help="the screen capture with the window and its flyout")
    parser.add_argument("hidden", help="the capture of the same screen without them")
    parser.add_argument("target", help="the photo to write, e.g. docs/images/update.png")
    parser.add_argument("--window", type=int, nargs=4, required=True, metavar=("LEFT", "TOP", "WIDTH", "HEIGHT"),
                        help="the window's frame bounds, as capture-screen.ps1 prints them")
    parser.add_argument("--popup", type=int, nargs=4, action="append", default=[],
                        metavar=("LEFT", "TOP", "WIDTH", "HEIGHT"), help="a popup window's bounds; repeatable")
    parser.add_argument("--trim", type=int, default=2, help="pixels of DWM border band per side (default 2)")
    parser.add_argument("--radius", type=float, default=12.0, help="corner radius in pixels (default 12)")
    parser.add_argument("--margin", type=int, default=32, help="pixels of wallpaper around the shapes (default 32)")
    args = parser.parse_args()
    width, height = compose(args.shown, args.hidden, args.target, tuple(args.window),
                            [tuple(popup) for popup in args.popup], args.trim, args.radius, args.margin)
    print(f"{args.target}: {width}x{height}")


if __name__ == "__main__":
    main()
