using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Shells;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Presentation;

/// <summary>
/// The words of Settings' <em>Delete stored…</em> dialog (CLAUDE.md §2.43): each part's label, each source's name and
/// note, in the interface language. Pure apart from the catalog lookups; the tests check that every part of every source
/// has its own label.
/// </summary>
/// <remarks>
/// The notes say what is never touched (the owner's rule, 2026-11-09: "we leave the source alone, always") and whether
/// anything comes back after the deletion, because that is what decides whether to turn the switch off too.
/// </remarks>
public static class StoredText
{
    /// <summary>
    /// The label of a part's checkbox ("Screenshots ShareX saved"), or the id itself for an id no source has (a bug the
    /// tests catch).
    /// </summary>
    /// <param name="partId">The part's <see cref="StoredPart.Id"/>.</param>
    /// <returns>The label in the interface language.</returns>
    public static string LabelOf(string partId) => partId switch
    {
        "sharex.saved" => UiText.Translate("Screenshots ShareX saved"),
        "sharex.copied" => UiText.Translate("Copied by ShareX"),
        "snipping.saved" => UiText.Translate("Screenshots Windows saved"),
        "snipping.copied" => UiText.Translate("Copied by Snipping Tool or Win+PrtScn"),
        "npp.tabs" => UiText.Translate("Versions of unsaved tabs"),
        "npp.files" => UiText.Translate("Paths of files opened in Notepad++"),
        "npp.copied" => UiText.Translate("Copied in Notepad++"),
        "everything.kept" => UiText.Translate("Picks you kept from the tab"),
        "everything.copied" => UiText.Translate("Copied in Everything"),
        "everything.hidden" => UiText.Translate("Picks you hid (they show in the tab again)"),
        "run.recorded" => UiText.Translate("Commands run with Win+R"),
        "run.copied" => UiText.Translate("Copied first, then run with Win+R"),
        "pwsh.kept" or "cmd.kept" => UiText.Translate("Commands you kept from the tab"),
        "pwsh.hidden" or "cmd.hidden" => UiText.Translate("Commands you hid (they show in the tab again)"),
        "cmd.list" => UiText.Translate("Commands kept from Command Prompt windows"),
        "claude.archive" or "codex.archive" => UiText.Translate("Archived prompts"),
        "claude.kept" or "codex.kept" => UiText.Translate("Prompts you kept from the tab"),
        "claude.copied" => UiText.Translate("Copied in Claude Code"),
        StoredData.ProtectedPartId => UiText.Translate("Pinned or in a group"),
        _ => partId,
    };

    /// <summary>
    /// The name the dialog's title gives a source ("Delete what BetterClipboard stored from ShareX?"): the app's name, kept
    /// in English like every product name, or the Snipping card's own title and Command Prompt's translated name.
    /// </summary>
    /// <param name="source">The integration.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined value.</exception>
    public static string SourceNameOf(StoredSource source) => source switch
    {
        StoredSource.ShareX => "ShareX",
        StoredSource.Snipping => UiText.Translate("Snipping Tool and Win+PrtScn"),
        StoredSource.NotepadPlusPlusTabs or StoredSource.NotepadPlusPlusFiles => "Notepad++",
        StoredSource.Everything => "Everything",
        StoredSource.RunHistory => "Win+R",
        StoredSource.PowerShell => "PowerShell",
        StoredSource.Cmd => SourceNames.Display(ShellSources.CmdName),
        StoredSource.ClaudeCode => "Claude Code",
        StoredSource.Codex => "Codex",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a stored source."),
    };

    /// <summary>The title of a source's Settings card, for "Also turn off “…”": the switch the user sees.</summary>
    /// <param name="source">The integration.</param>
    /// <returns>The card's title in the interface language.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined value.</exception>
    public static string SwitchTitleOf(StoredSource source) => source switch
    {
        StoredSource.ShareX => UiText.Translate("ShareX screenshots"),
        StoredSource.Snipping => UiText.Translate("Snipping Tool and Win+PrtScn"),
        StoredSource.NotepadPlusPlusTabs => UiText.Translate("Notepad++ unsaved tabs"),
        StoredSource.NotepadPlusPlusFiles => UiText.Translate("Files opened in Notepad++"),
        StoredSource.Everything => "Everything",
        StoredSource.RunHistory => UiText.Translate("Win+R history"),
        StoredSource.PowerShell => UiText.Translate("Pwsh tab (PowerShell history)"),
        StoredSource.Cmd => UiText.Translate("Cmd tab (Command Prompt history)"),
        StoredSource.ClaudeCode => UiText.Translate("Claude Code prompts"),
        StoredSource.Codex => UiText.Translate("Codex prompts"),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a stored source."),
    };

    /// <summary>
    /// Glues the keys of a shortcut written in a text ("Win+PrtScn", "Ctrl+Shift+V") with word joiners (U+2060) around
    /// each "+" that stands between two letters or digits, so a wrapping line never breaks inside one: DirectWrite broke
    /// the dialog's title as "…Win" / "+PrtScn?" (the test desktop, 2026-11-09). Joiners are invisible and screen readers
    /// skip them; a "+" anywhere else ("Notepad++", "C++17", "60+") is left alone.
    /// </summary>
    /// <param name="text">A text to show.</param>
    /// <returns>The text with its shortcuts glued; the same instance when it holds no such "+".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string KeepShortcutsTogether(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('+'))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '+' && i > 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]))
            {
                builder.Append('\u2060').Append('+').Append('\u2060');
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The dialog's note under the parts: what of the source is never touched, and what is recorded again afterwards while
    /// the switch stays on.
    /// </summary>
    /// <param name="source">The integration.</param>
    /// <returns>The note in the interface language.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined value.</exception>
    public static string NoteOf(StoredSource source) => source switch
    {
        StoredSource.ShareX => UiText.Translate("ShareX's own screenshots stay in its folders: only BetterClipboard's copies are deleted, and the next catch-up does not bring them back. New screenshots are recorded while the switch is on."),
        StoredSource.Snipping => UiText.Translate("Your Screenshots folder is not touched: only BetterClipboard's copies are deleted, and the next catch-up does not bring them back. New screenshots and snips are recorded while the switch is on."),
        StoredSource.NotepadPlusPlusTabs => UiText.Translate("Notepad++'s tabs and backup files are not touched. A tab's current text is not recorded again until it changes."),
        StoredSource.NotepadPlusPlusFiles => UiText.Translate("The files themselves are not touched. A file you open in Notepad++ again is recorded again while the switch is on."),
        StoredSource.Everything => UiText.Translate("Everything's own run history is not touched: the tab still lists the files you opened in Everything."),
        StoredSource.RunHistory => UiText.Translate("Windows' own Win+R list is not touched and is not imported again. Commands you run from now on are recorded while the switch is on."),
        StoredSource.PowerShell => UiText.Translate("PowerShell's history file is not touched: the tab still lists the commands in it."),
        StoredSource.Cmd => UiText.Translate("Open Command Prompt windows are not touched: the tab shows what they hold while they are open. Commands typed from now on are kept while the switch is on."),
        StoredSource.ClaudeCode => UiText.Translate("Claude Code's own files are not touched. While the switch is on, what Claude Code still has is read again right away: turn the switch off below to keep it out."),
        StoredSource.Codex => UiText.Translate("Codex's own files are not touched. While the switch is on, what Codex still has is read again right away: turn the switch off below to keep it out."),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a stored source."),
    };
}
