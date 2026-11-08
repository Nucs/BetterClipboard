using BetterClipboard.Core.Content;
using BetterClipboard.Core.Documents;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.NotepadPlusPlus;

/// <summary>
/// The names, sources and captures of the Notepad++ integration (CLAUDE.md §2.41): an unsaved tab's text
/// (<see cref="ClipOrigin.NotepadPlusPlusTab"/>) and a file opened in Notepad++ (<see cref="ClipOrigin.NotepadPlusPlusFile"/>).
/// </summary>
public static class NotepadPlusPlusSources
{
    /// <summary>The process name "Ignored apps" matches (<c>notepad++.exe</c> without its extension).</summary>
    public const string ProcessName = "notepad++";

    /// <summary>Notepad++'s executable file name, matched case-insensitively.</summary>
    public const string ExecutableName = "notepad++.exe";

    /// <summary>
    /// The source name every Notepad++ entry carries — also what Windows reports for a copy made in Notepad++ (its executable's
    /// file description is "Notepad++"), so the Notepad++ tab lists those too.
    /// </summary>
    public const string DisplayName = "Notepad++";

    /// <summary>The tracked-document scope of Notepad++'s unsaved tabs (<see cref="TrackedDocument.Scope"/>).</summary>
    public const string DocumentScope = "notepad++";

    /// <summary>
    /// The source of a Notepad++ entry. The process name is what "Ignored apps" matches, so ignoring <c>notepad++.exe</c> also
    /// keeps the integration's tabs and files out.
    /// </summary>
    /// <param name="executablePath">The Notepad++ executable, when known (the card's icon and the filter use it); may be <see langword="null"/>.</param>
    /// <returns>The source app info.</returns>
    public static SourceAppInfo Source(string? executablePath) => new(ProcessName, executablePath, DisplayName);

    /// <summary>
    /// The capture of an unsaved tab's text: <c>CF_UNICODETEXT</c> exactly as the tab holds it (its line endings included, so
    /// it merges with a copy of the whole tab made in Notepad++), the tab's name as the source title, and the backup file's
    /// write time as the version's time.
    /// </summary>
    /// <param name="text">The tab's text.</param>
    /// <param name="tabName">The tab's name ("new 3", or the name it was renamed to).</param>
    /// <param name="writtenUtc">When Notepad++ wrote this version.</param>
    /// <param name="executablePath">The Notepad++ executable, when known.</param>
    /// <returns>The capture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static ClipCapture TabCapture(string text, string? tabName, DateTimeOffset writtenUtc, string? executablePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ClipCapture
        {
            Formats = [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(text))],
            CapturedAtUtc = writtenUtc,
            Source = Source(executablePath),
            SourceTitle = tabName,
            Origin = ClipOrigin.NotepadPlusPlusTab,
        };
    }

    /// <summary>
    /// The capture of a file opened in Notepad++: its full path as text (as if the user copied it), at the moment the opening
    /// was seen. Opening it again yields the same text, so the store bumps the one entry from its old date to now.
    /// </summary>
    /// <param name="path">The file's full path.</param>
    /// <param name="openedUtc">When the opening was seen.</param>
    /// <param name="executablePath">The Notepad++ executable, when known.</param>
    /// <returns>The capture.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    public static ClipCapture FileCapture(string path, DateTimeOffset openedUtc, string? executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ClipCapture
        {
            Formats = [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(path))],
            CapturedAtUtc = openedUtc,
            Source = Source(executablePath),
            Origin = ClipOrigin.NotepadPlusPlusFile,
        };
    }
}
