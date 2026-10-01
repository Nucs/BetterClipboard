using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Prompts;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The prompt tabs' cards (Claude, Codex): an archived prompt that is not a history entry, shown through a stand-in entry
/// like a shell command (see <see cref="ForPrompt"/>).
/// </summary>
public sealed partial class ClipItemViewModel
{
    /// <summary>The glyph of prompt cards: Segoe Fluent Icons "Message", a speech bubble with text (E8BD, checked by rendering).</summary>
    public const string PromptGlyph = "\uE8BD";

    /// <summary>
    /// The archived prompt this card shows, or <see langword="null"/> for a history entry, a pick or a command. Prompt cards
    /// paste the prompt, keep it on Pin, and delete it from the archive on Delete — nothing of theirs is a history entry
    /// until then.
    /// </summary>
    public PromptSummary? Prompt { get; private set; }

    /// <summary>
    /// Whether the card is a prompt: an archived one (<see cref="Prompt"/>), or a history entry kept from a prompt tab
    /// (origin <see cref="ClipOrigin.ClaudeCode"/> or <see cref="ClipOrigin.Codex"/>). Drives the message glyph.
    /// </summary>
    private bool IsPromptCard => Prompt is not null || Entry.Origin is ClipOrigin.ClaudeCode or ClipOrigin.Codex;

    /// <summary>"Claude Code prompt", "Codex slash command", … for a prompt card; <see langword="null"/> otherwise.</summary>
    private string? PromptLabel => (Prompt?.Agent, Entry.Origin) switch
    {
        (PromptAgent agent, _) => $"{PromptAgents.NameOf(agent)} {(Prompt!.IsCommand ? "slash command" : "prompt")}",
        (null, ClipOrigin.ClaudeCode) => $"{PromptAgents.ClaudeCodeName} prompt",
        (null, ClipOrigin.Codex) => $"{PromptAgents.CodexName} prompt",
        _ => null,
    };

    /// <summary>
    /// A card for an archived prompt. It is not a history entry, so its <see cref="Entry"/> is a stand-in: a negative
    /// <paramref name="syntheticId"/> no stored entry can have (selection, drag and in-place updates key on ids), kind Text,
    /// the archive's preview, its text hash as content hash (a kept copy and the prompt are recognized as one), its last
    /// send as last use and its send count as use count. Actions must check <see cref="Prompt"/> before treating the card
    /// as stored.
    /// </summary>
    /// <param name="prompt">The archive row.</param>
    /// <param name="syntheticId">A negative id, unique within the list.</param>
    /// <returns>The card model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="prompt"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="syntheticId"/> is not negative.</exception>
    public static ClipItemViewModel ForPrompt(PromptSummary prompt, long syntheticId)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(syntheticId, 0);
        var entry = new ClipEntry
        {
            Id = syntheticId,
            Kind = ClipKind.Text,
            Preview = prompt.Preview,
            ContentHash = prompt.TextHash,
            CreatedUtc = prompt.FirstSentUtc,
            LastUsedUtc = prompt.LastSentUtc,
            UseCount = prompt.Sends,
            Origin = PromptAgents.OriginOf(prompt.Agent),
            SourceAppName = PromptAgents.NameOf(prompt.Agent),
            SizeBytes = prompt.TextLength * 2L,
            FormatNames = [ClipFormatNames.UnicodeText],
        };
        var item = new ClipItemViewModel(entry, _ => Task.FromResult<byte[]?>(null)) { Prompt = prompt };

        // The constructor built the caption before the prompt was set.
        item.RefreshCaption();
        return item;
    }

    /// <summary>
    /// "Claude Code · BetterClipboard · 5 min ago · sent 3 times · 1 image": the agent, the project folder of the last send,
    /// when, how often, and the images it carried (archived as a count only).
    /// </summary>
    /// <param name="prompt">The archive row.</param>
    /// <returns>The caption.</returns>
    private static string PromptCaption(PromptSummary prompt)
    {
        var parts = new List<string>(5) { PromptAgents.NameOf(prompt.Agent) };
        if (ProjectName(prompt.Project) is { } project)
        {
            parts.Add(project);
        }

        parts.Add(RelativeTimeFormatter.Format(prompt.LastSentUtc, DateTimeOffset.UtcNow));
        if (prompt.Sends > 1)
        {
            parts.Add($"sent {prompt.Sends:N0} times");
        }

        if (prompt.ImageCount > 0)
        {
            parts.Add(prompt.ImageCount == 1 ? "1 image" : $"{prompt.ImageCount:N0} images");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The last folder of a project path ("BetterClipboard" for <c>K:\source\BetterClipboard</c>), or <see langword="null"/>.</summary>
    /// <param name="project">The project path, or <see langword="null"/>.</param>
    /// <returns>The folder name.</returns>
    private static string? ProjectName(string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return null;
        }

        // Codex on Windows may record a WSL or POSIX-style cwd: split on both separators.
        var trimmed = project.TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length > 0 ? name : trimmed;
    }
}
