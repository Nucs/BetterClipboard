"""Check the Microsoft Store listing kit (docs/store-listing.md) against the Store's limits, before its texts are used.

Partner Center refuses a listing field that is too long only when the page is saved or the file is imported, one
complaint at a time, and it says nothing about a short description that is merely cut off in search results. This
script reads the kit the way make-store-listing-csv.py does (store_listing.py is their shared reader) and checks
every language at once:

    short description    present, and under 270 characters (longer ones may be cut where the Store shows them)
    description          present, at most 10,000 characters, and no web address (the Store allows none there)
    product features     1 to 20, each at most 200 characters
    search terms         1 to 7, each at most 40 characters, at most 21 separate words in all
    screenshot captions  present, each at most 200 characters, and the same number in every language (one set of
                         screenshots serves all of them)
    copyright line       present (the English section states it for every language), at most 200 characters
    runFullTrust reason  present, at most 500 characters (Partner Center refuses a longer one when the page is saved)
    certification notes  present

The limits are Microsoft's, read on 2026-11-12 and 2026-11-13 (docs/microsoft-store.md, "Sources"). A kit whose
layout changed fails loudly instead of passing empty.

Usage:  python tools/release/check-store-listing.py [docs/store-listing.md]
Exit code 0 when every text fits, 1 when one does not, 2 when the kit cannot be read as described above.
"""

import sys
from pathlib import Path

# The shared reader lives next to this script; a run from another folder must still find it.
sys.path.insert(0, str(Path(__file__).resolve().parent))

import store_listing  # noqa: E402  (after the path line above, on purpose)


def main():
    """Reads the kit named on the command line (or the repository's), checks it, and exits with the code described above."""
    try:
        kit = store_listing.read_kit(sys.argv[1] if len(sys.argv) > 1 else None)
    except store_listing.KitError as error:
        print(error)
        sys.exit(2)

    for listing in kit.listings:
        longest_feature = max((len(feature) for feature in listing.features), default=0)
        longest_caption = max((len(caption) for caption in listing.captions), default=0)
        words = sum(len(term.split()) for term in listing.search_terms)
        print(f"{listing.language} ({listing.code}): short {len(listing.short_description)}, "
              f"description {len(listing.description)}, {len(listing.features)} features (longest {longest_feature}), "
              f"{len(listing.search_terms)} search terms ({words} words), "
              f"{len(listing.captions)} captions (longest {longest_caption})")
    print(f"copyright: {len(kit.copyright)} characters; runFullTrust justification: {len(kit.justification)}; "
          f"notes for certification: {len(kit.certification_notes)}")

    problems = kit.problems()
    for language, problem in problems:
        print(f"   {language}: {problem}")
    print("ok" if not problems else f"{len(problems)} problem(s)")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
