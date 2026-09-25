"""Print the clips table of a BetterClipboard test database (no payloads).

PRIVACY: previews are shown only for test data (BC-TEST*, the bc-test link, images, files); every other
row prints "<user data hidden>" because test databases also contain items imported from the user's
real Win+V history.

Usage: python tools/e2e/dbq.py <data-dir>/history.db
"""
import sqlite3
import sys

db = sys.argv[1]
con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
rows = con.execute(
    "SELECT id, kind, substr(replace(preview, char(10), ' | '), 1, 60), source_app_name, origin, is_pinned, "
    "size_bytes, format_names, image_width, image_height, thumbnail IS NOT NULL, use_count "
    "FROM clips ORDER BY is_pinned DESC, last_used_utc DESC").fetchall()
kinds = {0: "Text", 1: "Rich", 2: "Link", 3: "Color", 4: "Image", 5: "Files"}
for r in rows:
    fmts = r[7].replace("\x1f", ",")
    print(f"#{r[0]:<3} {kinds.get(r[1], r[1]):<5} pin={r[5]} origin={r[4]} uses={r[11]} {r[6]:>8}B "
          f"img={r[8]}x{r[9]} thumb={r[10]} src={r[3]!s:<22} [{fmts}] {(r[2] if (r[2] or '').startswith(('BC-TEST','Image','https://bc-test')) or r[1] in (4,5) else '<user data hidden>')!r}")
print(len(rows), "rows")
