using BetterClipboard.Core.Prompts;

namespace BetterClipboard.Windows.Integrations;

/// <summary>One agent file the prompt archive tracks.</summary>
/// <param name="Path">Where it is now.</param>
/// <param name="Kind">What kind of file it is (how it is parsed).</param>
/// <param name="Key">Its tracking key (<see cref="PromptBatch.FileKey"/>): stable across moves, compression and decompression.</param>
/// <param name="Compressed">Whether it is a zstd-compressed Codex session file (<c>.jsonl.zst</c>).</param>
public sealed record AgentFile(string Path, PromptSourceKind Kind, string Key, bool Compressed);

/// <summary>A folder to watch for changes of an agent's files.</summary>
/// <param name="Folder">The folder (watched only while it exists).</param>
/// <param name="Recursive">Whether to watch its subfolders too.</param>
/// <param name="Filter">The file name filter (<see cref="FileSystemWatcher.Filter"/>).</param>
public sealed record AgentWatch(string Folder, bool Recursive, string Filter);

/// <summary>
/// Where one agent keeps its prompts and how to read them: the folder, the files to track, what to watch, and the line
/// parser. The pure parsing is in Core (<see cref="ClaudeCodeHistory"/>, <see cref="CodexSessions"/>).
/// </summary>
public abstract class AgentPromptSource
{
    /// <summary>
    /// Creates the source over an agent's folder.
    /// </summary>
    /// <param name="root">The agent's folder (it may not exist yet).</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or blank.</exception>
    protected AgentPromptSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The agent.</summary>
    public abstract PromptAgent Agent { get; }

    /// <summary>The agent's folder (<c>~/.claude</c>, <c>~/.codex</c> or their overrides).</summary>
    public string Root { get; }

    /// <summary>The folders to watch (only the existing ones are watched; a reconcile re-checks).</summary>
    public abstract IReadOnlyList<AgentWatch> Watches { get; }

    /// <summary>Whether the folder holds anything to read — decides, with the archive, whether the panel shows the tab.</summary>
    /// <returns><see langword="true"/> when at least one tracked file exists.</returns>
    public virtual bool HasData()
    {
        try
        {
            return EnumerateFiles().Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Every file of the agent to track, oldest session files first (so a first import stores prompts in time order).</summary>
    /// <returns>The files that exist now.</returns>
    /// <exception cref="IOException">A folder could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to a folder was denied.</exception>
    public abstract IEnumerable<AgentFile> EnumerateFiles();

    /// <summary>The tracked file a watcher event's path is, or <see langword="null"/> when it is none of them.</summary>
    /// <param name="path">The path from the event.</param>
    /// <returns>The file, or <see langword="null"/>.</returns>
    public abstract AgentFile? Classify(string path);

    /// <summary>Parses one line of a file into a prompt, if it is one.</summary>
    /// <param name="file">The file.</param>
    /// <param name="line">The line (without its line break).</param>
    /// <param name="thread">The Codex thread of a session file, else <see langword="null"/>.</param>
    /// <returns>The prompt, or <see langword="null"/>.</returns>
    public abstract AgentPrompt? ParseLine(AgentFile file, ReadOnlySpan<byte> line, CodexThread? thread);

    /// <summary>
    /// Resolves an agent folder: a BetterClipboard override (dev and test runs, so the user's real files are never read),
    /// else the agent's own variable, else the default under the user profile.
    /// </summary>
    /// <param name="overrideVariable">BetterClipboard's variable.</param>
    /// <param name="agentVariable">The agent's own variable (<c>CLAUDE_CONFIG_DIR</c>, <c>CODEX_HOME</c>).</param>
    /// <param name="defaultFolderName">The folder under the user profile (<c>.claude</c>, <c>.codex</c>).</param>
    /// <returns>The folder.</returns>
    protected static string ResolveRoot(string overrideVariable, string agentVariable, string defaultFolderName)
    {
        foreach (var variable in new[] { overrideVariable, agentVariable })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return Environment.ExpandEnvironmentVariables(value.Trim());
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), defaultFolderName);
    }

    /// <summary>Whether a path is directly inside a folder (not deeper).</summary>
    /// <param name="path">The path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/>'s parent is <paramref name="folder"/>.</returns>
    protected static bool IsDirectlyIn(string path, string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path is inside a folder, at any depth.</summary>
    /// <param name="path">The path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is below <paramref name="folder"/>.</returns>
    protected static bool IsBelow(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Claude Code's prompts: <c>history.jsonl</c> in its config folder, pastes from <c>paste-cache</c> beside it.
/// </summary>
/// <remarks>
/// The transcripts (<c>projects/**/*.jsonl</c>, 8 GB here) are not read: 99.4% of the prompts they mark as typed or queued
/// are in the history too (6,563 of 6,601 here), and the rest of their user messages are generated (interrupt notices,
/// teammate and task messages) or come from scripts (<c>claude -p</c>). See CLAUDE.md §2.21.
/// </remarks>
public sealed class ClaudeCodePromptSource : AgentPromptSource
{
    /// <summary>Environment variable that replaces Claude Code's folder for BetterClipboard only (dev and test runs).</summary>
    public const string FolderVariable = "BETTERCLIPBOARD_CLAUDE_DIR";

    /// <summary>Claude Code's own variable for its config folder.</summary>
    public const string AgentVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>Creates the source.</summary>
    /// <param name="root">The folder; <see langword="null"/> = <see cref="FolderVariable"/>, <c>CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>.</param>
    public ClaudeCodePromptSource(string? root = null)
        : base(root ?? ResolveRoot(FolderVariable, AgentVariable, ".claude"))
    {
        HistoryPath = Path.Combine(Root, ClaudeCodeHistory.FileName);
    }

    /// <inheritdoc/>
    public override PromptAgent Agent => PromptAgent.ClaudeCode;

    /// <summary>The prompt history's path.</summary>
    public string HistoryPath { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<AgentWatch> Watches => [new AgentWatch(Root, Recursive: false, ClaudeCodeHistory.FileName)];

    /// <inheritdoc/>
    public override IEnumerable<AgentFile> EnumerateFiles()
    {
        if (File.Exists(HistoryPath))
        {
            yield return new AgentFile(HistoryPath, PromptSourceKind.ClaudeHistory, ClaudeCodeHistory.FileName, Compressed: false);
        }
    }

    /// <inheritdoc/>
    public override AgentFile? Classify(string path) =>
        string.Equals(Path.GetFullPath(path), HistoryPath, StringComparison.OrdinalIgnoreCase)
            ? new AgentFile(HistoryPath, PromptSourceKind.ClaudeHistory, ClaudeCodeHistory.FileName, Compressed: false)
            : null;

    /// <inheritdoc/>
    public override AgentPrompt? ParseLine(AgentFile file, ReadOnlySpan<byte> line, CodexThread? thread) =>
        ClaudeCodeHistory.Parse(line, hash => PromptFileAccess.ReadPaste(Root, hash));
}

/// <summary>
/// Codex's prompts: its session files (<c>sessions/**/rollout-*.jsonl</c>, <c>archived_sessions/</c>, compressed or not) and
/// its CLI history (<c>history.jsonl</c>).
/// </summary>
public sealed class CodexPromptSource : AgentPromptSource
{
    /// <summary>Environment variable that replaces Codex's folder for BetterClipboard only (dev and test runs).</summary>
    public const string FolderVariable = "BETTERCLIPBOARD_CODEX_DIR";

    /// <summary>Codex's own variable for its home folder.</summary>
    public const string AgentVariable = "CODEX_HOME";

    /// <summary>Creates the source.</summary>
    /// <param name="root">The folder; <see langword="null"/> = <see cref="FolderVariable"/>, <c>CODEX_HOME</c> or <c>~/.codex</c>.</param>
    public CodexPromptSource(string? root = null)
        : base(root ?? ResolveRoot(FolderVariable, AgentVariable, ".codex"))
    {
        HistoryPath = Path.Combine(Root, CodexSessions.HistoryFileName);
        SessionsPath = Path.Combine(Root, CodexSessions.SessionsFolder);
        ArchivedPath = Path.Combine(Root, CodexSessions.ArchivedSessionsFolder);
    }

    /// <inheritdoc/>
    public override PromptAgent Agent => PromptAgent.Codex;

    /// <summary>The CLI history's path.</summary>
    public string HistoryPath { get; }

    /// <summary>The live session files' folder.</summary>
    public string SessionsPath { get; }

    /// <summary>The archived session files' folder.</summary>
    public string ArchivedPath { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<AgentWatch> Watches =>
    [
        new AgentWatch(Root, Recursive: false, CodexSessions.HistoryFileName),
        new AgentWatch(SessionsPath, Recursive: true, "rollout-*"),
        new AgentWatch(ArchivedPath, Recursive: false, "rollout-*"),
    ];

    /// <inheritdoc/>
    public override IEnumerable<AgentFile> EnumerateFiles()
    {
        // Oldest first (the name starts with the thread's start time), so a first import archives prompts in time order.
        var sessions = new Dictionary<string, AgentFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, recursive) in new[] { (SessionsPath, true), (ArchivedPath, false) })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "rollout-*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                if (ToSessionFile(path) is not { } file)
                {
                    continue;
                }

                // During compression or decompression both forms exist for a moment: the plain file is the live one.
                if (!sessions.TryGetValue(file.Key, out var known) || (known.Compressed && !file.Compressed))
                {
                    sessions[file.Key] = file;
                }
            }
        }

        foreach (var file in sessions.Values.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
        }

        // Last: its prompts merge into their session file records, which then already exist (either order works).
        if (File.Exists(HistoryPath))
        {
            yield return new AgentFile(HistoryPath, PromptSourceKind.CodexHistory, CodexSessions.HistoryFileName, Compressed: false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Stops at the first file found: listing every session file (hundreds) is a reconcile's job, not this check's.</remarks>
    public override bool HasData()
    {
        try
        {
            return File.Exists(HistoryPath)
                || (Directory.Exists(SessionsPath) && Directory.EnumerateFiles(SessionsPath, "rollout-*", SearchOption.AllDirectories).Any(p => CodexSessions.IsSessionFileName(Path.GetFileName(p))))
                || (Directory.Exists(ArchivedPath) && Directory.EnumerateFiles(ArchivedPath, "rollout-*").Any(p => CodexSessions.IsSessionFileName(Path.GetFileName(p))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override AgentFile? Classify(string path)
    {
        var full = Path.GetFullPath(path);
        if (string.Equals(full, HistoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return new AgentFile(HistoryPath, PromptSourceKind.CodexHistory, CodexSessions.HistoryFileName, Compressed: false);
        }

        return IsBelow(full, SessionsPath) || IsDirectlyIn(full, ArchivedPath) ? ToSessionFile(full) : null;
    }

    /// <inheritdoc/>
    public override AgentPrompt? ParseLine(AgentFile file, ReadOnlySpan<byte> line, CodexThread? thread) => file.Kind == PromptSourceKind.CodexHistory
        ? CodexSessions.ParseHistoryLine(line)
        : thread is null ? null : CodexSessions.ParseSessionLine(line, thread);

    /// <summary>A session file at a path, or <see langword="null"/> when the name is not one.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The file.</returns>
    private static AgentFile? ToSessionFile(string path)
    {
        var name = Path.GetFileName(path);
        return CodexSessions.IsSessionFileName(name)
            ? new AgentFile(path, PromptSourceKind.CodexRollout, CodexSessions.CanonicalName(name),
                name.EndsWith(CodexSessions.CompressedSuffix, StringComparison.OrdinalIgnoreCase))
            : null;
    }
}
