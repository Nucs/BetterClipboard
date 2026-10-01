using System.Globalization;
using System.Text.Json;

namespace BetterClipboard.Core.Prompts;

/// <summary>
/// What a Codex session file's first line (<c>session_meta</c>) says about its thread: whose prompts it holds.
/// </summary>
/// <param name="Id">The thread id (also in the file name).</param>
/// <param name="StartedUtc">When the thread started, or <see langword="null"/>.</param>
/// <param name="Cwd">Its working folder, or <see langword="null"/>.</param>
/// <param name="ExcludedAs">
/// <see langword="null"/> when the thread is the user's (its prompts are archived), else why not: <c>subagent</c>,
/// <c>exec</c>, <c>mcp</c>, <c>internal</c>, or the thread source (<c>guardian_review</c>, <c>memory_consolidation</c>).
/// </param>
public sealed record CodexThread(string Id, DateTimeOffset? StartedUtc, string? Cwd, string? ExcludedAs)
{
    /// <summary>Whether the thread's prompts were written by the user (and are archived).</summary>
    public bool IsUserThread => ExcludedAs is null;
}

/// <summary>
/// Reads Codex's prompt records: its CLI history (<c>~/.codex/history.jsonl</c>, or <c>CODEX_HOME</c>) and its session
/// files (<c>sessions/YYYY/MM/DD/rollout-*.jsonl</c>, <c>archived_sessions/</c>, either possibly compressed to
/// <c>.jsonl.zst</c>). Pure: the caller reads the files.
/// </summary>
/// <remarks>
/// <para>
/// <b>History</b> (codex-rs <c>message-history</c>, verified against 564 lines here): <c>{"session_id", "ts" (Unix
/// seconds), "text"}</c> per line, appended under an exclusive file lock — on Windows a mandatory byte-range lock, so a
/// read during a write fails and is retried — and trimmed from the front in place when <c>history.max_bytes</c> is set.
/// Only the CLI writes it (and some app builds): the app's prompts are in the session files alone.
/// </para>
/// <para>
/// <b>Session files.</b> The first line is the thread's <c>session_meta</c>. A typed prompt is a <c>user_message</c> event
/// (threads in the legacy history mode: <c>message</c> plus images) or a completed <c>UserMessage</c> item (the
/// paginated mode, every thread here: <c>content</c> = text, image, local image, skill and mention inputs; the text is
/// the concatenation of the text inputs, as Codex's <c>message()</c> builds it). The plain <c>role: "user"</c> messages are
/// not read: they also carry what Codex injects (AGENTS.md, the environment, plugin lists).
/// </para>
/// <para>
/// <b>Whose prompts</b> (<see cref="ReadThread"/>): threads spawned by an agent (<c>source: {"subagent": …}</c>,
/// thread source <c>subagent</c>), internal ones (guardian review, memory consolidation), <c>codex exec</c> runs and MCP
/// sessions hold prompts written by programs, not typed — on this PC the exec runs are another agent's helper calls. The
/// app, the TUI, the IDE extension and custom clients are the user's.
/// </para>
/// </remarks>
public static class CodexSessions
{
    /// <summary>The CLI history's file name in Codex's home folder.</summary>
    public const string HistoryFileName = "history.jsonl";

    /// <summary>The folder of live session files (<c>YYYY/MM/DD/rollout-*.jsonl</c> below it).</summary>
    public const string SessionsFolder = "sessions";

    /// <summary>The folder archived session files are moved to (flat).</summary>
    public const string ArchivedSessionsFolder = "archived_sessions";

    /// <summary>The suffix Codex adds when it compresses a cold session file (zstd).</summary>
    public const string CompressedSuffix = ".zst";

    /// <summary>Whether a file name is a session file (<c>rollout-*.jsonl</c> or <c>rollout-*.jsonl.zst</c>).</summary>
    /// <param name="fileName">The file name (no folder).</param>
    /// <returns><see langword="true"/> for a session file.</returns>
    public static bool IsSessionFileName(string? fileName) =>
        fileName is not null
        && fileName.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase)
        && (fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jsonl" + CompressedSuffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The name a session file is tracked under: its plain <c>.jsonl</c> name, whether it is compressed or not and in
    /// whichever folder — archiving moves it, compressing renames it, resuming decompresses it, and none of that is new.
    /// </summary>
    /// <param name="fileName">The file name (no folder).</param>
    /// <returns>The plain name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is <see langword="null"/>.</exception>
    public static string CanonicalName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.EndsWith(CompressedSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^CompressedSuffix.Length] : fileName;
    }

    /// <summary>The thread id at the end of a session file's name (<c>rollout-2026-09-21T16-26-03-&lt;uuid&gt;.jsonl</c>).</summary>
    /// <param name="fileName">The file name (no folder).</param>
    /// <returns>The id, or <see langword="null"/> when the name does not end with one.</returns>
    public static string? ThreadIdFromFileName(string? fileName)
    {
        if (fileName is null)
        {
            return null;
        }

        var plain = CanonicalName(fileName);
        var stem = plain.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? plain[..^".jsonl".Length] : plain;
        return stem.Length >= 36 && Guid.TryParse(stem.AsSpan(stem.Length - 36), out _) ? stem[^36..] : null;
    }

    /// <summary>
    /// Reads the thread a session file belongs to from its first line, falling back to the file name's id when the line
    /// is not a <c>session_meta</c> (then the thread is taken as the user's: nothing says otherwise).
    /// </summary>
    /// <param name="firstLine">The file's first line.</param>
    /// <param name="fileName">The file name, for the fallback id.</param>
    /// <returns>The thread.</returns>
    public static CodexThread ReadThread(ReadOnlySpan<byte> firstLine, string? fileName)
    {
        var fallbackId = ThreadIdFromFileName(fileName) ?? fileName ?? string.Empty;
        if (firstLine.IsEmpty)
        {
            return new CodexThread(fallbackId, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(firstLine.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new CodexThread(fallbackId, null, null, null);
            }

            // The meta is the payload of a "session_meta" line; the oldest files had it as the whole line.
            var meta = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                && StringOf(root, "type") == "session_meta" ? payload : root;
            var id = StringOf(meta, "id") ?? fallbackId;
            var started = TimeOf(StringOf(meta, "timestamp")) ?? TimeOf(StringOf(root, "timestamp"));
            return new CodexThread(id, started, StringOf(meta, "cwd"), ExclusionOf(meta));
        }
        catch (JsonException)
        {
            return new CodexThread(fallbackId, null, null, null);
        }
    }

    /// <summary>
    /// Whether a line can hold a prompt — a fast byte search before any JSON parsing, so the gigabytes of tool calls and
    /// reasoning in session files cost a scan, not a parse.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> when it mentions a user message.</returns>
    public static bool MayHoldPrompt(ReadOnlySpan<byte> line) =>
        line.IndexOf("\"UserMessage\""u8) >= 0 || line.IndexOf("\"user_message\""u8) >= 0;

    /// <summary>
    /// Parses one line of a session file into a prompt, if it is one (see the class remarks).
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="thread">The file's thread (<see cref="ReadThread"/>); prompts of threads that are not the user's are not read.</param>
    /// <returns>The prompt, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="thread"/> is <see langword="null"/>.</exception>
    public static AgentPrompt? ParseSessionLine(ReadOnlySpan<byte> line, CodexThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (!thread.IsUserThread || !MayHoldPrompt(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || StringOf(root, "type") != "event_msg"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || TimeOf(StringOf(root, "timestamp")) is not { } at)
            {
                return null;
            }

            switch (StringOf(payload, "type"))
            {
                case "item_completed"
                    when payload.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                        && StringOf(item, "type") == "UserMessage" && StringOf(item, "id") is { } itemId:
                {
                    var (text, images) = ReadInputs(item);
                    return string.IsNullOrWhiteSpace(text)
                        ? null
                        : new AgentPrompt(PromptSourceKind.CodexRollout, PromptRules.CodexItemKey(itemId, text), thread.Id, thread.Cwd, at, text, images, PromptRules.IsSlashCommand(text));
                }

                case "user_message":
                {
                    // Older builds also sent the injected instructions and environment as user messages, marked by a kind.
                    if (StringOf(payload, "kind") is { } kind && kind != "plain")
                    {
                        return null;
                    }

                    var text = StringOf(payload, "message");
                    if (string.IsNullOrWhiteSpace(text) || text.StartsWith("<user_instructions>", StringComparison.Ordinal)
                        || text.StartsWith("<environment_context>", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    int images = CountOf(payload, "images") + CountOf(payload, "local_images");
                    return new AgentPrompt(PromptSourceKind.CodexRollout, PromptRules.CodexEventKey(at.ToUnixTimeMilliseconds(), text), thread.Id, thread.Cwd, at, text, images, PromptRules.IsSlashCommand(text));
                }

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            // A line torn by a crash, or cut by the length limit: not a prompt.
            return null;
        }
    }

    /// <summary>
    /// Parses one line of the CLI history (<c>{"session_id", "ts", "text"}</c>).
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The prompt, or <see langword="null"/> for a malformed or blank line.</returns>
    public static AgentPrompt? ParseHistoryLine(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || StringOf(root, "text") is not { } text || string.IsNullOrWhiteSpace(text)
                || !root.TryGetProperty("ts", out var tsElement) || !tsElement.TryGetInt64(out long seconds)
                || seconds <= 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                return null;
            }

            var session = StringOf(root, "session_id");
            return new AgentPrompt(PromptSourceKind.CodexHistory, PromptRules.CodexHistoryKey(session, seconds, text), session, null,
                DateTimeOffset.FromUnixTimeSeconds(seconds), text, 0, PromptRules.IsSlashCommand(text));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why a thread's prompts are not the user's, or <see langword="null"/> when they are (see the class remarks).
    /// </summary>
    /// <param name="meta">The <c>session_meta</c> payload.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    private static string? ExclusionOf(JsonElement meta)
    {
        var threadSource = StringOf(meta, "thread_source");
        if (threadSource is "subagent" or "guardian_review" or "memory_consolidation")
        {
            return threadSource;
        }

        if (!meta.TryGetProperty("source", out var source))
        {
            return null;
        }

        if (source.ValueKind == JsonValueKind.String)
        {
            // cli, vscode (also the app) and unknown future front-ends are people; exec and mcp are programs.
            return source.GetString() is "exec" or "mcp" ? source.GetString() : null;
        }

        if (source.ValueKind == JsonValueKind.Object)
        {
            // {"custom": "client"} is a front-end someone built on Codex's app server: a person types there too.
            foreach (var property in source.EnumerateObject())
            {
                return property.Name is "subagent" or "internal" ? property.Name : null;
            }
        }

        return null;
    }

    /// <summary>The text and image count of a <c>UserMessage</c> item's inputs.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The concatenated text inputs and the number of image inputs.</returns>
    private static (string Text, int Images) ReadInputs(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, 0);
        }

        var text = new System.Text.StringBuilder();
        int images = 0;
        foreach (var input in content.EnumerateArray())
        {
            switch (StringOf(input, "type"))
            {
                case "text":
                    text.Append(StringOf(input, "text"));
                    break;
                case "image" or "local_image" or "localImage":
                    images++;
                    break;
            }
        }

        return (text.ToString(), images);
    }

    /// <summary>The length of an array property, 0 when missing or not an array.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The count.</returns>
    private static int CountOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    /// <summary>Parses an ISO 8601 time (Codex writes UTC with a Z), or <see langword="null"/>.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The instant.</returns>
    private static DateTimeOffset? TimeOf(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    /// <summary>A string property, or <see langword="null"/> when missing, not a string, or empty.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The value.</returns>
    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
