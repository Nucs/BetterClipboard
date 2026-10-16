"""Writes the BC-TEST documents of the rich-preview research (CLAUDE.md §2.33).

The files: bc-test.rtf (a heading, bold/italic/underline, colors, a highlight, a hyperlink field, a bulleted list, a
shaded table, a code line, an embedded PNG, Hebrew and an emoji), bc-test.html (the same as a page, with one picture as a
data: URI, one relative and one from http://127.0.0.1:8123, which serve.ps1 logs), bc-test.csv (a small range for Calc),
bc-test-big.rtf (40 sections with a picture each, ~2 MB: a Word copy full of screenshots) and the two PNGs.

Every document carries the marker BC-TEST, so probe_rich_formats.cs --save keeps copies of them and of nothing else.
Run: python make_samples.py OUTDIR
"""
import base64
import os
import struct
import sys
import zlib


def png(width, height, pixel):
    """Encodes an RGB picture as PNG without an imaging library (the samples need only gradients).

    Args:
        width: Width in pixels.
        height: Height in pixels.
        pixel: Called as pixel(x, y); returns the (r, g, b) of that pixel, each 0-255.
    Returns:
        The PNG bytes.
    """
    rows = b"".join(b"\x00" + b"".join(bytes(pixel(x, y)) for x in range(width)) for y in range(height))

    def chunk(kind, data):
        """Builds one PNG chunk.

        Args:
            kind: The four-byte chunk type.
            data: The chunk data.
        Returns:
            Length, type, data and the CRC over type and data.
        """
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def big_rtf(image):
    """Builds a ~2 MB RTF of 40 sections, each a heading, a paragraph and the picture: what a large Word copy weighs.

    Args:
        image: The PNG bytes embedded in every section (as \\pngblip hex).
    Returns:
        The RTF text (ASCII).
    """
    bs = chr(92)  # a backslash, built so no editor turns the control words into characters
    parts = [bs + "rtf1" + bs + "ansi{" + bs + "fonttbl{" + bs + "f0 Segoe UI;}}" + bs + "f0" + bs + "fs22 "]
    for i in range(40):
        parts.append(f"{bs}pard{bs}sa120{bs}b BC-TEST section {i + 1}{bs}b0{bs}par ")
        parts.append(f"{bs}pard Lorem ipsum BC-TEST paragraph {i + 1} with some {bs}i italic{bs}i0  and {bs}b bold{bs}b0  words "
                     "to wrap across the line width of the preview." + bs + "par ")
        parts.append("{" + bs + "pict" + bs + "pngblip" + bs + "picw160" + bs + "pich60" + bs + "picwgoal2400" + bs + "pichgoal900 "
                     + image.hex() + "}" + bs + "par ")
    return "{" + "".join(parts) + "}"


def main(out):
    """Writes every sample into a folder.

    Args:
        out: The folder (created when missing).
    """
    os.makedirs(out, exist_ok=True)
    image = png(160, 60, lambda x, y: (40 + x, 90 + y * 2, 200 - x // 2))
    with open(os.path.join(out, "bc-test-picture.png"), "wb") as f:
        f.write(image)
    other = png(120, 40, lambda x, y: (220, 120 + y * 2, 40 + x))
    with open(os.path.join(out, "bc-test-picture2.png"), "wb") as f:
        f.write(other)

    hexdata = image.hex()
    rtf = r"""{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}{\f1\fmodern\fcharset0 Cascadia Mono;}{\f2\froman\fcharset0 Georgia;}}
{\colortbl ;\red196\green43\blue28;\red0\green103\blue192;\red16\green124\blue16;\red255\green242\blue204;}
{\*\generator BC-TEST hand-written RTF;}\viewkind4\uc1
\pard\sa120\f2\fs36\b BC-TEST Quarterly notes\b0\par
\pard\sa120\f0\fs22 This line has \b bold\b0 , \i italic\i0 , \ul underlined\ulnone , \cf1 red\cf0 , \cf2 blue\cf0  and \highlight4 highlighted\highlight0  words, plus a link: {\field{\*\fldinst{HYPERLINK "https://example.com/bc-test"}}{\fldrslt{\ul\cf2 example.com/bc-test}}}.\par
{\pntext\f0 \bullet\tab}{\*\pn\pnlvlblt\pnf0\pnindent360{\pntxtb\bullet}}\pard{\pntext\f0 \bullet\tab}\fi-360\li720\sa60 First bullet with \b bold\b0\par
{\pntext\f0 \bullet\tab}Second bullet\par
\pard\sa120\par
\trowd\trgaph108\trleft0\trbrdrt\brdrs\brdrw10 \trbrdrb\brdrs\brdrw10 \trbrdrl\brdrs\brdrw10 \trbrdrr\brdrs\brdrw10
\clcbpat4\clbrdrt\brdrs\clbrdrb\brdrs\clbrdrl\brdrs\clbrdrr\brdrs\cellx2400\clcbpat4\clbrdrt\brdrs\clbrdrb\brdrs\clbrdrl\brdrs\clbrdrr\brdrs\cellx4800\clcbpat4\clbrdrt\brdrs\clbrdrb\brdrs\clbrdrl\brdrs\clbrdrr\brdrs\cellx7200
\pard\intbl\b Region\b0\cell\b Q3\b0\cell\b Q4\b0\cell\row
\trowd\trgaph108\trleft0\clbrdrt\brdrs\clbrdrb\brdrs\clbrdrl\brdrs\clbrdrr\brdrs\cellx2400\clbrdrt\brdrs\clbrdrb\brdrs\clbrdrl\brdrs\clbrdrr\brdrs\cellx4800\clbrdrt\brdrs\clbrdrb\brdrs\clbrdrl\brdrs\clbrdrr\brdrs\cellx7200
\pard\intbl North\cell\cf3 12.5\cf0\cell 14.0\cell\row
\pard\sa120\par
\pard\sa120\f1\fs20 var total = q3 + q4; // BC-TEST code\f0\fs22\par
\pard\sa120 A picture: {\pict\pngblip\picw160\pich60\picwgoal2400\pichgoal900 """ + hexdata + r"""}\par
\pard\sa120 Hebrew: HEBREW_ESCAPES and emoji: EMOJI_ESCAPES\par
}"""
    bs = chr(92)  # a backslash, built so no editor turns the escapes into characters
    rtf = rtf.replace("HEBREW_ESCAPES", "".join(f"{bs}u{cp}?" for cp in (0x5E9, 0x5DC, 0x5D5, 0x5DD)))
    rtf = rtf.replace("EMOJI_ESCAPES", "".join(f"{bs}u{unit - 65536}?" for unit in (0xD83D, 0xDE00)))
    with open(os.path.join(out, "bc-test.rtf"), "w", encoding="ascii") as f:
        f.write(rtf)

    data_uri = "data:image/png;base64," + base64.b64encode(image).decode()
    html = f"""<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><title>BC-TEST page</title>
<style>
body {{ font-family: Segoe UI, sans-serif; margin: 24px; }}
h1 {{ font-family: Georgia, serif; color: #1f3b57; }}
.note {{ background: #fff2cc; padding: 4px 8px; border-radius: 4px; }}
table {{ border-collapse: collapse; }} td, th {{ border: 1px solid #888; padding: 4px 10px; }}
th {{ background: #e8eef6; }}
pre {{ background: #f4f4f4; padding: 8px; }}
</style></head>
<body>
<h1>BC-TEST Quarterly notes</h1>
<p>This line has <b>bold</b>, <i>italic</i>, <u>underlined</u>, <span style="color:#c42b1c">red</span>,
<span style="color:#0067c0">blue</span> and <span class="note">highlighted</span> words, plus a link:
<a href="https://example.com/bc-test">example.com/bc-test</a>.</p>
<ul><li>First bullet with <b>bold</b></li><li>Second bullet</li></ul>
<ol><li>Numbered one</li><li>Numbered two</li></ol>
<table><tr><th>Region</th><th>Q3</th><th>Q4</th></tr><tr><td>North</td><td style="color:#107c10">12.5</td><td>14.0</td></tr></table>
<pre><code>var total = q3 + q4; // BC-TEST code</code></pre>
<p>Inline picture (data URI): <img alt="BC-TEST data picture" src="{data_uri}" width="160" height="60"></p>
<p>Relative picture: <img alt="BC-TEST relative picture" src="bc-test-picture2.png" width="120" height="40"></p>
<p>Remote picture (local server): <img alt="BC-TEST remote picture" src="http://127.0.0.1:8123/remote-pixel.png" width="40" height="40"></p>
<p>Hebrew: שלום and emoji: 😀</p>
</body></html>
"""
    with open(os.path.join(out, "bc-test.html"), "w", encoding="utf-8") as f:
        f.write(html)

    with open(os.path.join(out, "bc-test.csv"), "w", encoding="utf-8", newline="\r\n") as f:
        f.write("Region,Q3,Q4,Note\nNorth,12.5,14.0,BC-TEST row\nSouth,9.1,10.4,BC-TEST row\nEast,7.7,8.0,BC-TEST row\n")

    with open(os.path.join(out, "bc-test-big.rtf"), "w", encoding="ascii") as f:
        f.write(big_rtf(image))


if __name__ == "__main__":
    main(sys.argv[1])
