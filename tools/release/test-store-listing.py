"""Tests of the Store listing tools: the kit's reader, its checker and the listings file generator.

The Microsoft Store shows what Partner Center was given, and Partner Center is given a file that
make-store-listing-csv.py fills from docs/store-listing.md. A mistake there is public, in five languages, so the
three scripts are run here the way a release would run them, against the repository's own kit and against small
broken copies of it:

    the repository's kit fits every limit (check-store-listing.py exits 0);
    the generator fills Partner Center's export (packaging/msix/Listing/listings-template.csv, a pristine export
    of 2026-11-13) with the kit's texts, in the right rows and columns, and leaves everything else as exported;
    pictures already uploaded for one language are copied to the others, and never a trailer;
    a text beyond a limit, a kit without one of its parts, a template of another product (other languages, another
    row number) are refused, with the exit code the scripts document and without a file written.

No network and nothing outside a temporary folder. Usage:  python tools/release/test-store-listing.py
Exit code 0 when every check passes, 1 otherwise (each failed check is printed with what was expected).
"""

import csv
import io
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))

import store_listing  # noqa: E402  (after the path line above, on purpose)

KIT = ROOT / "docs" / "store-listing.md"
TEMPLATE = ROOT / "packaging" / "msix" / "Listing" / "listings-template.csv"
CHECKER = HERE / "check-store-listing.py"
GENERATOR = HERE / "make-store-listing-csv.py"

failures = []


def check(name, condition, detail=""):
    """Records one check and prints its result.

    Args:
        name: What is being checked, as a sentence fragment.
        condition: Whether it holds.
        detail: What was found instead, shown only for a failed check.
    """
    print(("PASS  " if condition else "FAIL  ") + name + ("" if condition or not detail else f"  [{detail}]"))
    if not condition:
        failures.append(name)


def run(script, *arguments):
    """Runs one of the scripts with this Python.

    Args:
        script: The script's path.
        arguments: Its command-line arguments.

    Returns:
        (exit code, everything it printed).
    """
    done = subprocess.run([sys.executable, "-X", "utf8", str(script), *map(str, arguments)],
                          capture_output=True, text=True, encoding="utf-8")
    return done.returncode, done.stdout + done.stderr


def read_rows(path):
    """Reads a listings file.

    Args:
        path: The .csv file.

    Returns:
        Its rows as lists of cells, the header first; a byte order mark is dropped.
    """
    return list(csv.reader(io.StringIO(io.open(path, encoding="utf-8-sig", newline="").read())))


def cell(rows, field, column):
    """One cell of a listings file.

    Args:
        rows: The file's rows.
        field: The row's name in the first column.
        column: The column's name in the header row.

    Returns:
        The cell's text, or None when the row or the column is missing.
    """
    header = [name.strip().lower() for name in rows[0]]
    if column not in header:
        return None
    for row in rows[1:]:
        if row and row[0] == field:
            return row[header.index(column)]
    return None


def main():
    """Runs every check and exits with the code described above."""
    kit = store_listing.read_kit(KIT)
    codes = [listing.code for listing in kit.listings]
    check("the kit has the five languages the package declares",
          sorted(codes) == ["en-us", "es", "he", "ru", "zh-hans"], str(codes))
    check("the repository's kit fits every limit", not kit.problems(), str(kit.problems()))
    code, output = run(CHECKER)
    check("check-store-listing.py passes on the repository's kit", code == 0 and output.rstrip().endswith("ok"), output[-200:])

    # --- right-to-left listings: the signs that end a Latin word stay with it -------------------------------------
    # A Hebrew Store page showed "Notepad++" as "++Notepad" (seen 2026-11-13 in a render of the listing).
    mark = store_listing.LEFT_TO_RIGHT_MARK
    keep = store_listing.keep_trailing_signs
    check("a left-to-right mark follows the signs that end a Latin word",
          keep("Notepad++, and") == "Notepad++" + mark + ", and" and keep("C#") == "C#" + mark)
    check("signs inside a word, and signs after a digit, are left alone",
          keep("Win+V") == "Win+V" and keep("C++17") == "C++17" and keep("60+") == "60+" and keep("+") == "+")
    check("finishing a text twice changes nothing", keep(keep("Notepad++ C#.")) == keep("Notepad++ C#."))
    check("the kit itself holds no invisible mark", mark not in KIT.read_text(encoding="utf-8"))
    right_to_left = [listing.code for listing in kit.listings if listing.right_to_left]
    check("Hebrew is the one right-to-left listing", right_to_left == ["he"], str(right_to_left))
    by_code = kit.by_code()
    check("its description and a feature keep \"Notepad++\" together",
          "Notepad++" + mark in by_code["he"].description
          and any("Notepad++" + mark in feature for feature in by_code["he"].features))
    check("its search terms, and every other listing, carry no mark",
          not any(mark in term for term in by_code["he"].search_terms)
          and not any(mark in text for listing in kit.listings if not listing.right_to_left
                      for text in [listing.short_description, listing.description, *listing.features, *listing.captions]))

    with tempfile.TemporaryDirectory() as folder:
        folder = Path(folder)

        # --- the generator on the pristine export -----------------------------------------------------------------
        out = folder / "import.csv"
        code, output = run(GENERATOR, "--template", TEMPLATE, "--output", out, "--screenshots", 6)
        check("the generator fills the pristine export", code == 0 and out.exists(), output[-300:])
        if out.exists():
            raw = out.read_bytes()
            check("its file starts with a byte order mark and uses CRLF, like Partner Center's own",
                  raw.startswith(b"\xef\xbb\xbf") and b"\r\n" in raw and b"\r\r\n" not in raw)
            rows = read_rows(out)
            before = read_rows(TEMPLATE)
            check("the first three columns are untouched", [r[:3] for r in rows] == [r[:3] for r in before])
            english = kit.by_code()["en-us"]
            hebrew = kit.by_code()["he"]
            check("the description is in each language's own column",
                  cell(rows, "Description", "en-us") == english.description
                  and cell(rows, "Description", "he") == hebrew.description)
            check("a description keeps its paragraphs", "\n\n" in (cell(rows, "Description", "en-us") or ""))
            hebrew_column = [name.strip().lower() for name in rows[0]].index("he")
            check("the file's left-to-right marks are all in the Hebrew column, and the description has one",
                  mark in (cell(rows, "Description", "he") or "")
                  and not any(mark in value for row in rows[1:] for index, value in enumerate(row) if index != hebrew_column))
            check("the short description is there", cell(rows, "ShortDescription", "ru") == kit.by_code()["ru"].short_description)
            check("features are in order, and the rows after the last one are empty",
                  cell(rows, "Feature1", "en-us") == english.features[0]
                  and cell(rows, f"Feature{len(english.features)}", "en-us") == english.features[-1]
                  and cell(rows, f"Feature{len(english.features) + 1}", "en-us") == "")
            check("search terms are in order", [cell(rows, f"SearchTerm{n}", "zh-hans") for n in range(1, 8)]
                  == kit.by_code()["zh-hans"].search_terms)
            check("captions follow the screenshots, and no more than asked",
                  cell(rows, "DesktopScreenshotCaption6", "es") == kit.by_code()["es"].captions[5]
                  and cell(rows, "DesktopScreenshotCaption7", "es") == "")
            check("the copyright line is in the default column only",
                  cell(rows, "CopyrightTrademarkInformation", "default") == kit.copyright
                  and cell(rows, "CopyrightTrademarkInformation", "en-us") == "")
            check("the title and the release notes stay as exported",
                  cell(rows, "Title", "en-us") == cell(before, "Title", "en-us")
                  and cell(rows, "ReleaseNotes", "en-us") == cell(before, "ReleaseNotes", "en-us"))

        # Without --screenshots the captions are left alone (a listing whose pictures are not uploaded yet).
        plain = folder / "plain.csv"
        code, _ = run(GENERATOR, "--template", TEMPLATE, "--output", plain)
        check("without --screenshots no caption is written",
              code == 0 and cell(read_rows(plain), "DesktopScreenshotCaption1", "en-us") == "")

        # --- pictures uploaded for one language reach the others, trailers never ----------------------------------
        with_pictures = read_rows(TEMPLATE)
        header = [name.strip().lower() for name in with_pictures[0]]
        english_column = header.index("en-us")
        for row in with_pictures[1:]:
            if row[0] in ("DesktopScreenshot1", "StoreLogo300x300", "Trailer1"):
                row[english_column] = f"https://example.invalid/{row[0]}.png"
        pictures_template = folder / "pictures.csv"
        buffer = io.StringIO()
        csv.writer(buffer, lineterminator="\r\n").writerows(with_pictures)
        pictures_template.write_text(buffer.getvalue(), encoding="utf-8-sig", newline="")
        copied = folder / "copied.csv"
        code, output = run(GENERATOR, "--template", pictures_template, "--output", copied, "--copy-pictures-from", "en-us")
        rows = read_rows(copied) if copied.exists() else [[]]
        check("--copy-pictures-from copies screenshots and logos to every language",
              code == 0 and cell(rows, "DesktopScreenshot1", "he") == "https://example.invalid/DesktopScreenshot1.png"
              and cell(rows, "StoreLogo300x300", "zh-hans") == "https://example.invalid/StoreLogo300x300.png", output[-200:])
        check("and never a trailer", cell(rows, "Trailer1", "he") == "")
        code, output = run(GENERATOR, "--template", TEMPLATE, "--output", folder / "x.csv", "--copy-pictures-from", "fr")
        check("a language the template lacks is refused (exit 2)", code == 2 and not (folder / "x.csv").exists(), output[-200:])

        # --- a caption goes under its own picture, whatever order the pictures are in ------------------------------
        # What Partner Center did on 2026-11-13: after an import the English listing's six pictures came back in
        # another order, each with its caption, while other languages kept the kit's order. And an import that sent
        # the right order did not restore it: the order is only put right by dragging, on the listing page.
        def listing_file(name, order_of, captions=True):
            """Writes an export in which every language has six pictures, with or without their captions.

            Addresses have the shape of a real export: one listing per language, and the same picture id in every
            language that shows the picture (".../listings/<language>/listingassets/<picture>").

            Args:
                name: The file's name in the temporary folder.
                order_of: Language code -> the picture numbers (1 to 6) of that language, in its order.
                captions: Whether picture n carries the kit's caption n (the way Partner Center keeps a caption
                    with its picture), or no picture has a caption yet.

            Returns:
                The file's path.
            """
            exported = read_rows(TEMPLATE)
            header = [name_.strip().lower() for name_ in exported[0]]
            by_field = {row[0]: row for row in exported[1:] if row and row[0]}
            for language in codes:
                column = header.index(language)
                for position, picture in enumerate(order_of(language), start=1):
                    by_field[f"DesktopScreenshot{position}"][column] = (
                        f"https://example.invalid/listings/{language}/listingassets/{picture}")
                    if captions:
                        by_field[f"DesktopScreenshotCaption{position}"][column] = by_code[language].captions[picture - 1]
            return write_rows(folder / name, exported)

        def write_rows(path, rows_):
            """Writes rows as a listings file (byte order mark, CRLF) and returns the path."""
            text = io.StringIO()
            csv.writer(text, lineterminator="\r\n").writerows(rows_)
            path.write_text(text.getvalue(), encoding="utf-8-sig", newline="")
            return path

        def screenshots_of(rows_, language):
            """The six pictures of one language in a listings file, in order, as "<listing's language>/<picture>"."""
            addresses = [cell(rows_, f"DesktopScreenshot{n}", language) or "" for n in range(1, 7)]
            return [address.replace("https://example.invalid/listings/", "").replace("/listingassets/", "/")
                    for address in addresses]

        def captions_of(rows_, language):
            """The six captions of one language in a listings file, in order."""
            return [cell(rows_, f"DesktopScreenshotCaption{n}", language) for n in range(1, 7)]

        def english_first_wrong(language):
            """The order of 2026-11-13: English rearranged, every other language in the kit's order."""
            return [5, 2, 6, 3, 4, 1] if language == "en-us" else [1, 2, 3, 4, 5, 6]

        rearranged = listing_file("rearranged.csv", english_first_wrong)
        code, output = run(GENERATOR, "--template", rearranged, "--output", folder / "r1.csv", "--screenshots", 6)
        rows = read_rows(folder / "r1.csv") if (folder / "r1.csv").exists() else [[]]
        check("a language whose pictures are out of the kit's order gets each caption under its own picture",
              code == 0 and screenshots_of(rows, "en-us") == [f"en-us/{n}" for n in (5, 2, 6, 3, 4, 1)]
              and captions_of(rows, "en-us") == [by_code["en-us"].captions[n - 1] for n in (5, 2, 6, 3, 4, 1)]
              and captions_of(rows, "he") == by_code["he"].captions, output[-300:])
        check("and the script says which language to put in order", "NOTE en-us" in output and "NOTE ru" not in output,
              output[-500:])
        code, output = run(GENERATOR, "--check", "--template", folder / "r1.csv", "--screenshots", 6)
        check("--check finds that language out of the kit's order (exit 1)",
              code == 1 and "en-us: its screenshots are not in the kit's order" in output and "5, 2, 6, 3, 4, 1" in output,
              output[-400:])

        same = listing_file("same.csv", lambda language: [1, 2, 3, 4, 5, 6])
        code, output = run(GENERATOR, "--template", same, "--output", folder / "r2.csv", "--screenshots", 6)
        rows = read_rows(folder / "r2.csv") if (folder / "r2.csv").exists() else [[]]
        check("the same pictures in the kit's order everywhere are filled without a note, each language's own addresses kept",
              code == 0 and "NOTE" not in output and screenshots_of(rows, "he") == [f"he/{n}" for n in range(1, 7)]
              and captions_of(rows, "en-us") == by_code["en-us"].captions, output[-300:])
        code, output = run(GENERATOR, "--check", "--template", folder / "r2.csv", "--screenshots", 6)
        check("--check accepts a file that is what the kit says (exit 0)", code == 0, output[-300:])

        edited = read_rows(folder / "r2.csv")
        edited_header = [name_.strip().lower() for name_ in edited[0]]
        next(row for row in edited[1:] if row and row[0] == "Feature2")[edited_header.index("ru")] += " and more"
        code, output = run(GENERATOR, "--check", "--template", write_rows(folder / "edited.csv", edited), "--screenshots", 6)
        check("--check names a text that is not the kit's (exit 1)", code == 1 and "ru: Feature2" in output, output[-300:])

        unmarked = [[value.replace(mark, "") for value in row] for row in read_rows(folder / "r2.csv")]
        code, output = run(GENERATOR, "--check", "--template", write_rows(folder / "unmarked.csv", unmarked), "--screenshots", 6)
        check("--check notices a right-to-left listing without its marks (exit 1)",
              code == 1 and "he: Description" in output and "left-to-right marks" in output, output[-300:])

        # What writing captions by position did: the kit's captions in order, over pictures in another order.
        misplaced = read_rows(folder / "r2.csv")
        misplaced_header = [name_.strip().lower() for name_ in misplaced[0]]
        column = misplaced_header.index("en-us")
        for position, picture in enumerate((5, 2, 6, 3, 4, 1), start=1):
            next(row for row in misplaced[1:] if row and row[0] == f"DesktopScreenshot{position}")[column] = (
                f"https://example.invalid/listings/en-us/listingassets/{picture}")
        write_rows(folder / "misplaced.csv", misplaced)
        code, output = run(GENERATOR, "--check", "--template", folder / "misplaced.csv", "--screenshots", 6)
        check("--check notices captions under the wrong pictures (exit 1)",
              code == 1 and "captions sit under the wrong pictures" in output, output[-300:])
        code, output = run(GENERATOR, "--template", folder / "misplaced.csv", "--output", folder / "r2b.csv", "--screenshots", 6)
        check("and the generator will not build on such a file (exit 2)",
              code == 2 and "mixed up" in output and not (folder / "r2b.csv").exists(), output[-300:])

        alike = listing_file("alike.csv", lambda language: [2, 1, 3, 4, 5, 6])
        code, output = run(GENERATOR, "--template", alike, "--output", folder / "r3.csv", "--screenshots", 6)
        rows = read_rows(folder / "r3.csv") if (folder / "r3.csv").exists() else [[]]
        check("pictures rearranged alike in every language: captions under their pictures, and a note for each",
              code == 0 and captions_of(rows, "es") == [by_code["es"].captions[n - 1] for n in (2, 1, 3, 4, 5, 6)]
              and output.count("NOTE ") == len(codes), output[-300:])

        # Another picture in one language: its third screenshot is not the others' third one.
        other = read_rows(listing_file("other.csv", lambda language: [1, 2, 3, 4, 5, 6]))
        other_header = [name_.strip().lower() for name_ in other[0]]
        next(row for row in other[1:] if row and row[0] == "DesktopScreenshot3")[other_header.index("es")] = (
            "https://example.invalid/listings/es/listingassets/77")
        code, output = run(GENERATOR, "--template", write_rows(folder / "other.csv", other), "--output", folder / "r4.csv",
                           "--screenshots", 6)
        check("languages that show different pictures are refused (exit 2)",
              code == 2 and "do not show the same pictures" in output and not (folder / "r4.csv").exists(), output[-300:])

        # Captions that contradict each other: in one language two captions sit under each other's picture.
        mixed = read_rows(listing_file("mixed.csv", lambda language: [1, 2, 3, 4, 5, 6]))
        mixed_header = [name_.strip().lower() for name_ in mixed[0]]
        first = next(row for row in mixed[1:] if row and row[0] == "DesktopScreenshotCaption1")
        second = next(row for row in mixed[1:] if row and row[0] == "DesktopScreenshotCaption2")
        column = mixed_header.index("ru")
        first[column], second[column] = second[column], first[column]
        code, output = run(GENERATOR, "--template", write_rows(folder / "mixed.csv", mixed), "--output", folder / "r5.csv",
                           "--screenshots", 6)
        check("captions that contradict each other between languages are refused (exit 2)",
              code == 2 and "mixed up" in output and not (folder / "r5.csv").exists(), output[-300:])

        # No caption anywhere, and one language in another order: nothing tells the pictures apart.
        bare = listing_file("bare.csv", english_first_wrong, captions=False)
        code, output = run(GENERATOR, "--template", bare, "--output", folder / "r6.csv", "--screenshots", 6)
        check("without captions, languages in different orders are refused (exit 2)",
              code == 2 and "--screenshots-in-order" in output and not (folder / "r6.csv").exists(), output[-300:])
        code, output = run(GENERATOR, "--template", bare, "--output", folder / "r7.csv", "--screenshots", 6,
                           "--screenshots-in-order", "he")
        rows = read_rows(folder / "r7.csv") if (folder / "r7.csv").exists() else [[]]
        check("--screenshots-in-order names a language in the kit's order, and every picture gets its caption",
              code == 0 and captions_of(rows, "en-us") == [by_code["en-us"].captions[n - 1] for n in (5, 2, 6, 3, 4, 1)]
              and captions_of(rows, "ru") == by_code["ru"].captions and "NOTE en-us" in output, output[-300:])
        code, output = run(GENERATOR, "--template", same, "--output", folder / "r8.csv", "--screenshots-in-order", "he")
        check("--screenshots-in-order without --screenshots is refused (exit 2)", code == 2 and not (folder / "r8.csv").exists(),
              output[-300:])
        code, output = run(GENERATOR, "--template", TEMPLATE, "--output", folder / "r9.csv", "--screenshots", 6,
                           "--screenshots-in-order", "he")
        check("a language without the pictures cannot be the one in order (exit 2)",
              code == 2 and not (folder / "r9.csv").exists(), output[-300:])
        code, output = run(GENERATOR, "--check", "--template", same, "--output", folder / "r10.csv")
        check("--check writes nothing and refuses --output (exit 2)", code == 2 and not (folder / "r10.csv").exists(), output[-200:])

        # --- broken kits ---------------------------------------------------------------------------------------------
        text = KIT.read_text(encoding="utf-8")

        def broken(name, old, new):
            """Writes a copy of the kit with one replacement and returns its path; the replacement must apply."""
            assert old in text, f"the kit no longer holds {old[:40]!r}"
            path = folder / f"{name}.md"
            path.write_text(text.replace(old, new, 1), encoding="utf-8")
            return path

        long_reason = broken("long-reason", kit.justification, kit.justification + " x" * 10)
        code, output = run(CHECKER, long_reason)
        check("a justification over 500 characters fails the check (exit 1)", code == 1 and "runFullTrust" in output, output[-200:])
        code, output = run(GENERATOR, "--kit", long_reason, "--template", TEMPLATE, "--output", folder / "y.csv")
        check("and the generator writes nothing for such a kit (exit 1)", code == 1 and not (folder / "y.csv").exists(), output[-200:])

        long_feature = broken("long-feature", english.features[0], english.features[0] + " and more" * 30)
        code, output = run(CHECKER, long_feature)
        check("a feature over 200 characters fails the check", code == 1 and "English" in output, output[-200:])

        many_terms = broken("many-terms", ", ".join(english.search_terms), ", ".join(english.search_terms + ["one more"]))
        code, output = run(CHECKER, many_terms)
        check("an eighth search term fails the check", code == 1 and "search terms" in output, output[-200:])

        fewer_captions = broken("fewer-captions", hebrew.captions[-1] + "\n", "")
        code, output = run(CHECKER, fewer_captions)
        check("a language with fewer captions than the others fails the check", code == 1 and "captions" in output, output[-200:])

        no_sections = folder / "empty.md"
        no_sections.write_text("# nothing\n\n## 3. Listing texts\n\n## 4. Keeping it true\n", encoding="utf-8")
        code, output = run(CHECKER, no_sections)
        check("a kit without language sections cannot be read (exit 2)", code == 2, output[-200:])

        # --- templates of something else ---------------------------------------------------------------------------
        other_language = folder / "other-language.csv"
        other_language.write_text(TEMPLATE.read_text(encoding="utf-8-sig").replace(",he\n", ",fr-fr\n", 1), encoding="utf-8")
        code, output = run(GENERATOR, "--template", other_language, "--output", folder / "z.csv")
        check("a template with other languages than the kit's is refused (exit 2)",
              code == 2 and "languages" in output and not (folder / "z.csv").exists(), output[-200:])

        other_number = folder / "other-number.csv"
        other_number.write_text(TEMPLATE.read_text(encoding="utf-8-sig").replace("Description,2,Text", "Description,22,Text", 1),
                                encoding="utf-8")
        code, output = run(GENERATOR, "--template", other_number, "--output", folder / "w.csv")
        check("a template whose row numbers differ is refused (exit 2)", code == 2 and "ID" in output, output[-200:])

        not_an_export = folder / "not-an-export.csv"
        not_an_export.write_text("a,b,c\n1,2,3\n", encoding="utf-8")
        code, output = run(GENERATOR, "--template", not_an_export, "--output", folder / "v.csv")
        check("a file that is not Partner Center's export is refused (exit 2)", code == 2, output[-200:])

    print(f"{'All ' if not failures else ''}{len(failures) if failures else 'checks passed'}{' check(s) failed' if failures else ''}.")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
