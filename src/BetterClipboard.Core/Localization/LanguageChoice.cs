namespace BetterClipboard.Core.Localization;

/// <summary>
/// When a start asks for the interface language (the language picker with flags), and which language it starts on.
/// Pure, so the rule is tested without a window; the App shows the picker.
/// </summary>
/// <remarks>
/// The picker runs before any other window is built. XAML reads its labels only when a window is built, so a language
/// chosen there applies to everything at once, with no restart. Changing it later in Settings needs a restart.
/// </remarks>
public static class LanguageChoice
{
    /// <summary>
    /// Whether this start asks for the language: whenever nobody has answered the question yet. That is a first start,
    /// and equally the first start after an update from a version that never asked (the owner's request, 2026-11-09:
    /// "Also should be true for users that just updated and never saw the dialog").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answered means: the picker closed with a language (every way it closes saves one), or Settings › App › Language
    /// was changed — both set <c>AppSettings.LanguageChosen</c>. Until then a start that ends with the picker still open
    /// (an exit from the tray, a crash) asks again next time.
    /// </para>
    /// <para>
    /// A language already in the settings is never asked over: one picked in an earlier version's picker or in Settings,
    /// or an administrator's pre-seeded <c>settings.json</c>. "system" (or nothing) alone is no answer: it is also what
    /// every settings file holds that was written before the language existed.
    /// </para>
    /// <para>
    /// Until 2026-11-09 the rule asked only when the data folder held no history yet, so whoever updated from a version
    /// without languages was never asked.
    /// </para>
    /// </remarks>
    /// <param name="languageChosen">Whether the user answered the question (<c>AppSettings.LanguageChosen</c>).</param>
    /// <param name="interfaceLanguage">The saved selection (<c>AppSettings.InterfaceLanguage</c>).</param>
    /// <returns>Whether to show the picker before any other window.</returns>
    public static bool ShouldAsk(bool languageChosen, string? interfaceLanguage) =>
        !languageChosen
        && (string.IsNullOrWhiteSpace(interfaceLanguage)
            || string.Equals(interfaceLanguage.Trim(), UiText.SystemSelection, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The language the picker starts on: the shipped catalog that fits Windows' display language, else English. One
    /// click on the green check then keeps what Windows already uses.
    /// </summary>
    /// <param name="systemLanguage">Windows' display language (<c>CultureInfo.CurrentUICulture.Name</c>).</param>
    /// <returns>The suggested language.</returns>
    public static UiLanguage Suggest(string? systemLanguage) => UiText.MatchSystem(systemLanguage);
}
