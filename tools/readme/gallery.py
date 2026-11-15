"""Cut the README gallery's pictures out of the Microsoft Store listing's screenshots.

Usage: python tools/readme/gallery.py [OUT_DIR]   (default: docs/images/gallery)

The Store screenshots (packaging/msix/Listing/Screenshots, 2560 x 1440 at 150 %, the owner's demo data; CLAUDE.md
section 3.5) show the panel over Notepad. The panel crops are cut exactly to the panel's window and get transparent
rounded corners like the README's other photos (8 DIP = 12 px at 150 %); the two viewers fill the screen, so they are
cut around their content and scaled down.

The boxes below were measured on the screenshots of 2026-11-13: the panel's rectangle is where the columns and rows
are dense with its background color, (44, 44, 44); the text viewer's card is (32, 32, 32). Retaken screenshots need
the boxes measured again. The file names must stay unique among every picture the README shows: the docs site copies
them all into one folder (docs/website-src/scripts/generate_pages.py refuses two pictures of the same name).
"""

from __future__ import annotations

import pathlib
import sys

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = ROOT / 'packaging' / 'msix' / 'Listing' / 'Screenshots'
DEFAULT_OUT = ROOT / 'docs' / 'images' / 'gallery'
RADIUS = 12  # 8 DIP at 150 %, the panel's own corner radius

# name: (scene, box (left, top, right, bottom) in the 2560 x 1440 screenshot, rounded corners, width to scale to)
CROPS = {
    'regex-search': ('2-search',  (573, 410, 1199, 1357), True, None),
    'group-view':   ('5-groups',  (899, 410, 1591, 1357), True, None),
    'claude-tab':   ('6-prompts', (965, 410, 1591, 1357), True, None),
    'text-viewer':  ('3-text',    (230, 139, 2326, 1366), False, 1200),
    'image-viewer': ('4-picture', (400, 40, 2160, 1290), False, 1200),
}


def rounded(im: Image.Image, radius: int) -> Image.Image:
    """The picture with transparent rounded corners, antialiased by drawing the mask at 4x and scaling it down.

    Args:
        im: the picture.
        radius: the corner radius in pixels.

    Returns:
        An RGBA copy with the corners cut.
    """
    scale = 4
    mask = Image.new('L', (im.width * scale, im.height * scale), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, mask.width - 1, mask.height - 1), radius * scale, fill=255)
    out = im.convert('RGBA')
    out.putalpha(mask.resize(im.size, Image.LANCZOS))
    return out


def main(out_dir: pathlib.Path) -> int:
    """Write every crop as a PNG and print its size.

    Args:
        out_dir: the folder for the pictures.

    Returns:
        The exit code (0).
    """
    out_dir.mkdir(parents=True, exist_ok=True)
    for name, (scene, box, round_corners, width) in CROPS.items():
        im = Image.open(SRC / f'{scene}.png').convert('RGB').crop(box)
        if width:
            im = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
        if round_corners:
            im = rounded(im, RADIUS)
        path = out_dir / f'{name}.png'
        im.save(path, optimize=True)
        print(f'{path.name}: {im.size[0]} x {im.size[1]}, {path.stat().st_size:,} bytes')
    return 0


if __name__ == '__main__':
    sys.exit(main(pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_OUT))
