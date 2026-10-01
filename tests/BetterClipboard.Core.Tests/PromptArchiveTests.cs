using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for reading Claude Code's prompt history (<c>history.jsonl</c>; CLAUDE.md §2.21, verified against 2.1.282): the
/// line shape, pastes put back in place of their placeholders, images counted, keys that survive a cleaned paste cache.
/// </summary>
public sealed class ClaudeCodeHistoryTests
{
    /// <summary>A plain line yields its text, session, project and time, with a key built from the display text.</summary>
    [Fact]
    public void Parse_ReadsTextSessionProjectAndTime()
    {
        var prompt = Parse("""{"display":"BC-TEST prompt","pastedContents":{},"timestamp":1759316465000,"project":"K:\\work\\BC-TEST","sessionId":"s-1"}""");
        Assert.NotNull(prompt);
        Assert.Equal("BC-TEST prompt", prompt.Text);
        Assert.Equal("s-1", prompt.SessionId);
        Assert.Equal(@"K:\work\BC-TEST", prompt.Project);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1759316465000), prompt.SentUtc);
        Assert.Equal(PromptAgent.ClaudeCode, prompt.Agent);
        Assert.Equal(PromptSourceKind.ClaudeHistory, prompt.Source);
        Assert.Equal("c1:s-1:1759316465000:d5170deba60d", prompt.Key);
        Assert.False(prompt.IsCommand);
    }

    /// <summary>
    /// Pastes go back where their placeholders were: inline text, text kept in the paste cache (asked for by hash), and a
    /// truncated-text placeholder. A paste whose placeholder was deleted before sending is left out.
    /// </summary>
    [Fact]
    public void Parse_PutsPastesBackInPlace()
    {
        var asked = new List<string>();
        var prompt = ClaudeCodeHistory.Parse(Utf8("""
            {"display":"BC-TEST [Pasted text #1 +2 lines] then [Pasted text #2] and [...Truncated text #4 +9 lines...]",
             "pastedContents":{"1":{"id":1,"type":"text","content":"BC-TEST one\ntwo"},
                               "2":{"id":2,"type":"text","contentHash":"0123456789abcdef"},
                               "3":{"id":3,"type":"text","content":"BC-TEST never sent"},
                               "4":{"id":4,"type":"text","content":"BC-TEST long"}},
             "timestamp":1759316465000,"project":"p","sessionId":"s"}
            """.Replace("\n", string.Empty, StringComparison.Ordinal)), hash =>
        {
            asked.Add(hash);
            return "BC-TEST cached";
        });
        Assert.NotNull(prompt);
        Assert.Equal("BC-TEST BC-TEST one\ntwo then BC-TEST cached and BC-TEST long", prompt.Text);
        Assert.Equal(["0123456789abcdef"], asked);
    }

    /// <summary>A paste whose cached text is gone keeps its placeholder, and the key (from the display) does not change.</summary>
    [Fact]
    public void Parse_KeepsThePlaceholderOfALostPasteAndTheSameKey()
    {
        var line = """{"display":"BC-TEST [Pasted text #1 +2 lines]","pastedContents":{"1":{"id":1,"type":"text","contentHash":"aaaaaaaaaaaaaaaa"}},"timestamp":5,"project":"p","sessionId":"s"}""";
        var found = ClaudeCodeHistory.Parse(Utf8(line), _ => "BC-TEST pasted");
        var lost = ClaudeCodeHistory.Parse(Utf8(line), _ => null);
        Assert.Equal("BC-TEST BC-TEST pasted", found!.Text);
        Assert.Equal("BC-TEST [Pasted text #1 +2 lines]", lost!.Text);
        Assert.Equal(found.Key, lost.Key);
        Assert.Equal("c1:s:5:fc4b844a48b4", lost.Key);
    }

    /// <summary>Image placeholders stay in the text (their data is not in the history) and are counted.</summary>
    [Fact]
    public void Parse_CountsImages()
    {
        var prompt = Parse("""{"display":"BC-TEST [Image #1] and [Image #2]","pastedContents":{},"timestamp":5,"project":"p","sessionId":"s"}""");
        Assert.Equal(2, prompt!.ImageCount);
        Assert.Equal("BC-TEST [Image #1] and [Image #2]", prompt.Text);
    }

    /// <summary>The oldest lines (2025-10-01) have no session id; slash commands are marked as commands.</summary>
    [Fact]
    public void Parse_AcceptsLinesWithoutSessionAndMarksSlashCommands()
    {
        var old = Parse("""{"display":"BC-TEST old","pastedContents":{},"timestamp":7,"project":"p"}""");
        Assert.Null(old!.SessionId);
        Assert.StartsWith("c1::7:", old.Key, StringComparison.Ordinal);
        Assert.True(Parse("""{"display":"/model opus","pastedContents":{},"timestamp":8,"project":"p","sessionId":"s"}""")!.IsCommand);
    }

    /// <summary>Malformed, blank and timeless lines are not prompts (Claude Code skips them too).</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"display":"   ","pastedContents":{},"timestamp":5}""")]
    [InlineData("""{"display":"BC-TEST","pastedContents":{}}""")]
    [InlineData("""{"display":"BC-TEST","timestamp":-1}""")]
    [InlineData("""{"display":5,"timestamp":5}""")]
    [InlineData("""[1,2]""")]
    public void Parse_RejectsWhatIsNotAPrompt(string line) => Assert.Null(Parse(line));

    /// <summary>A paste-cache path is built only from hex digits: a hand-edited hash cannot escape the folder.</summary>
    [Fact]
    public void PasteCachePath_AcceptsOnlyHexHashes()
    {
        Assert.Equal(Path.Combine("root", "paste-cache", "0123456789abcdef.txt"), ClaudeCodeHistory.PasteCachePath("root", "0123456789abcdef"));
        Assert.Null(ClaudeCodeHistory.PasteCachePath("root", @"..\..\secret"));
        Assert.Null(ClaudeCodeHistory.PasteCachePath("root", "abc"));
    }

    /// <summary>Parses a line with a paste cache that has nothing.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The prompt.</returns>
    private static AgentPrompt? Parse(string line) => ClaudeCodeHistory.Parse(Utf8(line), _ => null);

    /// <summary>UTF-8 bytes of a line.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}

/// <summary>
/// Tests for reading Codex's prompt records (codex-rs at 57ac6f5; CLAUDE.md §2.21): the CLI history, the thread on a session
/// file's first line and whose prompts it holds, and both prompt event shapes.
/// </summary>
public sealed class CodexSessionsTests
{
    /// <summary>A CLI history line: session, time in seconds, text.</summary>
    [Fact]
    public void ParseHistoryLine_ReadsSessionTimeAndText()
    {
        var prompt = CodexSessions.ParseHistoryLine(Encoding.UTF8.GetBytes("""{"session_id":"t-1","ts":1780000000,"text":"BC-TEST prompt"}"""));
        Assert.NotNull(prompt);
        Assert.Equal(PromptSourceKind.CodexHistory, prompt.Source);
        Assert.Equal(PromptAgent.Codex, prompt.Agent);
        Assert.Equal("t-1", prompt.SessionId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780000000), prompt.SentUtc);
        Assert.Equal("x1:h:t-1:1780000000:d5170deba60d", prompt.Key);
        Assert.Null(CodexSessions.ParseHistoryLine(Encoding.UTF8.GetBytes("""{"session_id":"t-1","ts":1,"text":"  "}""")));
        Assert.Null(CodexSessions.ParseHistoryLine("garbage"u8));
    }

    /// <summary>Whose prompts a thread holds, from its <c>session_meta</c>: people's front-ends yes, programs no.</summary>
    /// <param name="source">The <c>source</c> JSON.</param>
    /// <param name="threadSource">The <c>thread_source</c>, or null.</param>
    /// <param name="excludedAs">The expected exclusion, or null for the user's thread.</param>
    [Theory]
    [InlineData("\"vscode\"", "user", null)]
    [InlineData("\"cli\"", null, null)]
    [InlineData("""{"custom":"my-client"}""", "user", null)]
    [InlineData("\"vscode\"", "realtime_voice", null)]
    [InlineData("\"exec\"", "user", "exec")]
    [InlineData("\"mcp\"", null, "mcp")]
    [InlineData("""{"subagent":{"thread_spawn":{"parent_thread_id":"p","depth":1}}}""", "subagent", "subagent")]
    [InlineData("""{"subagent":"review"}""", null, "subagent")]
    [InlineData("""{"internal":"guardian"}""", null, "internal")]
    [InlineData("\"vscode\"", "guardian_review", "guardian_review")]
    [InlineData("\"vscode\"", "memory_consolidation", "memory_consolidation")]
    public void ReadThread_TellsWhosePromptsItHolds(string source, string? threadSource, string? excludedAs)
    {
        var threadSourceJson = threadSource is null ? string.Empty : $",\"thread_source\":\"{threadSource}\"";
        var line = $$$"""{"timestamp":"2026-09-30T07:10:48.123Z","type":"session_meta","payload":{"id":"t-1","timestamp":"2026-09-30T07:10:48.000Z","cwd":"K:\\BC-TEST","originator":"Codex Desktop","source":{{{source}}}{{{threadSourceJson}}}}}""";
        var thread = CodexSessions.ReadThread(Encoding.UTF8.GetBytes(line), "rollout-2026-09-30T07-10-48-01a0f082-0630-7d50-a1b2-c3d4e5f6a7b8.jsonl");
        Assert.Equal("t-1", thread.Id);
        Assert.Equal(@"K:\BC-TEST", thread.Cwd);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 7, 10, 48, TimeSpan.Zero), thread.StartedUtc);
        Assert.Equal(excludedAs, thread.ExcludedAs);
        Assert.Equal(excludedAs is null, thread.IsUserThread);
    }

    /// <summary>A first line that is not a <c>session_meta</c> falls back to the file name's thread id, as the user's.</summary>
    [Fact]
    public void ReadThread_FallsBackToTheFileName()
    {
        const string Name = "rollout-2026-09-30T07-10-48-01a0f082-0630-7d50-a1b2-c3d4e5f6a7b8.jsonl.zst";
        var thread = CodexSessions.ReadThread("not json"u8, Name);
        Assert.Equal("01a0f082-0630-7d50-a1b2-c3d4e5f6a7b8", thread.Id);
        Assert.True(thread.IsUserThread);
        Assert.Equal("01a0f082-0630-7d50-a1b2-c3d4e5f6a7b8", CodexSessions.ThreadIdFromFileName(Name));
        Assert.Null(CodexSessions.ThreadIdFromFileName("rollout-short.jsonl"));
    }

    /// <summary>
    /// A completed <c>UserMessage</c> (paginated history): its text inputs joined as Codex joins them, images counted, the
    /// key from the item id and text — no thread, so a fork's copy is the same prompt.
    /// </summary>
    [Fact]
    public void ParseSessionLine_ReadsAUserMessageItem()
    {
        var line = """{"timestamp":"2026-09-30T07:11:00.250Z","ordinal":6,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","id":"item-7","client_id":"c","content":[{"type":"text","text":"BC-TEST ","text_elements":[]},{"type":"image","image_url":"data:image/png;base64,AA=="},{"type":"local_image","path":"K:\\x.png"},{"type":"text","text":"prompt"}]}}}""";
        var prompt = CodexSessions.ParseSessionLine(Encoding.UTF8.GetBytes(line), UserThread("t-1"));
        Assert.NotNull(prompt);
        Assert.Equal("BC-TEST prompt", prompt.Text);
        Assert.Equal(2, prompt.ImageCount);
        Assert.Equal("t-1", prompt.SessionId);
        Assert.Equal(@"K:\BC-TEST", prompt.Project);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 7, 11, 0, 250, TimeSpan.Zero), prompt.SentUtc);
        Assert.Equal("x1:i:item-7:d5170deba60d", prompt.Key);

        // The same item in a forked thread's file: the same key.
        Assert.Equal(prompt.Key, CodexSessions.ParseSessionLine(Encoding.UTF8.GetBytes(line.Replace("07:11:00", "08:00:00", StringComparison.Ordinal)), UserThread("fork"))!.Key);
    }

    /// <summary>A legacy <c>user_message</c> event: plain ones are prompts; injected instructions and environment are not.</summary>
    [Fact]
    public void ParseSessionLine_ReadsLegacyUserMessagesButNotInjections()
    {
        var plain = """{"timestamp":"2025-08-01T10:00:00.000Z","type":"event_msg","payload":{"type":"user_message","message":"BC-TEST prompt","images":["a"],"local_images":["b","c"]}}""";
        var prompt = CodexSessions.ParseSessionLine(Encoding.UTF8.GetBytes(plain), UserThread("t-1"));
        Assert.NotNull(prompt);
        Assert.Equal(3, prompt.ImageCount);
        Assert.Equal($"x1:e:{new DateTimeOffset(2025, 8, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()}:d5170deba60d", prompt.Key);

        Assert.Null(CodexSessions.ParseSessionLine(Encoding.UTF8.GetBytes(plain.Replace("\"images\"", "\"kind\":\"user_instructions\",\"images\"", StringComparison.Ordinal)), UserThread("t-1")));
        Assert.Null(CodexSessions.ParseSessionLine(Encoding.UTF8.GetBytes(plain.Replace("BC-TEST prompt", "<environment_context>BC-TEST</environment_context>", StringComparison.Ordinal)), UserThread("t-1")));
    }

    /// <summary>Everything else in a session file is not a prompt, and nothing is read from a thread that is not the user's.</summary>
    [Fact]
    public void ParseSessionLine_IgnoresOtherLinesAndOtherThreads()
    {
        var item = """{"timestamp":"2026-09-30T07:11:00.000Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","id":"i","content":[{"type":"text","text":"BC-TEST"}]}}}""";
        Assert.Null(CodexSessions.ParseSessionLine(Encoding.UTF8.GetBytes(item), new CodexThread("t", null, null, "subagent")));
        Assert.Null(CodexSessions.ParseSessionLine("""{"timestamp":"2026-09-30T07:11:00.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"BC-TEST"}]}}"""u8, UserThread("t")));
        Assert.Null(CodexSessions.ParseSessionLine("""{"timestamp":"2026-09-30T07:11:00.000Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"i"}}}"""u8, UserThread("t")));
        Assert.False(CodexSessions.MayHoldPrompt("""{"type":"turn_context"}"""u8));
    }

    /// <summary>Session files are tracked by their plain name, in either folder, compressed or not.</summary>
    [Fact]
    public void SessionFileNames()
    {
        Assert.True(CodexSessions.IsSessionFileName("rollout-2026-09-30T07-10-48-x.jsonl"));
        Assert.True(CodexSessions.IsSessionFileName("rollout-2026-09-30T07-10-48-x.jsonl.zst"));
        Assert.False(CodexSessions.IsSessionFileName("rollout-x.jsonl.tmp"));
        Assert.False(CodexSessions.IsSessionFileName("history.jsonl"));
        Assert.Equal("rollout-a.jsonl", CodexSessions.CanonicalName("rollout-a.jsonl.zst"));
        Assert.Equal("rollout-a.jsonl", CodexSessions.CanonicalName("rollout-a.jsonl"));
    }

    /// <summary>A user thread with a working folder.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The thread.</returns>
    private static CodexThread UserThread(string id) => new(id, null, @"K:\BC-TEST", null);
}

/// <summary>Tests for the rules every prompt source shares: frozen key hashes and the slash-command test.</summary>
public sealed class PromptRulesTests
{
    /// <summary>The key hash is the first 12 hex characters of SHA-256 over UTF-8 (known answers from Python's hashlib).</summary>
    [Fact]
    public void ShortHash_KnownAnswers()
    {
        Assert.Equal("d5170deba60d", PromptRules.ShortHash("BC-TEST prompt"));
        Assert.Equal("7a0f0047fea1", PromptRules.ShortHash("BC-TEST héllo ✓"));
    }

    /// <summary>A slash, a command name, then a space or the end; a path typed as a prompt is a prompt.</summary>
    /// <param name="text">The prompt.</param>
    /// <param name="expected">Whether it is a slash command.</param>
    [Theory]
    [InlineData("/model", true)]
    [InlineData("  /compact keep the plan", true)]
    [InlineData("/review-pr 12", true)]
    [InlineData("/plugin:skill args", true)]
    [InlineData("/var/log is full", false)]
    [InlineData("/ alone", false)]
    [InlineData("/1st", false)]
    [InlineData("fix /model", false)]
    [InlineData("/", false)]
    public void IsSlashCommand(string text, bool expected) => Assert.Equal(expected, PromptRules.IsSlashCommand(text));

    /// <summary>A prompt's hashes are those a kept copy of its CRLF text would have.</summary>
    [Fact]
    public void Prompt_HashesItsClipboardText()
    {
        var prompt = new AgentPrompt(PromptSourceKind.ClaudeHistory, "k", null, null, TestData.Now, "BC-TEST a\nb");
        Assert.Equal("BC-TEST a\r\nb", prompt.ClipboardText);
        Assert.Equal(ContentHasher.ForText("BC-TEST a\r\nb"), prompt.TextHash);
        Assert.Equal(ForgetFingerprint.ForText("BC-TEST a\r\nb"), prompt.Fingerprint);
        Assert.Throws<ArgumentException>(() => new AgentPrompt(PromptSourceKind.ClaudeHistory, "k", null, null, TestData.Now, " "));
    }
}

/// <summary>
/// Tests for <see cref="JsonlTail"/>: reading only what was appended, proving it was only appended to, and reading from the
/// start — saying why — when a file was truncated, trimmed, filtered or replaced.
/// </summary>
public sealed class JsonlTailTests
{
    /// <summary>The first read hands over every complete line and leaves a partial last line for later.</summary>
    [Fact]
    public void FirstRead_ReadsCompleteLinesOnly()
    {
        using var file = new MemoryStream(Utf8("a1\nb2\nc3-partial"));
        var (result, lines) = Read(file, null);
        Assert.Equal(TailChange.First, result.Change);
        Assert.Equal(["a1", "b2"], lines);
        Assert.Equal(6, result.Checkpoint.Offset);
        Assert.Equal(16, result.Checkpoint.Length);
        Assert.NotEmpty(result.Checkpoint.HeadHash);
        Assert.NotEmpty(result.Checkpoint.AnchorHash);
    }

    /// <summary>An append is read from the checkpoint on: only the new lines, only the new bytes; a partial line once complete.</summary>
    [Fact]
    public void Append_ReadsOnlyTheNewLines()
    {
        using var file = new MemoryStream();
        Write(file, "a1\nb2\nc3-par");
        var first = Read(file, null).Result;

        Write(file, "tial\nd4\n");
        var (result, lines) = Read(file, first.Checkpoint);
        Assert.Equal(TailChange.Appended, result.Change);
        Assert.Equal(["c3-partial", "d4"], lines);
        Assert.Equal(file.Length - first.Checkpoint.Offset, result.BytesRead);
        Assert.False(result.IsReset);
    }

    /// <summary>Nothing new (or only a growing partial line): nothing is handed over, the checkpoint keeps its place.</summary>
    [Fact]
    public void Unchanged_HandsNothingOver()
    {
        using var file = new MemoryStream();
        Write(file, "a1\nb2\n");
        var first = Read(file, null).Result;
        var (same, none) = Read(file, first.Checkpoint);
        Assert.Equal(TailChange.Unchanged, same.Change);
        Assert.Empty(none);
        Assert.Equal(first.Checkpoint.Offset, same.Checkpoint.Offset);

        Write(file, "c3-still-wri");
        var (growing, stillNone) = Read(file, same.Checkpoint);
        Assert.Equal(TailChange.Unchanged, growing.Change);
        Assert.Empty(stillNone);
        Assert.Equal(first.Checkpoint.Offset, growing.Checkpoint.Offset);
    }

    /// <summary>A file shorter than what was consumed was truncated: read again from its start.</summary>
    [Fact]
    public void Truncated_IsReadFromItsStart()
    {
        using var file = new MemoryStream();
        Write(file, "a1\nb2\nc3\n");
        var first = Read(file, null).Result;
        file.SetLength(0);
        Write(file, "z9\n");
        var (result, lines) = Read(file, first.Checkpoint);
        Assert.Equal(TailChange.Truncated, result.Change);
        Assert.True(result.IsReset);
        Assert.Equal(["z9"], lines);
        Assert.Equal(1, result.Checkpoint.Rewrites);
    }

    /// <summary>
    /// A trim from the front that leaves the file longer than before (Codex's history.max_bytes, Claude Code's retention
    /// prune, then new prompts): the length alone would say "appended"; the fingerprints say "rewritten".
    /// </summary>
    [Fact]
    public void TrimmedFromTheFront_IsCaughtByTheFingerprints()
    {
        using var file = new MemoryStream();
        Write(file, "old-1\nold-2\nkeep-3\n");
        var first = Read(file, null).Result;
        file.SetLength(0);
        Write(file, "keep-3\nnew-4\nnew-5\nnew-6\n");
        Assert.True(file.Length > first.Checkpoint.Offset);
        var (result, lines) = Read(file, first.Checkpoint);
        Assert.Equal(TailChange.Rewritten, result.Change);
        Assert.Equal(["keep-3", "new-4", "new-5", "new-6"], lines);
    }

    /// <summary>A line dropped in the middle (<c>claude purge</c> filtering a project) shifts the bytes before the offset.</summary>
    [Fact]
    public void FilteredInTheMiddle_IsCaughtByTheAnchor()
    {
        using var file = new MemoryStream();
        Write(file, "line-1\nPURGED-2\nline-3\n");
        var first = Read(file, null).Result;
        file.SetLength(0);
        Write(file, "line-1\nline-3\nline-4\nline-5\n");
        var (result, _) = Read(file, first.Checkpoint);
        Assert.Equal(TailChange.Rewritten, result.Change);
    }

    /// <summary>Another file behind the same name (written aside and renamed over) is caught by its id, even with equal bytes.</summary>
    [Fact]
    public void Replaced_IsCaughtByTheFileId()
    {
        using var file = new MemoryStream(Utf8("a1\nb2\n"));
        var first = Read(file, null, "vol-1").Result;
        var (result, lines) = Read(file, first.Checkpoint, "vol-2");
        Assert.Equal(TailChange.Replaced, result.Change);
        Assert.Equal(["a1", "b2"], lines);
        Assert.Equal("vol-2", result.Checkpoint.FileId);

        // An unknown id proves nothing: same bytes, same verdict as before.
        Assert.Equal(TailChange.Unchanged, Read(file, result.Checkpoint, null).Result.Change);
    }

    /// <summary>CRLF line endings (Claude Code's first history lines) lose their CR; blank lines are handed over as empty.</summary>
    [Fact]
    public void CrlfLines_LoseTheirCr()
    {
        using var file = new MemoryStream(Utf8("a1\r\nb2\r\n\r\nc3\n"));
        Assert.Equal(["a1", "b2", "", "c3"], Read(file, null).Lines);
    }

    /// <summary>A line longer than a read chunk arrives whole; a line over the limit is consumed and counted, never handed over.</summary>
    [Fact]
    public void LongLines_AcrossChunksAndOverTheLimit()
    {
        var long1 = new string('x', JsonlTail.ChunkBytes * 2 + 17);
        using var file = new MemoryStream(Utf8($"a1\n{long1}\nb2\n{new string('y', 300)}\nc3\n"));
        var lines = new List<string>();
        var result = JsonlTail.Read(file, State(file), null, line => lines.Add(Encoding.UTF8.GetString(line)), maxLineBytes: JsonlTail.ChunkBytes * 3, CancellationToken.None);
        Assert.Equal(["a1", long1, "b2", new string('y', 300), "c3"], lines);
        Assert.Equal(0, result.SkippedLines);

        file.Position = 0;
        lines.Clear();
        var limited = JsonlTail.Read(file, State(file), null, line => lines.Add(Encoding.UTF8.GetString(line)), maxLineBytes: 200, CancellationToken.None);
        Assert.Equal(["a1", "b2", "c3"], lines);
        Assert.Equal(2, limited.SkippedLines);
        Assert.Equal(file.Length, limited.Checkpoint.Offset);
    }

    /// <summary>
    /// A stream that cannot seek (a decompressed session file) is read from its start, unless its file's state equals the
    /// checkpoint's — then not at all.
    /// </summary>
    [Fact]
    public void UnseekableStreams_AreReadWholeOrNotAtAll()
    {
        var state = new TailFileState(100, TestData.Now, "id-1");
        var lines = new List<string>();
        var first = JsonlTail.Read(new ForwardOnlyStream(Utf8("a1\nb2\n")), state, null, line => lines.Add(Encoding.UTF8.GetString(line)));
        Assert.Equal(TailChange.First, first.Change);
        Assert.Equal(["a1", "b2"], lines);

        lines.Clear();
        var same = JsonlTail.Read(new ForwardOnlyStream(Utf8("a1\nb2\n")), state, first.Checkpoint, line => lines.Add(Encoding.UTF8.GetString(line)));
        Assert.Equal(TailChange.Unchanged, same.Change);
        Assert.Empty(lines);

        var other = JsonlTail.Read(new ForwardOnlyStream(Utf8("a1\nb2\nc3\n")), state with { Length = 120 }, first.Checkpoint, line => lines.Add(Encoding.UTF8.GetString(line)));
        Assert.Equal(TailChange.Rewritten, other.Change);
        Assert.Equal(["a1", "b2", "c3"], lines);
    }

    /// <summary>An empty file: nothing to hand over, an empty checkpoint; its first line is read once written.</summary>
    [Fact]
    public void EmptyFile_ThenItsFirstLine()
    {
        using var file = new MemoryStream();
        var (empty, none) = Read(file, null);
        Assert.Equal(0, empty.Checkpoint.Offset);
        Assert.Empty(none);
        Write(file, "a1\n");
        Assert.Equal(["a1"], Read(file, empty.Checkpoint).Lines);
    }

    /// <summary>Reads a memory "file" with a fixed write time.</summary>
    /// <param name="file">The stream.</param>
    /// <param name="checkpoint">The checkpoint.</param>
    /// <param name="fileId">The file id to report.</param>
    /// <returns>The result and the lines handed over.</returns>
    private static (TailReadResult Result, List<string> Lines) Read(MemoryStream file, TailCheckpoint? checkpoint, string? fileId = null)
    {
        var lines = new List<string>();
        var result = JsonlTail.Read(file, State(file, fileId), checkpoint, line => lines.Add(Encoding.UTF8.GetString(line)));
        return (result, lines);
    }

    /// <summary>The state of a memory "file".</summary>
    /// <param name="file">The stream.</param>
    /// <param name="fileId">Its id.</param>
    /// <returns>The state.</returns>
    private static TailFileState State(MemoryStream file, string? fileId = null) => new(file.Length, TestData.Now, fileId);

    /// <summary>Appends text at the end of a memory "file".</summary>
    /// <param name="file">The stream.</param>
    /// <param name="text">The text.</param>
    private static void Write(MemoryStream file, string text)
    {
        file.Seek(0, SeekOrigin.End);
        file.Write(Utf8(text));
    }

    /// <summary>UTF-8 bytes.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A readable stream that cannot seek, like a decompressing stream.</summary>
    /// <param name="bytes">Its content.</param>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Tests for the prompt archive in the store: identity and merging, the Codex twin records, deletions that stick, rewrites,
/// "Forget forever", the distinct listing and its search, checkpoints stored with their prompts.
/// </summary>
public sealed class PromptStoreTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly ClipStore store;

    /// <summary>Creates a store in a temp folder.</summary>
    public PromptStoreTests()
    {
        store = new ClipStore(temp.DatabasePath);
        store.Initialize();
    }

    /// <summary>Deletes the temp folder.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>A prompt read again (same key) is merged, keeping its earliest send time.</summary>
    [Fact]
    public void Ingest_MergesByKeyKeepingTheEarliestTime()
    {
        var later = Claude("BC-TEST a", TestData.Now, key: "k-1");
        var earlier = Claude("BC-TEST a", TestData.Now.AddMinutes(-5), key: "k-1");
        Assert.Equal(new PromptIngestResult(1, 0, 0), Ingest(later));
        Assert.Equal(new PromptIngestResult(0, 1, 0), Ingest(earlier));
        var row = Assert.Single(Query(new PromptQuery { Agent = PromptAgent.ClaudeCode }));
        Assert.Equal(TestData.Now.AddMinutes(-5), row.LastSentUtc);
        Assert.Equal(1, row.Sends);
    }

    /// <summary>
    /// The two records of one Codex prompt merge in either order: a history line after its session record is dropped; a
    /// session record after its history line takes the row over (its key and source), keeping the earlier time.
    /// </summary>
    [Fact]
    public void Ingest_MergesTheTwoCodexRecordsInEitherOrder()
    {
        var at = TestData.Now;
        Assert.Equal(new PromptIngestResult(1, 0, 0), Ingest(CodexRollout("BC-TEST first", at.AddMilliseconds(700), "t-1", "item-1")));
        Assert.Equal(new PromptIngestResult(0, 1, 0), Ingest(CodexHistory("BC-TEST first", at, "t-1")));

        Assert.Equal(new PromptIngestResult(1, 0, 0), Ingest(CodexHistory("BC-TEST second", at.AddMinutes(1), "t-1")));
        Assert.Equal(new PromptIngestResult(0, 1, 0), Ingest(CodexRollout("BC-TEST second", at.AddMinutes(1).AddSeconds(2), "t-1", "item-2")));

        var rows = Query(new PromptQuery { Agent = PromptAgent.Codex, Distinct = false });
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.Sends));

        // Either order ends with the earlier time (the history line's), on the session record's row.
        Assert.Equal(at.AddMinutes(1), rows[0].LastSentUtc);
        Assert.Equal(at, rows[1].LastSentUtc);
        Assert.Equal(@"K:\BC-TEST", rows[0].Project);
        Assert.Equal(@"K:\BC-TEST", rows[1].Project);

        // Outside the window, or in another session, the same text is another send.
        Assert.Equal(1, Ingest(CodexHistory("BC-TEST first", at.AddMinutes(30), "t-1")).Added);
        Assert.Equal(1, Ingest(CodexHistory("BC-TEST first", at, "t-2")).Added);
    }

    /// <summary>Delete removes every send of a text until it is sent again: re-reads never bring it back, a new send does.</summary>
    [Fact]
    public void Delete_StaysDeletedUntilSentAgain()
    {
        Ingest(Claude("BC-TEST gone", TestData.Now.AddMinutes(-2), key: "k-1"), Claude("BC-TEST gone", TestData.Now.AddMinutes(-1), key: "k-2"));
        var hash = Claude("BC-TEST gone", TestData.Now, key: "x").TextHash;
        Assert.Equal(2, store.DeletePrompts(PromptAgent.ClaudeCode, hash, TestData.Now));
        Assert.Equal(new PromptIngestResult(0, 0, 2), Ingest(Claude("BC-TEST gone", TestData.Now.AddMinutes(-2), key: "k-1"), Claude("BC-TEST gone", TestData.Now.AddMinutes(-1), key: "k-2")));
        Assert.Empty(Query(new PromptQuery { Agent = PromptAgent.ClaudeCode }));

        Assert.Equal(1, Ingest(Claude("BC-TEST gone", TestData.Now.AddMinutes(1), key: "k-3")).Added);
    }

    /// <summary>After a rewrite, prompts at or before the old high-water mark (minus the margin) are not new.</summary>
    [Fact]
    public void Ingest_AfterARewrite_TakesOnlyNewerPrompts()
    {
        var mark = TestData.Now;
        var batch = Batch(
            [Claude("BC-TEST old", mark.AddHours(-1), key: "k-old"), Claude("BC-TEST recent", mark.AddMinutes(-1), key: "k-recent"), Claude("BC-TEST new", mark.AddMinutes(1), key: "k-new")],
            rewrittenAfter: mark);
        Assert.Equal(new PromptIngestResult(2, 0, 1), store.IngestPrompts(batch, long.MaxValue, writeCheckpoint: true, TestData.Now));
        Assert.Equal(["BC-TEST new", "BC-TEST recent"], Query(new PromptQuery { Agent = PromptAgent.ClaudeCode }).Select(r => r.Preview));
    }

    /// <summary>Forgotten texts are never archived, and forgetting a text deletes its archived sends (either agent).</summary>
    [Fact]
    public void ForgetForever_AppliesToTheArchive()
    {
        Ingest(Claude("BC-TEST secret", TestData.Now, key: "k-1"));
        Ingest(CodexHistory("BC-TEST secret\n", TestData.Now, "t-1"));
        store.ForgetText("BC-TEST secret", "Claude Code", TestData.Now);
        Assert.Empty(Query(new PromptQuery()));
        Assert.Equal(new PromptIngestResult(0, 0, 1), Ingest(Claude("  BC-TEST secret  ", TestData.Now.AddMinutes(1), key: "k-2")));

        // Forgetting a stored history entry reaches the archive too.
        Ingest(Claude("BC-TEST other", TestData.Now, key: "k-3"));
        var entry = store.Upsert(TestData.Text("BC-TEST other"), ContentClassifier.Classify(TestData.Text("BC-TEST other"))!, null, bumpIfExists: true)!.Entry;
        store.Forget(entry.Id, TestData.Now);
        Assert.Empty(Query(new PromptQuery()));
    }

    /// <summary>Prompts over the size limit are skipped; a batch marked to skip stores only its checkpoint.</summary>
    [Fact]
    public void Ingest_SkipsTooLargeAndSkippedBatches()
    {
        Assert.Equal(new PromptIngestResult(0, 0, 1), store.IngestPrompts(Batch([Claude("BC-TEST big", TestData.Now, key: "k")]), maxPromptBytes: 4, writeCheckpoint: true, TestData.Now));
        var skipped = Batch([Claude("BC-TEST paused", TestData.Now, key: "k-2")]) with { SkipPrompts = true };
        Assert.Equal(new PromptIngestResult(0, 0, 1), store.IngestPrompts(skipped, long.MaxValue, writeCheckpoint: true, TestData.Now));
        Assert.Empty(Query(new PromptQuery()));
        var file = Assert.Single(store.GetPromptFiles(PromptAgent.ClaudeCode));
        Assert.Equal(TestData.Now, file.Checkpoint.HighWaterUtc);
    }

    /// <summary>
    /// The distinct listing: one row per text, newest send first, with first and last send and the count; paging by text;
    /// every send without <see cref="PromptQuery.Distinct"/>; texts to exclude; a time limit.
    /// </summary>
    [Fact]
    public void Query_ListsEachTextOnceNewestFirst()
    {
        var at = TestData.Now;
        Ingest(
            Claude("BC-TEST continue", at.AddMinutes(-30), key: "a", project: @"K:\one"),
            Claude("BC-TEST fix the test", at.AddMinutes(-20), key: "b"),
            Claude("BC-TEST continue", at.AddMinutes(-10), key: "c", project: @"K:\two"),
            Claude("BC-TEST add docs", at.AddMinutes(-5), key: "d"));
        Ingest(CodexHistory("BC-TEST codex", at, "t-1"));

        var rows = Query(new PromptQuery { Agent = PromptAgent.ClaudeCode });
        Assert.Equal(["BC-TEST add docs", "BC-TEST continue", "BC-TEST fix the test"], rows.Select(r => r.Preview));
        var continued = rows[1];
        Assert.Equal(2, continued.Sends);
        Assert.Equal(at.AddMinutes(-10), continued.LastSentUtc);
        Assert.Equal(at.AddMinutes(-30), continued.FirstSentUtc);
        Assert.Equal(@"K:\two", continued.Project);
        Assert.Null(continued.Text);

        Assert.Equal(["BC-TEST continue"], Query(new PromptQuery { Agent = PromptAgent.ClaudeCode, Offset = 1, Limit = 1 }).Select(r => r.Preview));
        Assert.Equal(4, Query(new PromptQuery { Agent = PromptAgent.ClaudeCode, Distinct = false }).Count);
        Assert.Equal(5, Query(new PromptQuery { Distinct = false }).Count);
        Assert.Equal(["BC-TEST add docs", "BC-TEST fix the test"], Query(new PromptQuery { Agent = PromptAgent.ClaudeCode, ExcludeTextHashes = [continued.TextHash] }).Select(r => r.Preview));
        Assert.Equal(["BC-TEST add docs", "BC-TEST continue"], Query(new PromptQuery { Agent = PromptAgent.ClaudeCode, SentSince = at.AddMinutes(-15) }).Select(r => r.Preview));
        Assert.Equal(1, Query(new PromptQuery { Agent = PromptAgent.ClaudeCode, SentSince = at.AddMinutes(-15) })[1].Sends);

        var full = store.GetPrompt(continued.Id);
        Assert.Equal("BC-TEST continue", full!.Text);
        Assert.Null(store.GetPrompt(99_999));
    }

    /// <summary>The archive is searched like the history: classic words (trigram and short terms) and the toggles.</summary>
    [Fact]
    public void Query_SearchesLikeTheHistory()
    {
        Ingest(
            Claude("BC-TEST Fix the login bug", TestData.Now.AddMinutes(-3), key: "a"),
            Claude("BC-TEST fixture cleanup", TestData.Now.AddMinutes(-2), key: "b"),
            Claude("BC-TEST order 555-1234", TestData.Now.AddMinutes(-1), key: "c"));
        Assert.Equal(2, Query(new PromptQuery { SearchText = "fix" }).Count);
        Assert.Single(Query(new PromptQuery { SearchText = "fix", SearchOptions = SearchOptions.WholeWord }));
        Assert.Single(Query(new PromptQuery { SearchText = "Fix", SearchOptions = SearchOptions.MatchCase }));
        Assert.Single(Query(new PromptQuery { SearchText = @"\d{3}-\d{4}$", SearchOptions = SearchOptions.Regex }));
        Assert.Empty(Query(new PromptQuery { SearchText = "nothing-like-this" }));
        Assert.Throws<SearchPatternException>(() => Query(new PromptQuery { SearchText = "(", SearchOptions = SearchOptions.Regex }));
    }

    /// <summary>A checkpoint (with a Codex thread) is stored with its prompts and read back as stored.</summary>
    [Fact]
    public void Checkpoints_RoundTripWithTheirThread()
    {
        var checkpoint = new TailCheckpoint { Offset = 120, Length = 130, LastWriteUtc = TestData.Now, FileId = "vol-1", HeadHash = "h", AnchorHash = "a", Rewrites = 2 };
        var thread = new CodexThread("t-9", TestData.Now.AddDays(-1), @"K:\BC-TEST", "subagent");
        store.IngestPrompts(new PromptBatch { Agent = PromptAgent.Codex, Kind = PromptSourceKind.CodexRollout, FileKey = "rollout-x.jsonl", Path = @"K:\x\rollout-x.jsonl.zst", Checkpoint = checkpoint, Thread = thread }, long.MaxValue, writeCheckpoint: true, TestData.Now);
        var file = Assert.Single(store.GetPromptFiles(PromptAgent.Codex));
        Assert.Equal("rollout-x.jsonl", file.FileKey);
        Assert.Equal(@"K:\x\rollout-x.jsonl.zst", file.Path);
        Assert.Equal(PromptSourceKind.CodexRollout, file.Kind);
        Assert.Equal(checkpoint, file.Checkpoint);
        Assert.Equal(thread, file.Thread);
        Assert.Empty(store.GetPromptFiles(PromptAgent.ClaudeCode));

        // A later read without a thread keeps the one stored; one with writeCheckpoint false stores nothing of the file.
        store.IngestPrompts(new PromptBatch { Agent = PromptAgent.Codex, Kind = PromptSourceKind.CodexRollout, FileKey = "rollout-x.jsonl", Path = @"K:\y\rollout-x.jsonl", Checkpoint = checkpoint with { Offset = 200 } }, long.MaxValue, writeCheckpoint: true, TestData.Now);
        Assert.Equal(thread, Assert.Single(store.GetPromptFiles(PromptAgent.Codex)).Thread);
        store.IngestPrompts(new PromptBatch { Agent = PromptAgent.Codex, Kind = PromptSourceKind.CodexRollout, FileKey = "rollout-y.jsonl", Path = "y", Checkpoint = checkpoint }, long.MaxValue, writeCheckpoint: false, TestData.Now);
        Assert.Single(store.GetPromptFiles(PromptAgent.Codex));
    }

    /// <summary>Stats, and clearing one agent's archive: prompts, files, tombstones and the first-import mark; the other agent stays.</summary>
    [Fact]
    public void Stats_AndClear()
    {
        Ingest(Claude("BC-TEST a", TestData.Now.AddMinutes(-1), key: "1"), Claude("BC-TEST a", TestData.Now, key: "2"), Claude("BC-TEST b", TestData.Now, key: "3"));
        Ingest(CodexHistory("BC-TEST c", TestData.Now, "t"));
        store.DeletePrompts(PromptAgent.ClaudeCode, Claude("BC-TEST z", TestData.Now, key: "z").TextHash, TestData.Now);
        store.SetStateValue(PromptArchiveState.ImportedStateName(PromptAgent.ClaudeCode), PromptArchiveState.FormatImported(TestData.Now));

        Assert.Equal(new PromptStats(3, 2, 1, TestData.Now), store.GetPromptStats(PromptAgent.ClaudeCode));
        Assert.Equal(4, store.GetPromptStats(null).Sends);

        Assert.Equal(3, store.ClearPrompts(PromptAgent.ClaudeCode));
        Assert.Equal(PromptStats.Empty, store.GetPromptStats(PromptAgent.ClaudeCode));
        Assert.Null(store.GetStateValue(PromptArchiveState.ImportedStateName(PromptAgent.ClaudeCode)));
        Assert.Equal(1, store.GetPromptStats(PromptAgent.Codex).Sends);

        // The tombstone went with the archive: the text is new again.
        Assert.Equal(1, Ingest(Claude("BC-TEST z", TestData.Now.AddDays(-1), key: "z")).Added);
    }

    /// <summary>The archive's tables are added to a store that lacks them (an older build's file), and Initialize is idempotent.</summary>
    [Fact]
    public void Schema_IsAddedToAnOlderStore()
    {
        Ingest(Claude("BC-TEST kept", TestData.Now, key: "k"));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE prompts_fts; DROP TABLE prompts; DROP TABLE prompt_files; DROP TABLE prompt_tombstones;";
            drop.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = new ClipStore(temp.DatabasePath);
        reopened.Initialize();
        reopened.Initialize();
        Assert.Equal(PromptStats.Empty, reopened.GetPromptStats(null));
        Assert.Equal(1, reopened.IngestPrompts(Batch([Claude("BC-TEST again", TestData.Now, key: "k2")]), long.MaxValue, writeCheckpoint: true, TestData.Now).Added);
    }

    /// <summary>The prompt tabs' stored halves: kept prompts by origin, and what Claude Code itself copied by its source name.</summary>
    [Fact]
    public void Filters_FindKeptPrompts()
    {
        Classified(TestData.Text("BC-TEST kept claude", origin: ClipOrigin.ClaudeCode, source: PromptAgents.SourceOf(PromptAgent.ClaudeCode)));
        Classified(TestData.Text("BC-TEST copied by claude", source: new SourceAppInfo("claude", @"C:\x\claude.exe", "Claude Code")));
        Classified(TestData.Text("BC-TEST kept codex", origin: ClipOrigin.Codex, source: PromptAgents.SourceOf(PromptAgent.Codex)));
        Classified(TestData.Text("BC-TEST other"));
        Assert.Equal(["BC-TEST copied by claude", "BC-TEST kept claude"], store.Query(new ClipQuery { Filter = ClipFilter.ClaudeCode }).Select(e => e.Preview).Order());
        Assert.Equal(["BC-TEST kept codex"], store.Query(new ClipQuery { Filter = ClipFilter.Codex }).Select(e => e.Preview));
    }

    /// <summary>Stores a capture like the service would.</summary>
    /// <param name="capture">The capture.</param>
    private void Classified(ClipCapture capture) => store.Upsert(capture, ContentClassifier.Classify(capture)!, null, bumpIfExists: true);

    /// <summary>Stores prompts as one read of one file.</summary>
    /// <param name="prompts">The prompts.</param>
    /// <returns>The result.</returns>
    private PromptIngestResult Ingest(params AgentPrompt[] prompts) => store.IngestPrompts(Batch(prompts), long.MaxValue, writeCheckpoint: true, TestData.Now);

    /// <summary>Queries the archive.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The rows.</returns>
    private IReadOnlyList<PromptSummary> Query(PromptQuery query) => store.QueryPrompts(query, includeText: false, CancellationToken.None);

    /// <summary>A batch of prompts from one file of their (first prompt's) kind.</summary>
    /// <param name="prompts">The prompts.</param>
    /// <param name="rewrittenAfter">The rewrite mark, or null.</param>
    /// <returns>The batch.</returns>
    private static PromptBatch Batch(IReadOnlyList<AgentPrompt> prompts, DateTimeOffset? rewrittenAfter = null)
    {
        var kind = prompts.Count > 0 ? prompts[0].Source : PromptSourceKind.ClaudeHistory;
        return new PromptBatch
        {
            Agent = PromptAgents.AgentOf(kind),
            Kind = kind,
            FileKey = kind.ToString(),
            Path = "BC-TEST",
            Checkpoint = new TailCheckpoint { Offset = 1, Length = 1 },
            Prompts = prompts,
            RewrittenAfterUtc = rewrittenAfter,
        };
    }

    /// <summary>A Claude Code prompt.</summary>
    /// <param name="text">Text.</param>
    /// <param name="at">Send time.</param>
    /// <param name="key">Key.</param>
    /// <param name="project">Project.</param>
    /// <returns>The prompt.</returns>
    private static AgentPrompt Claude(string text, DateTimeOffset at, string key, string? project = null) =>
        new(PromptSourceKind.ClaudeHistory, key, "s-1", project, at, text);

    /// <summary>A Codex CLI history prompt (its key built like the parser's).</summary>
    /// <param name="text">Text.</param>
    /// <param name="at">Send time (seconds are kept).</param>
    /// <param name="session">Session.</param>
    /// <returns>The prompt.</returns>
    private static AgentPrompt CodexHistory(string text, DateTimeOffset at, string session) =>
        new(PromptSourceKind.CodexHistory, PromptRules.CodexHistoryKey(session, at.ToUnixTimeSeconds(), text), session, null, at, text);

    /// <summary>A Codex session file prompt.</summary>
    /// <param name="text">Text.</param>
    /// <param name="at">Send time.</param>
    /// <param name="thread">Thread.</param>
    /// <param name="itemId">Item id.</param>
    /// <returns>The prompt.</returns>
    private static AgentPrompt CodexRollout(string text, DateTimeOffset at, string thread, string itemId) =>
        new(PromptSourceKind.CodexRollout, PromptRules.CodexItemKey(itemId, text), thread, @"K:\BC-TEST", at, text);
}

/// <summary>
/// Tests for the prompt archive in the history service: the capture rules (pause except for a first import, ignored agents,
/// size), slicing a large import, and the change events the panel and Settings listen to.
/// </summary>
public sealed class PromptServiceTests : IAsyncLifetime
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private readonly List<PromptsChangedEventArgs> events = [];
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipHistoryService service = null!;

    /// <summary>Starts a service over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        service = new ClipHistoryService(store, null, () => rules);
        service.PromptsChanged += (_, e) =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };
        service.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the store.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await service.DisposeAsync();
        temp.Dispose();
    }

    /// <summary>
    /// While paused, prompts read live are skipped (their checkpoint still moves); a first import is not — it brings in what
    /// the agent already kept.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Pause_SkipsLivePromptsButNotTheFirstImport()
    {
        rules = rules with { IsPaused = true };
        var live = await service.IngestPromptsAsync(Batch("live", Prompt("BC-TEST live", "k1")));
        Assert.Equal(new PromptIngestResult(0, 0, 1), live);
        Assert.Single(await service.GetPromptFilesAsync(PromptAgent.ClaudeCode));

        var import = await service.IngestPromptsAsync(Batch("import", Prompt("BC-TEST imported", "k2")) with { IsImport = true });
        Assert.Equal(1, import.Added);
        Assert.Equal(1, (await service.GetPromptStatsAsync(PromptAgent.ClaudeCode)).Sends);
    }

    /// <summary>An ignored agent ("claude" in Ignored apps) stops the archive; the size limit skips large prompts.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task IgnoredAgentsAndTheSizeLimit()
    {
        rules = rules with { IgnoredProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude" } };
        Assert.Equal(new PromptIngestResult(0, 0, 1), await service.IngestPromptsAsync(Batch("a", Prompt("BC-TEST a", "k1")) with { IsImport = true }));

        rules = rules with { IgnoredProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase), MaxItemBytes = 10 };
        Assert.Equal(new PromptIngestResult(0, 0, 1), await service.IngestPromptsAsync(Batch("b", Prompt("BC-TEST too long", "k2"))));
    }

    /// <summary>A large read is stored in slices; every prompt arrives, the checkpoint once, and an event says how many were added.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task LargeImports_AreStoredInSlices()
    {
        var prompts = Enumerable.Range(0, (ClipHistoryService.PromptSliceSize * 2) + 7)
            .Select(i => Prompt($"BC-TEST prompt {i}", $"k{i}", TestData.Now.AddSeconds(i)))
            .ToList();
        var result = await service.IngestPromptsAsync(new PromptBatch
        {
            Agent = PromptAgent.ClaudeCode,
            Kind = PromptSourceKind.ClaudeHistory,
            FileKey = "history.jsonl",
            Path = "BC-TEST",
            Checkpoint = new TailCheckpoint { Offset = 1, Length = 1 },
            Prompts = prompts,
            IsImport = true,
        });
        Assert.Equal(prompts.Count, result.Added);
        Assert.Equal(prompts.Count, (await service.GetPromptStatsAsync(PromptAgent.ClaudeCode)).Distinct);
        Assert.Equal(TestData.Now.AddSeconds(prompts.Count - 1), Assert.Single(await service.GetPromptFilesAsync(PromptAgent.ClaudeCode)).Checkpoint.HighWaterUtc);
        lock (events)
        {
            Assert.Equal(prompts.Count, events.Sum(e => e.Added));
            Assert.Equal(3, events.Count);
        }
    }

    /// <summary>Delete and clear go through the worker and say what they removed.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DeleteAndClear_RaiseEvents()
    {
        await service.IngestPromptsAsync(Batch("a", Prompt("BC-TEST a", "k1"), Prompt("BC-TEST b", "k2")));
        Assert.Equal(1, await service.DeletePromptsAsync(PromptAgent.ClaudeCode, Prompt("BC-TEST a", "x").TextHash));
        Assert.Equal(1, await service.ClearPromptsAsync(PromptAgent.ClaudeCode));
        lock (events)
        {
            Assert.Equal([2, 0, 0], events.Select(e => e.Added));
            Assert.Equal([0, 1, 1], events.Select(e => e.Removed));
        }

        var page = await service.QueryPromptsAsync(new PromptQuery { Agent = PromptAgent.ClaudeCode });
        Assert.Empty(page);
    }

    /// <summary>A prompt kept from its tab is a new event: refused while paused like a copy, stored otherwise.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task KeepingAPrompt_IsANewEvent()
    {
        var keep = TestData.Text("BC-TEST kept prompt", origin: ClipOrigin.Codex, source: PromptAgents.SourceOf(PromptAgent.Codex));
        rules = rules with { IsPaused = true };
        Assert.Null(await service.AddAsync(keep));
        rules = rules with { IsPaused = false };
        var entry = await service.AddAsync(keep);
        Assert.Equal(ClipOrigin.Codex, entry!.Origin);
        Assert.Equal("Codex", entry.SourceAppName);
    }

    /// <summary>A batch of Claude Code prompts from one file.</summary>
    /// <param name="key">The file key.</param>
    /// <param name="prompts">The prompts.</param>
    /// <returns>The batch.</returns>
    private static PromptBatch Batch(string key, params AgentPrompt[] prompts) => new()
    {
        Agent = PromptAgent.ClaudeCode,
        Kind = PromptSourceKind.ClaudeHistory,
        FileKey = key,
        Path = "BC-TEST",
        Checkpoint = new TailCheckpoint { Offset = 1, Length = 1 },
        Prompts = prompts,
    };

    /// <summary>A Claude Code prompt.</summary>
    /// <param name="text">Text.</param>
    /// <param name="key">Key.</param>
    /// <param name="at">Send time (default <see cref="TestData.Now"/>).</param>
    /// <returns>The prompt.</returns>
    private static AgentPrompt Prompt(string text, string key, DateTimeOffset? at = null) =>
        new(PromptSourceKind.ClaudeHistory, key, "s", null, at ?? TestData.Now, text);
}
