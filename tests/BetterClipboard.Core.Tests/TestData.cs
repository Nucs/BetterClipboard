using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Builders for captures and temporary databases shared by the test classes.
/// </summary>
internal static class TestData
{
    /// <summary>A fixed "now" so ordering assertions never depend on the wall clock.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Source used by live-capture fixtures.</summary>
    public static readonly SourceAppInfo Notepad = new("Notepad", @"C:\Windows\notepad.exe", "Notepad");

    /// <summary>
    /// Builds a plain-text capture.
    /// </summary>
    /// <param name="text">Clip text.</param>
    /// <param name="at">Capture time (defaults to <see cref="Now"/>).</param>
    /// <param name="origin">Capture origin.</param>
    /// <param name="pin">Pin request.</param>
    /// <param name="source">Source app (defaults to Notepad).</param>
    /// <returns>The capture.</returns>
    public static ClipCapture Text(string text, DateTimeOffset? at = null, ClipOrigin origin = ClipOrigin.Captured, bool pin = false, SourceAppInfo? source = null) => new()
    {
        Formats = [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(text))],
        CapturedAtUtc = at ?? Now,
        Origin = origin,
        Pin = pin,
        Source = source ?? Notepad,
    };

    /// <summary>
    /// Builds a text capture that also carries HTML (like a browser copy).
    /// </summary>
    /// <param name="text">Plain text.</param>
    /// <param name="html">HTML payload.</param>
    /// <returns>The capture.</returns>
    public static ClipCapture Rich(string text, string html) => new()
    {
        Formats =
        [
            new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(text)),
            new ClipFormatData(ClipFormatNames.Html, Encoding.UTF8.GetBytes(html)),
        ],
        CapturedAtUtc = Now,
        Source = Notepad,
    };

    /// <summary>
    /// Builds a file-list capture.
    /// </summary>
    /// <param name="paths">File paths.</param>
    /// <returns>The capture.</returns>
    public static ClipCapture Files(params string[] paths) => new()
    {
        Formats = [new ClipFormatData(ClipFormatNames.HDrop, DropFilesCodec.Encode(paths))],
        CapturedAtUtc = Now,
        Source = Notepad,
    };

    /// <summary>
    /// Builds an image-only capture with fake PNG bytes (content only matters for hashing in Core tests).
    /// </summary>
    /// <param name="seed">Varies the bytes so different seeds are different images.</param>
    /// <param name="size">Payload size in bytes.</param>
    /// <returns>The capture.</returns>
    public static ClipCapture Image(byte seed, int size = 64) => new()
    {
        Formats = [new ClipFormatData(ClipFormatNames.Png, Enumerable.Repeat(seed, size).ToArray())],
        CapturedAtUtc = Now,
        Source = Notepad,
    };

    /// <summary>
    /// Creates a unique temp database path; the directory is deleted by <see cref="TempDirectory.Dispose"/>.
    /// </summary>
    /// <returns>A disposable temp directory.</returns>
    public static TempDirectory NewTempDirectory() => new();
}

/// <summary>
/// A temp directory that is removed on dispose (SQLite pools are cleared first so the file is not locked).
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates the directory under the system temp path.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Directory path.</summary>
    public string Path { get; }

    /// <summary>Path of a database file inside the directory.</summary>
    public string DatabasePath => System.IO.Path.Combine(Path, "history.db");

    /// <summary>Clears SQLite connection pools (which keep files open) and deletes the directory.</summary>
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering handle only leaves a temp folder behind.
        }
    }
}
