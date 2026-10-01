using System.Buffers.Binary;
using System.Text;

namespace BetterClipboard.Core.Everything;

/// <summary>
/// The wire format of voidtools Everything's window-message IPC (<c>everything_ipc.h</c>, "SDK 2"), as far as
/// BetterClipboard uses it: queries (<c>EVERYTHING_IPC_COPYDATA_QUERY2W</c>), their <c>LIST2</c> replies, state
/// questions and the command line. Everything 1.4.1 and 1.5 answer it the same way (verified, CLAUDE.md §2.14).
/// </summary>
/// <remarks>
/// <para>
/// Pure byte work, so it is tested without Everything; the window plumbing (finding the window, sending, pumping
/// the reply) lives in <c>BetterClipboard.Windows</c>. The same code serves both versions, so no native SDK DLL
/// (one per architecture) has to ship.
/// </para>
/// <para>
/// <b>Trust.</b> A reply comes from another process. <see cref="DecodeList2"/> therefore checks every count,
/// offset and length against the buffer and throws <see cref="FormatException"/> instead of reading past it — a
/// buggy or hostile peer can at worst make one query fail.
/// </para>
/// </remarks>
public static class EverythingIpc
{
    /// <summary><c>EVERYTHING_WM_IPC</c> (<c>WM_USER</c>): state questions sent to the IPC window; the answer is the message result.</summary>
    public const uint WmIpc = 0x0400;

    /// <summary><c>EVERYTHING_IPC_GET_MAJOR_VERSION</c>.</summary>
    public const uint GetMajorVersion = 0;

    /// <summary><c>EVERYTHING_IPC_GET_MINOR_VERSION</c>.</summary>
    public const uint GetMinorVersion = 1;

    /// <summary><c>EVERYTHING_IPC_GET_REVISION</c>.</summary>
    public const uint GetRevision = 2;

    /// <summary><c>EVERYTHING_IPC_GET_BUILD_NUMBER</c>.</summary>
    public const uint GetBuildNumber = 3;

    /// <summary>
    /// <c>EVERYTHING_IPC_IS_DB_LOADED</c>: 1 once the database is loaded. Until then Everything answers no query at
    /// all — queries wait (verified: answered only when loading ended, 3.6–16 s later for a folder index of
    /// C:\Windows, minutes for a big one), so a client asks this first instead of waiting on a query.
    /// </summary>
    public const uint IsDbLoaded = 401;

    /// <summary><c>EVERYTHING_IPC_IS_DB_BUSY</c>: 1 while Everything is (re)building, when queries wait as well.</summary>
    public const uint IsDbBusy = 402;

    /// <summary><c>EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8</c>: run an Everything command line (e.g. <c>-s text</c>) in the running instance.</summary>
    public const uint CopyDataCommandLineUtf8 = 0;

    /// <summary><c>EVERYTHING_IPC_COPYDATA_QUERY2W</c>: a query in <see cref="EncodeQuery2"/>'s layout; the reply is a <c>LIST2</c>.</summary>
    public const uint CopyDataQuery2W = 18;

    /// <summary>Request flag: full path and name (UTF-16).</summary>
    public const uint RequestFullPath = 0x4;

    /// <summary>Request flag: size (bytes; −1 for a folder in 1.4).</summary>
    public const uint RequestSize = 0x10;

    /// <summary>Request flag: date modified (FILETIME).</summary>
    public const uint RequestDateModified = 0x40;

    /// <summary>Request flag: attributes (DWORD).</summary>
    public const uint RequestAttributes = 0x100;

    /// <summary>Request flag: run count — how often the item was opened from Everything (DWORD).</summary>
    public const uint RequestRunCount = 0x400;

    /// <summary>Request flag: date run — when it was last opened from Everything (FILETIME).</summary>
    public const uint RequestDateRun = 0x800;

    /// <summary><c>EVERYTHING_IPC_SORT_NAME_ASCENDING</c>: always fast.</summary>
    public const uint SortNameAscending = 1;

    /// <summary><c>EVERYTHING_IPC_SORT_RUN_COUNT_DESCENDING</c>.</summary>
    public const uint SortRunCountDescending = 20;

    /// <summary><c>EVERYTHING_IPC_SORT_DATE_RUN_DESCENDING</c>: newest run first (the Everything tab's order).</summary>
    public const uint SortDateRunDescending = 26;

    /// <summary><c>EVERYTHING_IPC_FOLDER</c> item flag.</summary>
    public const uint ItemFolder = 0x1;

    /// <summary>
    /// Class of the IPC window of the unnamed instance (Everything 1.4 and the 1.5 beta). It exists whenever
    /// Everything runs, even with its tray icon hidden.
    /// </summary>
    public const string WindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    /// <summary>Size of the <c>EVERYTHING_IPC_LIST2</c> header: totitems, numitems, offset, request_flags, sort_type.</summary>
    private const int List2HeaderBytes = 20;

    /// <summary>Size of one <c>EVERYTHING_IPC_ITEM2</c>: flags, data_offset.</summary>
    private const int Item2Bytes = 8;

    /// <summary>Size of the fixed part of <c>EVERYTHING_IPC_QUERY2</c>: seven DWORDs.</summary>
    private const int Query2HeaderBytes = 28;

    /// <summary>
    /// The IPC window class of an instance: <c>EVERYTHING_TASKBAR_NOTIFICATION</c>, or <c>…_(name)</c> for a named one
    /// (the 1.5 alpha runs as <c>1.5a</c>; voidtools/es <c>_es_get_window_classname</c>).
    /// </summary>
    /// <param name="instance">Instance name; <see langword="null"/> or empty for the unnamed instance.</param>
    /// <returns>The window class name.</returns>
    public static string WindowClassFor(string? instance) =>
        string.IsNullOrEmpty(instance) ? WindowClass : $"{WindowClass}_({instance})";

    /// <summary>
    /// Encodes an <c>EVERYTHING_IPC_QUERY2</c> (packed): reply window, reply id, search flags, offset, maximum
    /// results, request flags, sort — seven little-endian DWORDs — then the search as NUL-terminated UTF-16.
    /// </summary>
    /// <remarks>
    /// Everything answers with a <c>WM_COPYDATA</c> to <paramref name="replyWindow"/> whose <c>dwData</c> is
    /// <paramref name="replyId"/>, after the sending call returned (never inside it). Footgun: it runs one query
    /// per reply window — a second query on the same window silently cancels the first, which then never gets a
    /// reply (verified), so a client gives every query a deadline.
    /// </remarks>
    /// <param name="replyWindow">The window that receives the reply. Only 32 bits are sent: window handles fit, also on x64.</param>
    /// <param name="replyId">Echoed as the reply's <c>dwData</c>, to match replies to queries.</param>
    /// <param name="search">Search text in Everything's syntax.</param>
    /// <param name="requestFlags">Which fields to return (<c>Request*</c>); they come back in bit order.</param>
    /// <param name="sort">A <c>Sort*</c> value.</param>
    /// <param name="maxResults">Most results to return (<see cref="uint.MaxValue"/> = all).</param>
    /// <param name="offset">Results to skip.</param>
    /// <param name="searchFlags">Match case/whole word/path/regex flags; 0 = Everything's defaults.</param>
    /// <returns>The <c>lpData</c> bytes of the <c>WM_COPYDATA</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="search"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeQuery2(nint replyWindow, uint replyId, string search, uint requestFlags, uint sort, uint maxResults, uint offset = 0, uint searchFlags = 0)
    {
        ArgumentNullException.ThrowIfNull(search);
        var bytes = new byte[Query2HeaderBytes + (search.Length + 1) * 2];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], unchecked((uint)(long)replyWindow));
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], replyId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], searchFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], maxResults);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], requestFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], sort);

        // UTF-16LE text; the array is zero-initialized, so the terminator is already there.
        Encoding.Unicode.GetBytes(search, span[Query2HeaderBytes..]);
        return bytes;
    }

    /// <summary>
    /// Encodes an <c>EVERYTHING_IPC_COMMAND_LINE</c>: the <c>ShowWindow</c> command, then the command line as
    /// NUL-terminated UTF-8. Everything runs it in the existing instance as if <c>Everything.exe</c> had been
    /// started with those arguments (e.g. <c>-s "text"</c> opens a search window showing <c>text</c>).
    /// </summary>
    /// <param name="showCommand">A <c>SW_*</c> value for the search window (<c>SW_SHOWNORMAL</c> = 1).</param>
    /// <param name="commandLine">Arguments in Everything's command-line syntax.</param>
    /// <returns>The <c>lpData</c> bytes of the <c>WM_COPYDATA</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="commandLine"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeCommandLine(int showCommand, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        int textBytes = Encoding.UTF8.GetByteCount(commandLine);
        var bytes = new byte[4 + textBytes + 1];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, showCommand);
        Encoding.UTF8.GetBytes(commandLine, bytes.AsSpan(4));
        return bytes;
    }

    /// <summary>
    /// Decodes an <c>EVERYTHING_IPC_LIST2</c> reply: a header (total, count, offset, request flags, sort), then
    /// <c>count</c> × {flags, data offset}; each item's fields sit at its data offset in <b>request-bit order</b>
    /// (name, path, full path, extension, size, dates, attributes, …).
    /// </summary>
    /// <remarks>
    /// The field order follows voidtools/es <c>_es_ipc2_get_column_data</c>; the comment in everything_ipc.h lists
    /// size before extension, which is wrong. The header's request flags are the fields really present, which can
    /// differ from what was asked. Strings are a DWORD length in characters, the UTF-16 text and a NUL; numbers are
    /// 8 bytes (FILETIME, sizes) or 4 (attributes, run count), unaligned.
    /// </remarks>
    /// <param name="data">The copied <c>WM_COPYDATA</c> payload.</param>
    /// <returns>The decoded list.</returns>
    /// <exception cref="FormatException">The buffer is shorter than its own counts, offsets or lengths claim.</exception>
    public static EverythingList DecodeList2(ReadOnlySpan<byte> data)
    {
        if (data.Length < List2HeaderBytes)
        {
            throw new FormatException($"An Everything reply needs at least {List2HeaderBytes} bytes, got {data.Length}.");
        }

        uint total = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint fields = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        uint sort = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);

        // The count is checked against the buffer before anything is allocated: a lying header cannot make us
        // allocate gigabytes.
        if (count > (uint)((data.Length - List2HeaderBytes) / Item2Bytes))
        {
            throw new FormatException($"An Everything reply claims {count} items but has room for {(data.Length - List2HeaderBytes) / Item2Bytes}.");
        }

        var items = new EverythingItem[count];
        for (int i = 0; i < items.Length; i++)
        {
            int itemAt = List2HeaderBytes + i * Item2Bytes;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[itemAt..]);
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(itemAt + 4)..]);
            items[i] = DecodeItem(data, flags, dataOffset, fields);
        }

        return new EverythingList(total, offset, fields, sort, items);
    }

    /// <summary>Reads one item's fields at its data offset, in request-bit order.</summary>
    /// <param name="data">The whole reply.</param>
    /// <param name="flags">The item's flags (<see cref="ItemFolder"/>).</param>
    /// <param name="dataOffset">Where its fields start.</param>
    /// <param name="fields">The request flags present in the reply.</param>
    /// <returns>The item.</returns>
    /// <exception cref="FormatException">A field runs past the buffer.</exception>
    private static EverythingItem DecodeItem(ReadOnlySpan<byte> data, uint flags, uint dataOffset, uint fields)
    {
        if (dataOffset > (uint)data.Length)
        {
            throw new FormatException($"An Everything item starts at {dataOffset}, past the {data.Length}-byte reply.");
        }

        int position = (int)dataOffset;
        string? fullPath = null;
        long? size = null;
        DateTimeOffset? modified = null, dateRun = null;
        uint? attributes = null, runCount = null;
        for (uint bit = 1; bit != 0 && bit <= 0x8000; bit <<= 1)
        {
            if ((fields & bit) == 0)
            {
                continue;
            }

            switch (bit)
            {
                case RequestFullPath:
                    fullPath = ReadString(data, ref position);
                    break;
                case 0x1 or 0x2 or 0x8 or 0x200 or 0x2000 or 0x4000 or 0x8000:
                    // Name, path, extension, file-list name and the highlighted variants: strings we skip.
                    _ = ReadString(data, ref position);
                    break;
                case RequestSize:
                    long rawSize = ReadInt64(data, ref position);
                    size = rawSize >= 0 ? rawSize : null; // 1.4 sends −1 for a folder whose size is not indexed
                    break;
                case RequestDateModified:
                    modified = FromFileTime(ReadInt64(data, ref position));
                    break;
                case RequestDateRun:
                    dateRun = FromFileTime(ReadInt64(data, ref position));
                    break;
                case 0x20 or 0x80 or 0x1000:
                    // Date created, accessed, recently changed: 8-byte FILETIMEs we skip.
                    _ = ReadInt64(data, ref position);
                    break;
                case RequestAttributes:
                    attributes = ReadUInt32(data, ref position);
                    break;
                case RequestRunCount:
                    runCount = ReadUInt32(data, ref position);
                    break;
                default:
                    // A bit this layout does not know: its size is unknown, so nothing after it can be trusted.
                    throw new FormatException($"An Everything reply carries an unknown field 0x{bit:X}.");
            }
        }

        return new EverythingItem(fullPath, (flags & ItemFolder) != 0, size, modified, attributes, runCount, dateRun);
    }

    /// <summary>Reads a DWORD-length-prefixed, NUL-terminated UTF-16 string and moves past it.</summary>
    /// <param name="data">The reply.</param>
    /// <param name="position">Read position; advanced past the string and its terminator.</param>
    /// <returns>The text.</returns>
    /// <exception cref="FormatException">The string runs past the buffer.</exception>
    private static string ReadString(ReadOnlySpan<byte> data, ref int position)
    {
        uint length = ReadUInt32(data, ref position);

        // In 64-bit math: a length near uint.MaxValue must not wrap around into a small number.
        long end = position + ((long)length + 1) * 2;
        if (end > data.Length)
        {
            throw new FormatException($"An Everything string of {length} characters runs past the reply.");
        }

        var text = Encoding.Unicode.GetString(data.Slice(position, (int)length * 2));
        position = (int)end;
        return text;
    }

    /// <summary>Reads a little-endian DWORD and moves past it.</summary>
    /// <param name="data">The reply.</param>
    /// <param name="position">Read position.</param>
    /// <returns>The value.</returns>
    /// <exception cref="FormatException">Fewer than 4 bytes are left.</exception>
    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int position)
    {
        if (position > data.Length - 4)
        {
            throw new FormatException("An Everything reply ends inside a 4-byte field.");
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
        position += 4;
        return value;
    }

    /// <summary>Reads a little-endian 64-bit value and moves past it.</summary>
    /// <param name="data">The reply.</param>
    /// <param name="position">Read position.</param>
    /// <returns>The value.</returns>
    /// <exception cref="FormatException">Fewer than 8 bytes are left.</exception>
    private static long ReadInt64(ReadOnlySpan<byte> data, ref int position)
    {
        if (position > data.Length - 8)
        {
            throw new FormatException("An Everything reply ends inside an 8-byte field.");
        }

        long value = BinaryPrimitives.ReadInt64LittleEndian(data[position..]);
        position += 8;
        return value;
    }

    /// <summary>
    /// Converts an Everything FILETIME (UTC) to a time; 0, negative and out-of-range values mean "not set" (an item
    /// never run has no date run).
    /// </summary>
    /// <param name="fileTime">The raw FILETIME.</param>
    /// <returns>The time, or <see langword="null"/>.</returns>
    internal static DateTimeOffset? FromFileTime(long fileTime)
    {
        // DateTime.MaxValue's FILETIME: anything above throws in FromFileTimeUtc.
        const long MaxFileTime = 2_650_467_743_999_999_999;
        return fileTime is > 0 and <= MaxFileTime ? new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)) : null;
    }
}

/// <summary>A decoded <c>EVERYTHING_IPC_LIST2</c> reply.</summary>
/// <param name="Total">Matches in the whole index (the reply may carry fewer: <see cref="Items"/>).</param>
/// <param name="Offset">Index of the first returned match.</param>
/// <param name="RequestFlags">Fields actually present per item; may differ from the request.</param>
/// <param name="Sort">Sort actually applied; may differ from the request.</param>
/// <param name="Items">The returned results.</param>
public sealed record EverythingList(uint Total, uint Offset, uint RequestFlags, uint Sort, IReadOnlyList<EverythingItem> Items);

/// <summary>One result of an Everything query; every field is <see langword="null"/> when it was not returned.</summary>
/// <param name="FullPath">Full path and name.</param>
/// <param name="IsFolder">Whether Everything flagged it as a folder.</param>
/// <param name="Size">Size in bytes (a folder's only when Everything indexes folder sizes).</param>
/// <param name="Modified">Date modified (UTC).</param>
/// <param name="Attributes">File attributes.</param>
/// <param name="RunCount">How often it was opened from Everything.</param>
/// <param name="DateRun">When it was last opened from Everything (UTC).</param>
public sealed record EverythingItem(string? FullPath, bool IsFolder, long? Size, DateTimeOffset? Modified, uint? Attributes, uint? RunCount, DateTimeOffset? DateRun);
