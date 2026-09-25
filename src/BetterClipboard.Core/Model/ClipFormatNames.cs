namespace BetterClipboard.Core.Model;

/// <summary>
/// Canonical clipboard format names used for persistence. Standard formats use their <c>CF_*</c> names;
/// registered formats use the exact string passed to <c>RegisterClipboardFormat</c>.
/// </summary>
/// <remarks>
/// <para>
/// We persist <b>names, never numeric IDs</b>: registered format IDs are per-session atoms and differ after
/// a reboot, so an ID stored yesterday may mean a different format today. The platform layer maps names
/// back to IDs at paste time (<c>RegisterClipboardFormat</c> is idempotent for the same name).
/// </para>
/// <para>
/// A registered format that happens to be literally named <c>CF_UNICODETEXT</c> would collide with the
/// standard one; no real application does this, so the ambiguity is accepted.
/// </para>
/// </remarks>
public static class ClipFormatNames
{
    /// <summary>UTF-16LE, NUL-terminated text (<c>CF_UNICODETEXT</c> = 13). The canonical text format.</summary>
    public const string UnicodeText = "CF_UNICODETEXT";

    /// <summary>Device-independent bitmap with a <c>BITMAPINFOHEADER</c> (<c>CF_DIB</c> = 8).</summary>
    public const string Dib = "CF_DIB";

    /// <summary>Device-independent bitmap with a <c>BITMAPV5HEADER</c> — keeps alpha/color space (<c>CF_DIBV5</c> = 17).</summary>
    public const string DibV5 = "CF_DIBV5";

    /// <summary>File-drop list, a <c>DROPFILES</c> struct followed by paths (<c>CF_HDROP</c> = 15).</summary>
    public const string HDrop = "CF_HDROP";

    /// <summary>Input locale (LCID DWORD) associated with <c>CF_TEXT</c> conversions (<c>CF_LOCALE</c> = 16).</summary>
    public const string Locale = "CF_LOCALE";

    /// <summary>UTF-8 "CF_HTML" payload with the <c>Version:/StartHTML:</c> header (registered format).</summary>
    public const string Html = "HTML Format";

    /// <summary>RTF document bytes (registered format).</summary>
    public const string Rtf = "Rich Text Format";

    /// <summary>PNG bytes as published by browsers, Office and the Snipping Tool (registered format).</summary>
    public const string Png = "PNG";

    /// <summary>PNG bytes under the MIME-style name some apps (e.g. Firefox) use (registered format).</summary>
    public const string PngMime = "image/png";

    /// <summary>DWORD <c>DROPEFFECT</c> telling Explorer whether a file list was copied (1) or cut (2).</summary>
    public const string PreferredDropEffect = "Preferred DropEffect";

    /// <summary>UTF-16 URL accompanying text copied from browsers' address bars (registered format).</summary>
    public const string UrlW = "UniformResourceLocatorW";

    /// <summary>
    /// Documented opt-out marker: <b>any</b> data in this format means "keep this copy out of every
    /// clipboard history and cloud sync". Password managers set it; we must never record such copies.
    /// </summary>
    public const string ExcludeFromMonitor = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>Documented DWORD marker: 0 = exclude from clipboard history, 1 = explicitly include.</summary>
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";

    /// <summary>Documented DWORD marker for cloud sync only (0/1); irrelevant to local history, never recorded.</summary>
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";

    /// <summary>Legacy (pre-cloud) convention used by KeePass &amp; co.: presence means "viewers, ignore me".</summary>
    public const string ViewerIgnore = "Clipboard Viewer Ignore";

    /// <summary>
    /// Returns whether <paramref name="name"/> is one of the image formats BetterClipboard can decode for
    /// thumbnails and replay (<see cref="Dib"/>, <see cref="DibV5"/>, <see cref="Png"/>, <see cref="PngMime"/>).
    /// </summary>
    /// <param name="name">A canonical format name; compared ordinally (format names are case-sensitive atoms).</param>
    /// <returns><see langword="true"/> for decodable image formats; otherwise <see langword="false"/>.</returns>
    public static bool IsImage(string name) => name is Dib or DibV5 or Png or PngMime;

    /// <summary>
    /// Returns whether <paramref name="name"/> is one of the privacy marker formats that must never be
    /// persisted or replayed (they describe the copy, they are not content).
    /// </summary>
    /// <param name="name">A canonical format name.</param>
    /// <returns><see langword="true"/> for the exclusion/cloud markers; otherwise <see langword="false"/>.</returns>
    public static bool IsPrivacyMarker(string name) =>
        name is ExcludeFromMonitor or CanIncludeInHistory or CanUploadToCloud or ViewerIgnore;
}
