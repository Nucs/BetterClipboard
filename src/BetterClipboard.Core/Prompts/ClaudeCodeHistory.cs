using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterClipboard.Core.Prompts;

/// <summary>
/// Reads Claude Code's prompt history — <c>history.jsonl</c> in its config folder (<c>~/.claude</c>, or
/// <c>CLAUDE_CONFIG_DIR</c>) — one line at a time. Pure: the caller reads the file and resolves pasted text kept beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format</b> (verified against Claude Code 2.1.282 and 17,370 lines written since 2025-10-01; CLAUDE.md §2.21): one JSON
/// object per line, LF (the first 166 lines here, from 2025-10-01, end with CRLF and have no session id):
/// <c>{"display": text as typed, "pastedContents": {"N": {"id": N, "type": "text", "content": … | "contentHash": …}},
/// "timestamp": Unix ms, "project": working folder, "sessionId": uuid}</c>. Lines are appended in time order under a lock;
/// exact duplicate lines happen (201 here), and Claude Code itself keys an entry by timestamp + session.
/// </para>
/// <para>
/// <b>Pastes.</b> The display keeps a placeholder per paste — <c>[Pasted text #N +M lines]</c>, <c>[Pasted text #N]</c>,
/// <c>[...Truncated text #N +M lines...]</c> — and <c>pastedContents</c> holds the text, inline or by
/// <c>contentHash</c> (the first 16 hex characters of the text's SHA-256) in <c>paste-cache/&lt;hash&gt;.txt</c>, written
/// before the history line. <see cref="Parse"/> puts the text back in place of its placeholder, which is what was sent;
/// a paste whose placeholder was deleted before sending (311 here) is left out, and one whose cached text is gone keeps
/// its placeholder. <c>[Image #N]</c> and <c>[Audio #N]</c> stay as they are: their data is not in the history.
/// </para>
/// </remarks>
public static partial class ClaudeCodeHistory
{
    /// <summary>The file name of the prompt history in Claude Code's config folder.</summary>
    public const string FileName = "history.jsonl";

    /// <summary>The folder (beside the history) that holds pasted text by content hash.</summary>
    public const string PasteCacheFolder = "paste-cache";

    /// <summary>
    /// Parses one line.
    /// </summary>
    /// <param name="line">The line's UTF-8 bytes (without the line break).</param>
    /// <param name="resolvePaste">
    /// Returns the text kept in the paste cache for a content hash (16 hex characters), or <see langword="null"/> when it
    /// is gone or unreadable. Only called for pastes the display still refers to.
    /// </param>
    /// <returns>The prompt, or <see langword="null"/> for a line that is not a prompt (blank, malformed, no timestamp).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolvePaste"/> is <see langword="null"/>.</exception>
    public static AgentPrompt? Parse(ReadOnlySpan<byte> line, Func<string, string?> resolvePaste)
    {
        ArgumentNullException.ThrowIfNull(resolvePaste);
        if (line.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("display", out var displayElement) || displayElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("timestamp", out var timeElement) || !timeElement.TryGetInt64(out long timestamp)
                || timestamp <= 0 || timestamp > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                return null;
            }

            var display = displayElement.GetString()!;
            var session = StringOf(root, "sessionId");
            var project = StringOf(root, "project");
            var pastes = root.TryGetProperty("pastedContents", out var pasteElement) && pasteElement.ValueKind == JsonValueKind.Object
                ? ReadPastes(pasteElement)
                : new Dictionary<int, PastedText>();
            var (text, images) = Expand(display, pastes, resolvePaste);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return new AgentPrompt(
                PromptSourceKind.ClaudeHistory,
                PromptRules.ClaudeHistoryKey(session, timestamp, display),
                session,
                project,
                DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
                text,
                images,
                PromptRules.IsSlashCommand(display));
        }
        catch (JsonException)
        {
            // A torn or hand-edited line: Claude Code itself skips such lines ("Skipping malformed history line").
            return null;
        }
    }

    /// <summary>
    /// Replaces each paste placeholder of a display text with the pasted text (see the class remarks) and counts the
    /// image placeholders.
    /// </summary>
    /// <param name="display">The display text as written in the history.</param>
    /// <param name="pastes">The line's pasted contents by id.</param>
    /// <param name="resolvePaste">Paste-cache lookup by content hash.</param>
    /// <returns>The prompt as sent, and how many images it carried.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static (string Text, int Images) Expand(string display, IReadOnlyDictionary<int, PastedText> pastes, Func<string, string?> resolvePaste)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(pastes);
        ArgumentNullException.ThrowIfNull(resolvePaste);
        int images = 0;
        var text = Placeholder().Replace(display, match =>
        {
            var kind = match.Groups["kind"].Value;
            if (kind == "Image")
            {
                images++;
                return match.Value;
            }

            if (kind is not ("Pasted text" or "...Truncated text")
                || !int.TryParse(match.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                || !pastes.TryGetValue(id, out var paste))
            {
                return match.Value;
            }

            // Inline text first; a hash only when the text was kept beside the history (and is still there).
            return paste.Content ?? (paste.ContentHash is { } hash ? resolvePaste(hash) : null) ?? match.Value;
        });
        return (text, images);
    }

    /// <summary>The paste-cache file of a content hash, or <see langword="null"/> when the hash is not a safe file name.</summary>
    /// <param name="configFolder">Claude Code's config folder.</param>
    /// <param name="contentHash">The hash from the history line.</param>
    /// <returns>The path; <see langword="null"/> for anything but hex digits (a hand-edited hash must not escape the folder).</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static string? PasteCachePath(string configFolder, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(configFolder);
        ArgumentNullException.ThrowIfNull(contentHash);
        return contentHash.Length is >= 8 and <= 64 && contentHash.All(char.IsAsciiHexDigit)
            ? Path.Combine(configFolder, PasteCacheFolder, contentHash + ".txt")
            : null;
    }

    /// <summary>Reads <c>pastedContents</c>: text pastes by id (images and malformed entries are left out).</summary>
    /// <param name="element">The object.</param>
    /// <returns>Text pastes by id.</returns>
    private static Dictionary<int, PastedText> ReadPastes(JsonElement element)
    {
        var pastes = new Dictionary<int, PastedText>();
        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object
                || !int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                || StringOf(value, "type") is not "text")
            {
                continue;
            }

            pastes[id] = new PastedText(
                value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null,
                StringOf(value, "contentHash"));
        }

        return pastes;
    }

    /// <summary>A string property, or <see langword="null"/> when missing, not a string, or blank.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The value.</returns>
    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>
    /// Claude Code's placeholder grammar (2.1.282): <c>[Pasted text #N]</c>, <c>[Pasted text #N +M lines]</c>,
    /// <c>[Image #N]</c>, <c>[Audio #N]</c>, <c>[...Truncated text #N +M lines...]</c>.
    /// </summary>
    /// <returns>The compiled expression.</returns>
    [GeneratedRegex(@"\[(?<kind>Pasted text|Image|Audio|\.\.\.Truncated text) #(?<id>\d{1,9})(?: \+\d{1,9} lines)?\.*\]", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

/// <summary>One text paste of a Claude Code history line.</summary>
/// <param name="Content">The text, when kept inline; otherwise <see langword="null"/>.</param>
/// <param name="ContentHash">Where the text was kept instead (<c>paste-cache/&lt;hash&gt;.txt</c>), or <see langword="null"/>.</param>
public sealed record PastedText(string? Content, string? ContentHash);
