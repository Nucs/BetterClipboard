using BetterClipboard.App.Localization;
using BetterClipboard.Core.Localization;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// One row of a language list — the first-start picker and Settings › Language: a shipped language with its own name,
/// its flag and its English name, or (Settings only) "Use Windows setting", which shows the flag of the language
/// Windows' setting picks now.
/// </summary>
/// <remarks>
/// <para>
/// Built on the UI thread (the flag is an image source). The texts are fixed when the row is made: a language's names
/// are the same in every interface language, and "Use Windows setting" is worded in the language in effect when the
/// list is built (a change of language shows fully after a restart anyway).
/// </para>
/// <para>
/// A row is drawn two ways in Settings (<c>LanguageOptionView</c>): in the open list as flag, <see cref="Name"/> and
/// <see cref="EnglishName"/>, and as the closed box's choice as flag, <see cref="SelectedName"/> and
/// <see cref="SelectedHint"/> — "🇺🇸 English · Windows setting" while BetterClipboard follows Windows, "🇨🇳 简体中文"
/// for a language picked (the owner's choice, 2026-11-09).
/// </para>
/// </remarks>
public sealed class LanguageOptionViewModel
{
    /// <summary>Creates the row of a shipped language.</summary>
    /// <param name="language">The language.</param>
    /// <exception cref="ArgumentNullException"><paramref name="language"/> is <see langword="null"/>.</exception>
    public LanguageOptionViewModel(UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        Language = language;
        Name = language.Name;

        // English's two names are the same word: the right column would only repeat the left one.
        EnglishName = string.Equals(language.Name, language.EnglishName, StringComparison.OrdinalIgnoreCase) ? string.Empty : language.EnglishName;
        Flag = FlagImages.For(language);
        SelectedName = language.Name;
        SelectedNameLanguageTag = language.Tag;
        SelectedHint = string.Empty;
        AccessibleName = EnglishName.Length > 0 ? $"{language.Name}, {language.EnglishName}" : language.Name;
    }

    /// <summary>Creates the "Use Windows setting" row (Settings › Language only; the picker offers real languages).</summary>
    /// <param name="systemLabel">The row's label in the interface language.</param>
    /// <param name="windowsLanguage">
    /// The language Windows' setting picks now: its name follows the label, its flag stands for the row, and the closed
    /// box shows it as the language in effect.
    /// </param>
    /// <param name="selectedHint">
    /// What the closed box says after that language's name while this row is chosen ("Windows setting"), in the
    /// interface language.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public LanguageOptionViewModel(string systemLabel, UiLanguage windowsLanguage, string selectedHint)
    {
        ArgumentNullException.ThrowIfNull(systemLabel);
        ArgumentNullException.ThrowIfNull(windowsLanguage);
        ArgumentNullException.ThrowIfNull(selectedHint);
        Language = null;
        Name = systemLabel;
        EnglishName = windowsLanguage.Name;
        Flag = FlagImages.For(windowsLanguage);
        SelectedName = windowsLanguage.Name;
        SelectedNameLanguageTag = windowsLanguage.Tag;
        SelectedHint = selectedHint;
        AccessibleName = $"{systemLabel}, {windowsLanguage.Name}";
    }

    /// <summary>The language, or <see langword="null"/> for "Use Windows setting".</summary>
    public UiLanguage? Language { get; }

    /// <summary>What the settings store for this row: the language's tag, or <see cref="UiText.SystemSelection"/>.</summary>
    public string Selection => Language?.Tag ?? UiText.SystemSelection;

    /// <summary>
    /// The tag for XAML's <c>Language</c> property of the row's name, so Han characters are drawn with the font of their
    /// own language (Simplified Chinese glyphs, not Japanese ones). The Windows row is worded in the interface language,
    /// so it gets that one's tag (never an empty one, which the property refuses).
    /// </summary>
    public string LanguageTag => Language?.Tag ?? UiText.Current.Tag;

    /// <summary>The row's main text: the language's name in itself, or the "Use Windows setting" label.</summary>
    public string Name { get; }

    /// <summary>
    /// The row's secondary text: the language's English name (empty for English itself), or for the Windows row the name
    /// of the language Windows' setting picks now.
    /// </summary>
    public string EnglishName { get; }

    /// <summary>
    /// The flag: the language's own, or for the Windows row the flag of the language Windows' setting picks now (until
    /// 2026-11-09 a globe stood there; the owner asked for a flag).
    /// </summary>
    public ImageSource Flag { get; }

    /// <summary>Whether the row is "Use Windows setting", which stores no language of its own.</summary>
    public bool IsSystem => Language is null;

    /// <summary>
    /// The name the closed box shows while this row is chosen: the language's own name, or for the Windows row the own
    /// name of the language Windows' setting picks now ("English", not "Use Windows setting").
    /// </summary>
    public string SelectedName { get; }

    /// <summary>The tag for XAML's <c>Language</c> property of <see cref="SelectedName"/> (see <see cref="LanguageTag"/>).</summary>
    public string SelectedNameLanguageTag { get; }

    /// <summary>
    /// What the closed box adds after <see cref="SelectedName"/>, dimmed: "Windows setting" for the Windows row, so it
    /// can be told apart from the same language picked; empty for a language.
    /// </summary>
    public string SelectedHint { get; }

    /// <summary>What screen readers say for the row: both names, since the flag is a picture without words.</summary>
    public string AccessibleName { get; }

    /// <summary>
    /// The accessible name: a list item's automation peer names the item after its data's text, so without this
    /// override screen readers and UI Automation would read the view model's type name for every row.
    /// </summary>
    /// <returns><see cref="AccessibleName"/>.</returns>
    public override string ToString() => AccessibleName;
}
