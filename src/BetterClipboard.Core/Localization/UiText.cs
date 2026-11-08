using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Core.Localization;

/// <summary>Resolves application-owned UI text without translating clipboard content or persisted identifiers.</summary>
/// <remarks>
/// <para>
/// <b>Where the text comes from.</b> Every interface language is one JSON catalog embedded in this assembly
/// (<c>Localization/{tag}.json</c>, with its names and flag in a <c>"$language"</c> header and its flag in
/// <c>Localization/Flags/{flag}.svg</c>). <see cref="Languages"/> finds them all when the class is first used, so a new
/// language needs no code, and a release is one build for every language.
/// </para>
/// <para>
/// <b>Keys.</b> A string's key is <c>T</c> + the first 12 hex digits of the SHA-256 of its English text. XAML names the
/// key (<c>{loc:Tr Key=…}</c>); C# names the English text (<see cref="Translate(string)"/>, <see cref="Format(FormattableString)"/>),
/// which is mapped back to the key. Footgun: an English text changed in C# without the catalogs no longer matches its
/// key, and shows in English everywhere until the catalogs follow (<c>tools/localization/catalog.py</c>).
/// </para>
/// <para>
/// <b>Never translated:</b> logs (the developer reads them), <c>bclip</c>'s output (scripts and agents parse it),
/// clipboard content, user names for things (groups), and identifiers. Pass such text around this class.
/// </para>
/// </remarks>
public static class UiText
{
    /// <summary>
    /// The selection that follows Windows' display language: <c>AppSettings.InterfaceLanguage</c>'s default, and what
    /// Settings saves for "Use Windows setting".
    /// </summary>
    public const string SystemSelection = "system";

    /// <summary>The tag of the source language every key's text is written in; it is also every missing translation's fallback.</summary>
    public const string EnglishTag = "en";

    /// <summary>Manifest-resource prefix of the catalogs (the project's root namespace plus the folder).</summary>
    private const string ResourcePrefix = "BetterClipboard.Core.Localization.";

    /// <summary>Manifest-resource prefix of the flags (<c>Localization/Flags/{flag}.svg</c>).</summary>
    private const string FlagResourcePrefix = ResourcePrefix + "Flags.";

    /// <summary>The catalog property that holds the language's names and flag instead of a UI string.</summary>
    private const string MetadataProperty = "$language";

    /// <summary>Every shipped language: English first, then the others by their English name.</summary>
    private static readonly IReadOnlyList<UiLanguage> AllLanguages = LoadLanguages();

    /// <summary>The source language (always the first of <see cref="AllLanguages"/>).</summary>
    private static readonly UiLanguage EnglishLanguage = AllLanguages[0];

    /// <summary>English text → key, for C#'s lookups by English text.</summary>
    private static readonly IReadOnlyDictionary<string, string> KeysByEnglish = IndexEnglish(EnglishLanguage);

    /// <summary>
    /// The last resolution of <see cref="Current"/>, with the inputs it came from. Replaced as a whole (a reference
    /// write is atomic), so a reader on another thread sees either the old or the new triple, never a mix.
    /// </summary>
    private static volatile ResolvedLanguage? resolved;

    /// <summary>
    /// Selected UI language: <see cref="SystemSelection"/> or a catalog tag (<c>en</c>, <c>zh-Hans</c>); an unknown value
    /// follows Windows. Read once at startup from the settings and set before any window resolves its labels, since XAML
    /// reads them only when a window is built: a later change shows fully after a restart.
    /// </summary>
    public static string Language { get; set; } = SystemSelection;

    /// <summary>
    /// Every language this build ships, English first, then by English name: what the first-start picker and
    /// Settings › Language list.
    /// </summary>
    public static IReadOnlyList<UiLanguage> Languages => AllLanguages;

    /// <summary>The source language, also the fallback for every string a catalog lacks.</summary>
    public static UiLanguage English => EnglishLanguage;

    /// <summary>
    /// The language in effect now: <see cref="Language"/> resolved against Windows' display language
    /// (<see cref="CultureInfo.CurrentUICulture"/>). Cached until either input changes, so the hundreds of lookups a
    /// window makes while it is built cost a dictionary read each.
    /// </summary>
    public static UiLanguage Current
    {
        get
        {
            string selection = Language;
            string system = CultureInfo.CurrentUICulture.Name;
            var last = resolved;
            if (last is not null && last.Selection == selection && last.System == system)
            {
                return last.Language;
            }

            var language = Resolve(selection, system);
            resolved = new ResolvedLanguage(selection, system, language);
            return language;
        }
    }

    /// <summary>Determines whether a language selection requests Simplified Chinese, excluding Traditional Chinese locales.</summary>
    /// <param name="selection">Saved language selection; unknown values follow the system language.</param>
    /// <param name="systemLanguage">System UI culture name, supplied explicitly for deterministic tests.</param>
    /// <returns>Whether the Simplified Chinese catalog should be used.</returns>
    public static bool UsesSimplifiedChinese(string? selection, string systemLanguage) =>
        string.Equals(Resolve(selection, systemLanguage).Tag, "zh-Hans", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The language a saved selection means: a shipped catalog chosen explicitly (any letter case), else the one that
    /// matches Windows (<see cref="MatchSystem"/>) — for <see cref="SystemSelection"/>, an empty value, and a tag this
    /// build does not ship (a newer version's language, or a hand edit).
    /// </summary>
    /// <param name="selection">The saved selection.</param>
    /// <param name="systemLanguage">Windows' display language (a culture name such as <c>zh-CN</c>).</param>
    /// <returns>The language; English when nothing else matches.</returns>
    public static UiLanguage Resolve(string? selection, string? systemLanguage) =>
        selection is not null
        && !string.Equals(selection, SystemSelection, StringComparison.OrdinalIgnoreCase)
        && TryFind(selection, out var chosen)
            ? chosen
            : MatchSystem(systemLanguage);

    /// <summary>
    /// The shipped language that fits a Windows display language: its own tag or a name it claims
    /// (<see cref="UiLanguage.AlsoFor"/>), else the nearest parent culture with a catalog (<c>zh-SG</c> → <c>zh-Hans</c>,
    /// <c>en-GB</c> → <c>en</c>), else English. What "Use Windows setting" shows, and what the first-start picker
    /// suggests.
    /// </summary>
    /// <param name="systemLanguage">A culture name; empty, unknown or malformed names give English.</param>
    /// <returns>The language.</returns>
    public static UiLanguage MatchSystem(string? systemLanguage)
    {
        if (string.IsNullOrWhiteSpace(systemLanguage))
        {
            return EnglishLanguage;
        }

        foreach (var language in AllLanguages)
        {
            if (string.Equals(language.Tag, systemLanguage, StringComparison.OrdinalIgnoreCase)
                || language.AlsoFor.Contains(systemLanguage, StringComparer.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        // Parents only match tags, never AlsoFor: zh-TW's parents end in "zh", which zh-Hans claims for plain "zh".
        foreach (string parent in ParentNames(systemLanguage))
        {
            if (TryFind(parent, out var language))
            {
                return language;
            }
        }

        return EnglishLanguage;
    }

    /// <summary>Finds a shipped language by its tag, ignoring letter case.</summary>
    /// <param name="tag">A catalog tag.</param>
    /// <param name="language">The language, when this build ships it.</param>
    /// <returns>Whether it does.</returns>
    public static bool TryFind(string? tag, [NotNullWhen(true)] out UiLanguage? language)
    {
        language = AllLanguages.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
        return language is not null;
    }

    /// <summary>Gets a catalog entry, falling back to English or the key when a translation is unavailable.</summary>
    /// <param name="key">Stable catalog key used by XAML markup.</param>
    /// <returns>Localized text; never clipboard or user-supplied text.</returns>
    public static string Get(string key) => Get(key, Current);

    /// <summary>
    /// Gets a catalog entry in a given language, whatever <see cref="Language"/> says: the first-start picker words
    /// itself in the language under its cursor before anything is chosen.
    /// </summary>
    /// <param name="key">Stable catalog key.</param>
    /// <param name="language">The language to read.</param>
    /// <returns>The language's text, else the English text, else the key itself (a key no catalog has is a bug the tests catch).</returns>
    public static string Get(string key, UiLanguage language) =>
        language.TryGet(key, out string? text) ? text
        : EnglishLanguage.TryGet(key, out string? english) ? english
        : key;

    /// <summary>Translates an application-owned English literal, retaining its exact value when not in the catalog.</summary>
    /// <param name="english">Developer-authored English literal, not a history item or user-entered group name.</param>
    /// <returns>Localized UI text with an English fallback.</returns>
    public static string Translate(string english) => Translate(english, Current);

    /// <summary>Translates an application-owned English literal into a given language.</summary>
    /// <param name="english">Developer-authored English literal.</param>
    /// <param name="language">The language to translate into.</param>
    /// <returns>The language's text; the literal itself when no catalog has it.</returns>
    public static string Translate(string english, UiLanguage language) =>
        KeysByEnglish.TryGetValue(english, out string? key) ? Get(key, language) : english;

    /// <summary>Translates an interpolated UI template before formatting, so argument values are never translated.</summary>
    /// <param name="message">Application-owned interpolation; arguments can contain paths or user text.</param>
    /// <returns>The translated template formatted using the user's numeric and date culture.</returns>
    public static string Format(FormattableString message) => string.Format(CultureInfo.CurrentCulture, Translate(message.Format), message.GetArguments());

    /// <summary>Formats a localized template while preserving a caller-supplied number and date culture.</summary>
    /// <param name="provider">Explicit culture, or null for the current culture.</param>
    /// <param name="message">Application-owned interpolation whose arguments are not translated.</param>
    /// <returns>Localized and formatted UI text.</returns>
    public static string Format(IFormatProvider? provider, FormattableString message) => string.Format(provider, Translate(message.Format), message.GetArguments());

    /// <summary>
    /// Reads every catalog embedded in this assembly. English must be there and valid (without it there is no text to
    /// show); any other catalog that does not parse is left out with a warning, so one broken translation cannot keep
    /// the app from starting.
    /// </summary>
    /// <returns>English first, then the others by English name.</returns>
    /// <exception cref="InvalidDataException">The English catalog is missing or broken (a broken build; the tests catch it).</exception>
    private static IReadOnlyList<UiLanguage> LoadLanguages()
    {
        var assembly = typeof(UiText).Assembly;
        var languages = new List<UiLanguage>();
        UiLanguage? english = null;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            // MSBuild names a resource by its folders with dots, so a dot left in the name means a subfolder: not a catalog.
            string tag = resource[ResourcePrefix.Length..^".json".Length];
            if (tag.Length == 0 || tag.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var language = ReadCatalog(assembly, resource, tag);
                if (tag == EnglishTag)
                {
                    english = language;
                }
                else
                {
                    languages.Add(language);
                }
            }
            catch (Exception ex) when (tag != EnglishTag && ex is InvalidDataException or JsonException)
            {
                AppLog.Warn($"The {tag} interface language is left out: its catalog does not load ({ex.Message}).");
            }
        }

        if (english is null)
        {
            throw new InvalidDataException("Missing UI catalog: en");
        }

        return [english, .. languages.OrderBy(l => l.EnglishName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Parses one embedded catalog: the <c>"$language"</c> header (names, flag, extra Windows names) and the UI strings.
    /// Other <c>$</c> properties are reserved for later headers and skipped, so an older build reads a newer catalog.
    /// </summary>
    /// <param name="assembly">This assembly.</param>
    /// <param name="resource">The catalog's manifest-resource name.</param>
    /// <param name="tag">The catalog's tag (its file name without <c>.json</c>).</param>
    /// <returns>The language.</returns>
    /// <exception cref="InvalidDataException">The header is missing or incomplete, a value is not a string, or the flag is not embedded.</exception>
    /// <exception cref="JsonException">The catalog is not valid JSON.</exception>
    private static UiLanguage ReadCatalog(Assembly assembly, string resource, string tag)
    {
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Missing UI catalog: {tag}");
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {tag} catalog is not a JSON object.");
        }

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonElement header = default;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == MetadataProperty)
            {
                header = property.Value;
                continue;
            }

            if (property.Name.StartsWith('$'))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"The {tag} catalog's {property.Name} is not a string.");
            }

            strings[property.Name] = property.Value.GetString()!;
        }

        if (header.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {tag} catalog has no \"{MetadataProperty}\" header.");
        }

        string name = HeaderText(header, "name", tag);
        string englishName = HeaderText(header, "englishName", tag);
        string flag = HeaderText(header, "flag", tag);
        if (flag.Length != 2 || !flag.All(char.IsAsciiLetterLower))
        {
            throw new InvalidDataException($"The {tag} catalog's flag \"{flag}\" is not a lowercase two-letter country code.");
        }

        string[] alsoFor = header.TryGetProperty("alsoFor", out var also) && also.ValueKind == JsonValueKind.Array
            ? [.. also.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0)]
            : [];

        using Stream flagStream = assembly.GetManifestResourceStream(FlagResourcePrefix + flag + ".svg")
            ?? throw new InvalidDataException($"The {tag} catalog's flag {flag}.svg is not embedded.");
        using var buffer = new MemoryStream();
        flagStream.CopyTo(buffer);
        return new UiLanguage(tag, name, englishName, flag, buffer.ToArray(), alsoFor, strings);
    }

    /// <summary>Reads one required, non-empty text of a catalog's header.</summary>
    /// <param name="header">The <c>"$language"</c> object.</param>
    /// <param name="property">The property's name.</param>
    /// <param name="tag">The catalog's tag, for the message.</param>
    /// <returns>The trimmed text.</returns>
    /// <exception cref="InvalidDataException">The property is missing, not a string, or blank.</exception>
    private static string HeaderText(JsonElement header, string property, string tag) =>
        header.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : throw new InvalidDataException($"The {tag} catalog's \"{MetadataProperty}\" has no {property}.");

    /// <summary>
    /// Maps each English text back to its key. Two keys with one English text cannot be told apart by C#'s lookups, so
    /// the first wins instead of throwing (which would break every lookup of the process); the tests forbid duplicates.
    /// </summary>
    /// <param name="english">The English catalog.</param>
    /// <returns>Key by English text.</returns>
    private static IReadOnlyDictionary<string, string> IndexEnglish(UiLanguage english)
    {
        var keys = new Dictionary<string, string>(english.Count, StringComparer.Ordinal);
        foreach (var (key, text) in english.Strings.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            keys.TryAdd(text, key);
        }

        return keys;
    }

    /// <summary>
    /// A culture's parents, nearest first (<c>zh-SG</c> → <c>zh-Hans</c>, <c>zh</c>), then the name cut at each dash for
    /// names .NET does not know (<c>xx-Hans-YY</c> → <c>xx-Hans</c>, <c>xx</c>), without repeats.
    /// </summary>
    /// <param name="cultureName">The culture name.</param>
    /// <returns>Candidate tags, nearest first.</returns>
    private static List<string> ParentNames(string cultureName)
    {
        var names = new List<string>(4);
        CultureInfo? culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(cultureName).Parent;
        }
        catch (ArgumentException)
        {
            // CultureNotFoundException (an ArgumentException): an unknown or malformed name; the cuts below still help.
            culture = null;
        }

        for (; culture is not null && culture.Name.Length > 0; culture = culture.Parent)
        {
            names.Add(culture.Name);
        }

        for (int dash = cultureName.LastIndexOf('-'); dash > 0; dash = cultureName.LastIndexOf('-', dash - 1))
        {
            string prefix = cultureName[..dash];
            if (!names.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(prefix);
            }
        }

        return names;
    }

    /// <summary>One resolution of <see cref="Current"/> and the inputs it came from.</summary>
    /// <param name="Selection">The <see cref="Language"/> value it was made for.</param>
    /// <param name="System">The display-language name it was made for.</param>
    /// <param name="Language">The resolved language.</param>
    private sealed record ResolvedLanguage(string Selection, string System, UiLanguage Language);
}
