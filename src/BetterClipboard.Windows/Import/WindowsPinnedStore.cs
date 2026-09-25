using System.Globalization;
using System.Text;
using System.Text.Json;
using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Windows.Import;

/// <summary>One stored format of a Windows pinned clipboard item.</summary>
/// <param name="Name">Format name (decoded from the Base64 file name), e.g. <c>Text</c>, <c>HTML Format</c>, <c>Bitmap</c>.</param>
/// <param name="DataType">Serialization type from metadata.json (<c>String</c>, <c>Stream</c>, …).</param>
/// <param name="Data">Decrypted payload (<c>String</c> = UTF-16LE without terminator).</param>
public sealed record WindowsPinnedFormat(string Name, string DataType, byte[] Data);

/// <summary>A Windows pinned clipboard item read from disk.</summary>
/// <param name="Id">Item GUID folder name.</param>
/// <param name="Timestamp">Item timestamp from the store metadata (equals the WinRT history item timestamp).</param>
/// <param name="Formats">Decrypted formats; formats that failed to decrypt are omitted.</param>
public sealed record WindowsPinnedItem(string Id, DateTimeOffset Timestamp, IReadOnlyList<WindowsPinnedFormat> Formats);

/// <summary>
/// Reads Windows' <b>on-disk</b> pinned clipboard store (<c>%LOCALAPPDATA%\Microsoft\Windows\Clipboard\Pinned</c>),
/// the only part of Win+V history that survives a reboot. Format documented in CLAUDE.md §1.4.
/// </summary>
/// <remarks>
/// <para>
/// This is an undocumented, reverse-observed format (verified on Windows 11 25H2) and may change with any
/// Windows update, so every step is defensive: unknown JSON shapes, missing files and decryption
/// failures skip the affected item/format and log a warning — they never throw to the caller.
/// </para>
/// <para>
/// Read-only by design: BetterClipboard never writes into Windows' store.
/// </para>
/// </remarks>
public sealed class WindowsPinnedStore
{
    /// <summary>
    /// Creates a reader over <paramref name="rootPath"/> (defaults to the current user's store).
    /// </summary>
    /// <param name="rootPath">Path of the <c>Pinned</c> folder; override in tests.</param>
    public WindowsPinnedStore(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Clipboard", "Pinned");
    }

    /// <summary>The <c>Pinned</c> folder being read.</summary>
    public string RootPath { get; }

    /// <summary>
    /// Reads only the pin timestamps (cheap; no decryption). Used to mark WinRT history items as pinned.
    /// </summary>
    /// <returns>Timestamps truncated to whole seconds, in UTC.</returns>
    public IReadOnlySet<DateTimeOffset> ReadPinnedTimestamps()
    {
        var result = new HashSet<DateTimeOffset>();
        foreach (var (_, timestamp, _) in EnumerateItemFolders())
        {
            result.Add(TruncateToSeconds(timestamp));
        }

        return result;
    }

    /// <summary>
    /// Reads and decrypts every pinned item.
    /// </summary>
    /// <returns>The items (possibly empty when the store does not exist).</returns>
    public IReadOnlyList<WindowsPinnedItem> ReadItems()
    {
        var items = new List<WindowsPinnedItem>();
        foreach (var (id, timestamp, folder) in EnumerateItemFolders())
        {
            var formats = ReadFormats(folder);
            if (formats.Count > 0)
            {
                items.Add(new WindowsPinnedItem(id, timestamp, formats));
            }
        }

        return items;
    }

    /// <summary>Truncates to whole seconds (the store's timestamp precision).</summary>
    /// <param name="value">A timestamp.</param>
    /// <returns>The truncated UTC timestamp.</returns>
    public static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    /// <summary>Enumerates item folders listed in every store's root <c>metadata.json</c>.</summary>
    /// <returns>(item id, timestamp, item folder) tuples.</returns>
    private IEnumerable<(string Id, DateTimeOffset Timestamp, string Folder)> EnumerateItemFolders()
    {
        if (!Directory.Exists(RootPath))
        {
            yield break;
        }

        foreach (var store in Directory.EnumerateDirectories(RootPath))
        {
            var metadataPath = Path.Combine(store, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            JsonDocument? document = null;
            try
            {
                // File.ReadAllText honors the UTF-16LE BOM Windows writes.
                document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Unreadable Windows pinned-store metadata: {ex.Message}");
            }

            if (document is null)
            {
                continue;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var item in itemsElement.EnumerateObject())
                {
                    var folder = Path.Combine(store, item.Name);
                    if (!Directory.Exists(folder))
                    {
                        continue;
                    }

                    var timestamp = item.Value.ValueKind == JsonValueKind.Object &&
                                    item.Value.TryGetProperty("timestamp", out var ts) &&
                                    DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed.ToUniversalTime()
                        : new DateTimeOffset(Directory.GetCreationTimeUtc(folder), TimeSpan.Zero);
                    yield return (item.Name, timestamp, folder);
                }
            }
        }
    }

    /// <summary>Reads and decrypts the formats of one item folder.</summary>
    /// <param name="folder">Item folder.</param>
    /// <returns>Decrypted formats.</returns>
    private static List<WindowsPinnedFormat> ReadFormats(string folder)
    {
        var formats = new List<WindowsPinnedFormat>();
        var metadataPath = Path.Combine(folder, "metadata.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (!document.RootElement.TryGetProperty("formatMetadata", out var formatMetadata) || formatMetadata.ValueKind != JsonValueKind.Object)
            {
                return formats;
            }

            foreach (var format in formatMetadata.EnumerateObject())
            {
                var file = FindFormatFile(folder, format.Name);
                if (file is null)
                {
                    continue;
                }

                string dataType = format.Value.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "" : "";
                bool encrypted = format.Value.TryGetProperty("isEncrypted", out var enc) && enc.ValueKind == JsonValueKind.True;
                try
                {
                    var raw = File.ReadAllBytes(file);
                    formats.Add(new WindowsPinnedFormat(format.Name, dataType, encrypted ? DpapiNg.Unprotect(raw) : raw));
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"Could not read pinned format '{format.Name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Unreadable pinned item metadata: {ex.Message}");
        }

        return formats;
    }

    /// <summary>
    /// Locates the payload file of a format: its file name is the Base64 of the format name; the URL-safe
    /// alphabet is tried too because '/' cannot appear in file names.
    /// </summary>
    /// <param name="folder">Item folder.</param>
    /// <param name="formatName">Format name.</param>
    /// <returns>The file path, or <see langword="null"/> when absent.</returns>
    private static string? FindFormatFile(string folder, string formatName)
    {
        var standard = Convert.ToBase64String(Encoding.UTF8.GetBytes(formatName));
        var urlSafe = standard.Replace('+', '-').Replace('/', '_');
        foreach (var candidate in new[] { standard, urlSafe, urlSafe.TrimEnd('=') })
        {
            var path = Path.Combine(folder, candidate);
            if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
