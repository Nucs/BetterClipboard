using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Shells;

namespace BetterClipboard.Core.Prompts;

/// <summary>
/// One prompt the user sent to an AI agent, as read from the agent's own files — before it is stored in the prompt
/// archive (<see cref="Storage.ClipStore"/>'s <c>prompts</c> table).
/// </summary>
/// <remarks>
/// Immutable; safe to hand between threads. <see cref="TextHash"/> and <see cref="Fingerprint"/> are computed from
/// <see cref="ClipboardText"/>, exactly what keeping the prompt as a history entry would compute, so a kept copy and its
/// archived prompt are recognized as one, and "Forget forever" recognizes either.
/// </remarks>
public sealed record AgentPrompt
{
    /// <summary>
    /// Creates a prompt.
    /// </summary>
    /// <param name="source">The kind of file it was read from (its agent follows from it).</param>
    /// <param name="key">
    /// Its identity across reads (<see cref="PromptRules"/>' key builders): the same prompt read again — from a re-read
    /// file, a moved or compressed session file, a fork that copied it — must produce the same key, or it is stored twice.
    /// </param>
    /// <param name="sessionId">The agent's session (Claude Code) or thread (Codex) id, when known.</param>
    /// <param name="project">The working folder it was sent from (Claude Code's project, Codex's cwd), when known.</param>
    /// <param name="sentUtc">When it was sent.</param>
    /// <param name="text">The prompt as sent: pastes expanded, line breaks as the agent wrote them.</param>
    /// <param name="imageCount">Images attached to it (their pixels are not archived, only the count).</param>
    /// <param name="isCommand">Whether it is a slash command (<c>/model</c>, <c>/clear</c>) rather than a prompt for the model.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> or <paramref name="text"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="imageCount"/> is negative.</exception>
    public AgentPrompt(PromptSourceKind source, string key, string? sessionId, string? project, DateTimeOffset sentUtc, string text, int imageCount = 0, bool isCommand = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegative(imageCount);
        Source = source;
        Key = key;
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        Project = string.IsNullOrWhiteSpace(project) ? null : project;
        SentUtc = sentUtc.ToUniversalTime();
        Text = text;
        ImageCount = imageCount;
        IsCommand = isCommand;
        ClipboardText = ShellCommand.ToClipboardText(text);
        TextHash = ContentHasher.ForText(ClipboardText);

        // Never null here: the text is not blank, and only blank text has no text fingerprint.
        Fingerprint = ForgetFingerprint.ForText(ClipboardText) ?? TextHash;
    }

    /// <summary>The kind of file it was read from.</summary>
    public PromptSourceKind Source { get; }

    /// <summary>The agent it was sent to.</summary>
    public PromptAgent Agent => PromptAgents.AgentOf(Source);

    /// <summary>Its identity across reads (unique in the archive; see the constructor).</summary>
    public string Key { get; }

    /// <summary>The agent's session or thread id, or <see langword="null"/> when the file does not say.</summary>
    public string? SessionId { get; }

    /// <summary>The working folder it was sent from, or <see langword="null"/>.</summary>
    public string? Project { get; }

    /// <summary>When it was sent (UTC). Claude Code records milliseconds, Codex's CLI history whole seconds.</summary>
    public DateTimeOffset SentUtc { get; }

    /// <summary>The prompt as sent (pastes expanded; images as their placeholders).</summary>
    public string Text { get; }

    /// <summary>Images attached to it (counted, never archived).</summary>
    public int ImageCount { get; }

    /// <summary>Whether it is a slash command rather than a prompt for the model.</summary>
    public bool IsCommand { get; }

    /// <summary>
    /// The text as it goes onto the clipboard and into the history: CRLF line breaks, the Windows convention every app
    /// pastes correctly (<see cref="ShellCommand.ToClipboardText"/>).
    /// </summary>
    public string ClipboardText { get; }

    /// <summary>
    /// The dedupe key a stored copy of this prompt has (<see cref="ContentHasher.ForText"/> of <see cref="ClipboardText"/>):
    /// the panel lists one card per text hash, and a kept copy hides its archived twin.
    /// </summary>
    public string TextHash { get; }

    /// <summary>The "Forget forever" fingerprint of <see cref="ClipboardText"/> (<see cref="ForgetFingerprint.ForText"/>).</summary>
    public string Fingerprint { get; }

    /// <summary>The size of <see cref="ClipboardText"/> in bytes as the history would count it (UTF-16).</summary>
    public long SizeBytes => ClipboardText.Length * 2L;
}

/// <summary>
/// The rules every prompt source shares: identity keys, the slash-command test, size and timing constants.
/// </summary>
/// <remarks>
/// Keys are frozen: changing one makes every archived prompt look new on the next re-read of its file (a duplicate per
/// prompt). Add a new prefix for a new scheme instead, and keep reading the old ones.
/// </remarks>
public static class PromptRules
{
    /// <summary>
    /// How far apart the two records of one Codex prompt may be — the CLI's <c>history.jsonl</c> line (seconds, taken at
    /// submit) and the session file's <c>UserMessage</c> (milliseconds, taken when recorded). Measured on this PC: 530 of
    /// 564 history lines had their twin in a session file, median 0.7 s apart, 95% within 2.3 s (CLAUDE.md §2.21).
    /// </summary>
    public static readonly TimeSpan CodexMergeWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// After a file was rewritten (pruned, trimmed, replaced) and read again from its start, prompts this much older than
    /// the newest one read from it before are taken as already decided (stored, deleted, or skipped while paused): only
    /// newer ones can be new. The margin covers two agent processes appending slightly out of order (Claude Code retries
    /// a contended write after 500 ms).
    /// </summary>
    public static readonly TimeSpan RewriteMargin = TimeSpan.FromMinutes(5);

    /// <summary>Hex characters of a text hash inside a key: 48 bits, enough to tell apart prompts sent in one instant.</summary>
    private const int KeyHashChars = 12;

    /// <summary>
    /// Key of a Claude Code history line: its session, its timestamp (Claude Code's own identity of an entry is
    /// timestamp + session) and a hash of the display text as written — never of the expanded text, which changes when
    /// Claude Code's paste cache is cleaned up.
    /// </summary>
    /// <param name="sessionId">The session id (empty for entries from before Claude Code recorded one).</param>
    /// <param name="timestampMs">The entry's timestamp, Unix milliseconds.</param>
    /// <param name="display">The display text as written in the file.</param>
    /// <returns>The key (<c>c1:…</c>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="display"/> is <see langword="null"/>.</exception>
    public static string ClaudeHistoryKey(string? sessionId, long timestampMs, string display) =>
        $"c1:{sessionId ?? string.Empty}:{timestampMs}:{ShortHash(display)}";

    /// <summary>
    /// Key of a Codex CLI history line. Its twin in a session file has another key; the store merges the two by session,
    /// text and <see cref="CodexMergeWindow"/>.
    /// </summary>
    /// <param name="sessionId">The session (thread) id.</param>
    /// <param name="unixSeconds">The line's <c>ts</c>.</param>
    /// <param name="text">The text.</param>
    /// <returns>The key (<c>x1:h:…</c>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string CodexHistoryKey(string? sessionId, long unixSeconds, string text) =>
        $"x1:h:{sessionId ?? string.Empty}:{unixSeconds}:{ShortHash(text)}";

    /// <summary>
    /// Key of a completed Codex <c>UserMessage</c> item: its item id and its text — no thread. A forked thread's session
    /// file repeats its parent's items with the same ids and texts (and a later time), and they must stay one prompt;
    /// short item ids are per-thread counters that do collide across threads, but then with other texts.
    /// </summary>
    /// <param name="itemId">The item's id.</param>
    /// <param name="text">The prompt text.</param>
    /// <returns>The key (<c>x1:i:…</c>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string CodexItemKey(string itemId, string text) => $"x1:i:{itemId}:{ShortHash(text)}";

    /// <summary>
    /// Key of a Codex <c>user_message</c> event (older session files, without item ids): its time and text, no thread, so
    /// a fork that copied the event verbatim still yields one prompt.
    /// </summary>
    /// <param name="timestampMs">The line's timestamp, Unix milliseconds.</param>
    /// <param name="text">The prompt text.</param>
    /// <returns>The key (<c>x1:e:…</c>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string CodexEventKey(long timestampMs, string text) => $"x1:e:{timestampMs}:{ShortHash(text)}";

    /// <summary>
    /// Whether a prompt is a slash command (<c>/model</c>, <c>/compact keep the plan</c>, <c>/review-pr 12</c>): a slash,
    /// then a name of letters, digits, <c>-</c>, <c>_</c>, <c>:</c> or <c>.</c> up to the first space or the end. A path
    /// typed as a prompt (<c>/var/log is full</c>) has a second slash in that first word and is a prompt.
    /// </summary>
    /// <param name="text">The prompt.</param>
    /// <returns><see langword="true"/> for a slash command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static bool IsSlashCommand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan().TrimStart();
        if (span.Length < 2 || span[0] != '/' || !char.IsAsciiLetter(span[1]))
        {
            return false;
        }

        foreach (var ch in span[1..])
        {
            if (char.IsWhiteSpace(ch))
            {
                return true;
            }

            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first <see cref="KeyHashChars"/> hex characters of the SHA-256 of a text's UTF-8 bytes.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Lowercase hex.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string ShortHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), digest);
        return Convert.ToHexStringLower(digest)[..KeyHashChars];
    }
}
