using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Prompts;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <c>bclip prompts</c> and <c>bclip prompt</c> (CLAUDE.md §2.21): the grammar, the commands against a real
/// store, and how they print — the prompt archive through the command line, for scripts and AI agents.
/// </summary>
public sealed class PromptCliTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = TestData.Now;
    private readonly TempDirectory temp = TestData.NewTempDirectory();
    private ClipHistoryService history = null!;
    private CliCommandProcessor processor = null!;

    /// <summary>Starts a service over a temp store and a processor over it.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        var store = new ClipStore(temp.DatabasePath);
        store.Initialize();
        history = new ClipHistoryService(store, null, () => new CaptureRules { Retention = RetentionPolicy.Unlimited });
        history.Start();
        processor = new CliCommandProcessor(history, new NoClipboard(), null, () => new AppSettings(), () => null, "9.9.9");
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the service and deletes the store.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        await history.DisposeAsync();
        temp.Dispose();
    }

    /// <summary><c>prompts</c> takes optional words, <c>-a</c>/<c>--agent</c>, <c>--all</c>, <c>--full</c>, paging and <c>--since</c>.</summary>
    [Fact]
    public void Arguments_Prompts()
    {
        var plain = Parse("prompts");
        Assert.Equal(CliCommands.Prompts, plain.Request!.Command);
        Assert.Null(plain.Request.Query);
        Assert.Null(plain.Request.Agent);

        var full = Parse("prompts", "fix", "the", "test", "-a", "Claude-Code", "--all", "--full", "-n", "5", "--offset", "10", "-s", "2h", "--json");
        Assert.Equal("fix the test", full.Request!.Query);
        Assert.Equal("claude", full.Request.Agent);
        Assert.True(full.Request.AllSends);
        Assert.True(full.Request.Full);
        Assert.Equal(5, full.Request.Limit);
        Assert.Equal(10, full.Request.Offset);
        Assert.Equal(Now.AddHours(-2), full.Request.Since);
        Assert.True(full.Json);
        Assert.Equal("codex", Parse("prompts", "--agent=codex").Request!.Agent);

        Assert.Contains("Unknown agent", Parse("prompts", "-a", "gemini").Error, StringComparison.Ordinal);
    }

    /// <summary><c>prompt</c> needs exactly one positive id.</summary>
    [Fact]
    public void Arguments_Prompt()
    {
        Assert.Equal(12, Parse("prompt", "12").Request!.Id);
        Assert.Equal("out.txt", Parse("prompt", "12", "-o", "out.txt").OutputFile);
        Assert.NotNull(Parse("prompt").Error);
        Assert.NotNull(Parse("prompt", "x").Error);
        Assert.NotNull(Parse("prompt", "0").Error);
        Assert.NotNull(Parse("prompt", "1", "2").Error);
    }

    /// <summary>Both commands have help topics ending with a line break, and the overview and list help name them.</summary>
    [Fact]
    public void Help_NamesThePromptCommands()
    {
        foreach (var command in new[] { CliCommands.Prompts, CliCommands.Prompt })
        {
            var help = CliArguments.Help(command);
            Assert.StartsWith($"bclip {command}", help, StringComparison.Ordinal);
            Assert.EndsWith("\n", help, StringComparison.Ordinal);
        }

        Assert.Contains("prompts [WORDS...]", CliArguments.Help(null), StringComparison.Ordinal);
        Assert.Contains("claude | codex", CliArguments.Help(CliCommands.List), StringComparison.Ordinal);
        Assert.Equal("claude", Parse("list", "-f", "claude").Request!.Filter);
        Assert.Equal("codex", Parse("list", "-f", "codex").Request!.Filter);
    }

    /// <summary>
    /// <c>prompts</c> lists the archive newest first, one row per text (or per send with <c>--all</c>), per agent or both,
    /// with words like <c>search</c> (exit 1 when nothing matches) and the whole text on request.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Prompts_ListsTheArchive()
    {
        await Ingest(PromptAgent.ClaudeCode,
            Prompt(PromptSourceKind.ClaudeHistory, "BC-TEST continue", Now.AddMinutes(-30), "a", @"K:\src\one"),
            Prompt(PromptSourceKind.ClaudeHistory, "BC-TEST fix the test\nsecond line", Now.AddMinutes(-20), "b", null),
            Prompt(PromptSourceKind.ClaudeHistory, "BC-TEST continue", Now.AddMinutes(-10), "c", @"K:\src\two"));
        await Ingest(PromptAgent.Codex, Prompt(PromptSourceKind.CodexHistory, "BC-TEST codex prompt", Now, "d", null));

        var all = await Run(new CliRequest { Command = CliCommands.Prompts });
        Assert.Equal(["BC-TEST codex prompt", "BC-TEST continue", "BC-TEST fix the test\nsecond line"], all.Prompts!.Select(p => p.Preview));
        Assert.Equal(["codex", "claude", "claude"], all.Prompts!.Select(p => p.Agent));
        var continued = all.Prompts![1];
        Assert.Equal(2, continued.Sends);
        Assert.Equal(@"K:\src\two", continued.Project);
        Assert.Equal(Now.AddMinutes(-30), continued.FirstSent);
        Assert.Null(continued.Text);

        Assert.Equal(3, (await Run(new CliRequest { Command = CliCommands.Prompts, Agent = "claude", AllSends = true })).Prompts!.Count);
        Assert.Single((await Run(new CliRequest { Command = CliCommands.Prompts, Agent = "codex" })).Prompts!);
        Assert.Equal("BC-TEST fix the test\nsecond line", (await Run(new CliRequest { Command = CliCommands.Prompts, Query = "second", Full = true })).Prompts!.Single().Text);
        Assert.Equal(2, (await Run(new CliRequest { Command = CliCommands.Prompts, Since = Now.AddMinutes(-15) })).Prompts!.Count);

        var none = await Run(new CliRequest { Command = CliCommands.Prompts, Query = "nothing-like-it" });
        Assert.Equal(CliErrorCodes.NotFound, none.ErrorCode);
        Assert.Equal(CliExitCodes.NothingFound, CliExitCodes.For(none));
        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = CliCommands.Prompts, Agent = "gemini" })).ErrorCode);
    }

    /// <summary><c>prompt ID</c> serves the whole text exactly (as content, printed like <c>get</c>) and the prompt for <c>--json</c>.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Prompt_PrintsOneExactly()
    {
        await Ingest(PromptAgent.ClaudeCode, Prompt(PromptSourceKind.ClaudeHistory, "BC-TEST line 1\nline 2 ✓", Now, "a", null));
        var id = (await Run(new CliRequest { Command = CliCommands.Prompts })).Prompts!.Single().Id;
        var response = await Run(new CliRequest { Command = CliCommands.Prompt, Id = id });
        Assert.True(response.Ok);
        Assert.Equal("BC-TEST line 1\nline 2 ✓", response.Content!.Text);
        Assert.Equal("text/plain", response.Content.MediaType);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("BC-TEST line 1\nline 2 ✓"), response.Content.Bytes);
        Assert.Equal("BC-TEST line 1\nline 2 ✓", response.Prompts!.Single().Text);
        Assert.Equal("BC-TEST line 1\nline 2 ✓", CliOutput.Render(CliCommands.Prompt, response, Now));

        Assert.Equal(CliErrorCodes.NotFound, (await Run(new CliRequest { Command = CliCommands.Prompt, Id = id + 100 })).ErrorCode);
        Assert.Equal(CliErrorCodes.BadRequest, (await Run(new CliRequest { Command = CliCommands.Prompt })).ErrorCode);
    }

    /// <summary><c>status</c> counts the archive, and prints it only when there is something to count.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Status_CountsTheArchive()
    {
        var empty = await Run(new CliRequest { Command = CliCommands.Status });
        Assert.Equal(0, empty.Status!.Prompts);
        Assert.DoesNotContain("Prompts:", CliOutput.RenderStatus(empty.Status), StringComparison.Ordinal);

        await Ingest(PromptAgent.Codex, Prompt(PromptSourceKind.CodexHistory, "BC-TEST a", Now, "a", null), Prompt(PromptSourceKind.CodexHistory, "BC-TEST b", Now, "b", null));
        var status = (await Run(new CliRequest { Command = CliCommands.Status })).Status!;
        Assert.Equal(2, status.Prompts);
        Assert.Contains("Prompts:  2 sent to Claude Code and Codex", CliOutput.RenderStatus(status), StringComparison.Ordinal);
    }

    /// <summary><c>list -f claude</c> lists prompts kept from the Claude tab (origin "claude"); the archive itself is not history.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task List_FiltersKeptPrompts()
    {
        await history.AddAsync(TestData.Text("BC-TEST kept", origin: ClipOrigin.ClaudeCode, source: PromptAgents.SourceOf(PromptAgent.ClaudeCode)));
        await history.AddAsync(TestData.Text("BC-TEST copied"));
        await Ingest(PromptAgent.ClaudeCode, Prompt(PromptSourceKind.ClaudeHistory, "BC-TEST archived", Now, "a", null));
        var kept = (await Run(new CliRequest { Command = CliCommands.List, Filter = "claude" })).Items!.Single();
        Assert.Equal("BC-TEST kept", kept.Preview);
        Assert.Equal("claude", kept.Origin);
        Assert.Equal("Claude Code", kept.Source);
        Assert.Empty((await Run(new CliRequest { Command = CliCommands.List, Filter = "codex" })).Items!);
    }

    /// <summary>The table: id, agent, age, project folder (either separator), ×count, first line; the full text indented below.</summary>
    [Fact]
    public void RenderPrompts_Columns()
    {
        var text = CliOutput.RenderPrompts(
        [
            new CliPrompt { Id = 7, Agent = "claude", Preview = "BC-TEST continue\nmore", LastSent = Now.AddMinutes(-5), Sends = 3, Project = @"K:\src\BetterClipboard\" },
            new CliPrompt { Id = 12, Agent = "codex", Preview = "BC-TEST once", LastSent = Now, Sends = 1, Project = "/home/me/repo", Text = "BC-TEST once\nfull" },
        ], Now);
        var lines = text.Split('\n');
        Assert.Equal("      7  claude 5 min ago      BetterClipboard    ×3    BC-TEST continue ↵", lines[0]);
        Assert.StartsWith("     12  codex  just now       repo                     BC-TEST once", lines[1], StringComparison.Ordinal);
        Assert.Equal("         │ BC-TEST once", lines[2]);
        Assert.Equal("         │ full", lines[3]);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    /// <summary>Stores prompts of one agent as one read.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="prompts">The prompts.</param>
    /// <returns>A task.</returns>
    private Task Ingest(PromptAgent agent, params AgentPrompt[] prompts) => history.IngestPromptsAsync(new PromptBatch
    {
        Agent = agent,
        Kind = prompts[0].Source,
        FileKey = Guid.NewGuid().ToString("N"),
        Path = "BC-TEST",
        Checkpoint = new TailCheckpoint { Offset = 1, Length = 1 },
        Prompts = prompts,
    });

    /// <summary>A prompt.</summary>
    /// <param name="kind">Its source kind.</param>
    /// <param name="text">Text.</param>
    /// <param name="at">Send time.</param>
    /// <param name="key">Key.</param>
    /// <param name="project">Project.</param>
    /// <returns>The prompt.</returns>
    private static AgentPrompt Prompt(PromptSourceKind kind, string text, DateTimeOffset at, string key, string? project) =>
        new(kind, key, "s", project, at, text);

    /// <summary>Executes a request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    private Task<CliResponse> Run(CliRequest request) => processor.ExecuteAsync(request, CancellationToken.None);

    /// <summary>Parses a command line at <see cref="Now"/>.</summary>
    /// <param name="args">Arguments.</param>
    /// <returns>The invocation.</returns>
    private static CliInvocation Parse(params string[] args) => CliArguments.Parse(args, Now);

    /// <summary>The prompt commands never write the clipboard.</summary>
    private sealed class NoClipboard : IClipboardWriter
    {
        /// <inheritdoc />
        public Task WriteAsync(IReadOnlyList<ClipFormatData> formats) => throw new InvalidOperationException("The prompt commands never write the clipboard.");
    }
}
