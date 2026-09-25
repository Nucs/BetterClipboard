namespace BetterClipboard.Windows.Clipboard;

/// <summary>
/// What the clipboard reader copies out of the clipboard. Evaluated per capture, so settings changes apply
/// to the next copy.
/// </summary>
public sealed record CaptureOptions
{
    /// <summary>Copy app-private formats too (see <c>AppSettings.PreserveAllFormats</c>).</summary>
    public bool PreserveAllFormats { get; init; }

    /// <summary>Copy image formats (DIB/DIBV5/PNG).</summary>
    public bool CaptureImages { get; init; } = true;

    /// <summary>Copy file lists (<c>CF_HDROP</c> + <c>Preferred DropEffect</c>).</summary>
    public bool CaptureFiles { get; init; } = true;

    /// <summary>
    /// Byte budget for one capture. A single format larger than this is skipped before it is ever copied
    /// out of the producer's memory (so a 2 GB bitmap never touches our heap); formats are added in
    /// priority order until the budget is used.
    /// </summary>
    public long MaxItemBytes { get; init; } = 64L * 1024 * 1024;
}
