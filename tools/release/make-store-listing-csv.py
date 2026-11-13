"""Fill Partner Center's exported Store listings file from the listing kit (docs/store-listing.md).

Partner Center's listing pages take some forty fields per language by hand. Its *Export listings* / *Import listings*
pair takes them all at once as a .csv with one row per field and one column per language. This script fills that
file from the kit, so what the Store shows is what the repository says, in every language:

    Description, ShortDescription      each language's own column
    Feature1 .. Feature20              the kit's features in order; the rows after the last one are emptied
    SearchTerm1 .. SearchTerm7         likewise
    CopyrightTrademarkInformation      the "default" column, which serves every language whose own cell is empty
    DesktopScreenshotCaption1 .. n     with --screenshots n: the kit's first n captions; the rows after are emptied

Everything else stays exactly as exported: the first three columns (Partner Center refuses a file whose Field, ID
or Type changed), the title, the release notes (the release pipeline writes "What's new" per release), and every
picture. Pictures that are already uploaded appear in an export as web addresses; --copy-pictures-from en-us copies
the screenshots' and logos' addresses from that language into every language that has none, so one upload serves
all listings. Trailer rows are never touched: emptying one deletes the trailer in Partner Center.

The format is the one Partner Center itself writes and reads (PapaParse on its pages, checked 2026-11-13): UTF-8 with
a byte order mark, comma-separated, CRLF, a field quoted when it holds a comma, a quote or a line break.

The template must be a file exported for the same submission: its language columns must be exactly the kit's
languages (the Store takes them from the packages), or the script stops and names the difference.

Usage:
    python tools/release/make-store-listing-csv.py --template exported.csv --output import.csv
                                                   [--kit docs/store-listing.md] [--screenshots N]
                                                   [--copy-pictures-from LANGUAGE]
Exit code 0 when the file was written, 1 when the kit's texts do not fit the Store's limits, 2 when the kit or the
template cannot be used (the message says why). Nothing is written unless everything fits.
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


def fill(rows, kit, screenshots=0, copy_pictures_from=None):
    """Fills the template's cells from the kit, in place.

    Args:
        rows: The template's rows (read_template); changed in place.
        kit: The kit (store_listing.read_kit).
        screenshots: How many desktop screenshots the listings have: that many captions are written, and the
            caption rows after them are emptied. 0 leaves every caption as exported.
        copy_pictures_from: A language code of the template. Its screenshot and logo cells are copied into every
            other language's cell that is empty. None copies nothing.

    Returns:
        A list of sentences saying what was written, for the caller to print.

    Raises:
        TemplateError: When the template's languages are not the kit's, a row this script writes is missing or
            carries another number, the kit has fewer captions than `screenshots`, or `copy_pictures_from` is
            not one of the template's languages.
    """
    header = rows[0]
    columns = {cell.strip().lower(): index for index, cell in enumerate(header) if index >= 4 and cell.strip()}
    listings = kit.by_code()
    if set(columns) != set(listings):
        raise TemplateError(
            "the template's languages (" + ", ".join(sorted(columns)) + ") are not the kit's ("
            + ", ".join(sorted(listings)) + "): the Store takes a submission's languages from its packages")

    by_field = {row[0].strip(): row for row in rows[1:] if row and row[0].strip()}

    def row_of(name, number):
        """The template's row for a field, checked against the number this script expects for it."""
        row = by_field.get(name)
        if row is None:
            raise TemplateError(f"the template has no '{name}' row")
        if row[1].strip() != str(number):
            raise TemplateError(f"the template's '{name}' row has ID {row[1].strip()}, not {number}")
        return row

    def numbered_rows(prefix):
        """The template's rows Prefix1, Prefix2, ... in order, as far as they go."""
        found = []
        while f"{prefix}{len(found) + 1}" in by_field:
            found.append(row_of(f"{prefix}{len(found) + 1}", NUMBERED_ROWS[prefix] + len(found)))
        return found

    report = []
    for code, column in columns.items():
        listing = listings[code]
        row_of("Description", SINGLE_ROWS["Description"])[column] = listing.description
        row_of("ShortDescription", SINGLE_ROWS["ShortDescription"])[column] = listing.short_description

        # Lists: the kit's values in order, and every row after them emptied, so a shorter list than last time
        # leaves no stale entry behind (an empty text cell removes that entry from the listing).
        lists = [("Feature", listing.features), ("SearchTerm", listing.search_terms)]
        if screenshots > 0:
            if len(listing.captions) < screenshots:
                raise TemplateError(f"{listing.language} has {len(listing.captions)} captions for {screenshots} screenshots")
            lists.append(("DesktopScreenshotCaption", listing.captions[:screenshots]))
        for prefix, values in lists:
            available = numbered_rows(prefix)
            if len(values) > len(available):
                raise TemplateError(f"{listing.language} has {len(values)} {prefix} values; the template has {len(available)} rows")
            for index, row in enumerate(available):
                row[column] = values[index] if index < len(values) else ""
        report.append(f"{code}: description {len(listing.description)} characters, {len(listing.features)} features, "
                      f"{len(listing.search_terms)} search terms"
                      + (f", {screenshots} captions" if screenshots > 0 else ""))

    # One line for every language: the "default" column is what a language without its own text gets.
    copyright_row = row_of("CopyrightTrademarkInformation", SINGLE_ROWS["CopyrightTrademarkInformation"])
    copyright_row[3] = kit.copyright
    for column in columns.values():
        copyright_row[column] = ""
    report.append(f"default: copyright line ({len(kit.copyright)} characters)")

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

    return report


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
    """Parses the command line, fills the template and writes the result; exits with the code described above."""
    parser = argparse.ArgumentParser(description="Fill Partner Center's exported Store listings .csv from docs/store-listing.md.")
    parser.add_argument("--template", required=True, help="the .csv that Partner Center's Export listings saved")
    parser.add_argument("--output", required=True, help="the .csv to write, for Import listings > Import .csv")
    parser.add_argument("--kit", default=None, help="the listing kit (default: docs/store-listing.md)")
    parser.add_argument("--screenshots", type=int, default=0,
                        help="how many desktop screenshots the listings have; that many captions are written")
    parser.add_argument("--copy-pictures-from", default=None, metavar="LANGUAGE",
                        help="copy that language's screenshot and logo addresses into languages that have none")
    arguments = parser.parse_args()
    if arguments.screenshots < 0:
        parser.error("--screenshots cannot be negative")

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
        print("The kit's texts do not fit the Store's limits; nothing was written.")
        sys.exit(1)

    try:
        report = fill(rows, kit, arguments.screenshots, arguments.copy_pictures_from)
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
