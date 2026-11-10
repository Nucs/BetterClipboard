using BetterClipboard.Core.Localization;
using Microsoft.UI.Xaml.Markup;

namespace BetterClipboard.App.Localization;

/// <summary>
/// Resolves a static XAML label through the same catalogs as the code's UI text: <c>{loc:Tr Text='Paste'}</c> names the
/// English text, which is also its key; <c>{loc:Tr Key=T…}</c> names a hash key, for the few texts kept out of the markup.
/// </summary>
/// <remarks>
/// The value is read once, when XAML builds the control: a window built before the language changed keeps its old
/// labels, which is why a language chosen in Settings applies with a restart. Quote a text with an apostrophe as
/// <c>Text='Windows\' list'</c> (a backslash escapes the next character inside a markup extension's quoted value).
/// </remarks>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    /// <summary>The English text to show, translated: also its catalog key. Takes precedence over <see cref="Key"/>.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>A hash key (<c>T</c> + 12 hex digits) of a text kept out of the markup; owned by the application like <see cref="Text"/>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Resolves text when XAML creates the control, preserving bindings and keyboard gestures.</summary>
    /// <returns>The selected-language string, with an English fallback.</returns>
    protected override object ProvideValue() => Text.Length > 0 ? UiText.Translate(Text) : UiText.Get(Key);
}
