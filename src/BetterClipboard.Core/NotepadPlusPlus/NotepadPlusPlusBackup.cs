using System.Globalization;
using System.Text;

namespace BetterClipboard.Core.NotepadPlusPlus;

/// <summary>
/// Notepad++'s backup files of unsaved tabs: their names and their text. Verified against Notepad++ 8.9.1's source
/// (<c>FileManager::backupCurrentBuffer</c>) and the 208 backups on this PC (CLAUDE.md §2.41).
/// </summary>
/// <remarks>
/// <para>
/// <b>Names.</b> <c>&lt;tab name&gt;@yyyy-MM-dd_HHmmss</c> (local time of the tab's first backup), in the <c>backup</c> folder
/// of Notepad++'s settings folder. The name part changes when the user renames the tab (Notepad++ renames the file and keeps
/// the stamp); the stamp never does. A tab's name may itself contain '@', so the stamp is the part after the last one.
/// </para>
/// <para>
/// <b>Text.</b> An untitled tab's backup is written to <c>&lt;name&gt;.tmp</c> and swapped in with <c>ReplaceFile</c>, so a
/// reader sees the old file or the new one, never half of one (older Notepad++ versions wrote in place; the watcher waits
/// for a quiet file anyway). UTF-8 without BOM — Notepad++'s default for new documents, 199 of this PC's backups — is
/// written byte for byte; UTF-8 with BOM and UTF-16 carry their BOM; a document in a legacy character set is written in that
/// code page (the session's <c>encoding</c>).
/// </para>
/// </remarks>
public static class NotepadPlusPlusBackup
{
    /// <summary>Length of the stamp after the '@': <c>yyyy-MM-dd_HHmmss</c>.</summary>
    private const int StampLength = 17;

    /// <summary>
    /// Splits a backup file name into the tab's name and its stamp.
    /// </summary>
    /// <param name="fileName">The file name (no folder), e.g. <c>new 3@2026-11-08_101530</c>.</param>
    /// <param name="tabName">The tab's name, e.g. <c>new 3</c>.</param>
    /// <param name="stamp">The stamp, e.g. <c>2026-11-08_101530</c>.</param>
    /// <returns><see langword="false"/> when the name has no valid stamp (not a Notepad++ backup).</returns>
    public static bool TryParseName(string? fileName, out string tabName, out string stamp)
    {
        tabName = string.Empty;
        stamp = string.Empty;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        int at = fileName.LastIndexOf('@');
        if (at <= 0 || fileName.Length - at - 1 != StampLength)
        {
            return false;
        }

        var candidate = fileName[(at + 1)..];
        if (!DateTime.TryParseExact(candidate, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return false;
        }

        tabName = fileName[..at];
        stamp = candidate;
        return true;
    }

    /// <summary>
    /// Decodes a backup's bytes: by its BOM (UTF-8, UTF-16 LE/BE), else as the session's code page when it names one, else as
    /// strict UTF-8, else as the system's ANSI code page (a document Notepad++ shows as "ANSI").
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="sessionEncoding">The session's <c>encoding</c> for the tab: -1 for Unicode modes, else a code page.</param>
    /// <param name="ansiCodePage">The system's ANSI code page (Windows' <c>GetACP</c>), for documents without a BOM that are not UTF-8.</param>
    /// <returns>The text (BOM removed); empty for an empty file.</returns>
    public static string DecodeText(ReadOnlySpan<byte> bytes, int sessionEncoding, int ansiCodePage)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (sessionEncoding > 0 && CodePage(sessionEncoding) is { } named)
        {
            // Notepad++ converted the document to this character set when it wrote the backup.
            return named.GetString(bytes);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: an "ANSI" document, written in the system's code page.
            return (CodePage(ansiCodePage) ?? Encoding.Latin1).GetString(bytes);
        }
    }

    /// <summary>UTF-8 that throws on invalid bytes instead of replacing them, so ANSI text is told apart.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A code page's encoding through .NET's code-pages provider (without registering it process-wide), or <see langword="null"/>.</summary>
    /// <param name="codePage">The code page.</param>
    /// <returns>The encoding, or <see langword="null"/> when .NET does not know it.</returns>
    private static Encoding? CodePage(int codePage)
    {
        if (codePage == 65001)
        {
            return Encoding.UTF8;
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
