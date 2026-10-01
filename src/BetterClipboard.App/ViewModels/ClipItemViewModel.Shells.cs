using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Shells;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// The shell tabs' cards (Pwsh, Cmd): a live command that is not a history entry, shown through a stand-in entry
/// like an Everything pick (see <see cref="ForCommand"/>).
/// </summary>
public sealed partial class ClipItemViewModel
{
    /// <summary>
    /// The shell command this card shows, or <see langword="null"/> for a history entry or a pick. Command cards paste
    /// the command, keep it on Pin, and hide it on Delete — nothing of theirs is stored until then.
    /// </summary>
    public ShellCommand? Command { get; private set; }

    /// <summary>
    /// Whether the card is a shell command: a live one (<see cref="Command"/>), or a history entry kept from a shell tab
    /// (origin <see cref="ClipOrigin.PowerShell"/> or <see cref="ClipOrigin.Cmd"/>). Drives the command-prompt glyph and
    /// the monospace body.
    /// </summary>
    private bool IsShellCommandCard => Command is not null || Entry.Origin is ClipOrigin.PowerShell or ClipOrigin.Cmd;

    /// <summary>"PowerShell command" or "Command Prompt command" for a shell command card; <see langword="null"/> otherwise.</summary>
    private string? ShellCommandLabel => (Command?.Shell, Entry.Origin) switch
    {
        (ShellKind.PowerShell, _) or (null, ClipOrigin.PowerShell) => "PowerShell command",
        (ShellKind.Cmd, _) or (null, ClipOrigin.Cmd) => "Command Prompt command",
        _ => null,
    };

    /// <summary>
    /// A card for a shell tab's live command. It is not a history entry, so its <see cref="Entry"/> is a stand-in: a
    /// negative <paramref name="syntheticId"/> that no stored entry can have (selection, drag and in-place updates key
    /// on ids), kind Text, the command's preview, its content hash (so a stored copy and the command are recognized
    /// as one), and its last time seen (PowerShell's file has none: the oldest possible time, never shown).
    /// Actions must check <see cref="Command"/> before treating the card as stored.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="syntheticId">A negative id, unique within the list.</param>
    /// <returns>The card model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="syntheticId"/> is not negative.</exception>
    public static ClipItemViewModel ForCommand(ShellCommand command, long syntheticId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(syntheticId, 0);

        // The classifier builds the same preview a stored copy of the command would get (first lines, trimmed).
        var classified = ContentClassifier.Classify(new ClipCapture
        {
            Formats = [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(command.ClipboardText))],
        });
        var seen = command.LastSeen ?? DateTimeOffset.MinValue;
        var entry = new ClipEntry
        {
            Id = syntheticId,
            Kind = ClipKind.Text,
            Preview = classified?.Preview ?? command.Text,
            ContentHash = command.ContentHash,
            CreatedUtc = seen,
            LastUsedUtc = seen,
            UseCount = command.Count,
            Origin = ShellSources.OriginOf(command.Shell),
            SourceAppName = command.Source,
            SizeBytes = command.ClipboardText.Length * 2L,
            FormatNames = [ClipFormatNames.UnicodeText],
        };
        var item = new ClipItemViewModel(entry, _ => Task.FromResult<byte[]?>(null)) { Command = command };

        // The constructor built the caption before the command was set.
        item.RefreshCaption();
        return item;
    }

    /// <summary>
    /// "PowerShell · typed 3 times", "Command Prompt · 5 min ago · typed once": where it was typed, when (only cmd
    /// commands have a time — PowerShell's file keeps none, and a made-up one would mislead), and how often.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <returns>The caption.</returns>
    private static string CommandCaption(ShellCommand command)
    {
        var times = command.Count == 1 ? "typed once" : $"typed {command.Count:N0} times";
        return command.LastSeen is { } seen
            ? $"{command.Source} · {RelativeTimeFormatter.Format(seen, DateTimeOffset.UtcNow)} · {times}"
            : $"{command.Source} · {times}";
    }
}
