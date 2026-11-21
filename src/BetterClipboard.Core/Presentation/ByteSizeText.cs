using BetterClipboard.Core.Localization;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Formats a byte count the way the app shows sizes everywhere ("812 B", "48.3 KB", "1.2 MB", "3.75 GB"): Settings' history
/// chips, the <em>Delete stored…</em> dialog, and a picture's file details.
/// </summary>
/// <remarks>
/// <para>
/// Binary units (1 KB = 1024 bytes), like Windows Explorer's sizes, so a file's size here matches what Explorer shows for it.
/// The number uses the current culture's decimal mark ("1,2 MB" in German).
/// </para>
/// <para>
/// The units are catalog entries and follow the interface language the way Explorer does in that language (Russian
/// "1,2 МБ", Chinese "812 字节"); until 2026-11-16 they were English letters in every language. Only the interface shows
/// these sizes: <c>bclip</c> has its own English formatter (<c>CliOutput.FormatBytes</c>), and no log line uses this one.
/// </para>
/// </remarks>
public static class ByteSizeText
{
    /// <summary>Formats a byte count (B, KB, MB or GB, one or two decimals at most) in the interface language.</summary>
    /// <param name="bytes">Byte count; a negative count is shown as it is in bytes.</param>
    /// <returns>The formatted size.</returns>
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => UiText.Format($"{bytes} B"),
        < 1024 * 1024 => UiText.Format($"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => UiText.Format($"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => UiText.Format($"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };
}
