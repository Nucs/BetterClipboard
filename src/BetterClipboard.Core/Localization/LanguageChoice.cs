namespace BetterClipboard.Core.Localization;

/// <summary>
/// When the first start asks for the interface language (the language picker with flags), and which language it
/// starts on. Pure, so the rule is tested without a window; the App shows the picker.
/// </summary>
/// <remarks>
/// The picker runs before any other window is built. XAML reads its labels only when a window is built, so a language
/// chosen there applies to everything at once, with no restart. Changing it later in Settings needs a restart.
/// </remarks>
public static class LanguageChoice
{
    /// <summary>
    /// Whether this start asks for the language: only on the app's very first start in this data folder (no history was
    /// ever kept there), and only while nobody picked a language yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the history and not the settings file: the installer writes <c>settings.json</c> before the first start when
    /// it sets shortcuts (<c>-Hotkey</c>, <c>-NoTakeOverWinV</c>) or switches the update check off (<c>-NoUpdateCheck</c>),
    /// but nothing except the app creates a history store. So a user who installs with options is still asked, and a
    /// user who updates from a version without languages is not: their history exists.
    /// </para>
    /// <para>
    /// A language already in the settings (an administrator's pre-seeded <c>settings.json</c>, or a choice saved by a
    /// start that ended before its history was created) is respected: the picker never asks over it.
    /// </para>
    /// </remarks>
    /// <param name="historyExisted">Whether the data folder held a history before this start (<see cref="AppPaths.HasHistory"/>).</param>
    /// <param name="interfaceLanguage">The saved selection (<c>AppSettings.InterfaceLanguage</c>).</param>
    /// <returns>Whether to show the picker before any other window.</returns>
    public static bool ShouldAsk(bool historyExisted, string? interfaceLanguage) =>
        !historyExisted
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
