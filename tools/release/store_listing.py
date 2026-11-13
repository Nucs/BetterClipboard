"""Reads the Microsoft Store listing kit (docs/store-listing.md) into data.

Two scripts need the kit's texts and must agree on what they are: check-store-listing.py (do they fit the Store's
limits?) and make-store-listing-csv.py (the file Partner Center imports). Both import this module, so a change of the
kit's layout breaks them together, loudly, instead of one of them reading yesterday's layout in silence.

The layout this module relies on (docs/store-listing.md):

  before "## 3. Listing texts", the two long answers of the submission pages:
    **Restricted capabilities: why runFullTrust** followed by a fenced ```text block (the 500-character justification)
    **Notes for certification**                   followed by a fenced ```text block

  under "## 3. Listing texts", one section per language:
    ### <Language name> (<language code>)        e.g. "### Simplified Chinese (zh-Hans)"
    **Short description**                         followed by a fenced ```text block
    **Description**                               followed by a fenced ```text block
    **Product features**                          followed by a fenced ```text block, one feature per line
    **Search terms:** a, b, c                     one line, comma-separated
    **Screenshot captions**                       followed by a fenced ```text block, one caption per line, in the
                                                  screenshots' order
    **Copyright and trademark info:** `text`      English section only; it serves every language

Nothing here prints or exits: a kit that cannot be read raises KitError, and the caller decides what that means.
"""

import io
import re
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# Where the kit lives when no path is given.
DEFAULT_KIT = ROOT / "docs" / "store-listing.md"

# The Store's limits, in characters unless said otherwise. Microsoft's documentation, read on 2026-11-12 and, for the
# search terms, on the listing page itself on 2026-11-13 (docs/microsoft-store.md, "Sources").
SHORT_DESCRIPTION_BELOW = 270       # a recommendation: longer ones may be cut where the Store shows them
DESCRIPTION_AT_MOST = 10000
FEATURES_AT_MOST = 20               # features per listing
FEATURE_AT_MOST = 200
CAPTION_AT_MOST = 200
SEARCH_TERMS_AT_MOST = 7            # terms per listing
SEARCH_TERM_AT_MOST = 40
SEARCH_TERM_WORDS_AT_MOST = 21      # separate words across all terms of one listing
COPYRIGHT_AT_MOST = 200
# "The justification must be 500 characters or fewer": Partner Center's own words when the first, 993-character
# one was saved (2026-11-13). The page saves nothing and says so only in red under the box.
JUSTIFICATION_AT_MOST = 500


class KitError(Exception):
    """The kit is missing, or is not laid out the way this module reads it (the message says what was looked for)."""


@dataclass
class Listing:
    """One language's listing texts, as the kit states them.

    Attributes:
        language: The language's name as the kit's heading writes it, e.g. "Simplified Chinese".
        code: The language code from the heading, lower-cased the way Partner Center names its columns ("zh-hans").
        short_description: The short description; empty when the kit has none.
        description: The description; empty when the kit has none.
        features: The product features, in the kit's order.
        search_terms: The search terms, in the kit's order.
        captions: The screenshot captions, in the screenshots' order.
    """

    language: str
    code: str
    short_description: str = ""
    description: str = ""
    features: list = field(default_factory=list)
    search_terms: list = field(default_factory=list)
    captions: list = field(default_factory=list)

    def problems(self):
        """What in this listing does not fit the Store's limits.

        Returns:
            A list of sentences, one per problem; empty when every text fits.
        """
        found = []
        if not self.short_description or len(self.short_description) >= SHORT_DESCRIPTION_BELOW:
            found.append(f"short description missing, or {SHORT_DESCRIPTION_BELOW}+ characters")
        if (not self.description or len(self.description) > DESCRIPTION_AT_MOST
                or re.search(r"https?://|www\.", self.description)):
            found.append(f"description missing, over {DESCRIPTION_AT_MOST:,} characters, or it holds a web address")
        longest_feature = max((len(feature) for feature in self.features), default=0)
        if not self.features or len(self.features) > FEATURES_AT_MOST or longest_feature > FEATURE_AT_MOST:
            found.append(f"features missing, more than {FEATURES_AT_MOST}, or one over {FEATURE_AT_MOST} characters")
        longest_term = max((len(term) for term in self.search_terms), default=0)
        words = sum(len(term.split()) for term in self.search_terms)
        if (not self.search_terms or len(self.search_terms) > SEARCH_TERMS_AT_MOST
                or longest_term > SEARCH_TERM_AT_MOST or words > SEARCH_TERM_WORDS_AT_MOST):
            found.append(f"search terms missing, more than {SEARCH_TERMS_AT_MOST}, one over {SEARCH_TERM_AT_MOST} "
                         f"characters, or more than {SEARCH_TERM_WORDS_AT_MOST} words in all")
        longest_caption = max((len(caption) for caption in self.captions), default=0)
        if not self.captions or longest_caption > CAPTION_AT_MOST:
            found.append(f"screenshot captions missing, or one over {CAPTION_AT_MOST} characters")
        return found


@dataclass
class Kit:
    """The whole kit: every language's listing, and what all of them share.

    Attributes:
        listings: The listings in the kit's order (English first).
        copyright: The copyright and trademark line, which serves every language.
        justification: Why the package needs the restricted capability runFullTrust (Submission options page).
        certification_notes: The notes for the Store's testers (Additional Testing Information page).
    """

    listings: list
    copyright: str
    justification: str = ""
    certification_notes: str = ""

    def by_code(self):
        """The listings by language code.

        Returns:
            A dict from the lower-cased language code ("en-us") to its Listing.
        """
        return {listing.code: listing for listing in self.listings}

    def problems(self):
        """What in the kit as a whole does not fit: each listing's problems, and what must agree between them.

        Returns:
            A list of (language name, sentence) pairs; empty when everything fits. Problems of the kit as a whole
            carry the name "kit".
        """
        found = [(listing.language, problem) for listing in self.listings for problem in listing.problems()]
        counts = {len(listing.captions) for listing in self.listings}
        if len(counts) > 1:
            # One set of screenshots serves every language, so a language with fewer captions leaves a picture bare.
            found.append(("kit", "the languages have different numbers of screenshot captions: "
                          + ", ".join(f"{listing.code} {len(listing.captions)}" for listing in self.listings)))
        if not self.copyright or len(self.copyright) > COPYRIGHT_AT_MOST:
            found.append(("kit", f"copyright line missing, or over {COPYRIGHT_AT_MOST} characters"))
        if not self.justification or len(self.justification) > JUSTIFICATION_AT_MOST:
            found.append(("kit", f"runFullTrust justification missing, or over {JUSTIFICATION_AT_MOST} characters "
                          f"(it has {len(self.justification)})"))
        if not self.certification_notes:
            found.append(("kit", "notes for certification missing"))
        return found


def _between(text, start, end):
    """The part of `text` after `start` and before the next `end`.

    Args:
        text: The text to cut from.
        start: The heading (or phrase) the part follows.
        end: The heading the part ends before.

    Returns:
        The part between the two.

    Raises:
        KitError: When either marker is missing.
    """
    if start not in text or end not in text.split(start, 1)[1]:
        raise KitError(f"cannot find the part between '{start}' and '{end}': has the kit's layout changed?")
    return text.split(start, 1)[1].split(end, 1)[0]


def _lines(block):
    """The non-empty lines of a fenced block, each without surrounding white space.

    Args:
        block: The block's text, without its fences.

    Returns:
        The lines in order; an empty list for an empty block.
    """
    return [line.strip() for line in block.split("\n") if line.strip()]


def read_kit(path=None):
    """Reads the kit.

    Args:
        path: The kit's file; the repository's docs/store-listing.md when omitted.

    Returns:
        The Kit.

    Raises:
        KitError: When the file cannot be read, has no language sections, or a section's heading has no language
            code. Missing texts are not an error here: they come back empty and Listing.problems() names them.
    """
    path = Path(path) if path else DEFAULT_KIT
    try:
        text = io.open(path, encoding="utf-8").read()
    except OSError as error:
        raise KitError(f"cannot read {path}: {error}") from error

    sections = re.split(r"^### ", _between(text, "## 3. Listing texts", "## 4."), flags=re.M)[1:]
    if not sections:
        raise KitError("no language sections under '## 3. Listing texts'")

    listings = []
    copyright_line = ""
    for section in sections:
        heading = section.split("\n", 1)[0].strip()
        match = re.fullmatch(r"(.+?)\s*\(([A-Za-z]{2,3}(?:-[A-Za-z0-9]+)*)\)", heading)
        if not match:
            raise KitError(f"the heading '### {heading}' does not end with a language code in brackets")
        blocks = dict(re.findall(
            r"\*\*(Short description|Description|Product features|Screenshot captions)\*\*\s*```text\n(.*?)\n```",
            section, flags=re.S))
        terms = re.search(r"^\*\*Search terms:\*\*[ \t]*(.+)$", section, flags=re.M)
        listings.append(Listing(
            language=match.group(1),
            code=match.group(2).lower(),
            short_description=blocks.get("Short description", "").strip(),
            description=blocks.get("Description", "").strip(),
            features=_lines(blocks.get("Product features", "")),
            search_terms=[term.strip() for term in terms.group(1).split(",") if term.strip()] if terms else [],
            captions=_lines(blocks.get("Screenshot captions", ""))))
        stated = re.search(r"^\*\*Copyright and trademark info:\*\*[ \t]*`(.+?)`[ \t]*$", section, flags=re.M)
        if stated and not copyright_line:
            copyright_line = stated.group(1).strip()

    # The two long answers of the submission pages stand before the listing texts. Missing ones come back empty and
    # Kit.problems() names them: a kit from before they were fenced blocks must fail the check, not the reading.
    pages = text.split("## 3. Listing texts", 1)[0]
    answers = dict(re.findall(
        r"\*\*(Restricted capabilities: why runFullTrust|Notes for certification)\*\*[^\n]*\n\s*```text\n(.*?)\n```",
        pages, flags=re.S))

    return Kit(listings=listings, copyright=copyright_line,
               justification=answers.get("Restricted capabilities: why runFullTrust", "").strip(),
               certification_notes=answers.get("Notes for certification", "").strip())
