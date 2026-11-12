"""Render the logo files of BetterClipboard's Microsoft Store package (CLAUDE.md §2.47) with Pillow.

A Store (MSIX) package names its logos in its manifest, and Windows picks the file that fits the place and the
display scale by the qualifiers in the file name. This script draws them all from the app icon's own drawing
(tools/make_icon.py), so the tile, the taskbar button, the Start menu entry and the Store listing show the same icon
as the unpackaged app.

What is written (into packaging/msix/Assets, which only the package build reads):

    Square44x44Logo.scale-{100,125,150,200,400}.png    the app list / taskbar logo on its plate: the icon with a margin
    Square44x44Logo.targetsize-{16,24,32,48,256}.png   the same for exact pixel sizes
    ...targetsize-N_altform-unplated.png               without a plate (the taskbar and Start on Windows 11): full bleed
    ...targetsize-N_altform-lightunplated.png          the same for the light theme (the icon has its own background,
                                                       so both are the same picture; Windows asks for both names)
    Square150x150Logo.scale-*.png                      the medium tile: the icon centered on a transparent canvas
    Wide310x150Logo.scale-*.png                        the wide tile
    StoreLogo.scale-*.png                              the 50 x 50 logo of the package itself (Store, installer dialogs)

Sizes of 32 px and below use the icon's simplified drawing (thicker strokes, two lines), like the tray icon.

It also writes the one picture the Store's listing page asks for that is not part of the package (into
packaging/msix/Listing, which no build reads; it is uploaded by hand in Partner Center, docs/store-listing.md):

    AppTileIcon.300x300.png                            the listing's 1:1 app tile icon: the bare icon at 300 px

Usage:  python tools/make_store_assets.py            (rewrites every file; they are checked in)
"""

from pathlib import Path

from PIL import Image

from make_icon import render

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "packaging" / "msix" / "Assets"

# Pictures for the Store's listing page. A folder of its own: everything in ASSETS goes into the package.
LISTING = ROOT / "packaging" / "msix" / "Listing"

# The listing's 1:1 app tile icon; the Store shows it at this size and strongly recommends providing it.
LISTING_TILE_SIZE = 300

# Windows' display scales; a logo's size at scale s is its base size times s / 100, rounded as Windows rounds.
SCALES = [100, 125, 150, 200, 400]

# Exact sizes Windows asks for in lists, the taskbar and Start.
TARGET_SIZES = [16, 24, 32, 48, 256]

# Below and at this many pixels the simplified drawing stays legible where the detailed one turns to mush.
SIMPLE_UP_TO = 32


def icon(size):
    """The app icon at `size` px, simplified when small.

    Args:
        size: The side of the square picture, in pixels.

    Returns:
        The icon as an RGBA image of `size` x `size`.
    """
    return render(size, simple=size <= SIMPLE_UP_TO)


def on_canvas(width, height, share):
    """The icon centered on a transparent canvas, `share` of the shorter side high (the rest is the tile's margin).

    Args:
        width: The canvas's width in pixels.
        height: The canvas's height in pixels.
        share: The icon's side as a share of the canvas's shorter side (0 to 1).

    Returns:
        The canvas as an RGBA image of `width` x `height`.
    """
    side = max(1, round(min(width, height) * share))
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    canvas.alpha_composite(icon(side), ((width - side) // 2, (height - side) // 2))
    return canvas


def scaled(base, scale):
    """A base size in pixels at a display scale, rounded half up (44 at 125 % is 55).

    Args:
        base: The size at 100 %, in pixels.
        scale: The display scale in percent.

    Returns:
        The size in whole pixels, as Windows rounds it.
    """
    return int(base * scale / 100 + 0.5)


def main():
    """Writes every logo file of the package and the listing's tile icon, and prints what was written."""
    ASSETS.mkdir(parents=True, exist_ok=True)
    for stale in ASSETS.glob("*.png"):
        stale.unlink()

    written = []

    def save(image, name):
        image.save(ASSETS / name, optimize=True)
        written.append(name)

    for scale in SCALES:
        # The small logo sits on a plate in some places: three quarters of the canvas keeps it clear of the edge.
        side = scaled(44, scale)
        save(on_canvas(side, side, 0.75), f"Square44x44Logo.scale-{scale}.png")

        # Tiles: Microsoft's guidance keeps the content within about two thirds of a medium tile.
        side = scaled(150, scale)
        save(on_canvas(side, side, 0.64), f"Square150x150Logo.scale-{scale}.png")
        save(on_canvas(scaled(310, scale), scaled(150, scale), 0.64), f"Wide310x150Logo.scale-{scale}.png")

        # The package's own logo is shown bare, so it fills its canvas.
        side = scaled(50, scale)
        save(icon(side), f"StoreLogo.scale-{scale}.png")

    for size in TARGET_SIZES:
        save(on_canvas(size, size, 0.75), f"Square44x44Logo.targetsize-{size}.png")
        bare = icon(size)
        save(bare, f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
        save(bare, f"Square44x44Logo.targetsize-{size}_altform-lightunplated.png")

    total = sum((ASSETS / name).stat().st_size for name in written)
    print(f"wrote {len(written)} files ({total / 1024:.0f} KB) into {ASSETS}")

    # The listing's tile is shown bare on the Store's own background, so it fills its canvas like the package's logo.
    LISTING.mkdir(parents=True, exist_ok=True)
    tile = LISTING / f"AppTileIcon.{LISTING_TILE_SIZE}x{LISTING_TILE_SIZE}.png"
    icon(LISTING_TILE_SIZE).save(tile, optimize=True)
    print(f"wrote {tile.name} ({tile.stat().st_size / 1024:.0f} KB) into {LISTING}")


if __name__ == "__main__":
    main()
