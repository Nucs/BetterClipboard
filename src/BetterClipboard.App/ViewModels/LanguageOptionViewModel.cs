using BetterClipboard.App.Localization;
using BetterClipboard.Core.Localization;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.ViewModels;

/// <summary>
/// One row of a language list — the first-start picker and Settings › Language: a shipped language with its own name,
/// its flag and its English name, or (Settings only) "Use Windows setting", which has no language of its own.
/// </summary>
/// <remarks>
/// Built on the UI thread (the flag is an image source). The texts are fixed when the row is made: a language's names
/// are the same in every interface language, and "Use Windows setting" is worded in the language in effect when the
/// list is built (a change of language shows fully after a restart anyway).
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
        AccessibleName = EnglishName.Length > 0 ? $"{language.Name}, {language.EnglishName}" : language.Name;
    }

    /// <summary>Creates the "Use Windows setting" row (Settings › Language only; the picker offers real languages).</summary>
    /// <param name="systemLabel">The row's label in the interface language.</param>
    /// <param name="windowsLanguage">The language Windows' setting picks now, named after the label so the choice is clear.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public LanguageOptionViewModel(string systemLabel, UiLanguage windowsLanguage)
    {
        ArgumentNullException.ThrowIfNull(systemLabel);
        ArgumentNullException.ThrowIfNull(windowsLanguage);
        Language = null;
        Name = systemLabel;
        EnglishName = windowsLanguage.Name;
        Flag = null;
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

    /// <summary>The flag, or <see langword="null"/> for the Windows row (which shows a globe instead).</summary>
    public ImageSource? Flag { get; }

    /// <summary>Whether the row is "Use Windows setting": it shows a globe where the others show a flag.</summary>
    public bool IsSystem => Language is null;

    /// <summary>Whether the row has a flag to show (the opposite of <see cref="IsSystem"/>, for x:Bind visibility).</summary>
    public bool HasFlag => Language is not null;

    /// <summary>What screen readers say for the row: both names, since the flag is a picture without words.</summary>
    public string AccessibleName { get; }

    /// <summary>
    /// The accessible name: a list item's automation peer names the item after its data's text, so without this
    /// override screen readers and UI Automation would read the view model's type name for every row.
    /// </summary>
    /// <returns><see cref="AccessibleName"/>.</returns>
    public override string ToString() => AccessibleName;
}
