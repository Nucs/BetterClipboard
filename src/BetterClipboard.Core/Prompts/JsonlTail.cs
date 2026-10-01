using System.Buffers;
using System.Security.Cryptography;

namespace BetterClipboard.Core.Prompts;

/// <summary>
/// What a file's state is when
/// <see cref="JsonlTail.Read(Stream, TailFileState, TailCheckpoint, TailLineHandler, CancellationToken)"/> opens it — from
/// the open handle, not the folder.
/// </summary>
/// <param name="Length">Its length in bytes, read from the open stream. The folder's copy of it can be stale: a file an
/// agent keeps open for appending shows its old size there until something opens it (CLAUDE.md §2.21, measured).</param>
/// <param name="LastWriteUtc">Its last write time (diagnostics and the cheap "unchanged" test of a reconcile scan).</param>
/// <param name="FileId">The file's identity on its volume (NTFS file id), or <see langword="null"/> when unknown. A
/// different id behind the same name means the file was replaced (written aside and renamed over the old one).</param>
public readonly record struct TailFileState(long Length, DateTimeOffset LastWriteUtc, string? FileId);

/// <summary>
/// How far a file was read before, and fingerprints of what was read: enough to tell "only appended to since" from
/// "rewritten" without keeping a copy of the file — the version-control part of tailing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why fingerprints, not bytes.</b> The agents' files grow to gigabytes (8 GB of Claude Code transcripts, 5 GB of Codex
/// sessions on this PC). Keeping a copy to diff against would double that; keeping a hash of the first
/// <see cref="JsonlTail.HeadBytes"/> and of the <see cref="JsonlTail.AnchorBytes"/> before <see cref="Offset"/> costs 64
/// characters per file, and checking them costs two small reads.
/// </para>
/// <para>
/// <b>What they catch.</b> A trim from the front (Codex's <c>history.max_bytes</c>, Claude Code's retention prune) changes
/// the head; a filter that drops lines in the middle (<c>claude purge</c>) shifts the bytes before the offset; a truncation
/// makes the file shorter than the offset; a replacement changes <see cref="FileId"/>. Only an edit that keeps the length,
/// the head and the anchor while changing bytes in between escapes, and no agent writes that way.
/// </para>
/// </remarks>
public sealed record TailCheckpoint
{
    /// <summary>Bytes consumed: the end of the last complete line read. A partial last line is read once it is complete.</summary>
    public long Offset { get; init; }

    /// <summary>The file's length at the last read (may exceed <see cref="Offset"/> by a partial line).</summary>
    public long Length { get; init; }

    /// <summary>The file's last write time at the last read.</summary>
    public DateTimeOffset LastWriteUtc { get; init; }

    /// <summary>The file's identity at the last read, or <see langword="null"/> when unknown.</summary>
    public string? FileId { get; init; }

    /// <summary>Hash of the first <see cref="JsonlTail.HeadBytes"/> consumed bytes (fewer when the file is shorter); empty when nothing was consumed.</summary>
    public string HeadHash { get; init; } = string.Empty;

    /// <summary>Hash of the <see cref="JsonlTail.AnchorBytes"/> bytes before <see cref="Offset"/>; empty when nothing was consumed.</summary>
    public string AnchorHash { get; init; } = string.Empty;

    /// <summary>
    /// When the newest prompt read from this file was sent, or <see langword="null"/> before the first. After a rewrite
    /// the file is read again from its start, and only prompts newer than this (minus <see cref="PromptRules.RewriteMargin"/>)
    /// can be new: older ones were already stored, deleted, or skipped while capture was paused.
    /// </summary>
    public DateTimeOffset? HighWaterUtc { get; init; }

    /// <summary>How many times the file was found rewritten and read again from its start (diagnostics).</summary>
    public int Rewrites { get; init; }
}

/// <summary>
/// What <see cref="JsonlTail.Read(Stream, TailFileState, TailCheckpoint, TailLineHandler, CancellationToken)"/> found the
/// file to be since the checkpoint.
/// </summary>
public enum TailChange
{
    /// <summary>No checkpoint: the whole file was read.</summary>
    First,

    /// <summary>Nothing new since the checkpoint (a partial last line may have grown).</summary>
    Unchanged,

    /// <summary>Only appended to: the new bytes were read.</summary>
    Appended,

    /// <summary>Shorter than the bytes consumed before: read again from its start.</summary>
    Truncated,

    /// <summary>Its head or the bytes before the offset changed (pruned, trimmed, filtered): read again from its start.</summary>
    Rewritten,

    /// <summary>Another file now has its name (a different file id): read from its start.</summary>
    Replaced,
}

/// <summary>
/// The outcome of one <see cref="JsonlTail.Read(Stream, TailFileState, TailCheckpoint, TailLineHandler, CancellationToken)"/>.
/// </summary>
/// <param name="Change">What happened to the file since the checkpoint.</param>
/// <param name="Checkpoint">The checkpoint to keep for the next read (store it only together with what was read).</param>
/// <param name="BytesRead">Bytes read past the checkpoint (or from the start after a reset), for the log and Settings.</param>
/// <param name="Lines">Complete lines handed to the line handler.</param>
/// <param name="SkippedLines">Lines longer than <see cref="JsonlTail.MaxLineBytes"/>, consumed without being handed over.</param>
public sealed record TailReadResult(TailChange Change, TailCheckpoint Checkpoint, long BytesRead, int Lines, int SkippedLines)
{
    /// <summary>Whether the file was read from its start although a checkpoint existed (truncated, rewritten, replaced).</summary>
    public bool IsReset => Change is TailChange.Truncated or TailChange.Rewritten or TailChange.Replaced;
}

/// <summary>
/// Receives one complete line (without its line break) during
/// <see cref="JsonlTail.Read(Stream, TailFileState, TailCheckpoint, TailLineHandler, CancellationToken)"/>.
/// </summary>
/// <param name="line">The line's bytes; only valid during the call (the buffer is reused).</param>
public delegate void TailLineHandler(ReadOnlySpan<byte> line);

/// <summary>
/// Reads JSON Lines files incrementally: only what was appended since the last read, after proving with the
/// <see cref="TailCheckpoint"/>'s fingerprints that the file was only appended to — else from its start, saying so.
/// </summary>
/// <remarks>
/// <para>
/// Pure over a <see cref="Stream"/>: the caller opens the file (sharing read, write and delete, so the agent writing it is
/// never blocked) and supplies its state. Lines end with LF; a CR before it is dropped (Claude Code's first
/// <c>history.jsonl</c> lines ended with CRLF). Bytes after the last LF are a line still being written: they are not
/// consumed, and the next read starts at that line.
/// </para>
/// <para>
/// <b>Streams that cannot seek</b> (a decompressed <c>.zst</c> session file) have no cheap way to check fingerprints: they
/// are read from their start every time the caller decides to read them (the caller compares length, write time and id
/// first), and their checkpoint keeps no fingerprints.
/// </para>
/// <para>
/// <b>Cost.</b> An unchanged file: two reads of at most 4 KB. An append: those plus the new bytes, in
/// <see cref="ChunkBytes"/> chunks. Lines are handed over as spans of a pooled buffer; only a line that grows past a
/// chunk is copied into a larger pooled buffer (up to <see cref="MaxLineBytes"/>).
/// </para>
/// </remarks>
public static class JsonlTail
{
    /// <summary>Bytes of a file's start fingerprinted in <see cref="TailCheckpoint.HeadHash"/>.</summary>
    public const int HeadBytes = 4096;

    /// <summary>Bytes before the offset fingerprinted in <see cref="TailCheckpoint.AnchorHash"/>.</summary>
    public const int AnchorBytes = 4096;

    /// <summary>
    /// Longest line handed to the handler. Longer lines are consumed and counted, never parsed: no prompt line is near it
    /// (Codex caps a message at 1 Mi characters; Claude Code keeps pastes beside its history), only a session file's
    /// tool output or inline image can be.
    /// </summary>
    public const int MaxLineBytes = 64 * 1024 * 1024;

    /// <summary>Read size.</summary>
    public const int ChunkBytes = 256 * 1024;

    /// <summary>
    /// Reads what is new in a file since <paramref name="checkpoint"/> and hands every new complete line to
    /// <paramref name="onLine"/>.
    /// </summary>
    /// <param name="stream">The open file (or a decompressing stream over it), positioned anywhere; read only.</param>
    /// <param name="state">The file's length, write time and id, read from the same open handle.</param>
    /// <param name="checkpoint">The last read's checkpoint, or <see langword="null"/> for a file never read.</param>
    /// <param name="onLine">Called for each new complete line, in order. It must not keep the span.</param>
    /// <param name="cancellationToken">Stops between chunks.</param>
    /// <returns>What happened, and the checkpoint to keep.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="onLine"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="IOException">Reading failed (the file was locked, deleted or truncated under the read).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static TailReadResult Read(Stream stream, TailFileState state, TailCheckpoint? checkpoint, TailLineHandler onLine, CancellationToken cancellationToken = default) =>
        Read(stream, state, checkpoint, onLine, MaxLineBytes, cancellationToken);

    /// <summary>
    /// <see cref="Read(Stream, TailFileState, TailCheckpoint?, TailLineHandler, CancellationToken)"/> with another line length
    /// limit: how the tests exercise the limit without building a 64 MB line.
    /// </summary>
    /// <param name="stream">The open file, positioned anywhere; read only.</param>
    /// <param name="state">The file's length, write time and id.</param>
    /// <param name="checkpoint">The last read's checkpoint, or <see langword="null"/>.</param>
    /// <param name="onLine">Called for each new complete line.</param>
    /// <param name="maxLineBytes">Longest line handed over; longer ones are consumed and counted.</param>
    /// <param name="cancellationToken">Stops between chunks.</param>
    /// <returns>What happened, and the checkpoint to keep.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="onLine"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLineBytes"/> is not positive.</exception>
    /// <exception cref="IOException">Reading failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static TailReadResult Read(Stream stream, TailFileState state, TailCheckpoint? checkpoint, TailLineHandler onLine, int maxLineBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        ArgumentNullException.ThrowIfNull(onLine);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        if (!stream.CanSeek)
        {
            return ReadUnseekable(stream, state, checkpoint, onLine, maxLineBytes, cancellationToken);
        }

        long length = state.Length;
        var change = checkpoint is null ? TailChange.First : Classify(stream, length, state.FileId, checkpoint);
        long start = change is TailChange.Unchanged or TailChange.Appended ? checkpoint!.Offset : 0;
        long consumed = start;
        long bytesRead = 0;
        int lines = 0;
        int skipped = 0;
        if (length > start)
        {
            stream.Seek(start, SeekOrigin.Begin);
            (consumed, bytesRead, lines, skipped) = ReadLines(stream, start, length, onLine, maxLineBytes, cancellationToken);
        }

        // Classify only knows "nothing but appends"; no complete line since means nothing new (a partial line may have
        // grown — it is read once complete).
        if (change == TailChange.Appended && lines == 0 && skipped == 0)
        {
            change = TailChange.Unchanged;
        }

        // Nothing consumed past the verified checkpoint: its fingerprints still hold, no need to read them again.
        bool sameOffset = checkpoint is not null && change == TailChange.Unchanged && consumed == checkpoint.Offset;
        var next = new TailCheckpoint
        {
            Offset = consumed,
            Length = length,
            LastWriteUtc = state.LastWriteUtc,
            FileId = state.FileId,
            HeadHash = sameOffset ? checkpoint!.HeadHash : HashRange(stream, 0, Math.Min(HeadBytes, consumed)),
            AnchorHash = sameOffset ? checkpoint!.AnchorHash : HashRange(stream, Math.Max(0, consumed - AnchorBytes), consumed),
            HighWaterUtc = checkpoint?.HighWaterUtc,
            Rewrites = (checkpoint?.Rewrites ?? 0) + (change is TailChange.Truncated or TailChange.Rewritten or TailChange.Replaced ? 1 : 0),
        };
        return new TailReadResult(change, next, bytesRead, lines, skipped);
    }

    /// <summary>
    /// Whether a checkpoint still describes the file: same id, at least as long as what was consumed, and the same bytes
    /// at its head and before its offset.
    /// </summary>
    /// <param name="stream">The open file (seekable).</param>
    /// <param name="length">Its current length.</param>
    /// <param name="fileId">Its current id, or <see langword="null"/>.</param>
    /// <param name="checkpoint">The checkpoint.</param>
    /// <returns>
    /// <see cref="TailChange.Appended"/> when only appended to
    /// (<see cref="Read(Stream, TailFileState, TailCheckpoint, TailLineHandler, CancellationToken)"/> narrows it to
    /// Unchanged when no line followed), else the reset reason.
    /// </returns>
    /// <exception cref="IOException">Reading failed.</exception>
    private static TailChange Classify(Stream stream, long length, string? fileId, TailCheckpoint checkpoint)
    {
        // An unknown id on either side proves nothing; only two known, different ids prove a replacement.
        if (fileId is not null && checkpoint.FileId is not null && !string.Equals(fileId, checkpoint.FileId, StringComparison.Ordinal))
        {
            return TailChange.Replaced;
        }

        if (length < checkpoint.Offset)
        {
            return TailChange.Truncated;
        }

        if (checkpoint.Offset == 0)
        {
            // Nothing was consumed (empty file, or one partial line): there is nothing to verify.
            return TailChange.Appended;
        }

        // The anchor first: an append-only file passes both checks, and the bytes before the offset are the most likely
        // to change when lines are dropped anywhere before it.
        if (!string.Equals(HashRange(stream, Math.Max(0, checkpoint.Offset - AnchorBytes), checkpoint.Offset), checkpoint.AnchorHash, StringComparison.Ordinal)
            || !string.Equals(HashRange(stream, 0, Math.Min(HeadBytes, checkpoint.Offset)), checkpoint.HeadHash, StringComparison.Ordinal))
        {
            return TailChange.Rewritten;
        }

        return TailChange.Appended;
    }

    /// <summary>
    /// Reads a stream that cannot seek: from its start (wherever it is now), unless the caller's state equals the
    /// checkpoint's — then nothing is read.
    /// </summary>
    /// <param name="stream">The stream, positioned at the start of the content.</param>
    /// <param name="state">The underlying file's state.</param>
    /// <param name="checkpoint">The last checkpoint, or <see langword="null"/>.</param>
    /// <param name="onLine">Line handler.</param>
    /// <param name="maxLineBytes">Longest line handed over.</param>
    /// <param name="cancellationToken">Stops between chunks.</param>
    /// <returns>The result (<see cref="TailChange.First"/>, <see cref="TailChange.Unchanged"/> or <see cref="TailChange.Rewritten"/>).</returns>
    /// <exception cref="IOException">Reading failed.</exception>
    /// <exception cref="InvalidDataException">The decompressing stream found corrupt data.</exception>
    private static TailReadResult ReadUnseekable(Stream stream, TailFileState state, TailCheckpoint? checkpoint, TailLineHandler onLine, int maxLineBytes, CancellationToken cancellationToken)
    {
        if (checkpoint is not null && checkpoint.Length == state.Length && checkpoint.LastWriteUtc == state.LastWriteUtc
            && string.Equals(checkpoint.FileId, state.FileId, StringComparison.Ordinal))
        {
            return new TailReadResult(TailChange.Unchanged, checkpoint, 0, 0, 0);
        }

        var (consumed, bytesRead, lines, skipped) = ReadLines(stream, 0, long.MaxValue, onLine, maxLineBytes, cancellationToken);
        var change = checkpoint is null ? TailChange.First : TailChange.Rewritten;
        var next = new TailCheckpoint
        {
            // The offset is in decompressed bytes; it is never seeked to, only kept for diagnostics.
            Offset = consumed,
            Length = state.Length,
            LastWriteUtc = state.LastWriteUtc,
            FileId = state.FileId,
            HighWaterUtc = checkpoint?.HighWaterUtc,
            Rewrites = (checkpoint?.Rewrites ?? 0) + (checkpoint is null ? 0 : 1),
        };
        return new TailReadResult(change, next, bytesRead, lines, skipped);
    }

    /// <summary>
    /// Reads complete lines from the stream's current position until <paramref name="end"/> (or the stream's end), handing
    /// each to <paramref name="onLine"/>.
    /// </summary>
    /// <param name="stream">The stream, positioned at <paramref name="start"/>.</param>
    /// <param name="start">The stream's position (in the file).</param>
    /// <param name="end">Where to stop: the length snapshot (bytes written after it are for the next read).</param>
    /// <param name="onLine">Line handler.</param>
    /// <param name="maxLineBytes">Longest line handed over; longer ones are consumed and counted.</param>
    /// <param name="cancellationToken">Stops between chunks.</param>
    /// <returns>The offset after the last complete line, the bytes read, lines handed over and lines skipped as too long.</returns>
    /// <exception cref="IOException">Reading failed.</exception>
    private static (long Consumed, long BytesRead, int Lines, int Skipped) ReadLines(Stream stream, long start, long end, TailLineHandler onLine, int maxLineBytes, CancellationToken cancellationToken)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkBytes);

        // A line that does not fit in the rest of a chunk is assembled here (grown as needed, up to MaxLineBytes).
        byte[]? carry = null;
        int carryLength = 0;
        bool skippingLongLine = false;
        long position = start;
        long consumed = start;
        long bytesRead = 0;
        int lines = 0;
        int skipped = 0;
        try
        {
            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(chunk.Length, end - position);
                int read = stream.Read(chunk, 0, want);
                if (read <= 0)
                {
                    // Shorter than the length snapshot: truncated under the read. What was consumed stays valid; the next
                    // read's checks decide whether the file was rewritten.
                    break;
                }

                bytesRead += read;
                position += read;
                var data = chunk.AsSpan(0, read);
                int lineStart = 0;
                while (true)
                {
                    int newline = data[lineStart..].IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        // The rest of this chunk is the start (or middle) of a line that ends in a later chunk.
                        AppendCarry(ref carry, ref carryLength, ref skippingLongLine, data[lineStart..], maxLineBytes);
                        break;
                    }

                    var piece = data.Slice(lineStart, newline);
                    lineStart += newline + 1;
                    consumed = position - read + lineStart;
                    if (skippingLongLine)
                    {
                        skippingLongLine = false;
                        carryLength = 0;
                        skipped++;
                        continue;
                    }

                    if (carryLength > 0)
                    {
                        AppendCarry(ref carry, ref carryLength, ref skippingLongLine, piece, maxLineBytes);
                        if (skippingLongLine)
                        {
                            skippingLongLine = false;
                            carryLength = 0;
                            skipped++;
                            continue;
                        }

                        Deliver(carry.AsSpan(0, carryLength), onLine);
                        carryLength = 0;
                    }
                    else if (piece.Length > maxLineBytes)
                    {
                        // Whole within one chunk, but over the limit (only a limit below the chunk size can see this).
                        skipped++;
                        continue;
                    }
                    else
                    {
                        Deliver(piece, onLine);
                    }

                    lines++;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            if (carry is not null)
            {
                ArrayPool<byte>.Shared.Return(carry);
            }
        }

        return (consumed, bytesRead, lines, skipped);
    }

    /// <summary>Hands a line to the handler without its trailing CR.</summary>
    /// <param name="line">The line (without LF).</param>
    /// <param name="onLine">The handler.</param>
    private static void Deliver(ReadOnlySpan<byte> line, TailLineHandler onLine) =>
        onLine(line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line);

    /// <summary>
    /// Appends part of a line to the carry buffer, switching to "skipping" once the line passes the length limit (its bytes
    /// are then dropped until its end).
    /// </summary>
    /// <param name="carry">The pooled buffer (rented or grown here).</param>
    /// <param name="carryLength">Bytes in it.</param>
    /// <param name="skipping">Whether the current line is being skipped.</param>
    /// <param name="part">The bytes to add.</param>
    /// <param name="maxLineBytes">The length limit.</param>
    private static void AppendCarry(ref byte[]? carry, ref int carryLength, ref bool skipping, ReadOnlySpan<byte> part, int maxLineBytes)
    {
        if (skipping || part.IsEmpty)
        {
            return;
        }

        if ((long)carryLength + part.Length > maxLineBytes)
        {
            skipping = true;
            carryLength = 0;
            return;
        }

        if (carry is null || carry.Length < carryLength + part.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(carryLength + part.Length, carry is null ? 4096 : carry.Length * 2));
            if (carry is not null)
            {
                carry.AsSpan(0, carryLength).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(carry);
            }

            carry = bigger;
        }

        part.CopyTo(carry.AsSpan(carryLength));
        carryLength += part.Length;
    }

    /// <summary>The SHA-256 (hex, first 32 characters) of a byte range of a seekable stream; empty for an empty range.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="from">Start offset.</param>
    /// <param name="to">End offset (exclusive).</param>
    /// <returns>The hash, or empty when the range is empty or shorter than asked (the file shrank under the read).</returns>
    /// <exception cref="IOException">Reading failed.</exception>
    private static string HashRange(Stream stream, long from, long to)
    {
        int count = (int)Math.Max(0, to - from);
        if (count == 0)
        {
            return string.Empty;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            stream.Seek(from, SeekOrigin.Begin);
            int total = stream.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);
            if (total < count)
            {
                // A file that shrank since its length was read cannot match any fingerprint: report a value no checkpoint
                // holds, so the next check finds it rewritten (or truncated) instead of trusting half a range.
                return "short";
            }

            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(buffer.AsSpan(0, count), digest);
            return Convert.ToHexStringLower(digest[..16]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
