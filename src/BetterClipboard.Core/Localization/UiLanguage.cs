using System.Diagnostics.CodeAnalysis;

namespace BetterClipboard.Core.Localization;

/// <summary>
/// One interface language shipped inside <c>BetterClipboard.Core.dll</c>: its own name, its English name, its flag and
/// its catalog of UI strings — everything the first-start language picker, Settings › Language and the lookups need,
/// read once from the embedded <c>Localization/{tag}.json</c> and <c>Localization/Flags/{flag}.svg</c>.
/// </summary>
/// <remarks>
/// <para>
/// A language is data, not code: adding one means adding its catalog (and its flag when no shipped language uses that
/// flag yet) to <c>src/BetterClipboard.Core/Localization</c>. <see cref="UiText.Languages"/> finds it, the picker and
/// Settings list it, and <c>LocalizationTests</c> check that it is complete. Nothing is read from disk at run time, so a
/// release is one build for every language, and a copy can never miss a language file.
/// </para>
/// <para>Instances are immutable and shared by every thread.</para>
/// </remarks>
public sealed class UiLanguage
{
    /// <summary>The flag's SVG document, kept private so no caller can change what every other caller draws.</summary>
    private readonly byte[] flagSvg;

    /// <summary>
    /// Creates a language from its parsed catalog; only <see cref="UiText"/> does, from the embedded resources.
    /// </summary>
    /// <param name="tag">The catalog's file name without <c>.json</c>.</param>
    /// <param name="name">The language's name in itself.</param>
    /// <param name="englishName">The language's name in English.</param>
    /// <param name="flag">The flag's two-letter code (the SVG's file name).</param>
    /// <param name="flagSvg">The flag's SVG document; owned by the new instance from now on.</param>
    /// <param name="alsoFor">Extra Windows display-language names that pick this catalog by their exact name.</param>
    /// <param name="strings">The UI strings by key; owned by the new instance from now on.</param>
    internal UiLanguage(
        string tag,
        string name,
        string englishName,
        string flag,
        byte[] flagSvg,
        IReadOnlyList<string> alsoFor,
        IReadOnlyDictionary<string, string> strings)
    {
        Tag = tag;
        Name = name;
        EnglishName = englishName;
        Flag = flag;
        this.flagSvg = flagSvg;
        AlsoFor = alsoFor;
        Strings = strings;
    }

    /// <summary>
    /// The catalog's tag: its file name without <c>.json</c> (<c>en</c>, <c>zh-Hans</c>). An explicit choice stores it in
    /// <c>AppSettings.InterfaceLanguage</c>, and windows hand it to XAML's <c>Language</c> property so Han characters get
    /// the font of the right language. Never translated, never shown.
    /// </summary>
    public string Tag { get; }

    /// <summary>
    /// The language's name in itself (<c>简体中文</c>, <c>English</c>). The picker shows it first, so people find their
    /// language without reading English.
    /// </summary>
    public string Name { get; }

    /// <summary>The language's name in English (<c>Chinese (Simplified)</c>), shown next to its own name.</summary>
    public string EnglishName { get; }

    /// <summary>
    /// The flag drawn next to the names: the lowercase ISO 3166-1 code of a country whose flag stands for the language
    /// (<c>us</c>, <c>cn</c>), which is also the SVG's file name. A flag is a hint for the eye only; the names say which
    /// language it is, and screen readers read the names.
    /// </summary>
    public string Flag { get; }

    /// <summary>
    /// The flag's SVG document (UTF-8), for an image source. Read-only: it is the same buffer for every caller.
    /// </summary>
    public ReadOnlyMemory<byte> FlagSvg => flagSvg;

    /// <summary>
    /// Windows display-language names that pick this catalog when Windows uses exactly that name although the tag is
    /// not one of its parent cultures (<c>zh</c> for <c>zh-Hans</c>). Matched only by the exact name, never through a
    /// parent: <c>zh-TW</c>'s parents end in <c>zh</c>, and Traditional Chinese must not turn into Simplified.
    /// </summary>
    public IReadOnlyList<string> AlsoFor { get; }

    /// <summary>How many UI strings the catalog holds (every key of the English catalog, when it is complete).</summary>
    public int Count => Strings.Count;

    /// <summary>The catalog's UI strings by key ("T" + 12 hex digits of the English text's SHA-256).</summary>
    internal IReadOnlyDictionary<string, string> Strings { get; }

    /// <summary>Looks up one UI string of this catalog, without falling back to English.</summary>
    /// <param name="key">The string's key.</param>
    /// <param name="text">The catalog's text for it, when it has one.</param>
    /// <returns>Whether this catalog has the key.</returns>
    public bool TryGet(string key, [NotNullWhen(true)] out string? text) => Strings.TryGetValue(key, out text);

    /// <summary>The tag, for logs and debugging.</summary>
    /// <returns>The tag.</returns>
    public override string ToString() => Tag;
}
