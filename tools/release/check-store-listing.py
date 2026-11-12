"""Check the Microsoft Store listing kit (docs/store-listing.md) against the Store's limits, before its texts are pasted.

Partner Center refuses a listing field that is too long only when the page is saved, one field at a time, and it says
nothing about a short description that is merely cut off in search results. This script reads the kit the way a person
would copy from it and checks every language at once:

    short description    present, and under 270 characters (longer ones may be cut where the Store shows them)
    description          present, at most 10,000 characters, and no web address (the Store allows none there)
    product features     1 to 20, each at most 200 characters
    screenshot captions  each at most 200 characters

The limits are Microsoft's, read on 2026-11-12 (docs/microsoft-store.md, "Sources"). The texts are the fenced ```text
blocks that follow the bold labels inside each "### <language>" section of "## 3. Listing texts"; the captions are the
numbered list after "Suggested captions" in "## 2.". A kit whose layout changed fails loudly instead of passing empty.

Usage:  python tools/release/check-store-listing.py [docs/store-listing.md]
Exit code 0 when every text fits, 1 when one does not, 2 when the kit cannot be read as described above.
"""

import io
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# Characters. The short description's is a recommendation; the others are hard limits of Partner Center's fields.
SHORT_DESCRIPTION_BELOW = 270
DESCRIPTION_AT_MOST = 10000
FEATURES_AT_MOST = 20
FEATURE_AT_MOST = 200
CAPTION_AT_MOST = 200


def between(text, start, end):
    """The part of `text` between two headings; exits the script with code 2 when either is missing.

    Args:
        text: The whole kit.
        start: The heading (or phrase) the part follows.
        end: The heading the part ends before.

    Returns:
        The text after `start` and before the next `end`.
    """
    if start not in text or end not in text.split(start, 1)[1]:
        print(f"cannot find the part between '{start}' and '{end}': has the kit's layout changed?")
        sys.exit(2)
    return text.split(start, 1)[1].split(end, 1)[0]


def check_language(section):
    """Checks one "### <language>" section and prints its numbers and what does not fit.

    Args:
        section: The section's text, starting with the language's name (the "### " already cut off).

    Returns:
        The number of problems found in the section (0 when every text fits).
    """
    language = section.split("\n", 1)[0].strip()
    blocks = dict(re.findall(
        r"\*\*(Short description|Description|Product features)\*\*\s*```text\n(.*?)\n```", section, flags=re.S))
    short = blocks.get("Short description", "")
    description = blocks.get("Description", "")
    features = [line for line in blocks.get("Product features", "").split("\n") if line.strip()]
    longest = max((len(feature) for feature in features), default=0)
    print(f"{language}: short {len(short)}, description {len(description)}, {len(features)} features (longest {longest})")

    problems = 0
    if not short or len(short) >= SHORT_DESCRIPTION_BELOW:
        problems += 1
        print(f"   short description missing, or {SHORT_DESCRIPTION_BELOW}+ characters")
    if not description or len(description) > DESCRIPTION_AT_MOST or re.search(r"https?://|www\.", description):
        problems += 1
        print(f"   description missing, over {DESCRIPTION_AT_MOST:,} characters, or it holds a web address")
    if not features or len(features) > FEATURES_AT_MOST or longest > FEATURE_AT_MOST:
        problems += 1
        print(f"   features missing, more than {FEATURES_AT_MOST}, or one over {FEATURE_AT_MOST} characters")
    return problems


def main():
    """Reads the kit named on the command line (or the repository's), checks it, and exits with the code described above."""
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "docs" / "store-listing.md"
    try:
        text = io.open(path, encoding="utf-8").read()
    except OSError as error:
        print(f"cannot read {path}: {error}")
        sys.exit(2)

    problems = 0
    sections = re.split(r"^### ", between(text, "## 3. Listing texts", "## 4."), flags=re.M)[1:]
    if not sections:
        print("no language sections under '## 3. Listing texts'")
        sys.exit(2)
    for section in sections:
        problems += check_language(section)

    captions = re.findall(r"^\d+\. (.+)$", between(text, "Suggested captions", "## 3. Listing texts"), flags=re.M)
    longest = max((len(caption) for caption in captions), default=0)
    print(f"captions: {len(captions)} (longest {longest})")
    if not captions or longest > CAPTION_AT_MOST:
        problems += 1
        print(f"   captions missing, or one over {CAPTION_AT_MOST} characters")

    print("ok" if problems == 0 else f"{problems} problem(s)")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
