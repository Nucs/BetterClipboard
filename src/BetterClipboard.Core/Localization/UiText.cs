using System.Globalization;
using System.Text.Json;

namespace BetterClipboard.Core.Localization;

/// <summary>Resolves application-owned UI text without translating clipboard content or persisted identifiers.</summary>
public static class UiText
{
    private static readonly IReadOnlyDictionary<string, string> English = ReadCatalog("en");
    private static readonly IReadOnlyDictionary<string, string> Chinese = ReadCatalog("zh-Hans");
    private static readonly IReadOnlyDictionary<string, string> KeysByEnglish = English.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    /// <summary>Selected UI language: system, en, or zh-Hans. Changes take effect fully after restarting the app.</summary>
    public static string Language { get; set; } = "system";

    /// <summary>Determines whether a language selection requests Simplified Chinese, excluding Traditional Chinese locales.</summary>
    /// <param name="selection">Saved language selection; unknown values follow the system language.</param>
    /// <param name="systemLanguage">System UI culture name, supplied explicitly for deterministic tests.</param>
    /// <returns>Whether the Simplified Chinese catalog should be used.</returns>
    public static bool UsesSimplifiedChinese(string? selection, string systemLanguage)
    {
        string name = selection is "en" or "zh-Hans" ? selection : systemLanguage;
        return name.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets a catalog entry, falling back to English or the key when a translation is unavailable.</summary>
    /// <param name="key">Stable catalog key used by XAML markup.</param>
    /// <returns>Localized text; never clipboard or user-supplied text.</returns>
    public static string Get(string key)
    {
        if (UsesSimplifiedChinese(Language, CultureInfo.CurrentUICulture.Name) && Chinese.TryGetValue(key, out string? localized))
            return localized;
        return English.TryGetValue(key, out string? english) ? english : key;
    }

    /// <summary>Translates an application-owned English literal, retaining its exact value when not in the catalog.</summary>
    /// <param name="english">Developer-authored English literal, not a history item or user-entered group name.</param>
    /// <returns>Localized UI text with an English fallback.</returns>
    public static string Translate(string english) => KeysByEnglish.TryGetValue(english, out string? key) ? Get(key) : english;

    /// <summary>Translates an interpolated UI template before formatting, so argument values are never translated.</summary>
    /// <param name="message">Application-owned interpolation; arguments can contain paths or user text.</param>
    /// <returns>The translated template formatted using the user's numeric and date culture.</returns>
    public static string Format(FormattableString message) => string.Format(CultureInfo.CurrentCulture, Translate(message.Format), message.GetArguments());

    /// <summary>Loads an embedded catalog once; catalogs ship inside the assembly and require no external language files.</summary>
    /// <param name="language">Catalog name without the JSON suffix.</param>
    /// <returns>The catalog's stable keys and display strings.</returns>
    private static IReadOnlyDictionary<string, string> ReadCatalog(string language)
    {
        using Stream stream = typeof(UiText).Assembly.GetManifestResourceStream($"BetterClipboard.Core.Localization.{language}.json")
            ?? throw new InvalidOperationException($"Missing UI catalog: {language}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Invalid UI catalog: {language}");
    }
}
