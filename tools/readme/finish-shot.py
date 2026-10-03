"""Turn a window capture into a README photo: DWM's border band trimmed, corners rounded and transparent.

tools/readme/capture-window.ps1 saves a window by its DWM frame bounds. Those carry a band around the window
(2 px at 150 %): DWM's border, half transparent, so it shows whatever lay behind the window and its color
changes with the wallpaper. In a VM without a GPU, DWM also draws the corners square, while on real
Windows 11 hardware windows have 8-DIP rounded corners. This trims the band and cuts the corners round with an
anti-aliased mask, so the photo looks like the app does on a real PC and sits cleanly on GitHub's light and
dark pages (the corners are transparent, not filled with one page's color).

Usage:
  python tools/readme/finish-shot.py <capture.png> <photo.png> [--trim 2] [--radius 12]

The defaults fit captures at 150 % scaling: 2 px of border band, and 8 DIP = 12 px of corner radius. At
another scale use round(1.5 * scale) for --trim and 8 * scale for --radius.
"""
import argparse

from PIL import Image, ImageDraw

# The corner mask is drawn this many times larger, then scaled down: smooth arcs instead of stair steps.
SUPERSAMPLE = 4


def finish(source: str, target: str, trim: int, radius: float) -> tuple[int, int]:
    """Writes `target`: `source` without `trim` pixels on each side, with corners cut round.

    The result is an RGBA PNG saved with Pillow's optimizer, so a photo of flat UI stays small.

    Args:
        source: The capture to read (any mode Pillow opens; it is converted to RGBA).
        target: The PNG file to write; an existing file is replaced.
        trim: Pixels to cut from every edge, the width of DWM's border band. 0 keeps the band.
        radius: Corner radius in pixels, measured on the trimmed image.

    Returns:
        The photo's width and height in pixels.

    Raises:
        OSError: The source cannot be read or the target cannot be written.
        ValueError: `trim` leaves no pixels (twice the trim is at least the width or the height).
    """
    image = Image.open(source).convert("RGBA")
    width, height = image.size
    if 2 * trim >= min(width, height):
        raise ValueError(f"Trimming {trim} px per side leaves nothing of a {width}x{height} capture.")
    image = image.crop((trim, trim, width - trim, height - trim))
    width, height = image.size
    mask = Image.new("L", (width * SUPERSAMPLE, height * SUPERSAMPLE), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, width * SUPERSAMPLE - 1, height * SUPERSAMPLE - 1), radius=radius * SUPERSAMPLE, fill=255)
    # The capture is opaque, so the mask can simply become its alpha channel.
    image.putalpha(mask.resize((width, height), Image.LANCZOS))
    image.save(target, optimize=True)
    return width, height


def main() -> None:
    """Parses the command line, finishes one capture and prints the photo's size."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("source", help="the capture from capture-window.ps1")
    parser.add_argument("target", help="the photo to write, e.g. docs/images/panel.png")
    parser.add_argument("--trim", type=int, default=2, help="pixels of DWM border band per side (default 2)")
    parser.add_argument("--radius", type=float, default=12.0, help="corner radius in pixels (default 12)")
    args = parser.parse_args()
    width, height = finish(args.source, args.target, args.trim, args.radius)
    print(f"{args.target}: {width}x{height}")


if __name__ == "__main__":
    main()
