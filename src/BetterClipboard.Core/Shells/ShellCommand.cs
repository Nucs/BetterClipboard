using BetterClipboard.Core.Content;

namespace BetterClipboard.Core.Shells;

/// <summary>
/// The shell a command was typed into — which of the panel's shell tabs (Pwsh, Cmd) lists it.
/// </summary>
public enum ShellKind
{
    /// <summary>
    /// PowerShell (Windows PowerShell 5.1 or PowerShell 7). Its line editor, PSReadLine, appends every accepted line
    /// to a history file that is never trimmed, so the file itself is the source of truth (CLAUDE.md §2.16).
    /// </summary>
    PowerShell = 1,

    /// <summary>
    /// The Command Prompt (<c>cmd.exe</c>). It keeps no history file: the console host holds the last commands of
    /// each window in memory only, gone when the window closes — so BetterClipboard keeps what it reads.
    /// </summary>
    Cmd = 2,
}

/// <summary>
/// One command as a shell remembers it, shown live in the panel's Pwsh or Cmd tab. Not a history entry: it is
/// stored only when the user pastes, copies or keeps it (see <see cref="ShellTab"/>).
/// </summary>
/// <remarks>
/// Immutable; safe to hand between threads. <see cref="ContentHash"/> and <see cref="Fingerprint"/> are computed from
/// <see cref="ClipboardText"/>, exactly what storing the command would compute, so a stored copy and its live command
/// can be matched (shown once) and a forgotten one recognized.
/// </remarks>
public sealed record ShellCommand
{
    /// <summary>
    /// Creates a command.
    /// </summary>
    /// <param name="shell">The shell it was typed into.</param>
    /// <param name="text">The command as typed; inner line breaks of a multi-line command as <c>\n</c>.</param>
    /// <param name="count">How often it was typed, as far as the source can tell (at least 1).</param>
    /// <param name="lastSeen">When it was last seen typed, or <see langword="null"/> when the source keeps no times (PowerShell's file).</param>
    /// <param name="source">Where it came from, for the card's caption (e.g. "PowerShell", "PowerShell in VS Code", "Command Prompt").</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or blank, or <paramref name="source"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1.</exception>
    public ShellCommand(ShellKind shell, string text, int count, DateTimeOffset? lastSeen, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Shell = shell;
        Text = text;
        Count = count;
        LastSeen = lastSeen;
        Source = source;
        ClipboardText = ToClipboardText(text);
        ContentHash = ContentHasher.ForText(ClipboardText);

        // Never null here: the text is not blank, and only blank text has no text fingerprint.
        Fingerprint = ForgetFingerprint.ForText(ClipboardText) ?? ContentHash;
    }

    /// <summary>The shell it was typed into.</summary>
    public ShellKind Shell { get; }

    /// <summary>The command as typed; a multi-line command's inner breaks are <c>\n</c>.</summary>
    public string Text { get; }

    /// <summary>
    /// How often it was typed: the number of records in PowerShell's file, or how many times BetterClipboard saw it
    /// added to a cmd window's history. Hiding a command remembers this number, and a larger one shows it again.
    /// </summary>
    public int Count { get; }

    /// <summary>When it was last seen typed; <see langword="null"/> for PowerShell, whose history file has no times.</summary>
    public DateTimeOffset? LastSeen { get; }

    /// <summary>Where it came from, for the card's caption.</summary>
    public string Source { get; }

    /// <summary>
    /// The text as it goes onto the clipboard and into the history: line breaks as CRLF, the Windows convention every
    /// app pastes correctly (PowerShell and cmd accept both).
    /// </summary>
    public string ClipboardText { get; }

    /// <summary>The dedupe key a stored copy of this command has (<see cref="ContentHasher.ForText"/> of <see cref="ClipboardText"/>).</summary>
    public string ContentHash { get; }

    /// <summary>The "Forget forever" fingerprint of <see cref="ClipboardText"/> (<see cref="ForgetFingerprint.ForText"/>).</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Normalizes line breaks to CRLF: a lone <c>\n</c> or <c>\r</c> becomes <c>\r\n</c>, an existing <c>\r\n</c> stays.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with CRLF line breaks (the same instance when it has none).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string ToClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            return text;
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
    }
}
