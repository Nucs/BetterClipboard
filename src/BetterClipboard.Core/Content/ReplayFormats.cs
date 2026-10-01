using System.Buffers.Binary;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Content;

/// <summary>
/// Prepares stored formats for being placed back on the clipboard (paste from history).
/// </summary>
public static class ReplayFormats
{
    /// <summary><c>DROPEFFECT_COPY</c>.</summary>
    private const uint DropEffectCopy = 1;

    /// <summary>
    /// Returns the formats to write for a paste.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Plain text</b> keeps only <c>CF_UNICODETEXT</c>; for file lists it synthesizes the paths as
    /// text (one per line) — handy for pasting paths into a terminal.</item>
    /// <item><b>Cut semantics are neutralized</b>: a stored <c>Preferred DropEffect</c> of "move" (the user
    /// pressed Ctrl+X in Explorer days ago) is rewritten to "copy", so replaying an old history item can
    /// never move files out of their folder.</item>
    /// </list>
    /// </remarks>
    /// <param name="stored">Formats as stored, in replay order.</param>
    /// <param name="plainTextOnly">Strip formatting (the "paste as plain text" action).</param>
    /// <returns>Formats to write; empty when nothing suitable exists (e.g. plain text of an image).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stored"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ClipFormatData> Prepare(IReadOnlyList<ClipFormatData> stored, bool plainTextOnly)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (plainTextOnly)
        {
            var text = stored.FirstOrDefault(f => f.Name == ClipFormatNames.UnicodeText);
            if (text is not null)
            {
                return [text];
            }

            var drop = stored.FirstOrDefault(f => f.Name == ClipFormatNames.HDrop);
            if (drop is not null && DropFilesCodec.Decode(drop.Data) is { Count: > 0 } paths)
            {
                return [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(string.Join(Environment.NewLine, paths)))];
            }

            return [];
        }

        var result = new List<ClipFormatData>(stored.Count);
        foreach (var format in stored)
        {
            if (ClipFormatNames.IsPrivacyMarker(format.Name))
            {
                continue;
            }

            if (format.Name == ClipFormatNames.PreferredDropEffect)
            {
                var copy = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(copy, DropEffectCopy);
                result.Add(new ClipFormatData(format.Name, copy));
                continue;
            }

            result.Add(format);
        }

        return result;
    }

    /// <summary>
    /// Builds the formats for files that exist only on disk, not in the history — the Everything tab's picks:
    /// what Explorer's "Copy" puts on the clipboard, or just the paths.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Files: <c>CF_HDROP</c> plus <c>Preferred DropEffect</c> = copy. Without the drop effect some targets
    /// (Explorer among them) treat a paste as a move, which must never happen from a history tool.</item>
    /// <item>Plain text: the paths, one per line (CRLF), like "paste paths as text" on a stored file list.</item>
    /// </list>
    /// The paths are not checked here: the caller got them from Everything's index a moment ago, and a target
    /// that cannot open a vanished file reports it like for any stale file list.
    /// </remarks>
    /// <param name="paths">Absolute file or folder paths, in paste order.</param>
    /// <param name="plainTextOnly">Paste the paths as text instead of the files.</param>
    /// <returns>Formats to write, in replay order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="paths"/> is empty or contains a blank path.</exception>
    public static IReadOnlyList<ClipFormatData> ForFiles(IReadOnlyList<string> paths, bool plainTextOnly)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0 || paths.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-blank path is required.", nameof(paths));
        }

        if (plainTextOnly)
        {
            return [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(string.Join("\r\n", paths)))];
        }

        var copy = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(copy, DropEffectCopy);
        return
        [
            new ClipFormatData(ClipFormatNames.HDrop, DropFilesCodec.Encode(paths)),
            new ClipFormatData(ClipFormatNames.PreferredDropEffect, copy),
        ];
    }
}
