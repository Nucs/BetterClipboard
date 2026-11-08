using System.Xml;

namespace BetterClipboard.Core.NotepadPlusPlus;

/// <summary>
/// Reads Notepad++'s <c>session.xml</c>: the tabs it remembers, and for the unsaved ones the backup file holding their text.
/// Facts verified against Notepad++ 8.9.1's source (<c>NppParameters::writeSession</c>, <c>getCurrentOpenedFiles</c>) and this
/// PC's file (CLAUDE.md §2.41).
/// </summary>
/// <remarks>
/// <para>
/// <b>Format.</b> <c>NotepadPlus/Session/{mainView,subView}/File</c>, one per tab: <c>filename</c> is the full path of a saved
/// file, or the tab's name for an untitled one ("new 3", localized, or the name the user gave it — then
/// <c>untitleTabRenamed="yes"</c>); <c>backupFilePath</c> is the absolute path of the backup Notepad++ keeps while the tab has
/// unsaved changes (empty otherwise); <c>encoding</c> is -1 for Unicode modes, else the code page the backup is written in.
/// Untitled tabs without text are not written at all.
/// </para>
/// <para>
/// <b>When Notepad++ writes it.</b> On exit, and — with "session snapshot and periodic backup" on (the default) — whenever a
/// tab's backup file is created or deleted. Not on every opened file, nor on every change of a tab's text (only its backup
/// file changes then). TinyXML writes it in place, so a read can catch it half-written: <see cref="Parse"/> then fails, and the
/// caller reads again after the next change.
/// </para>
/// <para>
/// <b>Safety.</b> No DTD, no external entities, at most <see cref="MaxBytes"/>; malformed input throws
/// <see cref="FormatException"/>, never anything else from the parser.
/// </para>
/// </remarks>
public static class NotepadPlusPlusSession
{
    /// <summary>Largest session file read (this PC's 328 tabs take 206 KB): a bigger one is not Notepad++'s.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>Parses a session file.</summary>
    /// <param name="stream">The file's bytes (read from the start; not closed).</param>
    /// <returns>The tabs, main view first, in tab order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The file is not a Notepad++ session (malformed XML, no session element, too large).</exception>
    /// <exception cref="IOException">Reading the stream failed.</exception>
    public static IReadOnlyList<NotepadPlusPlusSessionFile> Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaxBytes,
        };

        var files = new List<NotepadPlusPlusSessionFile>();
        bool sawSession = false;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            int view = -1;
            int index = 0;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "Session" when reader.Depth == 1:
                        sawSession = true;
                        break;
                    case "mainView" when reader.Depth == 2:
                        view = 0;
                        index = 0;
                        break;
                    case "subView" when reader.Depth == 2:
                        view = 1;
                        index = 0;
                        break;
                    case "File" when reader.Depth == 3 && view >= 0:
                        files.Add(ReadFile(reader, view, index++));
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new FormatException("Notepad++'s session file is not well-formed (it may be half-written).", ex);
        }

        if (!sawSession)
        {
            throw new FormatException("The file is not a Notepad++ session (no Session element).");
        }

        return files;
    }

    /// <summary>Whether a session <c>filename</c> names an untitled tab: anything that is not a full path is a tab's name.</summary>
    /// <param name="fileName">The <c>filename</c> attribute.</param>
    /// <returns><see langword="true"/> for an untitled tab.</returns>
    public static bool IsUntitledName(string fileName) =>
        !string.IsNullOrEmpty(fileName) && fileName.IndexOfAny(['\\', '/']) < 0 && !(fileName.Length >= 2 && fileName[1] == ':');

    /// <summary>Reads one <c>File</c> element's attributes.</summary>
    /// <param name="reader">A reader positioned on the element.</param>
    /// <param name="view">0 = main view, 1 = sub view.</param>
    /// <param name="index">The tab's position in its view.</param>
    /// <returns>The tab.</returns>
    private static NotepadPlusPlusSessionFile ReadFile(XmlReader reader, int view, int index)
    {
        string fileName = reader.GetAttribute("filename") ?? string.Empty;
        string? backup = reader.GetAttribute("backupFilePath");
        int encoding = int.TryParse(reader.GetAttribute("encoding"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : -1;
        bool renamed = string.Equals(reader.GetAttribute("untitleTabRenamed"), "yes", StringComparison.OrdinalIgnoreCase);
        return new NotepadPlusPlusSessionFile(
            fileName,
            string.IsNullOrWhiteSpace(backup) ? null : backup,
            encoding,
            IsUntitledName(fileName),
            renamed,
            view,
            index);
    }
}

/// <summary>One tab of a Notepad++ session (see <see cref="NotepadPlusPlusSession"/>).</summary>
/// <param name="FileName">A saved file's full path, or an untitled tab's name.</param>
/// <param name="BackupFilePath">The backup holding the tab's unsaved text, or <see langword="null"/> when it has none.</param>
/// <param name="Encoding">-1 for Unicode modes (UTF-8 or UTF-16 with or without BOM), else the code page its backup is written in.</param>
/// <param name="IsUntitled">Whether the tab was never saved (a "new" tab).</param>
/// <param name="IsRenamed">Whether the user renamed the untitled tab.</param>
/// <param name="View">0 = main view, 1 = sub view.</param>
/// <param name="Index">The tab's position in its view.</param>
public sealed record NotepadPlusPlusSessionFile(string FileName, string? BackupFilePath, int Encoding, bool IsUntitled, bool IsRenamed, int View, int Index);
