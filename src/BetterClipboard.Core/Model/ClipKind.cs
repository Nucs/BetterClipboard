namespace BetterClipboard.Core.Model;

/// <summary>
/// The primary presentation category of a clip, decided once at capture time by
/// <see cref="Content.ContentClassifier"/> and persisted as an integer.
/// </summary>
/// <remarks>
/// The numeric values are stored in SQLite (<c>clips.kind</c>); never renumber existing members, only
/// append new ones, or previously stored history will be mis-rendered. The kind drives the card template,
/// the filter tabs and the "paste as plain text" affordance — it does <b>not</b> limit which formats are
/// replayed on paste (all stored formats are).
/// </remarks>
public enum ClipKind
{
    /// <summary>Plain text without richer formats.</summary>
    Text = 0,

    /// <summary>Text that also carries HTML and/or RTF (e.g. copied from a browser or Word).</summary>
    RichText = 1,

    /// <summary>A single absolute http/https/ftp/mailto URL (or a bare <c>www.</c> host).</summary>
    Link = 2,

    /// <summary>A single color literal such as <c>#1E90FF</c> or <c>rgb(30, 144, 255)</c>.</summary>
    Color = 3,

    /// <summary>A bitmap (DIB/DIBV5/PNG) without accompanying text.</summary>
    Image = 4,

    /// <summary>A file-drop list (<c>CF_HDROP</c>), e.g. files copied in Explorer.</summary>
    Files = 5,
}
