using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Core.Localization;

/// <summary>Resolves application-owned UI text without translating clipboard content or persisted identifiers.</summary>
/// <remarks>
/// <para>
/// <b>Where the text comes from.</b> Every interface language is one JSON catalog embedded in this assembly
/// (<c>Localization/{tag}.json</c>, with its names, flag and plural rules in a <c>"$language"</c> header and its flag in
/// <c>Localization/Flags/{flag}.svg</c>). <see cref="Languages"/> finds them all when the class is first used, so a new
/// language needs no code, and a release is one build for every language. Every row of <c>en.json</c> ends with a
/// <c>//</c> comment describing the string for translators (where it shows, what each placeholder holds, how long it may
/// be); the loader skips comments (<see cref="CatalogParseOptions"/>), and the tests require one on every row.
/// </para>
/// <para>
/// <b>Keys are the English text.</b> XAML writes <c>{loc:Tr Text='Paste'}</c>, C# <see cref="Translate(string)"/>,
/// <see cref="Format(FormattableString)"/> and <see cref="Plural(long, FormattableString, FormattableString)"/> with
/// the English text, and a catalog maps that text to its translation (<c>en.json</c> maps it to itself and is the list of
/// every UI string). A plural string has one key per form, <c>{other form}#{category}</c>. Where the English text is
/// better kept out of the code (a very long text, or one that is awkward to quote in XAML), a hash key
/// <c>T</c> + the first 12 hex digits of the text's SHA-256 stands in for it (<c>{loc:Tr Key=T…}</c>).
/// Footgun: changing an English text changes its key, so every translation of it is lost until
/// <c>tools/localization/catalog.py rename</c> moves it (and marks it for review).
/// </para>
/// <para>
/// <b>Right-to-left languages, left-to-right windows.</b> A catalog whose header says <c>"direction": "rtl"</c>
/// (Hebrew) keeps the left-to-right layout of every window; each of its translations that holds right-to-left text is
/// embedded right to left when it loads, and lines the code joins from pieces go through <see cref="JoinLine(string, IEnumerable{string})"/>,
/// so sentences read in their own order (<see cref="UiLanguage.IsRightToLeft"/> says why). Catalogs never carry the
/// embedding marks themselves.
/// </para>
/// <para>
/// <b>Partial languages.</b> A catalog may lack strings: each one shows in English. A language whose catalog lacks
/// more than <see cref="MaximumMissingShare"/> of the strings is not offered at all (<see cref="Languages"/>), so a
/// half-done contribution can be merged without putting half-English screens in front of people.
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

    /// <summary>
    /// The largest share of the English strings a catalog may lack and still be offered: one in ten. Below that, so
    /// much of the interface would show in English that offering the language would mislead. Never shown to anyone
    /// as a number.
    /// </summary>
    internal const double MaximumMissingShare = 0.1;

    /// <summary>Manifest-resource prefix of the catalogs (the project's root namespace plus the folder).</summary>
    private const string ResourcePrefix = "BetterClipboard.Core.Localization.";

    /// <summary>Manifest-resource prefix of the flags (<c>Localization/Flags/{flag}.svg</c>).</summary>
    private const string FlagResourcePrefix = ResourcePrefix + "Flags.";

    /// <summary>The catalog property that holds the language's names, flag and plural rules instead of a UI string.</summary>
    private const string MetadataProperty = "$language";

    /// <summary>The separator between a plural string's English other form and its category in a key (<c>…#one</c>).</summary>
    private const char PluralSeparator = '#';

    /// <summary>
    /// U+202B RIGHT-TO-LEFT EMBEDDING: opens the right-to-left run a right-to-left language's text is wrapped in
    /// (<see cref="UiLanguage.IsRightToLeft"/>). Invisible; never written into a catalog by hand.
    /// </summary>
    private const char RightToLeftEmbedding = '\u202B';

    /// <summary>U+202C POP DIRECTIONAL FORMATTING: closes the run <see cref="RightToLeftEmbedding"/> opened. Invisible.</summary>
    private const char PopDirectionalFormatting = '\u202C';

    /// <summary>
    /// U+200E LEFT-TO-RIGHT MARK: an invisible strong left-to-right character, put after the signs that end a Latin word
    /// inside right-to-left text so they stay with it (<see cref="KeepTrailingSignsWithTheirWord"/>).
    /// </summary>
    private const char LeftToRightMark = '\u200E';

    /// <summary>
    /// How a catalog is parsed: <c>//</c> and <c>/* */</c> comments are skipped, because <c>en.json</c> ends every row
    /// with its description (<c>"Paste": "Paste", // Card menu item…</c>); everything else is strict JSON, so a stray
    /// trailing comma still fails the load (and the tests) instead of being silently accepted.
    /// </summary>
    /// <remarks>
    /// Shared by the loader and anything else that reads an embedded catalog (the tests do): a reader that forgot the
    /// option would throw on <c>en.json</c>'s first description.
    /// </remarks>
    public static JsonDocumentOptions CatalogParseOptions { get; } = new() { CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Every catalog this build ships, offered or not: English first, then the others by their English name.</summary>
    private static readonly IReadOnlyList<UiLanguage> ShippedLanguages = LoadLanguages();

    /// <summary>The source language (always the first of <see cref="ShippedLanguages"/>).</summary>
    private static readonly UiLanguage EnglishLanguage = ShippedLanguages[0];

    /// <summary>English, then every other language translated enough to offer (<see cref="IsOffered"/>).</summary>
    private static readonly IReadOnlyList<UiLanguage> OfferedLanguages = SelectOffered(ShippedLanguages);

    /// <summary>English text → hash key, for C#'s lookups of the few strings kept under a hash key.</summary>
    private static readonly IReadOnlyDictionary<string, string> HashKeysByEnglish = IndexHashKeys(EnglishLanguage);

    /// <summary>
    /// The matching pattern of every template <see cref="TranslateFormatted(string?, IReadOnlyList{string}, UiLanguage)"/>
    /// has seen, built once per template: the lists are fixed sets of a few dozen strings, so the cache stays small.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TemplateShape> TemplateShapes = new(StringComparer.Ordinal);

    /// <summary>
    /// The last resolution of <see cref="Current"/>, with the inputs it came from. Replaced as a whole (a reference
    /// write is atomic), so a reader on another thread sees either the old or the new triple, never a mix.
    /// </summary>
    private static volatile ResolvedLanguage? resolved;

    /// <summary>
    /// Selected UI language: <see cref="SystemSelection"/> or a catalog tag (<c>en</c>, <c>zh-Hans</c>); an unknown value
    /// follows Windows. Read once at startup from the settings and set before any window resolves its labels, since XAML
    /// reads them only when a window is built: a choice made later in Settings is saved and applied by restarting
    /// (Settings' <em>Restart now</em>), never by changing this while windows are up, which would mix two languages.
    /// </summary>
    public static string Language { get; set; } = SystemSelection;

    /// <summary>
    /// Every language this build offers, English first, then by English name: what the first-start picker and
    /// Settings › Language list. A shipped catalog that lacks too many strings is not among them.
    /// </summary>
    public static IReadOnlyList<UiLanguage> Languages => OfferedLanguages;

    /// <summary>
    /// Every catalog this build ships, offered or not (<see cref="Languages"/>): what the tests check and what
    /// <c>catalog.py</c> reports on. Never what people choose from.
    /// </summary>
    public static IReadOnlyList<UiLanguage> AllShipped => ShippedLanguages;

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

    /// <summary>
    /// The language a saved selection means: an offered catalog chosen explicitly (any letter case), else the one that
    /// matches Windows (<see cref="MatchSystem"/>) — for <see cref="SystemSelection"/>, an empty value, and a tag this
    /// build does not offer (a newer version's language, one not translated enough yet, or a hand edit).
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
    /// The offered language that fits a Windows display language: its own tag or a name it claims
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

        foreach (var language in OfferedLanguages)
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

    /// <summary>Finds an offered language by its tag, ignoring letter case.</summary>
    /// <param name="tag">A catalog tag.</param>
    /// <param name="language">The language, when this build offers it.</param>
    /// <returns>Whether it does.</returns>
    public static bool TryFind(string? tag, [NotNullWhen(true)] out UiLanguage? language)
    {
        language = OfferedLanguages.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
        return language is not null;
    }

    /// <summary>Gets a catalog entry by its key, falling back to English or the key when a translation is unavailable.</summary>
    /// <param name="key">An English text or a hash key, as XAML names it.</param>
    /// <returns>Localized text; never clipboard or user-supplied text.</returns>
    public static string Get(string key) => Get(key, Current);

    /// <summary>
    /// Gets a catalog entry in a given language, whatever <see cref="Language"/> says: the first-start picker words
    /// itself in the language under its cursor before anything is chosen.
    /// </summary>
    /// <param name="key">An English text or a hash key.</param>
    /// <param name="language">The language to read.</param>
    /// <returns>
    /// The language's text, else the English text, else the key itself — which for an English-text key is the English
    /// text (a hash key no catalog has is a bug the tests catch).
    /// </returns>
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
    /// <returns>
    /// The language's text (embedded right to left in a right-to-left language); the literal itself when the language
    /// lacks it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="english"/> or <paramref name="language"/> is <see langword="null"/>.</exception>
    public static string Translate(string english, UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(language);
        if (language.TryGet(english, out string? text))
        {
            return text;
        }

        // A text kept under a hash key (a long one) is found through its key.
        return HashKeysByEnglish.TryGetValue(english, out string? key) && language.TryGet(key, out text) ? text : english;
    }

    /// <summary>
    /// The English text a string shown in the current language was translated from, so that a translated Settings page can
    /// also be searched with the English words (a Chinese user who knows Windows' English "shortcut").
    /// </summary>
    /// <remarks>
    /// Only whole catalog strings are known: a label, a title, a description. Text built from a template with values (a
    /// status line with a path) has no English counterpart here. The reverse table is built once per language, on first
    /// use; when two English strings share one translation, the first in the catalog's order is returned.
    /// </remarks>
    /// <param name="shown">The text as shown (a right-to-left translation with its embedding marks, as the catalog gives it).</param>
    /// <returns>
    /// The English text, or <see langword="null"/> when the interface is English, the text is not a whole catalog string, or
    /// its English is the same text (a product name).
    /// </returns>
    public static string? EnglishOf(string? shown)
    {
        var language = Current;
        if (string.IsNullOrEmpty(shown) || ReferenceEquals(language, EnglishLanguage))
        {
            return null;
        }

        var reverse = ReverseCatalogs.GetOrAdd(language, BuildReverseCatalog);
        return reverse.TryGetValue(shown, out string? english) && !string.Equals(english, shown, StringComparison.Ordinal) ? english : null;
    }

    /// <summary>
    /// Translation → English text, per language (<see cref="EnglishOf"/>). A handful of languages ship, each table is built
    /// once, so the cache never grows past them.
    /// </summary>
    private static readonly ConcurrentDictionary<UiLanguage, IReadOnlyDictionary<string, string>> ReverseCatalogs = new();

    /// <summary>
    /// Builds a language's reverse table: each plain string's translation mapped to its English text (a hash key's English
    /// read from the English catalog). Plural forms are left out: their translations carry placeholders, never shown as
    /// such.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns>The table.</returns>
    private static IReadOnlyDictionary<string, string> BuildReverseCatalog(UiLanguage language)
    {
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in language.Strings)
        {
            if (TryParsePluralKey(key, out _, out _))
            {
                continue;
            }

            string? english = IsHashKey(key) ? (EnglishLanguage.TryGet(key, out string? text) ? text : null) : key;
            if (english is not null)
            {
                reverse.TryAdd(value, english);
            }
        }

        return reverse;
    }

    /// <summary>Translates an interpolated UI template before formatting, so argument values are never translated.</summary>
    /// <param name="message">Application-owned interpolation; arguments can contain paths or user text.</param>
    /// <returns>The translated template formatted using the user's numeric and date culture.</returns>
    public static string Format(FormattableString message) =>
        Finish(string.Format(CultureInfo.CurrentCulture, Translate(message.Format), message.GetArguments()));

    /// <summary>Formats a localized template while preserving a caller-supplied number and date culture.</summary>
    /// <param name="provider">Explicit culture, or null for the current culture.</param>
    /// <param name="message">Application-owned interpolation whose arguments are not translated.</param>
    /// <returns>Localized and formatted UI text.</returns>
    public static string Format(IFormatProvider? provider, FormattableString message) =>
        Finish(string.Format(provider, Translate(message.Format), message.GetArguments()));

    /// <summary>
    /// Joins the pieces of one line that the code puts together instead of a catalog template (a card's caption
    /// "Notepad · 5 min ago · 3 lines", two sentences of the update dialog): a plain join, except in a right-to-left
    /// language, where the whole line is embedded right to left so it reads from its first piece, at the right.
    /// </summary>
    /// <remarks>
    /// Each translated piece is embedded on its own already (<see cref="UiLanguage.IsRightToLeft"/>), which keeps its own
    /// words in order. Without the line's embedding the left-to-right paragraph would still lay the pieces out from the
    /// left, so a Hebrew reader would meet the last piece first. Use it for lines, not for lists inside a sentence: a
    /// list that goes into a template (<c>"In {0}"</c>) is ordered by the template's own embedding.
    /// </remarks>
    /// <param name="separator">What goes between the pieces (<c>" · "</c>, <c>" "</c>).</param>
    /// <param name="parts">The pieces in reading order; joined as <see cref="string.Join(string?, IEnumerable{string?})"/> joins them.</param>
    /// <returns>The line in <see cref="Current"/>'s direction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parts"/> is <see langword="null"/>.</exception>
    public static string JoinLine(string separator, IEnumerable<string> parts) => JoinLine(separator, parts, Current);

    /// <summary><see cref="JoinLine(string, IEnumerable{string})"/> for a given language (the tests' route).</summary>
    /// <param name="separator">What goes between the pieces.</param>
    /// <param name="parts">The pieces in reading order.</param>
    /// <param name="language">The language whose direction the line takes.</param>
    /// <returns>The joined line, embedded right to left when <paramref name="language"/> is written so and the line is not empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parts"/> or <paramref name="language"/> is <see langword="null"/>.</exception>
    public static string JoinLine(string separator, IEnumerable<string> parts, UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(language);
        string line = string.Join(separator, parts);
        return language.IsRightToLeft && line.Length > 0 ? EmbedRightToLeft(line) : line;
    }

    /// <summary>
    /// Translates a text that was already formatted in English from one of the given templates: a reason or a status
    /// that a component words once, in English, for the log, and that the interface also shows (why Everything was not
    /// trusted, how ShareX was found, why an update failed, why a shortcut cannot be used).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first template whose shape matches the whole text wins; the parts its placeholders matched are carried into
    /// the template's translation as they are. A part that is itself exactly one of the templates (a fragment such as
    /// "the release list" inside a sentence) is translated too, through the same list, so nothing else is: a part is
    /// usually a file name, a process name or a number, and must never turn into a catalog word that happens to be
    /// spelled the same.
    /// </para>
    /// <para>
    /// The component keeps its English (its tests and the log read it), and the template list is its contract with the
    /// catalogs: every template needs an entry in every catalog, and every text the component makes must match one of
    /// them, or it shows in English. Each component's tests check both.
    /// </para>
    /// <para>
    /// A placeholder's format (<c>{0:X8}</c>) is not applied again: the matched part is text, and composite formatting
    /// ignores a format for text. Footgun: a template made only of a placeholder ("{0}") would match every text; none
    /// may be.
    /// </para>
    /// </remarks>
    /// <param name="text">The English text; <see langword="null"/> gives an empty string.</param>
    /// <param name="templates">Composite format strings in the order to try ("signed by {0}"); a template without
    /// placeholders matches only itself.</param>
    /// <returns>The translation in <see cref="Current"/>; the text as it is when no template matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="templates"/> is <see langword="null"/>.</exception>
    public static string TranslateFormatted(string? text, IReadOnlyList<string> templates) => TranslateFormatted(text, templates, Current);

    /// <summary>
    /// <see cref="TranslateFormatted(string?, IReadOnlyList{string})"/> into a given language (the tests' route, and
    /// any caller that must not depend on <see cref="Language"/>).
    /// </summary>
    /// <param name="text">The English text; <see langword="null"/> gives an empty string.</param>
    /// <param name="templates">Composite format strings in the order to try.</param>
    /// <param name="language">The language to translate into.</param>
    /// <returns>The translation; the text as it is when no template matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="templates"/> or <paramref name="language"/> is <see langword="null"/>.</exception>
    public static string TranslateFormatted(string? text, IReadOnlyList<string> templates, UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(language);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // English is what the component wrote: nothing to do, and no regular expression runs for it.
        if (ReferenceEquals(language, EnglishLanguage))
        {
            return text;
        }

        foreach (string template in templates)
        {
            var shape = TemplateShapes.GetOrAdd(template, BuildTemplateShape);
            Match match;
            try
            {
                match = shape.Pattern.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                // Lazy groups over a long text with many near-misses: treat as no match rather than stall the UI thread.
                continue;
            }

            if (!match.Success)
            {
                continue;
            }

            var parts = new object[shape.PartCount];
            for (int i = 0; i < parts.Length; i++)
            {
                // A placeholder index the template skipped ({1} without {0}) has no group: an empty part.
                string part = match.Groups[$"p{i}"].Value;

                // A nested fragment of the same list, exactly; anything else (a name, a number) is never translated.
                parts[i] = templates.Contains(part, StringComparer.Ordinal) ? Translate(part, language) : part;
            }

            try
            {
                return Finish(string.Format(CultureInfo.InvariantCulture, Translate(template, language), parts));
            }
            catch (FormatException)
            {
                // A translation whose placeholders do not fit the template's (a catalog bug the tests catch): the English
                // text is better than nothing.
                return text;
            }
        }

        return text;
    }

    /// <summary>
    /// The culture whose day and month names, and time pattern, the panel's dates use (<c>RelativeTimeFormatter</c>): the
    /// regional culture Windows is set to while it belongs to the interface language (<c>en-GB</c> for English keeps its
    /// 24-hour clock, <c>zh-CN</c> for Simplified Chinese), else the interface language's own culture — so an English
    /// Windows with the Chinese interface shows "周三", not "Wed", and a Chinese Windows with the English one shows "Wed".
    /// </summary>
    /// <param name="regional">Windows' formatting culture (usually <see cref="CultureInfo.CurrentUICulture"/>).</param>
    /// <returns>The culture to format dates with.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="regional"/> is <see langword="null"/>.</exception>
    public static CultureInfo DateCulture(CultureInfo regional)
    {
        ArgumentNullException.ThrowIfNull(regional);
        var language = Current;

        // The invariant culture names days and months in English: it belongs to English (callers that pin it, such as
        // the tests, keep their exact output), and to no other language.
        if (regional.Name.Length == 0)
        {
            return ReferenceEquals(language, EnglishLanguage) ? regional : language.Culture;
        }

        // "Belongs to": the interface language's culture is the regional one or one of its parents (zh-CN → zh-Hans,
        // en-GB → en). zh-TW's parents are zh-Hant and zh, so Traditional names never stand in for Simplified ones.
        for (var culture = regional; culture.Name.Length > 0; culture = culture.Parent)
        {
            if (string.Equals(culture.Name, language.Tag, StringComparison.OrdinalIgnoreCase))
            {
                return regional;
            }
        }

        return language.Culture;
    }

    /// <summary>
    /// Words a count with the form its language needs: <c>UiText.Plural(n, $"{n:N0} item", $"{n:N0} items")</c> gives
    /// "1 item" and "5 items" in English, one form in Chinese, and one of four in Russian (CLDR's rules, from each
    /// catalog's header).
    /// </summary>
    /// <param name="count">The count that decides the form (also usually one of the interpolated values).</param>
    /// <param name="one">English for a count of one; it may leave out values the other form shows ("opened once").</param>
    /// <param name="other">
    /// English for every other count. Its template is the plural string's key, and its values are the ones every form
    /// is formatted with, so <paramref name="one"/> must use the same values in the same order.
    /// </param>
    /// <returns>The text in <see cref="Current"/>, formatted with the current culture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="one"/> or <paramref name="other"/> is <see langword="null"/>.</exception>
    public static string Plural(long count, FormattableString one, FormattableString other) =>
        Plural(CultureInfo.CurrentCulture, count, one, other, Current);

    /// <summary><see cref="Plural(long, FormattableString, FormattableString)"/> with an explicit number and date culture.</summary>
    /// <param name="provider">Explicit culture, or <see langword="null"/> for the current culture.</param>
    /// <param name="count">The count that decides the form.</param>
    /// <param name="one">English for a count of one.</param>
    /// <param name="other">English for every other count; its template is the key and its values format every form.</param>
    /// <returns>The text in <see cref="Current"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="one"/> or <paramref name="other"/> is <see langword="null"/>.</exception>
    public static string Plural(IFormatProvider? provider, long count, FormattableString one, FormattableString other) =>
        Plural(provider, count, one, other, Current);

    /// <summary><see cref="Plural(long, FormattableString, FormattableString)"/> into a given language (the tests' route).</summary>
    /// <param name="provider">Explicit culture, or <see langword="null"/> for the current culture.</param>
    /// <param name="count">The count that decides the form.</param>
    /// <param name="one">English for a count of one.</param>
    /// <param name="other">English for every other count; its template is the key and its values format every form.</param>
    /// <param name="language">The language to word it in.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="one"/>, <paramref name="other"/> or <paramref name="language"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A translation whose placeholders do not fit the values (a catalog bug the tests catch) gives the English form
    /// rather than an exception in the middle of drawing a window.
    /// </remarks>
    public static string Plural(IFormatProvider? provider, long count, FormattableString one, FormattableString other, UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(language);
        object?[] values = other.GetArguments();
        string english = EnglishLanguage.Plurals.Select(count) == PluralCategory.One ? one.Format : other.Format;
        string template = PluralTemplate(count, one.Format, other.Format, language);
        try
        {
            return Finish(string.Format(provider, template, values));
        }
        catch (FormatException) when (!ReferenceEquals(template, english))
        {
            return string.Format(provider, english, values);
        }
    }

    /// <summary>
    /// The template a count takes in a language: the catalog's form for the count's category
    /// (<see cref="PluralKey"/>), else English's own form (a missing form falls back like a missing string).
    /// </summary>
    /// <param name="count">The count.</param>
    /// <param name="one">English's template for a count of one.</param>
    /// <param name="other">English's template for every other count: the plural string's key.</param>
    /// <param name="language">The language.</param>
    /// <returns>A composite format string.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static string PluralTemplate(long count, string one, string other, UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(language);

        // English's forms are the ones in the code: the catalog only lists them for translators and the tests.
        if (!ReferenceEquals(language, EnglishLanguage)
            && language.TryGet(PluralKey(other, language.Plurals.Select(count)), out string? text))
        {
            return text;
        }

        return EnglishLanguage.Plurals.Select(count) == PluralCategory.One ? one : other;
    }

    /// <summary>The catalog key of one form of a plural string: its English other form, <c>#</c>, the category's name.</summary>
    /// <param name="other">The English other form's template ("{0:N0} items").</param>
    /// <param name="category">The form's category.</param>
    /// <returns>The key ("{0:N0} items#one").</returns>
    public static string PluralKey(string other, PluralCategory category) => other + PluralSeparator + PluralRules.Name(category);

    /// <summary>
    /// Splits a catalog key into a plural string's English other form and its category, when it is a plural form
    /// (it ends in <c>#zero</c>, <c>#one</c>, <c>#two</c>, <c>#few</c>, <c>#many</c> or <c>#other</c>).
    /// </summary>
    /// <param name="key">A catalog key.</param>
    /// <param name="other">The plural string's key (its English other form).</param>
    /// <param name="category">The form's category.</param>
    /// <returns>Whether the key is a plural form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public static bool TryParsePluralKey(string key, [NotNullWhen(true)] out string? other, out PluralCategory category)
    {
        ArgumentNullException.ThrowIfNull(key);
        int separator = key.LastIndexOf(PluralSeparator);
        if (separator > 0 && PluralRules.TryParseCategory(key[(separator + 1)..], out category))
        {
            other = key[..separator];
            return true;
        }

        other = null;
        category = PluralCategory.Other;
        return false;
    }

    /// <summary>Whether a key is a hash key (<c>T</c> + 12 lowercase hex digits) rather than an English text.</summary>
    /// <param name="key">A catalog key.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsHashKey(string key) =>
        key is { Length: 13 } && key[0] == 'T' && key.AsSpan(1).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>The hash key of an English text: <c>T</c> + the first 12 hex digits of the SHA-256 of its UTF-8.</summary>
    /// <param name="english">The English text.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="english"/> is <see langword="null"/>.</exception>
    public static string HashKey(string english)
    {
        ArgumentNullException.ThrowIfNull(english);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(english));
        return "T" + Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>
    /// How many of the English catalog's strings a language lacks: each plain string counts once, and so does each
    /// plural string, which a language lacks unless it has every form its own rules need. What decides whether the
    /// language is offered (<see cref="MaximumMissingShare"/>), and what the tests report.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns>The strings it lacks, and how many strings there are.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="language"/> is <see langword="null"/>.</exception>
    public static (int Missing, int Total) Coverage(UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return Coverage(language, EnglishLanguage);
    }

    /// <summary>
    /// <see cref="Coverage(UiLanguage)"/> against a given source catalog: the loader's route (it runs before
    /// <see cref="English"/> exists) and the tests' (with catalogs of their own).
    /// </summary>
    /// <param name="language">The language.</param>
    /// <param name="english">The source catalog.</param>
    /// <returns>The strings it lacks, and how many strings there are.</returns>
    internal static (int Missing, int Total) Coverage(UiLanguage language, UiLanguage english)
    {
        int missing = 0;
        int total = 0;
        var pluralStrings = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in english.Strings.Keys)
        {
            if (TryParsePluralKey(key, out string? other, out _))
            {
                // A plural string counts once however many forms English has.
                if (pluralStrings.Add(other))
                {
                    total++;
                    if (!language.Plurals.Categories.All(category => language.TryGet(PluralKey(other, category), out _)))
                    {
                        missing++;
                    }
                }

                continue;
            }

            total++;
            if (!language.TryGet(key, out _))
            {
                missing++;
            }
        }

        return (missing, total);
    }

    /// <summary>Whether a language is translated enough to offer (<see cref="MaximumMissingShare"/>); English always is.</summary>
    /// <param name="language">The language.</param>
    /// <param name="english">The source catalog.</param>
    /// <returns>Whether to offer it.</returns>
    internal static bool IsOffered(UiLanguage language, UiLanguage english)
    {
        if (ReferenceEquals(language, english))
        {
            return true;
        }

        var (missing, total) = Coverage(language, english);
        return missing <= total * MaximumMissingShare;
    }

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
    /// The languages to offer: English, then every catalog translated enough (<see cref="IsOffered"/>). Each one left
    /// out is logged once with its counts, so "why is my language not in the list" has an answer.
    /// </summary>
    /// <param name="shipped">Every shipped catalog, English first.</param>
    /// <returns>The offered languages in the same order.</returns>
    private static IReadOnlyList<UiLanguage> SelectOffered(IReadOnlyList<UiLanguage> shipped)
    {
        var english = shipped[0];
        var offered = new List<UiLanguage>(shipped.Count);
        foreach (var language in shipped)
        {
            if (IsOffered(language, english))
            {
                offered.Add(language);
                continue;
            }

            var (missing, total) = Coverage(language, english);
            AppLog.Info($"The {language.Tag} interface language is not offered yet: its catalog lacks {missing:N0} of {total:N0} strings.");
        }

        return offered;
    }

    /// <summary>
    /// Parses one embedded catalog: the <c>"$language"</c> header (names, flag, extra Windows names, plural rules) and
    /// the UI strings. Other <c>$</c> properties (<c>"$review"</c>, which <c>catalog.py</c> keeps, and later headers) are
    /// skipped, so an older build reads a newer catalog. So are <c>//</c> comments: <c>en.json</c> ends every row with
    /// the string's description for translators (where it shows, what each placeholder holds), which the app never
    /// reads.
    /// </summary>
    /// <param name="assembly">This assembly.</param>
    /// <param name="resource">The catalog's manifest-resource name.</param>
    /// <param name="tag">The catalog's tag (its file name without <c>.json</c>).</param>
    /// <returns>The language.</returns>
    /// <exception cref="InvalidDataException">
    /// The header is missing or incomplete, its plural rules do not parse, a value is not a string, or the flag is not
    /// embedded.
    /// </exception>
    /// <exception cref="JsonException">The catalog is not valid JSON.</exception>
    private static UiLanguage ReadCatalog(Assembly assembly, string resource, string tag)
    {
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Missing UI catalog: {tag}");

        // Skip, not Allow: a comment is a translator's description, never a value, so the element tree must not carry it.
        // Trailing commas stay an error, like any other malformed catalog.
        using var document = JsonDocument.Parse(stream, CatalogParseOptions);
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

        var plurals = ReadPlurals(header, tag);
        bool rightToLeft = ReadDirection(header, tag);
        if (rightToLeft)
        {
            // Embedded once here, so every route to a string (XAML's labels, Translate, Format, Plural, the templates of
            // TranslateFormatted) hands out the same, already embedded text. Values without right-to-left text (the list
            // separator, date patterns, a template of placeholders only) stay as they are: embedding ", " would reorder
            // the English items a list joins with it.
            foreach (string key in strings.Keys.ToList())
            {
                if (HoldsRightToLeftText(strings[key]))
                {
                    strings[key] = EmbedRightToLeft(strings[key]);
                }
            }
        }

        using Stream flagStream = assembly.GetManifestResourceStream(FlagResourcePrefix + flag + ".svg")
            ?? throw new InvalidDataException($"The {tag} catalog's flag {flag}.svg is not embedded.");
        using var buffer = new MemoryStream();
        flagStream.CopyTo(buffer);
        return new UiLanguage(tag, name, englishName, flag, buffer.ToArray(), alsoFor, plurals, strings, rightToLeft);
    }

    /// <summary>
    /// Reads the header's <c>"direction"</c>: <c>"rtl"</c> for a language written right to left, <c>"ltr"</c> or nothing
    /// for every other. Anything else is refused rather than read as left to right, because a misspelt <c>"rlt"</c> would
    /// otherwise put every Hebrew sentence on screen in scrambled order without a single error.
    /// </summary>
    /// <param name="header">The <c>"$language"</c> object.</param>
    /// <param name="tag">The catalog's tag, for the message.</param>
    /// <returns>Whether the language is written right to left.</returns>
    /// <exception cref="InvalidDataException"><c>"direction"</c> is there but is neither <c>"ltr"</c> nor <c>"rtl"</c>.</exception>
    internal static bool ReadDirection(JsonElement header, string tag)
    {
        if (!header.TryGetProperty("direction", out var direction))
        {
            return false;
        }

        return (direction.ValueKind == JsonValueKind.String ? direction.GetString() : null) switch
        {
            "ltr" => false,
            "rtl" => true,
            _ => throw new InvalidDataException($"The {tag} catalog's \"direction\" must be \"ltr\" or \"rtl\"."),
        };
    }

    /// <summary>
    /// Whether a text holds a strongly right-to-left character: a letter of a right-to-left script (Hebrew, Arabic,
    /// Syriac, Thaana, N'Ko and the others of U+0590–U+08FF, their presentation forms, and the right-to-left blocks beyond
    /// the BMP), or a right-to-left mark (U+200F) or Arabic letter mark (U+061C).
    /// </summary>
    /// <remarks>
    /// The marks count so that a catalog can ask for the embedding where a template holds no letter of its own: Hebrew's
    /// <c>"{0} ({1})"</c> starts with U+200F, so the phrase and its parenthesis are ordered right to left as one line.
    /// Arabic-Indic digits are not strong (they take the direction around them) and do not count.
    /// </remarks>
    /// <param name="text">A catalog value.</param>
    /// <returns><see langword="true"/> when the text has one such character.</returns>
    internal static bool HoldsRightToLeftText(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value is 0x200F or 0x061C)
            {
                return true;
            }

            if (Rune.IsLetter(rune)
                && value is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
                    or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Wraps a text in a right-to-left embedding: U+202B RIGHT-TO-LEFT EMBEDDING before it and U+202C POP DIRECTIONAL
    /// FORMATTING after it, both invisible. Inside, the bidirectional algorithm orders the text right to left as a whole,
    /// whatever the left-to-right paragraph around it; the two marks always come as a pair, so the embedding never leaks
    /// into the text that follows.
    /// </summary>
    /// <remarks>
    /// An embedding, not an isolate (U+2067 … U+2069): every text engine Windows has honors embeddings, and the panel's
    /// strings never need the isolate's one advantage (shielding the neighbors from the inside).
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <returns>The embedded text.</returns>
    private static string EmbedRightToLeft(string text) =>
        RightToLeftEmbedding + KeepTrailingSignsWithTheirWord(text) + PopDirectionalFormatting;

    /// <summary>
    /// The last step of every text <see cref="Format(FormattableString)"/>, <see cref="Plural(IFormatProvider?, long, FormattableString, FormattableString, UiLanguage)"/>
    /// and <see cref="TranslateFormatted(string?, IReadOnlyList{string}, UiLanguage)"/> put together: a right-to-left
    /// template filled with values gets <see cref="KeepTrailingSignsWithTheirWord"/> for the values (an app's name such as
    /// "Notepad++" goes in only now, after the catalog's load); every other text comes back as it is.
    /// </summary>
    /// <param name="formatted">The formatted text.</param>
    /// <returns>The text, with left-to-right marks only when it holds a right-to-left embedding.</returns>
    private static string Finish(string formatted) =>
        formatted.Contains(RightToLeftEmbedding) ? KeepTrailingSignsWithTheirWord(formatted) : formatted;

    /// <summary>
    /// Puts U+200E LEFT-TO-RIGHT MARK after the signs that end a Latin word ("Notepad++", "C#") in text bound for a
    /// right-to-left embedding, so those signs stay with their word instead of jumping to its other side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under UAX #9 a run of neutral signs between a left-to-right word and right-to-left text (or the embedding's end)
    /// takes the embedding's direction: "Notepad++" next to Hebrew showed as "++Notepad". Found on 2026-11-09 in the
    /// Hebrew panel's captions and in 26 places of the Hebrew catalog ("קבצים שנפתחו ב-Notepad++"). The mark is an
    /// invisible strong left-to-right character, so the signs then sit between two left-to-right characters and keep
    /// the word's direction.
    /// </para>
    /// <para>
    /// Only "+" and "#" right after a Latin letter are touched. A sign followed by a letter or digit ("Win+V", "C++17") is
    /// inside its word already, and one after a digit ("60+") is left to the algorithm on purpose: a right-to-left reader
    /// expects "60+" shown as "+60". A mark already there (a text finished twice) is not doubled.
    /// </para>
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <returns>The text with a mark after each such run of signs; the same instance when there is none.</returns>
    internal static string KeepTrailingSignsWithTheirWord(string text)
    {
        StringBuilder? result = null;
        int copied = 0;
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] is not ('+' or '#') || !char.IsAsciiLetter(text[i - 1]))
            {
                continue;
            }

            int end = i;
            while (end < text.Length && text[end] is '+' or '#')
            {
                end++;
            }

            // Followed by a letter, a digit or a mark: the signs belong to a longer word, or are kept already.
            if (end == text.Length || !(char.IsAsciiLetterOrDigit(text[end]) || text[end] == LeftToRightMark))
            {
                result ??= new StringBuilder(text.Length + 4);
                result.Append(text, copied, end - copied).Append(LeftToRightMark);
                copied = end;
            }

            i = end - 1;
        }

        return result is null ? text : result.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// Reads the header's <c>"plurals"</c>: category name → CLDR rule. A header without it gets
    /// <see cref="PluralRules.OtherOnly"/> (the tests require it in every shipped catalog, so a forgotten rule is caught
    /// before Russian shows "21 элементов").
    /// </summary>
    /// <param name="header">The <c>"$language"</c> object.</param>
    /// <param name="tag">The catalog's tag, for the message.</param>
    /// <returns>The rules.</returns>
    /// <exception cref="InvalidDataException"><c>"plurals"</c> is not an object of strings, or a rule does not parse.</exception>
    private static PluralRules ReadPlurals(JsonElement header, string tag)
    {
        if (!header.TryGetProperty("plurals", out var plurals))
        {
            return PluralRules.OtherOnly;
        }

        if (plurals.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {tag} catalog's \"plurals\" is not an object.");
        }

        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in plurals.EnumerateObject())
        {
            rules[rule.Name] = rule.Value.ValueKind == JsonValueKind.String
                ? rule.Value.GetString()!
                : throw new InvalidDataException($"The {tag} catalog's plural rule \"{rule.Name}\" is not a string.");
        }

        try
        {
            return PluralRules.Parse(rules);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"The {tag} catalog's plural rules do not parse: {ex.Message}", ex);
        }
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
    /// Maps the English text of each hash-keyed string back to its key. Two hash keys with one English text cannot be
    /// told apart by C#'s lookups, so the first wins instead of throwing (which would break every lookup of the process);
    /// the tests forbid it.
    /// </summary>
    /// <param name="english">The English catalog.</param>
    /// <returns>Hash key by English text.</returns>
    private static IReadOnlyDictionary<string, string> IndexHashKeys(UiLanguage english)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, text) in english.Strings.Where(p => IsHashKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
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

    /// <summary>
    /// The matching pattern of one composite format string: its literal text escaped, each placeholder a lazy group named
    /// <c>p{index}</c> (a repeated index must match the same text again), anchored at both ends. A brace that is not a
    /// well-formed placeholder is matched as a literal character, like the text the template would have produced.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <returns>Its pattern and how many parts a match yields (the highest index plus one).</returns>
    private static TemplateShape BuildTemplateShape(string template)
    {
        var pattern = new StringBuilder("^");
        var literal = new StringBuilder();
        var seen = new HashSet<int>();
        int partCount = 0;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                // "{{" and "}}" are a literal brace in a composite format.
                literal.Append(c);
                i++;
                continue;
            }

            int close = c == '{' ? template.IndexOf('}', i + 1) : -1;
            if (close > i)
            {
                var spec = template.AsSpan(i + 1, close - i - 1);
                int end = spec.IndexOfAny(',', ':');
                if (int.TryParse(end < 0 ? spec : spec[..end], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                {
                    pattern.Append(Regex.Escape(literal.ToString()));
                    literal.Clear();
                    pattern.Append(seen.Add(index) ? $"(?<p{index}>.*?)" : $"\\k<p{index}>");
                    partCount = Math.Max(partCount, index + 1);
                    i = close;
                    continue;
                }
            }

            literal.Append(c);
        }

        pattern.Append(Regex.Escape(literal.ToString())).Append('$');

        // Singleline: a reason may hold a line break. The timeout bounds the lazy groups on a hostile text (the matched
        // parts can come from outside: an exception message, a signer's name).
        var regex = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return new TemplateShape(regex, partCount);
    }

    /// <summary>One template's matching pattern (<see cref="BuildTemplateShape"/>).</summary>
    /// <param name="Pattern">The anchored pattern with a group per placeholder.</param>
    /// <param name="PartCount">How many parts a match yields: the template's highest placeholder index plus one.</param>
    private sealed record TemplateShape(Regex Pattern, int PartCount);

    /// <summary>One resolution of <see cref="Current"/> and the inputs it came from.</summary>
    /// <param name="Selection">The <see cref="Language"/> value it was made for.</param>
    /// <param name="System">The display-language name it was made for.</param>
    /// <param name="Language">The resolved language.</param>
    private sealed record ResolvedLanguage(string Selection, string System, UiLanguage Language);
}
