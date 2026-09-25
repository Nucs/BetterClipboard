using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterClipboard.Core.Cli;

/// <summary>Command names understood by the <c>bclip</c> pipe server (the wire values).</summary>
public static class CliCommands
{
    /// <summary>Most recent items.</summary>
    public const string List = "list";

    /// <summary>Items containing all words (the panel's substring search).</summary>
    public const string Search = "search";

    /// <summary>Lines matching a regular expression.</summary>
    public const string Grep = "grep";

    /// <summary>One item's content.</summary>
    public const string Get = "get";

    /// <summary>Put an item back on the clipboard.</summary>
    public const string Copy = "copy";

    /// <summary>Put new text on the clipboard (and into history).</summary>
    public const string Put = "put";

    /// <summary>Pin an item.</summary>
    public const string Pin = "pin";

    /// <summary>Unpin an item.</summary>
    public const string Unpin = "unpin";

    /// <summary>Delete an item.</summary>
    public const string Delete = "delete";

    /// <summary>Delete an item and never record its content again ("Forget forever").</summary>
    public const string Forget = "forget";

    /// <summary>Block until the next copy arrives.</summary>
    public const string Wait = "wait";

    /// <summary>Version, counts and capture statistics.</summary>
    public const string Status = "status";
}

/// <summary>Machine-readable error categories in <see cref="CliResponse.ErrorCode"/> (stable across versions).</summary>
public static class CliErrorCodes
{
    /// <summary>The request is malformed or an argument is invalid (exit code 2).</summary>
    public const string BadRequest = "bad_request";

    /// <summary>No such item, or nothing matched (exit code 1).</summary>
    public const string NotFound = "not_found";

    /// <summary>The item exists but cannot be served in the requested form (e.g. text of an image).</summary>
    public const string Unsupported = "unsupported";

    /// <summary><c>wait</c> ended without a new copy (exit code 1).</summary>
    public const string Timeout = "timeout";

    /// <summary>Another application kept the clipboard locked.</summary>
    public const string ClipboardBusy = "clipboard_busy";

    /// <summary>Unexpected server-side failure (details are in the app log).</summary>
    public const string Internal = "internal";
}

/// <summary>
/// One request from <c>bclip</c> to the running app. Only the members relevant to
/// <see cref="Command"/> are read; the rest stay <see langword="null"/>/default and are omitted on the wire.
/// </summary>
public sealed record CliRequest
{
    /// <summary>Protocol version the client speaks; the server rejects versions it does not know.</summary>
    public int ProtocolVersion { get; init; } = CliEndpoint.ProtocolVersion;

    /// <summary>One of <see cref="CliCommands"/>.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Search words (<c>search</c>) or the regular expression (<c>grep</c>).</summary>
    public string? Query { get; init; }

    /// <summary><c>all</c>, <c>pinned</c>, <c>text</c>, <c>images</c>, <c>links</c> or <c>files</c>; <see langword="null"/> = all.</summary>
    public string? Filter { get; init; }

    /// <summary>Maximum items to return (server default 20, clamped to 1–1000).</summary>
    public int? Limit { get; init; }

    /// <summary>Items to skip, for paging <c>list</c>/<c>search</c>.</summary>
    public int? Offset { get; init; }

    /// <summary>Only items last used at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Target item id (from a listing). Takes precedence over <see cref="Recent"/>.</summary>
    public long? Id { get; init; }

    /// <summary>Target the n-th most recently used item (1 = latest) when no <see cref="Id"/> is given.</summary>
    public int? Recent { get; init; }

    /// <summary><c>grep</c>: case-insensitive matching.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary><c>copy</c>: plain text only (like Shift+Enter in the panel).</summary>
    public bool PlainText { get; init; }

    /// <summary><c>list</c>/<c>search</c>/<c>grep</c>: include each item's full text, not only its preview.</summary>
    public bool Full { get; init; }

    /// <summary><c>get</c>: <c>auto</c> (default), <c>text</c>, <c>html</c>, <c>rtf</c>, <c>files</c>, <c>png</c> or <c>formats</c>.</summary>
    public string? Format { get; init; }

    /// <summary><c>put</c>: the text to place on the clipboard.</summary>
    public string? Text { get; init; }

    /// <summary><c>wait</c>: seconds to wait for a new copy (server default 60, clamped to 1–3600).</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>The server's answer to one <see cref="CliRequest"/>.</summary>
public sealed record CliResponse
{
    /// <summary>Whether the command succeeded; when <see langword="false"/>, <see cref="Error"/> explains why.</summary>
    public bool Ok { get; init; }

    /// <summary>Human-readable error.</summary>
    public string? Error { get; init; }

    /// <summary>One of <see cref="CliErrorCodes"/>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Short confirmation for commands that change something (copy, put, pin, delete).</summary>
    public string? Message { get; init; }

    /// <summary>Result list (<c>list</c>, <c>search</c>, <c>grep</c>), most recently used first.</summary>
    public IReadOnlyList<CliItem>? Items { get; init; }

    /// <summary>The single item a command acted on (<c>get</c>, <c>copy</c>, <c>put</c>, <c>pin</c>, <c>wait</c>).</summary>
    public CliItem? Item { get; init; }

    /// <summary>The payload returned by <c>get</c>.</summary>
    public CliContent? Content { get; init; }

    /// <summary>Returned by <c>status</c>.</summary>
    public CliStatus? Status { get; init; }

    /// <summary><c>grep</c>: how many items were scanned (tells a caller whether history was searched exhaustively).</summary>
    public int? Scanned { get; init; }

    /// <summary>Creates a failure response.</summary>
    /// <param name="code">One of <see cref="CliErrorCodes"/>.</param>
    /// <param name="error">Human-readable reason.</param>
    /// <returns>The response.</returns>
    public static CliResponse Fail(string code, string error) => new() { Ok = false, ErrorCode = code, Error = error };
}

/// <summary>One history item as seen by <c>bclip</c> (metadata plus, when asked, the full text).</summary>
public sealed record CliItem
{
    /// <summary>Stable id; pass it to <c>get</c>/<c>copy</c>/<c>pin</c>/<c>delete</c>.</summary>
    public long Id { get; init; }

    /// <summary><c>text</c>, <c>rich-text</c>, <c>link</c>, <c>color</c>, <c>image</c> or <c>files</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Display preview (up to 1000 characters; file names for file lists; "Image · W × H" for images).</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>Full text (or file paths, one per line) when requested with <see cref="CliRequest.Full"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Whether the item is pinned.</summary>
    public bool Pinned { get; init; }

    /// <summary>Friendly name of the app it was copied from, when known.</summary>
    public string? Source { get; init; }

    /// <summary>Executable path of that app, when known.</summary>
    public string? SourcePath { get; init; }

    /// <summary><c>copied</c> (seen live), <c>windows-history</c> or <c>windows-pinned</c> (imported from Win+V).</summary>
    public string Origin { get; init; } = string.Empty;

    /// <summary>When this content was first copied.</summary>
    public DateTimeOffset FirstCopied { get; init; }

    /// <summary>When it was last copied or pasted (the history order).</summary>
    public DateTimeOffset LastUsed { get; init; }

    /// <summary>How many times it was copied or pasted.</summary>
    public int UseCount { get; init; }

    /// <summary>Stored payload size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Stored clipboard format names, in replay order.</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];

    /// <summary>Pixel width of image items.</summary>
    public int? ImageWidth { get; init; }

    /// <summary>Pixel height of image items.</summary>
    public int? ImageHeight { get; init; }

    /// <summary><c>grep</c>: the matching lines.</summary>
    public IReadOnlyList<CliMatch>? Matches { get; init; }
}

/// <summary>One matching line found by <c>grep</c>.</summary>
/// <param name="Line">1-based line number within the item's text.</param>
/// <param name="Text">The line (cut at 400 characters).</param>
public sealed record CliMatch(int Line, string Text);

/// <summary>The payload of an item in one representation (<c>get</c>).</summary>
public sealed record CliContent
{
    /// <summary>The representation served: <c>text</c>, <c>html</c>, <c>rtf</c>, <c>files</c>, <c>png</c> or <c>formats</c>.</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>MIME type of the payload (<c>text/plain</c>, <c>text/html</c>, <c>text/rtf</c>, <c>image/png</c>, …).</summary>
    public string MediaType { get; init; } = string.Empty;

    /// <summary>Textual payload (text, HTML fragment, RTF, or file paths one per line).</summary>
    public string? Text { get; init; }

    /// <summary>File paths, for file lists.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>Binary payload (PNG) as Base64; <c>bclip</c> writes it to <c>--out</c> instead of printing it.</summary>
    public string? DataBase64 { get; init; }

    /// <summary>Size of the payload in bytes (UTF-8 for text).</summary>
    public long Bytes { get; init; }

    /// <summary><c>formats</c>: every stored format with its size.</summary>
    public IReadOnlyList<CliFormatInfo>? StoredFormats { get; init; }
}

/// <summary>A stored clipboard format and its size.</summary>
/// <param name="Name">Format name (e.g. <c>CF_UNICODETEXT</c>, <c>HTML Format</c>).</param>
/// <param name="Bytes">Payload size.</param>
public sealed record CliFormatInfo(string Name, long Bytes);

/// <summary>Returned by <c>status</c>.</summary>
public sealed record CliStatus
{
    /// <summary>BetterClipboard version serving the request.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Protocol version the server speaks.</summary>
    public int ProtocolVersion { get; init; } = CliEndpoint.ProtocolVersion;

    /// <summary>Items in history.</summary>
    public long Items { get; init; }

    /// <summary>Pinned items.</summary>
    public long Pinned { get; init; }

    /// <summary>Stored payload bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Entries of the "Forget forever" list: content that is never recorded (managed in the app's Settings;
    /// 0 from servers that predate the list).
    /// </summary>
    public long Forgotten { get; init; }

    /// <summary>Whether live capture is paused.</summary>
    public bool CapturePaused { get; init; }

    /// <summary>Clipboard listener accounting since the app started (see the app's Settings › Capture reliability).</summary>
    public CliCaptureStats? Capture { get; init; }
}

/// <summary>Clipboard listener accounting, mirrored from the Windows layer's monitor statistics.</summary>
/// <param name="Notifications">Change notifications received.</param>
/// <param name="Read">Clipboard states read.</param>
/// <param name="Captured">States recorded.</param>
/// <param name="Superseded">Changes overwritten before they could be read (upper bound on losses).</param>
/// <param name="SelfWrites">Our own writes, ignored on purpose.</param>
/// <param name="LockedOut">Changes given up because the clipboard stayed locked.</param>
/// <param name="Recovered">Changes the watchdog recovered without a notification.</param>
public sealed record CliCaptureStats(long Notifications, long Read, long Captured, long Superseded, long SelfWrites, long LockedOut, long Recovered);

/// <summary>
/// JSON (de)serialization for the protocol: camelCase, nulls omitted, source-generated (no reflection,
/// fast startup for the command line). One request and one response per line on the pipe.
/// </summary>
public static class CliJson
{
    /// <summary>Human-friendly indented options for <c>bclip --json</c> output.</summary>
    private static readonly JsonSerializerOptions Indented = new(CliJsonContext.Default.Options) { WriteIndented = true };

    /// <summary>Serializes a request as one line.</summary>
    /// <param name="request">The request.</param>
    /// <returns>Compact JSON (no newlines).</returns>
    public static string Serialize(CliRequest request) => JsonSerializer.Serialize(request, CliJsonContext.Default.CliRequest);

    /// <summary>Serializes a response, compact for the wire or indented for people and AI tools.</summary>
    /// <param name="response">The response.</param>
    /// <param name="indented">Pretty-print (never use on the wire: the protocol is one JSON value per line).</param>
    /// <returns>The JSON.</returns>
    public static string Serialize(CliResponse response, bool indented = false) => indented
        // The copied options keep the source-generated resolver, so this stays reflection-free.
        ? JsonSerializer.Serialize(response, Indented)
        : JsonSerializer.Serialize(response, CliJsonContext.Default.CliResponse);

    /// <summary>Parses a request line.</summary>
    /// <param name="json">The line.</param>
    /// <returns>The request.</returns>
    /// <exception cref="JsonException">Not a valid request.</exception>
    public static CliRequest DeserializeRequest(string json) =>
        JsonSerializer.Deserialize(json, CliJsonContext.Default.CliRequest) ?? throw new JsonException("Empty request.");

    /// <summary>Parses a response line.</summary>
    /// <param name="json">The line.</param>
    /// <returns>The response.</returns>
    /// <exception cref="JsonException">Not a valid response.</exception>
    public static CliResponse DeserializeResponse(string json) =>
        JsonSerializer.Deserialize(json, CliJsonContext.Default.CliResponse) ?? throw new JsonException("Empty response.");
}

/// <summary>Source-generated serializer metadata for the protocol types (internal: see <see cref="CliJson"/>).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliRequest))]
[JsonSerializable(typeof(CliResponse))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
