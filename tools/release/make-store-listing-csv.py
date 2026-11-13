"""Fill Partner Center's exported Store listings file from the listing kit (docs/store-listing.md), or check one.

Partner Center's listing pages take some forty fields per language by hand. Its *Export listings* / *Import listings*
pair takes them all at once as a .csv with one row per field and one column per language. This script fills that
file from the kit, so what the Store shows is what the repository says, in every language:

    Description, ShortDescription      each language's own column
    Feature1 .. Feature20              the kit's features in order; the rows after the last one are emptied
    SearchTerm1 .. SearchTerm7         likewise
    CopyrightTrademarkInformation      the "default" column, which serves every language whose own cell is empty
    DesktopScreenshotCaption1 .. n     with --screenshots n: each picture's caption (below); the rows after are emptied

Everything else stays exactly as exported: the first three columns (Partner Center refuses a file whose Field, ID
or Type changed), the title, the release notes (the release pipeline writes "What's new" per release), and every
picture. Pictures that are already uploaded appear in an export as web addresses; --copy-pictures-from en-us copies
the screenshots' and logos' addresses from that language into every language that has none, so one upload serves
all listings. Trailer rows are never touched: emptying one deletes the trailer in Partner Center.

Captions go under their pictures, not into fixed rows. An import does not keep the order of a listing's screenshots
(measured 2026-11-13, three imports: a language sent in the kit's order came back in another, and languages that
were not changed at all changed their order), while a caption always stays with its picture. So the script first
finds out which of the kit's screenshots sits at each position of each language:

    from the exported captions (a caption that is one of the kit's captions names its picture);
    from --screenshots-in-order LANGUAGE, a language whose pictures are in the kit's order;
    or, when there is neither, from the languages agreeing position by position (pictures just uploaded, in order).

It then writes each picture's own caption. When it cannot tell, or the languages show different pictures, it stops.
The order itself is put right on Partner Center's listing pages: dragging one screenshot onto another makes the two
change places, and the page saves that at once. This script says which languages need it, and --check verifies an
export against the kit afterwards: every text, every caption under its picture, every language in the kit's order.

The format is the one Partner Center itself writes and reads (PapaParse on its pages, checked 2026-11-13): UTF-8 with
a byte order mark, comma-separated, CRLF, a field quoted when it holds a comma, a quote or a line break.

The template must be a file exported for the same submission: its language columns must be exactly the kit's
languages (the Store takes them from the packages), or the script stops and names the difference.

Usage:
    python tools/release/make-store-listing-csv.py --template exported.csv --output import.csv
                                                   [--kit docs/store-listing.md] [--screenshots N]
                                                   [--screenshots-in-order LANGUAGE] [--copy-pictures-from LANGUAGE]
    python tools/release/make-store-listing-csv.py --check --template exported.csv [--kit ...] [--screenshots N]

Exit code 0 when the file was written (or, with --check, the export is what the kit says), 1 when the kit's texts do
not fit the Store's limits (or, with --check, the export differs from the kit), 2 when the kit or the template cannot
be used (the message says why). Nothing is written unless everything fits.
"""

import argparse
import csv
import io
import sys
from pathlib import Path

# The shared reader lives next to this script; a run from another folder must still find it.
sys.path.insert(0, str(Path(__file__).resolve().parent))

import store_listing  # noqa: E402  (after the path line above, on purpose)

# The rows this script writes, with the number Partner Center gives each (its "ID" column). The numbers are checked
# against the template: a template whose numbers differ is not the file this script knows, and filling it blind
# could put a text into the wrong field.
SINGLE_ROWS = {"Description": 2, "ShortDescription": 8, "CopyrightTrademarkInformation": 12}
NUMBERED_ROWS = {"Feature": 700, "SearchTerm": 900, "DesktopScreenshotCaption": 150, "DesktopScreenshot": 100}

# Picture rows --copy-pictures-from copies: the desktop screenshots and the Store's logo images. Not the pictures of
# other device families (this app has none) and never a trailer's.
COPIED_PICTURE_PREFIXES = ("DesktopScreenshot", "StoreLogo", "PromoImage")
PICTURE_TYPE_PREFIX = "Relative path"

# The "default" column: the fourth, after Field, ID and Type.
DEFAULT_COLUMN = 3


class TemplateError(Exception):
    """The exported file is not what this script can fill (the message says what was expected)."""


def read_template(path):
    """Reads the exported listings file.

    Args:
        path: The .csv file Partner Center exported.

    Returns:
        The file's rows as lists of cells, the header row first. A leading byte order mark is dropped.

    Raises:
        TemplateError: When the file cannot be read, is not UTF-8, or does not start with the header row
            "Field, ID, Type (Type), default, <languages>".
    """
    try:
        text = io.open(path, encoding="utf-8-sig", newline="").read()
    except (OSError, UnicodeDecodeError) as error:
        raise TemplateError(f"cannot read {path} as UTF-8: {error}") from error
    rows = list(csv.reader(io.StringIO(text)))
    if not rows or [cell.strip() for cell in rows[0][:4]] != ["Field", "ID", "Type (Type)", "default"]:
        raise TemplateError(f"{path} does not start with 'Field,ID,Type (Type),default': is it Partner Center's export?")
    return rows


def _without_marks(text):
    """A text without the left-to-right marks a right-to-left listing carries, for comparing texts.

    Args:
        text: The text.

    Returns:
        The text with every U+200E removed, Windows line breaks as plain ones, and without surrounding white space.
    """
    return text.replace(store_listing.LEFT_TO_RIGHT_MARK, "").replace("\r\n", "\n").strip()


def _picture(address):
    """Which picture a picture cell names, whatever language's listing holds it.

    An export names an uploaded picture as
    ".../submissions/<submission>/listings/<the language's listing>/listingassets/<the picture>": the last part is
    the same in every language that shows the picture, the listing part never is. A cell that is not such an
    address (a file's relative path in an import with new pictures) names its picture by the whole text.

    Args:
        address: The cell's text.

    Returns:
        The picture's id, or the trimmed text; an empty string for an empty cell.
    """
    return address.strip().rsplit("/listingassets/", 1)[-1]


class Template:
    """The exported file, with what both modes of this script need to find in it.

    Attributes:
        rows: The file's rows, the header first; fill changes them in place.
        columns: Language code (lower case) -> column index.
        listings: Language code -> the kit's Listing for it.
        by_field: Field name -> its row.
    """

    def __init__(self, rows, kit):
        """Indexes the rows and checks that the file is one exported for the kit's languages.

        Args:
            rows: The file's rows (read_template).
            kit: The kit (store_listing.read_kit).

        Raises:
            TemplateError: When the file's language columns are not exactly the kit's languages.
        """
        self.rows = rows
        self.columns = {cell.strip().lower(): index for index, cell in enumerate(rows[0])
                        if index > DEFAULT_COLUMN and cell.strip()}
        self.listings = kit.by_code()
        if set(self.columns) != set(self.listings):
            raise TemplateError(
                "the template's languages (" + ", ".join(sorted(self.columns)) + ") are not the kit's ("
                + ", ".join(sorted(self.listings)) + "): the Store takes a submission's languages from its packages")
        self.by_field = {row[0].strip(): row for row in rows[1:] if row and row[0].strip()}

    def row_of(self, name, number):
        """The row of a field, checked against the number this script expects for it.

        Args:
            name: The field's name (the first column).
            number: The ID Partner Center gives that field.

        Returns:
            The row.

        Raises:
            TemplateError: When the row is missing or carries another number.
        """
        row = self.by_field.get(name)
        if row is None:
            raise TemplateError(f"the template has no '{name}' row")
        if row[1].strip() != str(number):
            raise TemplateError(f"the template's '{name}' row has ID {row[1].strip()}, not {number}")
        return row

    def numbered_rows(self, prefix):
        """The rows Prefix1, Prefix2, ... in order, as far as they go.

        Args:
            prefix: The field name without its number (a key of NUMBERED_ROWS).

        Returns:
            The rows, the first one first.

        Raises:
            TemplateError: When one of them carries another number than expected.
        """
        found = []
        while f"{prefix}{len(found) + 1}" in self.by_field:
            found.append(self.row_of(f"{prefix}{len(found) + 1}", NUMBERED_ROWS[prefix] + len(found)))
        return found

    def screenshot_rows(self, count):
        """The picture rows and the caption rows of the first `count` desktop screenshots.

        Args:
            count: How many screenshots.

        Returns:
            (picture rows, caption rows), each `count` long.

        Raises:
            TemplateError: When the file has rows for fewer screenshots.
        """
        pictures = self.numbered_rows("DesktopScreenshot")
        captions = self.numbered_rows("DesktopScreenshotCaption")
        available = min(len(pictures), len(captions))
        if available < count:
            raise TemplateError(f"the template has rows for {available} screenshots, not {count}")
        return pictures[:count], captions[:count]


def picture_positions(template, picture_rows, caption_rows, in_order=None):
    """Tells, for every language, which of the kit's screenshots sits at each of its positions.

    A caption must be written under its own picture, and the pictures of a listing are not always in the kit's
    order (an import does not keep it). What tells which picture is which, in this order:

      1. The exported captions. Partner Center keeps a caption with its picture, so a caption that equals exactly
         one of the kit's captions for that language names the picture above it.
      2. `in_order`: a language whose pictures are in the kit's order.
      3. With neither (pictures just uploaded, no caption yet): the languages that have pictures must agree
         position by position, and that order is taken to be the kit's.

    A picture is the same picture in every language (_picture), so what one language tells serves all.

    Args:
        template: The exported file.
        picture_rows: Its DesktopScreenshot rows for the screenshots in question, in order.
        caption_rows: Their caption rows, still as exported.
        in_order: A language code whose pictures are in the kit's order, or None.

    Returns:
        Language code -> a list with, for each position, the index (from 0) of the kit's screenshot there. A
        language without pictures gets the kit's own order: its captions wait for its pictures.

    Raises:
        TemplateError: When `in_order` is not a language of the file or lacks a picture; when the evidence
            contradicts itself (one picture as two of the kit's screenshots, or two pictures as the same one, which
            is what languages showing different pictures look like); or when it cannot be told which screenshot a
            picture is.
    """
    count = len(picture_rows)
    index_of = {}       # picture -> index of the kit's screenshot
    told_by = {}        # picture -> who told (for the messages)

    def learn(picture, index, who):
        """Records that a picture is the kit's screenshot `index`, and stops on a contradiction."""
        if index_of.setdefault(picture, index) != index:
            raise TemplateError(f"{who} takes a picture for the kit's screenshot {index + 1}, {told_by[picture]} for "
                                f"screenshot {index_of[picture] + 1}: the captions in Partner Center are mixed up")
        told_by.setdefault(picture, who)

    def pictures_of(code):
        """The pictures of one language at the positions in question; empty strings where there is none."""
        return [_picture(row[template.columns[code]]) for row in picture_rows]

    for code, column in template.columns.items():
        expected = [_without_marks(caption) for caption in template.listings[code].captions[:count]]
        for picture, caption_row in zip(pictures_of(code), caption_rows):
            caption = _without_marks(caption_row[column])
            if picture and caption and expected.count(caption) == 1:
                learn(picture, expected.index(caption), f"{code}'s caption")

    if in_order:
        code = in_order.lower()
        if code not in template.columns:
            raise TemplateError(f"--screenshots-in-order {in_order}: the template has no such language column")
        pictures = pictures_of(code)
        present = sum(1 for picture in pictures if picture)
        if present < count:
            raise TemplateError(f"--screenshots-in-order {code}: it has {present} of the {count} screenshots")
        for index, picture in enumerate(pictures):
            learn(picture, index, f"--screenshots-in-order {code}")

    if not index_of:
        # Nothing tells the pictures apart: fresh uploads without captions. Then every language that has pictures
        # must show the same ones in the same order, and that order is taken as the kit's.
        reference = None
        for code in template.columns:
            pictures = pictures_of(code)
            if not any(pictures):
                continue
            if reference is None:
                reference = (code, pictures)
            elif pictures != reference[1]:
                raise TemplateError(
                    f"the first {count} screenshots of {code} are not those of {reference[0]}, or are in another "
                    "order, and no caption tells which picture is which. Pass --screenshots-in-order LANGUAGE with "
                    "a language whose pictures are in the kit's order")
        if reference:
            for index, picture in enumerate(reference[1]):
                if picture:
                    learn(picture, index, f"{reference[0]}'s order")

    # Two pictures that count as the same screenshot: languages that show different pictures.
    by_index = {}
    for picture, index in index_of.items():
        by_index.setdefault(index, []).append(picture)
    clashes = [index for index, pictures in by_index.items() if len(pictures) > 1]
    if clashes:
        index = min(clashes)
        raise TemplateError(
            f"two different pictures count as the kit's screenshot {index + 1} ("
            + " and ".join(sorted(told_by[picture] for picture in by_index[index]))
            + "): the languages do not show the same pictures")

    positions = {}
    for code in template.columns:
        pictures = pictures_of(code)
        if not any(pictures):
            positions[code] = list(range(count))
            continue
        unknown = [str(position + 1) for position, picture in enumerate(pictures) if picture not in index_of]
        if unknown:
            raise TemplateError(
                f"{code}: it cannot be told which of the kit's screenshots its picture(s) " + ", ".join(unknown)
                + " are (no caption names them, or a picture is missing). Pass --screenshots-in-order LANGUAGE with a "
                "language whose pictures are in the kit's order")
        order = [index_of[picture] for picture in pictures]
        if sorted(order) != list(range(count)):
            raise TemplateError(f"{code}: its {count} screenshots are not the kit's {count} (one is there twice)")
        positions[code] = order
    return positions


def fill(rows, kit, screenshots=0, copy_pictures_from=None, in_order=None):
    """Fills the template's cells from the kit, in place.

    Args:
        rows: The template's rows (read_template); changed in place.
        kit: The kit (store_listing.read_kit).
        screenshots: How many desktop screenshots the listings have: that many captions are written, each under
            its own picture (picture_positions), and the caption rows after them are emptied. 0 leaves every
            caption as exported.
        copy_pictures_from: A language code of the template. Its screenshot and logo cells are copied into every
            other language's cell that is empty. None copies nothing.
        in_order: A language code of the template whose pictures are in the kit's order, for telling the pictures
            apart when the captions cannot. None when the captions can, or every language agrees. Needs
            `screenshots` above 0.

    Returns:
        A list of sentences saying what was written, and which languages' screenshots are out of the kit's order,
        for the caller to print.

    Raises:
        TemplateError: When the template's languages are not the kit's, a row this script writes is missing or
            carries another number, the kit has fewer captions than `screenshots`, `copy_pictures_from` or
            `in_order` is not one of the template's languages, or it cannot be told which picture is which
            (picture_positions has the cases).
    """
    template = Template(rows, kit)
    columns, listings = template.columns, template.listings
    report = []

    if copy_pictures_from:
        source = columns.get(copy_pictures_from.lower())
        if source is None:
            raise TemplateError(f"--copy-pictures-from {copy_pictures_from}: the template has no such language column")
        copied = 0
        for row in rows[1:]:
            name = row[0].strip() if row else ""
            is_picture = len(row) > 2 and row[2].strip().startswith(PICTURE_TYPE_PREFIX)
            if not is_picture or not name.startswith(COPIED_PICTURE_PREFIXES) or not row[source].strip():
                continue
            for column in columns.values():
                if column != source and not row[column].strip():
                    row[column] = row[source]
                    copied += 1
        report.append(f"pictures: {copied} cells filled from {copy_pictures_from.lower()}")

    # Which picture is where, before any caption is written: the answer comes from the captions as exported.
    if in_order and screenshots <= 0:
        raise TemplateError("--screenshots-in-order needs --screenshots N: it is about the first N screenshots")
    positions = {}
    if screenshots > 0:
        for listing in kit.listings:
            if len(listing.captions) < screenshots:
                raise TemplateError(f"{listing.language} has {len(listing.captions)} captions for {screenshots} screenshots")
        picture_rows, caption_rows = template.screenshot_rows(screenshots)
        positions = picture_positions(template, picture_rows, caption_rows, in_order)

    for code, column in columns.items():
        listing = listings[code]
        template.row_of("Description", SINGLE_ROWS["Description"])[column] = listing.description
        template.row_of("ShortDescription", SINGLE_ROWS["ShortDescription"])[column] = listing.short_description

        # Lists: the kit's values in order, and every row after them emptied, so a shorter list than last time
        # leaves no stale entry behind (an empty text cell removes that entry from the listing).
        lists = [("Feature", listing.features), ("SearchTerm", listing.search_terms)]
        if screenshots > 0:
            # Each position gets the caption of the picture that sits there, whatever the pictures' order is.
            lists.append(("DesktopScreenshotCaption", [listing.captions[index] for index in positions[code]]))
        for prefix, values in lists:
            available = template.numbered_rows(prefix)
            if len(values) > len(available):
                raise TemplateError(f"{listing.language} has {len(values)} {prefix} values; the template has {len(available)} rows")
            for index, row in enumerate(available):
                row[column] = values[index] if index < len(values) else ""
        report.append(f"{code}: description {len(listing.description)} characters, {len(listing.features)} features, "
                      f"{len(listing.search_terms)} search terms"
                      + (f", {screenshots} captions" if screenshots > 0 else ""))

    # One line for every language: the "default" column is what a language without its own text gets.
    copyright_row = template.row_of("CopyrightTrademarkInformation", SINGLE_ROWS["CopyrightTrademarkInformation"])
    copyright_row[DEFAULT_COLUMN] = kit.copyright
    for column in columns.values():
        copyright_row[column] = ""
    report.append(f"default: copyright line ({len(kit.copyright)} characters)")

    for code, order in positions.items():
        if order != list(range(len(order))):
            report.append(
                f"NOTE {code}: its screenshots are not in the kit's order (they are the kit's "
                + ", ".join(str(index + 1) for index in order) + "). Each caption was written under its own "
                "picture. Put the pictures in order on that language's listing page in Partner Center: drag one "
                "onto another and the two change places. Then export again and run this script with --check")
    return report


def check(rows, kit, screenshots=0):
    """Compares an export with the kit: is Partner Center holding what the repository says?

    Args:
        rows: The exported file's rows (read_template).
        kit: The kit (store_listing.read_kit).
        screenshots: How many desktop screenshots the listings must have. With it, every language must show that
            many pictures, the same ones in the same order in every language, with the kit's captions in the
            kit's order (which, captions staying with their pictures, means the pictures are in the kit's order).
            0 checks the texts only.

    Returns:
        A list of sentences, one per difference; empty when the export is what the kit says.

    Raises:
        TemplateError: When the file's languages are not the kit's, or a row is missing or carries another number.
    """
    template = Template(rows, kit)
    problems = []

    def compare(code, field, number, expected):
        """Records a difference between one cell and the kit's text for it.

        The words are compared first, then the left-to-right marks: a right-to-left listing must really carry
        them, or the Store shows "Notepad++" as "++Notepad".
        """
        cell = template.row_of(field, number)[template.columns[code]]
        actual = _without_marks(cell)
        if actual != _without_marks(expected):
            problems.append(f"{code}: {field} is not the kit's ({len(actual)} characters there, "
                            f"{len(_without_marks(expected))} in the kit)")
        elif cell.count(store_listing.LEFT_TO_RIGHT_MARK) != expected.count(store_listing.LEFT_TO_RIGHT_MARK):
            problems.append(f"{code}: {field} has the kit's words, but not the left-to-right marks of a "
                            "right-to-left listing")

    for code, column in template.columns.items():
        listing = template.listings[code]
        compare(code, "Description", SINGLE_ROWS["Description"], listing.description)
        compare(code, "ShortDescription", SINGLE_ROWS["ShortDescription"], listing.short_description)
        for prefix, values in (("Feature", listing.features), ("SearchTerm", listing.search_terms)):
            for index in range(len(template.numbered_rows(prefix))):
                compare(code, f"{prefix}{index + 1}", NUMBERED_ROWS[prefix] + index, values[index] if index < len(values) else "")
        copyright_row = template.row_of("CopyrightTrademarkInformation", SINGLE_ROWS["CopyrightTrademarkInformation"])
        if (copyright_row[column].strip() or copyright_row[DEFAULT_COLUMN].strip()) != kit.copyright:
            problems.append(f"{code}: the copyright line is not the kit's")

    if screenshots > 0:
        picture_rows, caption_rows = template.screenshot_rows(screenshots)
        reference = None    # the first language with all its pictures
        captioned = None    # the first one that also has the kit's captions in the kit's order
        for code, column in template.columns.items():
            pictures = [_picture(row[column]) for row in picture_rows]
            if not all(pictures):
                problems.append(f"{code}: it has {sum(1 for picture in pictures if picture)} of the {screenshots} screenshots")
                continue
            # Which pictures: every language must show the same ones. Their order is judged below, by the captions,
            # so that one language out of order is named once and the others are not blamed for differing from it.
            if reference is None:
                reference = (code, pictures)
            elif sorted(pictures) != sorted(reference[1]):
                problems.append(f"{code}: it does not show the same {screenshots} pictures as {reference[0]}")
            expected = [_without_marks(caption) for caption in template.listings[code].captions[:screenshots]]
            actual = [_without_marks(row[column]) for row in caption_rows]
            if actual != expected:
                if sorted(actual) == sorted(expected):
                    order = ", ".join(str(expected.index(caption) + 1) for caption in actual)
                    problems.append(f"{code}: its screenshots are not in the kit's order (they are the kit's {order}); "
                                    "drag them into order on its listing page")
                else:
                    problems.append(f"{code}: its screenshot captions are not the kit's"
                                    + (f", and its pictures are in another order than those of {reference[0]}"
                                       if pictures != reference[1] and sorted(pictures) == sorted(reference[1]) else ""))
            else:
                # The kit's captions in the kit's order. Then the pictures must be in one order in all such
                # languages, or in one of them captions sit under the wrong pictures.
                if captioned is None:
                    captioned = (code, pictures)
                elif pictures != captioned[1]:
                    problems.append(f"{code}: it has the kit's captions in order like {captioned[0]}, but its pictures "
                                    "are in another order: captions sit under the wrong pictures in one of the two")
                if any(row[column].count(store_listing.LEFT_TO_RIGHT_MARK) != caption.count(store_listing.LEFT_TO_RIGHT_MARK)
                       for row, caption in zip(caption_rows, template.listings[code].captions)):
                    problems.append(f"{code}: a screenshot caption has the kit's words, but not the left-to-right "
                                    "marks of a right-to-left listing")
    return problems


def write_csv(rows, path):
    """Writes the rows the way Partner Center writes its own export: UTF-8 with a byte order mark, CRLF, minimal quoting.

    Args:
        rows: The rows, the header first.
        path: The file to write (replaced when it exists).

    Raises:
        OSError: When the file cannot be written.
    """
    buffer = io.StringIO()
    csv.writer(buffer, lineterminator="\r\n", quoting=csv.QUOTE_MINIMAL).writerows(rows)
    with io.open(path, "w", encoding="utf-8-sig", newline="") as output:
        output.write(buffer.getvalue())


def main():
    """Parses the command line, then fills the template or checks it; exits with the code described above."""
    parser = argparse.ArgumentParser(description="Fill Partner Center's exported Store listings .csv from docs/store-listing.md, or check one against it.")
    parser.add_argument("--template", required=True, help="the .csv that Partner Center's Export listings saved")
    parser.add_argument("--output", default=None, help="the .csv to write, for Import listings > Import .csv")
    parser.add_argument("--check", action="store_true",
                        help="write nothing: say whether the exported file is what the kit says (exit 0) or not (exit 1)")
    parser.add_argument("--kit", default=None, help="the listing kit (default: docs/store-listing.md)")
    parser.add_argument("--screenshots", type=int, default=0,
                        help="how many desktop screenshots the listings have; that many captions are written or checked")
    parser.add_argument("--copy-pictures-from", default=None, metavar="LANGUAGE",
                        help="copy that language's screenshot and logo addresses into languages that have none")
    parser.add_argument("--screenshots-in-order", default=None, metavar="LANGUAGE",
                        help="a language whose screenshots are in the kit's order, when no caption tells the pictures apart")
    arguments = parser.parse_args()
    if arguments.screenshots < 0:
        parser.error("--screenshots cannot be negative")
    if arguments.check and (arguments.output or arguments.copy_pictures_from or arguments.screenshots_in_order):
        parser.error("--check only reads: it takes --template, --kit and --screenshots")
    if not arguments.check and not arguments.output:
        parser.error("--output is needed (or --check, to compare the exported file with the kit)")

    try:
        kit = store_listing.read_kit(arguments.kit)
        rows = read_template(arguments.template)
    except (store_listing.KitError, TemplateError) as error:
        print(error)
        sys.exit(2)

    problems = kit.problems()
    if problems:
        for language, problem in problems:
            print(f"{language}: {problem}")
        print("The kit's texts do not fit the Store's limits; nothing was " + ("checked." if arguments.check else "written."))
        sys.exit(1 if not arguments.check else 2)

    if arguments.check:
        try:
            differences = check(rows, kit, arguments.screenshots)
        except TemplateError as error:
            print(error)
            sys.exit(2)
        for difference in differences:
            print(difference)
        print(f"{len(differences)} difference(s) between {arguments.template} and the kit." if differences
              else f"{arguments.template} is what the kit says"
              + (f", with {arguments.screenshots} screenshots in the kit's order in every language." if arguments.screenshots else "."))
        sys.exit(1 if differences else 0)

    try:
        report = fill(rows, kit, arguments.screenshots, arguments.copy_pictures_from, arguments.screenshots_in_order)
        write_csv(rows, arguments.output)
    except TemplateError as error:
        print(error)
        sys.exit(2)
    except OSError as error:
        print(f"cannot write {arguments.output}: {error}")
        sys.exit(2)

    for line in report:
        print(line)
    print(f"wrote {arguments.output}")


if __name__ == "__main__":
    main()
