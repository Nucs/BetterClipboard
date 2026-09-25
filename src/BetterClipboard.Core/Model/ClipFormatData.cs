namespace BetterClipboard.Core.Model;

/// <summary>
/// One clipboard format of a clip: its canonical name plus the raw bytes exactly as they sat in the
/// clipboard's HGLOBAL (e.g. <c>CF_UNICODETEXT</c> includes its trailing NUL).
/// </summary>
/// <param name="Name">Canonical format name (see <see cref="ClipFormatNames"/>); names, not IDs, are persisted.</param>
/// <param name="Data">
/// Raw payload. The array is shared, not copied — treat it as immutable after construction. Record
/// equality compares the array by <b>reference</b>, not content; use <see cref="Content.ContentHasher"/>
/// when content equality matters.
/// </param>
public sealed record ClipFormatData(string Name, byte[] Data);
