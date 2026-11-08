using BetterClipboard.Core.Localization;
using Microsoft.UI.Xaml.Markup;

namespace BetterClipboard.App.Localization;

/// <summary>Resolves a static XAML label through the same catalog used by dynamic UI messages.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    /// <summary>Stable catalog key; unlike user data it is owned by the application.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Resolves text when XAML creates the control, preserving bindings and keyboard gestures.</summary>
    /// <returns>The selected-language string, with an English fallback.</returns>
    protected override object ProvideValue() => UiText.Get(Key);
}
