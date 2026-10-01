using System.Text;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Integrations;
using ZstdSharp;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for the prompt archive's readers on temp folders shaped like Claude Code's and Codex's (never the user's real
/// ones; BC-TEST prompts only, CLAUDE.md §2.21): the first import, appends found by the watchers and by the poll (a writer
/// that keeps its file open), rewrites and replacements, moves and compression, locks, pause, off and on, restarts.
/// </summary>
public sealed class PromptArchiveTests : IAsyncLifetime
{
    /// <summary>How long a test waits for the reader to store something.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"))).FullName;
    private CaptureRules rules = new() { Retention = RetentionPolicy.Unlimited };
    private ClipStore store = null!;
    private ClipHistoryService history = null!;

    /// <summary>Folder of the fake Claude Code config.</summary>
    private string ClaudeRoot => Path.Combine(root, ".claude");

    /// <summary>Folder of the fake Codex home.</summary>
    private string CodexRoot => Path.Combine(root, ".codex");

    /// <summary>Starts a history service over a temp store.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(ClaudeRoot);
        Directory.CreateDirectory(CodexRoot);
        store = new ClipStore(Path.Combine(root, "history.db"));
        store.Initialize();
        history = new ClipHistoryService(store, null, () => rules);
        history.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the temp folders.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await history.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A scanner briefly holding a file: the temp folder is cleaned up eventually anyway.
        }
    }

    /// <summary>
    /// Claude Code: the first import reads the whole history (pastes put back from the paste cache), marks the agent imported,
    /// then a prompt appended later is found by the folder watcher alone.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Claude_ImportsThenFollowsAppends()
    {
        Directory.CreateDirectory(Path.Combine(ClaudeRoot, "paste-cache"));
        await File.WriteAllTextAsync(Path.Combine(ClaudeRoot, "paste-cache", "0123456789abcdef.txt"), "BC-TEST pasted\nlines");
        AppendLines(Path.Combine(ClaudeRoot, "history.jsonl"),
            ClaudeLine("BC-TEST first", 1_000, "s-1"),
            ClaudeLine("BC-TEST with [Pasted text #1 +2 lines]", 2_000, "s-1", """{"1":{"id":1,"type":"text","contentHash":"0123456789abcdef"}}"""));

        await using var reader = new AgentPromptsIntegration(history, new ClaudeCodePromptSource(ClaudeRoot));
        await reader.SetEnabledAsync(true);
        await WaitFor(() => store.GetPromptStats(PromptAgent.ClaudeCode).Sends == 2);
        await WaitFor(() => store.GetStateValue(PromptArchiveState.ImportedStateName(PromptAgent.ClaudeCode)) is not null);
        Assert.False(reader.IsImporting);
        Assert.Contains(Texts(PromptAgent.ClaudeCode), t => t == "BC-TEST with BC-TEST pasted\nlines");

        // Claude Code opens, appends and closes: the watcher sees it without any poll or catch-up.
        AppendLines(Path.Combine(ClaudeRoot, "history.jsonl"), ClaudeLine("BC-TEST live", 3_000, "s-2"));
        await WaitFor(() => store.GetPromptStats(PromptAgent.ClaudeCode).Sends == 3);
        Assert.Equal(3, reader.AddedThisSession);
        Assert.Equal(1, reader.TrackedFiles);
    }

    /// <summary>
    /// Claude Code's retention prune writes a shorter history aside and renames it over the old one: nothing is stored twice,
    /// and prompts appended after the prune are stored.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Claude_PruneByRenameOver_StoresNothingTwice()
    {
        var path = Path.Combine(ClaudeRoot, "history.jsonl");
        AppendLines(path, ClaudeLine("BC-TEST old 1", 1_000, "s"), ClaudeLine("BC-TEST old 2", 2_000, "s"), ClaudeLine("BC-TEST kept 3", 3_000, "s"));
        await using var reader = new AgentPromptsIntegration(history, new ClaudeCodePromptSource(ClaudeRoot));
        await reader.SetEnabledAsync(true);
        await WaitFor(() => store.GetPromptStats(PromptAgent.ClaudeCode).Sends == 3);

        var staging = path + ".staging";
        AppendLines(staging, ClaudeLine("BC-TEST kept 3", 3_000, "s"), ClaudeLine("BC-TEST new 4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "s"));
        File.Move(staging, path, overwrite: true);
        await WaitFor(() => store.GetPromptStats(PromptAgent.ClaudeCode).Sends == 4);
        await reader.CatchUpAsync(Deadline);
        Assert.Equal(4, store.GetPromptStats(PromptAgent.ClaudeCode).Sends);
        Assert.Equal(1, Assert.Single(store.GetPromptFiles(PromptAgent.ClaudeCode)).Checkpoint.Rewrites);
    }

    /// <summary>
    /// Codex: a user thread's prompts are archived, a subagent's and an exec run's are not (their files are tracked, never
    /// read past the first line), and the CLI history merges with the session records instead of doubling them.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Codex_ArchivesTheUsersThreadsOnly()
    {
        var day = Path.Combine(CodexRoot, "sessions", "2026", "09", "30");
        Directory.CreateDirectory(day);
        var at = new DateTimeOffset(2026, 9, 30, 7, 0, 0, TimeSpan.Zero);
        AppendLines(Path.Combine(day, RolloutName("01a0f082-0630-7d50-a1b2-c3d4e5f6a7b8")),
            Meta("01a0f082-0630-7d50-a1b2-c3d4e5f6a7b8", "\"vscode\"", "user", at),
            UserMessage("item-1", "BC-TEST mine", at.AddSeconds(5)),
            """{"timestamp":"2026-09-30T07:00:06.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>BC-TEST</environment_context>"}]}}""");
        AppendLines(Path.Combine(day, RolloutName("01a0f082-0630-7d50-a1b2-c3d4e5f6a7b9")),
            Meta("01a0f082-0630-7d50-a1b2-c3d4e5f6a7b9", """{"subagent":{"thread_spawn":{"parent_thread_id":"p","depth":1}}}""", "subagent", at),
            UserMessage("item-1", "BC-TEST written by an agent", at.AddSeconds(5)));
        AppendLines(Path.Combine(day, RolloutName("01a0f082-0630-7d50-a1b2-c3d4e5f6a7ba")),
            Meta("01a0f082-0630-7d50-a1b2-c3d4e5f6a7ba", "\"exec\"", "user", at),
            UserMessage("item-9", "BC-TEST exec run", at.AddSeconds(5)));
        AppendLines(Path.Combine(CodexRoot, "history.jsonl"),
            $$"""{"session_id":"01a0f082-0630-7d50-a1b2-c3d4e5f6a7b8","ts":{{at.AddSeconds(4).ToUnixTimeSeconds()}},"text":"BC-TEST mine"}""",
            $$"""{"session_id":"01a0f082-0630-7d50-a1b2-c3d4e5f6a7b8","ts":{{at.AddSeconds(30).ToUnixTimeSeconds()}},"text":"/status"}""");

        await using var reader = new AgentPromptsIntegration(history, new CodexPromptSource(CodexRoot));
        await reader.SetEnabledAsync(true);
        await WaitFor(() => store.GetStateValue(PromptArchiveState.ImportedStateName(PromptAgent.Codex)) is not null);
        Assert.Equal(["/status", "BC-TEST mine"], Texts(PromptAgent.Codex));
        Assert.Equal(2, reader.ExcludedFiles);
        Assert.Equal(4, reader.TrackedFiles);
        var mine = store.QueryPrompts(new PromptQuery { Agent = PromptAgent.Codex, SearchText = "mine" }, includeText: false, CancellationToken.None).Single();
        Assert.Equal(1, mine.Sends);
        Assert.Equal(at.AddSeconds(4), mine.LastSentUtc);
    }

    /// <summary>
    /// Codex keeps a session file open while it appends: no change notification arrives and the folder shows the old size,
    /// so the poll of files being written (here forced by a catch-up) is what finds the new prompt.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Codex_AFileKeptOpenForAppending_IsFoundByThePoll()
    {
        var day = Directory.CreateDirectory(Path.Combine(CodexRoot, "sessions", "2026", "10", "01")).FullName;
        var id = "01a0f082-0630-7d50-a1b2-c3d4e5f6a7c1";
        var path = Path.Combine(day, RolloutName(id));
        var now = DateTimeOffset.UtcNow;
        await using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Write(writer, Meta(id, "\"vscode\"", "user", now), UserMessage("item-1", "BC-TEST before", now));

        await using var reader = new AgentPromptsIntegration(history, new CodexPromptSource(CodexRoot));
        await reader.SetEnabledAsync(true);
        await WaitFor(() => Texts(PromptAgent.Codex).Contains("BC-TEST before"));

        Write(writer, UserMessage("item-2", "BC-TEST while open", now.AddSeconds(1)));
        await reader.CatchUpAsync(Deadline);
        await WaitFor(() => Texts(PromptAgent.Codex).Contains("BC-TEST while open"));
        Assert.Equal(2, store.GetPromptStats(PromptAgent.Codex).Sends);
    }

    /// <summary>
    /// Archiving moves a session file, and Codex compresses a cold one to .jsonl.zst: both are tracked under the plain name, so
    /// nothing is stored twice; a compressed file holding a new prompt is read (zstd) and adds just that prompt.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Codex_MovesAndCompression_StoreNothingTwice()
    {
        var day = Directory.CreateDirectory(Path.Combine(CodexRoot, "sessions", "2026", "09", "01")).FullName;
        var archived = Directory.CreateDirectory(Path.Combine(CodexRoot, "archived_sessions")).FullName;
        var id = "01a0f082-0630-7d50-a1b2-c3d4e5f6a7d1";
        var at = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var lines = new[] { Meta(id, "\"cli\"", null, at), UserMessage("1", "BC-TEST one", at), UserMessage("2", "BC-TEST two", at.AddMinutes(1)) };
        var plain = Path.Combine(day, RolloutName(id));
        AppendLines(plain, lines);

        await using var reader = new AgentPromptsIntegration(history, new CodexPromptSource(CodexRoot));
        await reader.SetEnabledAsync(true);
        await WaitFor(() => store.GetPromptStats(PromptAgent.Codex).Sends == 2);

        // Archived: moved to the flat archive folder.
        var moved = Path.Combine(archived, RolloutName(id));
        File.Move(plain, moved);
        await WaitFor(() => store.GetPromptFiles(PromptAgent.Codex).Single().Path == moved);

        // Compressed (cold), with a prompt the plain file never had: only that one is new.
        var compressed = moved + ".zst";
        await using (var output = File.Create(compressed))
        await using (var zstd = new CompressionStream(output, level: 3))
        {
            zstd.Write(Encoding.UTF8.GetBytes(string.Join('\n', [.. lines, UserMessage("3", "BC-TEST three", at.AddMinutes(2))]) + "\n"));
        }

        File.Delete(moved);
        await WaitFor(() => store.GetPromptStats(PromptAgent.Codex).Sends == 3);
        await reader.CatchUpAsync(Deadline);
        Assert.Equal(["BC-TEST one", "BC-TEST three", "BC-TEST two"], Texts(PromptAgent.Codex).Order());
        Assert.Single(store.GetPromptFiles(PromptAgent.Codex));
    }

    /// <summary>
    /// Codex's history is written under a mandatory Windows file lock: a read that runs into it is retried until the lock is
    /// gone, then reads the file.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Codex_HistoryLockedByAWriter_IsReadOnceUnlocked()
    {
        var path = Path.Combine(CodexRoot, "history.jsonl");
        AppendLines(path, """{"session_id":"t","ts":1780000000,"text":"BC-TEST locked"}""");
        using (var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            locker.Lock(0, locker.Length);
            var source = new CodexPromptSource(CodexRoot);
            var read = Task.Run(() => PromptFileAccess.WithLockRetries(() =>
            {
                using var stream = PromptFileAccess.OpenShared(path);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes.Length;
            }, CancellationToken.None));
            await Task.Delay(150);
            Assert.False(read.IsCompleted);
            locker.Unlock(0, locker.Length);
            Assert.True(await read > 0);
            Assert.True(source.HasData());
        }
    }

    /// <summary>
    /// While capture is paused, prompts appended after the first import are skipped for good (the checkpoint moves past them);
    /// the first import itself was not skipped. Off stops reading; on again catches up on what was appended meanwhile.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task PauseAndOffOn()
    {
        var path = Path.Combine(ClaudeRoot, "history.jsonl");
        rules = rules with { IsPaused = true };
        AppendLines(path, ClaudeLine("BC-TEST imported while paused", 1_000, "s"));
        await using var reader = new AgentPromptsIntegration(history, new ClaudeCodePromptSource(ClaudeRoot));
        await reader.SetEnabledAsync(true);
        await WaitFor(() => store.GetStateValue(PromptArchiveState.ImportedStateName(PromptAgent.ClaudeCode)) is not null);
        Assert.Equal(["BC-TEST imported while paused"], Texts(PromptAgent.ClaudeCode));

        AppendLines(path, ClaudeLine("BC-TEST sent while paused", 2_000, "s"));
        await WaitFor(() => store.GetPromptFiles(PromptAgent.ClaudeCode).Single().Checkpoint.Offset == new FileInfo(path).Length);
        Assert.Equal(["BC-TEST imported while paused"], Texts(PromptAgent.ClaudeCode));

        rules = rules with { IsPaused = false };
        await reader.SetEnabledAsync(false);
        AppendLines(path, ClaudeLine("BC-TEST sent while off", 3_000, "s"));
        await Task.Delay(500);
        Assert.Equal(1, store.GetPromptStats(PromptAgent.ClaudeCode).Sends);

        await reader.SetEnabledAsync(true);
        await WaitFor(() => store.GetPromptStats(PromptAgent.ClaudeCode).Sends == 2);
        Assert.Contains("BC-TEST sent while off", Texts(PromptAgent.ClaudeCode));
        Assert.DoesNotContain("BC-TEST sent while paused", Texts(PromptAgent.ClaudeCode));
    }

    /// <summary>A new reader over the same store (an app restart) reads only what was appended since: nothing twice.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Restart_ReadsOnlyWhatWasAppended()
    {
        var path = Path.Combine(ClaudeRoot, "history.jsonl");
        AppendLines(path, ClaudeLine("BC-TEST a", 1_000, "s"), ClaudeLine("BC-TEST b", 2_000, "s"));
        await using (var first = new AgentPromptsIntegration(history, new ClaudeCodePromptSource(ClaudeRoot)))
        {
            await first.SetEnabledAsync(true);
            await WaitFor(() => store.GetStateValue(PromptArchiveState.ImportedStateName(PromptAgent.ClaudeCode)) is not null);
        }

        AppendLines(path, ClaudeLine("BC-TEST c", 3_000, "s"));
        await using var second = new AgentPromptsIntegration(history, new ClaudeCodePromptSource(ClaudeRoot));
        await second.SetEnabledAsync(true);
        await WaitFor(() => store.GetPromptStats(PromptAgent.ClaudeCode).Sends == 3);
        await second.CatchUpAsync(Deadline);
        Assert.Equal(1, second.AddedThisSession);
        Assert.False(second.IsImporting);
    }

    /// <summary>The file state comes from the open handle, and a file keeps its id when it is moved (that is how a move is told from a replacement).</summary>
    [Fact]
    public void FileState_IdSurvivesAMove()
    {
        var path = Path.Combine(root, "a.jsonl");
        File.WriteAllText(path, "BC-TEST\n");
        string? before;
        using (var stream = PromptFileAccess.OpenShared(path))
        {
            var state = PromptFileAccess.StateOf(stream);
            Assert.Equal(8, state.Length);
            before = state.FileId;
            Assert.NotNull(before);
        }

        var moved = Path.Combine(root, "b.jsonl");
        File.Move(path, moved);
        using (var stream = PromptFileAccess.OpenShared(moved))
        {
            Assert.Equal(before, PromptFileAccess.StateOf(stream).FileId);
        }

        File.WriteAllText(path, "BC-TEST\n");
        using (var stream = PromptFileAccess.OpenShared(path))
        {
            Assert.NotEqual(before, PromptFileAccess.StateOf(stream).FileId);
        }
    }

    /// <summary>The paste cache is read back by hash, and only hex hashes are looked up.</summary>
    [Fact]
    public void ReadPaste_ByHashOnly()
    {
        Directory.CreateDirectory(Path.Combine(ClaudeRoot, "paste-cache"));
        File.WriteAllText(Path.Combine(ClaudeRoot, "paste-cache", "00000000aaaaaaaa.txt"), "BC-TEST paste ✓");
        Assert.Equal("BC-TEST paste ✓", PromptFileAccess.ReadPaste(ClaudeRoot, "00000000aaaaaaaa"));
        Assert.Null(PromptFileAccess.ReadPaste(ClaudeRoot, "00000000bbbbbbbb"));
        Assert.Null(PromptFileAccess.ReadPaste(ClaudeRoot, @"..\history"));
    }

    /// <summary>Which files each source tracks, and which watcher paths are theirs.</summary>
    [Fact]
    public void Sources_FilesAndPaths()
    {
        var codex = new CodexPromptSource(CodexRoot);
        Assert.False(codex.HasData());
        var day = Directory.CreateDirectory(Path.Combine(CodexRoot, "sessions", "2026", "09", "30")).FullName;
        var archived = Directory.CreateDirectory(Path.Combine(CodexRoot, "archived_sessions")).FullName;
        File.WriteAllText(Path.Combine(day, "rollout-2026-09-30T07-00-00-a.jsonl"), string.Empty);
        File.WriteAllText(Path.Combine(day, "rollout-2026-09-30T07-00-00-a.jsonl.zst"), string.Empty);
        File.WriteAllText(Path.Combine(archived, "rollout-2026-08-01T07-00-00-b.jsonl.zst"), string.Empty);
        File.WriteAllText(Path.Combine(day, "notes.txt"), string.Empty);
        Assert.True(codex.HasData());

        var files = codex.EnumerateFiles().ToList();
        Assert.Equal(["rollout-2026-08-01T07-00-00-b.jsonl", "rollout-2026-09-30T07-00-00-a.jsonl"], files.Select(f => f.Key));
        Assert.False(files[1].Compressed);
        Assert.True(files[0].Compressed);
        Assert.NotNull(codex.Classify(Path.Combine(day, "rollout-2026-09-30T07-00-00-c.jsonl")));
        Assert.Null(codex.Classify(Path.Combine(day, "notes.txt")));
        Assert.Null(codex.Classify(Path.Combine(archived, "deeper", "rollout-x.jsonl")));

        var claude = new ClaudeCodePromptSource(ClaudeRoot);
        Assert.False(claude.HasData());
        File.WriteAllText(Path.Combine(ClaudeRoot, "history.jsonl"), string.Empty);
        Assert.Equal(PromptSourceKind.ClaudeHistory, Assert.Single(claude.EnumerateFiles()).Kind);
        Assert.NotNull(claude.Classify(Path.Combine(ClaudeRoot, "HISTORY.jsonl")));
        Assert.Null(claude.Classify(Path.Combine(ClaudeRoot, "projects", "history.jsonl")));
    }

    /// <summary>The prompt texts of an agent's archive, newest first.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The texts.</returns>
    private List<string> Texts(PromptAgent agent) =>
        [.. store.QueryPrompts(new PromptQuery { Agent = agent, Limit = 1000 }, includeText: true, CancellationToken.None).Select(p => p.Text!)];

    /// <summary>Waits until a condition holds (the reader works on its own thread).</summary>
    /// <param name="condition">The condition.</param>
    /// <returns>A task completing when it holds.</returns>
    /// <exception cref="TimeoutException">It did not hold within <see cref="Deadline"/>.</exception>
    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The prompt reader did not get there in time.");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Appends lines like an agent that opens, writes and closes the file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="lines">The lines.</param>
    private static void AppendLines(string path, params string[] lines)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Write(stream, lines);
    }

    /// <summary>Writes lines to an open stream and flushes them to the OS (no close).</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="lines">The lines.</param>
    private static void Write(FileStream stream, params string[] lines)
    {
        stream.Write(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))));
        stream.Flush();
    }

    /// <summary>A Claude Code history line.</summary>
    /// <param name="display">The display text.</param>
    /// <param name="timestamp">Unix ms.</param>
    /// <param name="session">The session.</param>
    /// <param name="pastes">The pastedContents JSON.</param>
    /// <returns>The line.</returns>
    private static string ClaudeLine(string display, long timestamp, string session, string pastes = "{}") =>
        $$"""{"display":"{{display}}","pastedContents":{{pastes}},"timestamp":{{timestamp}},"project":"K:\\BC-TEST","sessionId":"{{session}}"}""";

    /// <summary>A session file's name for a thread.</summary>
    /// <param name="id">The thread id.</param>
    /// <returns>The file name.</returns>
    private static string RolloutName(string id) => $"rollout-2026-09-30T07-00-00-{id}.jsonl";

    /// <summary>A session file's first line.</summary>
    /// <param name="id">The thread id.</param>
    /// <param name="source">The source JSON.</param>
    /// <param name="threadSource">The thread source, or null.</param>
    /// <param name="at">The start time.</param>
    /// <returns>The line.</returns>
    private static string Meta(string id, string source, string? threadSource, DateTimeOffset at)
    {
        var threadSourceJson = threadSource is null ? string.Empty : $",\"thread_source\":\"{threadSource}\"";
        return $$$"""{"timestamp":"{{{Iso(at)}}}","type":"session_meta","payload":{"id":"{{{id}}}","timestamp":"{{{Iso(at)}}}","cwd":"K:\\BC-TEST","originator":"Codex Desktop","source":{{{source}}}{{{threadSourceJson}}}}}""";
    }

    /// <summary>A completed UserMessage item line.</summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="text">The text.</param>
    /// <param name="at">The time.</param>
    /// <returns>The line.</returns>
    private static string UserMessage(string itemId, string text, DateTimeOffset at) =>
        $$$$"""{"timestamp":"{{{{Iso(at)}}}}","type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","id":"{{{{itemId}}}}","content":[{"type":"text","text":"{{{{text}}}}","text_elements":[]}]}}}""";

    /// <summary>An ISO 8601 UTC time like Codex writes.</summary>
    /// <param name="at">The time.</param>
    /// <returns>The text.</returns>
    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
