namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Formats a byte count the way the app shows sizes everywhere ("812 B", "48.3 KB", "1.2 MB", "3.75 GB"): Settings' history
/// chips, the <em>Delete stored…</em> dialog, and a picture's file details.
/// </summary>
/// <remarks>
/// Binary units (1 KB = 1024 bytes), like Windows Explorer's sizes, so a file's size here matches what Explorer shows for it.
/// The number uses the current culture's decimal mark ("1,2 MB" in German); the unit letters are not translated, as in
/// Explorer.
/// </remarks>
public static class ByteSizeText
{
    /// <summary>Formats a byte count (B, KB, MB or GB, one or two decimals at most).</summary>
    /// <param name="bytes">Byte count; a negative count is shown as it is in bytes.</param>
    /// <returns>The formatted size.</returns>
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
