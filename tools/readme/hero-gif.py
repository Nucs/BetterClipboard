"""Compose the README's hero animation from the panel's frames: centered, it widens, holds, narrows, holds, repeats.

Usage:
    python tools/readme/hero-gif.py FRAMES_DIR [OUT_GIF] [--width 960] [--step-ms 50] [--hold-wide-ms 2600]
                                    [--hold-narrow-ms 1600] [--webp]

FRAMES_DIR holds 00.png, 01.png, ... from hero-frames.ps1: the panel captured at widths from narrow to wide, DWM's
border band included. Each frame goes through finish-shot.py's finish() (the band trimmed, corners rounded and
transparent), is centered on a transparent canvas as wide as the widest frame, and is scaled to --width. The loop plays
the frames forward, holds the widest, plays them back and holds the narrowest; the step and hold times are in
milliseconds. OUT_GIF defaults to docs/images/hero.gif.

The owner asked for a GIF (2026-11-15): 256 colors per frame, 1-bit transparency, so the corners lose their
anti-aliasing and the screenshot thumbnail on a card bands slightly. --webp also writes an animated WebP next to the
GIF (full color and alpha, about 40 % of the size), to compare the two.
"""

from __future__ import annotations

import argparse
import importlib.util
import pathlib
import sys
import tempfile

from PIL import Image

HERE = pathlib.Path(__file__).resolve().parent
DEFAULT_OUT = HERE.parents[1] / 'docs' / 'images' / 'hero.gif'


def load_finish():
    """finish-shot.py's finish(), from this script's folder: its file name has a dash, so it is loaded by path.

    Returns:
        The function.

    Raises:
        FileNotFoundError: finish-shot.py is not next to this script.
    """
    spec = importlib.util.spec_from_file_location('finish_shot', HERE / 'finish-shot.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.finish


def frames_on_canvas(frames_dir: pathlib.Path, width: int) -> list[Image.Image]:
    """Every frame finished, centered on a common transparent canvas, and scaled to the output width.

    Args:
        frames_dir: the folder of raw captures.
        width: the output width in pixels.

    Returns:
        RGBA images of one size, in order.

    Raises:
        SystemExit: the folder holds no frames.
        OSError: a frame cannot be read.
    """
    finish = load_finish()
    raws = sorted(frames_dir.glob('[0-9][0-9].png'))
    if not raws:
        raise SystemExit(f'No frames in {frames_dir}.')
    finished = []
    with tempfile.TemporaryDirectory() as tmp:
        for raw in raws:
            out = pathlib.Path(tmp) / raw.name
            # 2 px of DWM border band and an 8-DIP corner at 150 %: finish-shot.py's defaults for these captures.
            finish(str(raw), str(out), 2, 12.0)
            finished.append(Image.open(out).convert('RGBA'))
    canvas_w = max(im.width for im in finished)
    canvas_h = max(im.height for im in finished)
    size = (width, round(canvas_h * width / canvas_w))
    result = []
    for im in finished:
        canvas = Image.new('RGBA', (canvas_w, canvas_h), (0, 0, 0, 0))
        # Centered horizontally: the panel grows to both sides, as the README's centered picture expects.
        canvas.alpha_composite(im, ((canvas_w - im.width) // 2, 0))
        result.append(canvas.resize(size, Image.LANCZOS))
    return result


def to_gif_frame(im: Image.Image) -> Image.Image:
    """One RGBA frame as a palette frame whose index 255 is transparent (alpha below one half).

    Args:
        im: the RGBA frame.

    Returns:
        A "P" image whose palette's last entry is the transparent color.
    """
    rgb = im.convert('RGB')
    # No dithering: the panel is flat UI and text, where dithering only adds noise; 255 colors leave one for "clear".
    pal = rgb.quantize(colors=255, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    mask = im.getchannel('A').point(lambda a: 255 if a < 128 else 0)
    pal.paste(255, mask=mask)
    return pal


def main() -> int:
    """Build the GIF (and the WebP with --webp); see the module's docstring.

    Returns:
        The exit code (0).

    Raises:
        SystemExit: bad arguments, or no frames.
        OSError: a frame cannot be read, or the output cannot be written.
    """
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('frames_dir', type=pathlib.Path, help='00.png, 01.png, ... from hero-frames.ps1')
    parser.add_argument('out_gif', type=pathlib.Path, nargs='?', default=DEFAULT_OUT, help='the GIF to write')
    parser.add_argument('--width', type=int, default=960, help='output width in pixels (default 960)')
    parser.add_argument('--step-ms', type=int, default=50, help='each step of the widening (default 50)')
    parser.add_argument('--hold-wide-ms', type=int, default=2600, help='the hold at full width (default 2600)')
    parser.add_argument('--hold-narrow-ms', type=int, default=1600, help='the hold at the start (default 1600)')
    parser.add_argument('--webp', action='store_true', help='also write an animated WebP next to the GIF')
    args = parser.parse_args()

    frames = frames_on_canvas(args.frames_dir, args.width)
    # Forward 0..n-1, then back n-2..1: each end appears once and carries the hold.
    order = list(range(len(frames))) + list(range(len(frames) - 2, 0, -1))
    durations = [args.hold_wide_ms if i == len(frames) - 1 else args.hold_narrow_ms if i == 0 else args.step_ms
                 for i in order]
    sequence = [frames[i] for i in order]

    outputs = [args.out_gif]
    gif_frames = [to_gif_frame(im) for im in sequence]
    # disposal=2 clears each frame back to transparent, so a narrower frame never shows the wider one behind it.
    gif_frames[0].save(args.out_gif, save_all=True, append_images=gif_frames[1:], duration=durations, loop=0,
                       transparency=255, disposal=2, optimize=False)
    if args.webp:
        webp = args.out_gif.with_suffix('.webp')
        sequence[0].save(webp, save_all=True, append_images=sequence[1:], duration=durations, loop=0,
                         quality=88, method=6, lossless=False)
        outputs.append(webp)
    for path in outputs:
        print(f'{path.name}: {sequence[0].size[0]} x {sequence[0].size[1]}, {len(sequence)} frames, '
              f'{sum(durations) / 1000:.1f} s a loop, {path.stat().st_size:,} bytes')
    return 0


if __name__ == '__main__':
    sys.exit(main())
