using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Windows.Interop;
using ZstdSharp;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// How the prompt archive opens the agents' files: never in a way an agent could notice, and always with the file's real
/// state (from the open handle, not the folder).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never in the agents' way.</b> Files are opened for reading with <see cref="FileShare.ReadWrite"/> |
/// <see cref="FileShare.Delete"/>: an agent can append, rewrite, rename over or delete the file while it is open, and the
/// handle is closed as soon as the read ends. No lock is ever taken — neither Claude Code's lock folder nor Codex's file
/// lock.
/// </para>
/// <para>
/// <b>Codex's write lock.</b> Codex appends its CLI history under <c>File::try_lock</c>, which on Windows is a mandatory
/// byte-range lock: a read during that write fails with <c>ERROR_LOCK_VIOLATION</c> (33). The write takes microseconds, so
/// the read is retried (<see cref="LockRetries"/> × <see cref="LockRetryDelay"/>) before giving up until the next change.
/// </para>
/// <para>
/// <b>Why the handle.</b> A file another process keeps open for appending (Codex's session files) keeps its old size and
/// time in the folder until something opens it: measured on this PC, 8 appends over 2.5 s raised no change notification
/// and the folder kept reporting 0 bytes (CLAUDE.md §2.21). The length is read from the open stream instead.
/// </para>
/// </remarks>
internal static class PromptFileAccess
{
    /// <summary>How often a read blocked by another process's lock or sharing mode is retried.</summary>
    public const int LockRetries = 20;

    /// <summary>The wait between those retries.</summary>
    public static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Longest first line read for a Codex session file's thread (its <c>session_meta</c> carries the base instructions).</summary>
    public const int MaxFirstLineBytes = 16 * 1024 * 1024;

    /// <summary>Largest paste-cache file read back into a Claude Code prompt.</summary>
    public const long MaxPasteBytes = 64L * 1024 * 1024;

    /// <summary><c>ERROR_SHARING_VIOLATION</c> as an <see cref="IOException.HResult"/>.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary><c>ERROR_LOCK_VIOLATION</c> as an <see cref="IOException.HResult"/>.</summary>
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// Runs a read of a file, retrying while another process's lock or sharing mode blocks it (see the class remarks).
    /// </summary>
    /// <typeparam name="T">The read's result.</typeparam>
    /// <param name="read">The read (opens and closes the file itself; repeated from scratch on a retry).</param>
    /// <param name="cancellationToken">Stops waiting between retries.</param>
    /// <returns>The read's result.</returns>
    /// <exception cref="IOException">Still blocked after <see cref="LockRetries"/> attempts, or another I/O failure.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static T WithLockRetries<T>(Func<T> read, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return read();
            }
            catch (IOException ex) when (IsLockConflict(ex) && attempt < LockRetries)
            {
                // Codex's history write holds its lock for microseconds; anything longer is someone else, still harmless.
                cancellationToken.WaitHandle.WaitOne(LockRetryDelay);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Whether an I/O failure is another process's lock or sharing mode (worth a retry), not a real error.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="true"/> for a sharing or lock violation.</returns>
    public static bool IsLockConflict(IOException exception) => exception.HResult is SharingViolation or LockViolation;

    /// <summary>Opens a file for reading without ever blocking its writer (see the class remarks).</summary>
    /// <param name="path">The file.</param>
    /// <returns>The stream (the caller disposes it).</returns>
    /// <exception cref="IOException">The file could not be opened (missing, locked).</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

    /// <summary>
    /// The state of an open file: length from the stream, last write time and identity from the handle.
    /// </summary>
    /// <param name="stream">The open file.</param>
    /// <returns>The state (the identity is <see langword="null"/> when the handle could not be queried).</returns>
    /// <exception cref="IOException">The length could not be read.</exception>
    public static TailFileState StateOf(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;
        if (NativeMethods.GetFileInformationByHandle(stream.SafeFileHandle, out var info))
        {
            long write = ((long)info.ftLastWriteTimeHigh << 32) | info.ftLastWriteTimeLow;
            var id = string.Create(CultureInfo.InvariantCulture, $"{info.dwVolumeSerialNumber:x8}-{info.nFileIndexHigh:x8}{info.nFileIndexLow:x8}");
            return new TailFileState(length, DateTimeOffset.FromFileTime(write).ToUniversalTime(), id);
        }

        // Read the error at once (before any other call); the state is still usable without an identity.
        _ = Marshal.GetLastPInvokeError();
        return new TailFileState(length, File.GetLastWriteTimeUtc(stream.SafeFileHandle), null);
    }

    /// <summary>
    /// The content stream of an agent file: the file itself, or a decompressing stream over a <c>.zst</c> session file
    /// (which cannot seek, so it is read from its start; see <see cref="JsonlTail"/>).
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="compressed">Whether it is a zstd-compressed session file.</param>
    /// <returns>The stream to read lines from (dispose it; the file is disposed separately).</returns>
    public static Stream ContentOf(FileStream file, bool compressed) =>
        compressed ? new DecompressionStream(file, leaveOpen: true) : file;

    /// <summary>
    /// Reads a stream's first line (up to <see cref="MaxFirstLineBytes"/>; a longer one is cut), from its current position.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The line without its line break (empty when the stream is empty).</returns>
    /// <exception cref="IOException">Reading failed.</exception>
    public static byte[] ReadFirstLine(Stream stream)
    {
        using var line = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (line.Length < MaxFirstLineBytes)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                line.Write(buffer, 0, newline);
                break;
            }

            line.Write(buffer, 0, read);
        }

        var bytes = line.ToArray();
        return bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes[..^1] : bytes;
    }

    /// <summary>
    /// Reads a Claude Code paste-cache file back (UTF-8), or <see langword="null"/> when it is gone, too large or unreadable
    /// — the prompt then keeps its placeholder.
    /// </summary>
    /// <param name="configFolder">Claude Code's config folder.</param>
    /// <param name="contentHash">The paste's content hash from the history line.</param>
    /// <returns>The pasted text, or <see langword="null"/>.</returns>
    public static string? ReadPaste(string configFolder, string contentHash)
    {
        if (ClaudeCodeHistory.PasteCachePath(configFolder, contentHash) is not { } path)
        {
            return null;
        }

        try
        {
            using var stream = OpenShared(path);
            if (stream.Length > MaxPasteBytes)
            {
                return null;
            }

            using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaned up by Claude Code (its cleanup period), or never written: keep the placeholder.
            return null;
        }
    }
}
