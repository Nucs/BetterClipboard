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
