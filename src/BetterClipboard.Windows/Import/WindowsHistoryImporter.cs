using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinRtClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace BetterClipboard.Windows.Import;

/// <summary>
/// Result of reading Windows' own clipboard history.
/// </summary>
/// <param name="Captures">Items converted to captures (history items first, then on-disk pins).</param>
/// <param name="HistoryEnabled">Whether Windows clipboard history (Win+V) is turned on.</param>
/// <param name="HistoryStatus">Status of the WinRT history call, for diagnostics.</param>
/// <param name="PinnedOnDisk">How many pinned items were found in Windows' on-disk store.</param>
public sealed record WindowsImportResult(IReadOnlyList<ClipCapture> Captures, bool HistoryEnabled, string HistoryStatus, int PinnedOnDisk);

/// <summary>
/// Pulls everything Windows still remembers into BetterClipboard: the live Win+V history (WinRT
/// <c>Clipboard.GetHistoryItemsAsync</c>) plus the pinned items from the encrypted on-disk store.
/// </summary>
/// <remarks>
/// <para>
/// The WinRT call works from a background, unpackaged desktop process (verified — the "must be in the
/// foreground" rule applies to UWP/AppContainer callers), so the import runs silently at startup.
/// </para>
/// <para>
/// WinRT history items carry no pinned flag; pins are recognized by matching timestamps with the
/// on-disk store (same second). The disk store is read regardless, so pins are still imported when the
/// user has turned Windows history off after switching to BetterClipboard. Duplicates between the two
/// sources collapse in the store by content hash; imports never reorder existing entries.
/// </para>
/// </remarks>
public sealed class WindowsHistoryImporter
{

    private readonly WindowsPinnedStore pinnedStore;

    /// <summary>
    /// Creates an importer.
    /// </summary>
    /// <param name="pinnedStore">Pinned-store reader; defaults to the current user's store.</param>
    public WindowsHistoryImporter(WindowsPinnedStore? pinnedStore = null) => this.pinnedStore = pinnedStore ?? new WindowsPinnedStore();

    /// <summary>
    /// Reads and converts Windows' clipboard history and pins. Individual unreadable items are skipped.
    /// </summary>
    /// <param name="includeHistory">Read the WinRT history (disable in tests to read only the pinned store).</param>
    /// <param name="cancellationToken">Cancels between items.</param>
    /// <returns>The converted captures and diagnostics.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<WindowsImportResult> ReadAllAsync(bool includeHistory = true, CancellationToken cancellationToken = default)
    {
        var captures = new List<ClipCapture>();
        var pinnedTimestamps = pinnedStore.ReadPinnedTimestamps();
        bool enabled = false;
        string status = "skipped";

        if (includeHistory)
        {
            try
            {
                enabled = WinRtClipboard.IsHistoryEnabled();
                if (enabled)
                {
                    var result = await WinRtClipboard.GetHistoryItemsAsync().AsTask(cancellationToken).ConfigureAwait(false);
                    status = result.Status.ToString();
                    if (result.Status == ClipboardHistoryItemsResultStatus.Success)
                    {
                        foreach (var item in result.Items)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            bool pinned = pinnedTimestamps.Contains(WindowsPinnedStore.TruncateToSeconds(item.Timestamp));
                            if (await TryConvertAsync(item, pinned, cancellationToken).ConfigureAwait(false) is { } capture)
                            {
                                captures.Add(capture);
                            }
                        }
                    }
                }
                else
                {
                    status = "ClipboardHistoryDisabled";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = "Error: " + ex.Message;
                AppLog.Warn($"Reading Windows clipboard history failed: {ex.Message}");
            }
        }

        var pinnedItems = pinnedStore.ReadItems();
        foreach (var pinned in pinnedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryConvertPinnedAsync(pinned, cancellationToken).ConfigureAwait(false) is { } capture)
            {
                captures.Add(capture);
            }
        }

        return new WindowsImportResult(captures, enabled, status, pinnedItems.Count);
    }

    /// <summary>Converts one WinRT history item.</summary>
    /// <param name="item">History item.</param>
    /// <param name="pinned">Whether it is pinned in Win+V.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The capture, or <see langword="null"/> when nothing convertible was found or reading failed.</returns>
    private static async Task<ClipCapture?> TryConvertAsync(ClipboardHistoryItem item, bool pinned, CancellationToken cancellationToken)
    {
        try
        {
            var content = item.Content;
            var formats = new List<ClipFormatData>();
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await content.GetStorageItemsAsync().AsTask(cancellationToken).ConfigureAwait(false);
                var paths = storageItems.Select(s => s.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (paths.Count > 0)
                {
                    formats.Add(new ClipFormatData(ClipFormatNames.HDrop, DropFilesCodec.Encode(paths)));
                }
            }

            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync().AsTask(cancellationToken).ConfigureAwait(false);
                formats.Add(new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(text)));
            }

            if (content.Contains(StandardDataFormats.Html))
            {
                // Already in CF_HTML form (header + fragment); its byte offsets are UTF-8 based, so re-encode as UTF-8.
                var html = await content.GetHtmlFormatAsync().AsTask(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(html))
                {
                    formats.Add(new ClipFormatData(ClipFormatNames.Html, Encoding.UTF8.GetBytes(html)));
                }
            }

            if (content.Contains(StandardDataFormats.Rtf))
            {
                // RTF is 7-bit with escapes; Latin-1 maps chars 0–255 back to the original bytes.
                var rtf = await content.GetRtfAsync().AsTask(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(rtf))
                {
                    formats.Add(new ClipFormatData(ClipFormatNames.Rtf, Encoding.Latin1.GetBytes(rtf)));
                }
            }

            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync().AsTask(cancellationToken).ConfigureAwait(false);
                using var stream = await reference.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
                var encoded = await ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
                formats.AddRange(await ImageCodec.ToClipboardFormatsAsync(encoded, cancellationToken).ConfigureAwait(false));
            }

            return formats.Count == 0 ? null : new ClipCapture
            {
                Formats = formats,
                CapturedAtUtc = item.Timestamp.ToUniversalTime(),

                // Windows does not record which app produced a history item, so none is claimed: the
                // panel shows imported items with just their time (v0.1.0 showed a made-up
                // "Windows clipboard history" source here — ClipStore clears it from old rows).
                Source = null,
                Origin = ClipOrigin.WindowsHistory,
                Pin = pinned,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"Skipped one Windows history item: {ex.Message}");
            return null;
        }
    }

    /// <summary>Converts one decrypted on-disk pinned item.</summary>
    /// <param name="item">Pinned item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The capture, or <see langword="null"/> when no known format was present.</returns>
    private static async Task<ClipCapture?> TryConvertPinnedAsync(WindowsPinnedItem item, CancellationToken cancellationToken)
    {
        var formats = new List<ClipFormatData>();
        foreach (var format in item.Formats)
        {
            try
            {
                switch (format.Name)
                {
                    case "Text":
                        // dataType "String" = UTF-16LE without terminator.
                        formats.Add(new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(Encoding.Unicode.GetString(format.Data).TrimEnd('\0'))));
                        break;
                    case "HTML Format":
                        formats.Add(new ClipFormatData(ClipFormatNames.Html, Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(format.Data).TrimEnd('\0'))));
                        break;
                    case "Rich Text Format":
                        formats.Add(new ClipFormatData(ClipFormatNames.Rtf, Encoding.Latin1.GetBytes(Encoding.Unicode.GetString(format.Data).TrimEnd('\0'))));
                        break;
                    case "Bitmap":
                        formats.AddRange(await ImageCodec.ToClipboardFormatsAsync(format.Data, cancellationToken).ConfigureAwait(false));
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn($"Skipped pinned format '{format.Name}': {ex.Message}");
            }
        }

        return formats.Count == 0 ? null : new ClipCapture
        {
            Formats = formats,
            CapturedAtUtc = item.Timestamp,
            Source = null, // the pinned store does not record the producing app either (see above)
            Origin = ClipOrigin.WindowsPinned,
            Pin = true,
        };
    }

    /// <summary>Reads an entire WinRT stream.</summary>
    /// <param name="stream">Stream positioned at 0.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The bytes.</returns>
    private static async Task<byte[]> ReadAllAsync(IRandomAccessStreamWithContentType stream, CancellationToken cancellationToken)
    {
        var size = (uint)stream.Size;
        var buffer = await stream.ReadAsync(new global::Windows.Storage.Streams.Buffer(size), size, InputStreamOptions.None)
            .AsTask(cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
