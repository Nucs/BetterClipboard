using System.Text.RegularExpressions;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// One release's notes in every language it carries: the English that every reader sees, and the translations hidden
/// in the same body (<see cref="ReleaseNotesLanguages"/>).
/// </summary>
/// <param name="English">
/// The body as published, drawn as it is: <see cref="MarkdownParser"/> leaves the hidden translations out by itself,
/// exactly as every released version does, so the English a reader sees here is the English everyone sees.
/// </param>
/// <param name="Translations">
/// Each translation's Markdown by its catalog tag (<c>zh-Hans</c>), keys compared without regard to case. Empty for a
/// release without translations, which is every release before 0.2.12 and any later one published without them.
/// </param>
/// <param name="Pages">
/// The web page of each translation, by tag: the address the English's language line links to (the translation as a file
/// in the repository, at the release's tag). Empty when the English has no such link; a translation without a page is
/// still shown, only "View on GitHub" then opens the English page.
/// </param>
public sealed record ReleaseNotesVariants(
    string English,
    IReadOnlyDictionary<string, string> Translations,
    IReadOnlyDictionary<string, Uri> Pages)
{
    /// <summary>Whether the release carries at least one translation, so that its language line can switch anything.</summary>
    public bool HasTranslations => Translations.Count > 0;
}

/// <summary>
/// Reads the translations a release body carries, chooses the language a release is shown in, and tells which language
/// a click on a release's language line asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The format</b> (written by <c>tools/release/release_notes.py</c> when a release is published; CLAUDE.md §2.67). The
/// English notes are the body as before, with a language line as their first row (<c>**ENG** / [简体中文](…)</c>), whose
/// link opens the translation as a file in the repository. Each translation follows in a block of its own: a line
/// <c>&lt;!-- release-notes zh-Hans</c>, the translation as its page shows it (its own language line first,
/// <c>[ENG](release page) / **简体中文**</c>, then the notes and the footer), and a line <c>--&gt;</c>.
/// </para>
/// <para>
/// <b>Why a comment.</b> Every version with the update dialog (0.2.6 and later) draws the whole body and skips a comment
/// from a line that starts with <c>&lt;!--</c> to the next line that contains <c>--&gt;</c>, and GitHub's release page
/// drops it too (measured with <c>tools/probes/probe_release_notes.py</c>). So copies installed before this one and the
/// release page show the English with its language line as links (a click opens the translation's page in the browser),
/// while this version swaps the English for a block in place, with no request of its own.
/// </para>
/// <para>
/// <b>Read as the parser reads.</b> A block is found only where <see cref="MarkdownParser"/> sees a comment: at the start
/// of a line, outside a code fence (its own fence rules decide). Whatever the parser hides stays hidden whatever this
/// class makes of it: a block that is not closed by a line of nothing but <c>--&gt;</c>, whose opening line names no
/// valid tag, or that holds nothing, is no translation, and the English never changes. The text comes from the network:
/// at most <see cref="MaxTranslations"/> blocks are kept, the body is cut by <see cref="GitHubReleases.MaxNotesLength"/>
/// before it gets here, and every pattern is anchored and bounded.
/// </para>
/// </remarks>
public static partial class ReleaseNotesLanguages
{
    /// <summary>The tag of the English notes: the visible body, never a block.</summary>
    public const string EnglishTag = "en";

    /// <summary>
    /// The most translations kept per release. The app has five interface languages; the rest of a body that holds more
    /// blocks is ignored, so a release cannot make the dialog keep an unbounded number of texts.
    /// </summary>
    public const int MaxTranslations = 16;

    /// <summary>
    /// Splits a release body into its English and its translations.
    /// </summary>
    /// <param name="notes">The release's notes as the feed gave them (<see cref="ReleaseInfo.Notes"/>); may be <see langword="null"/>.</param>
    /// <returns>
    /// The variants: <see cref="ReleaseNotesVariants.English"/> is <paramref name="notes"/> itself (empty for none), the
    /// translations those blocks hold, and the pages the English's language line links to for them.
    /// </returns>
    public static ReleaseNotesVariants Split(string? notes)
    {
        var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pages = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(notes))
        {
            return new ReleaseNotesVariants(string.Empty, translations, pages);
        }

        var lines = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        char fenceChar = '\0';
        int fenceLength = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (fenceLength > 0)
            {
                // Inside a code fence a comment is code, as the parser draws it.
                if (MarkdownParser.IsClosingFence(trimmed, fenceChar, fenceLength))
                {
                    fenceLength = 0;
                }

                continue;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (MarkdownParser.TryReadFence(trimmed, out char openChar, out int openLength))
            {
                fenceChar = openChar;
                fenceLength = openLength;
                continue;
            }

            if (!trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            // The parser hides from this line to the first line that contains "-->", this one included; so does this
            // loop, whether or not the span turns out to be a translation.
            int end = i;
            while (end < lines.Length && !lines[end].Contains("-->", StringComparison.Ordinal))
            {
                end++;
            }

            var opening = OpeningLine().Match(trimmed);
            if (opening.Success && end > i && end < lines.Length && lines[end].Trim() == "-->" && translations.Count < MaxTranslations)
            {
                string tag = opening.Groups["tag"].Value;
                string text = string.Join('\n', lines, i + 1, end - i - 1).Trim();

                // The first block of a language wins; English is the visible body, never a block.
                if (text.Length > 0 && !tag.Equals(EnglishTag, StringComparison.OrdinalIgnoreCase) && !translations.ContainsKey(tag))
                {
                    translations[tag] = text;
                }
            }

            i = end;
        }

        if (translations.Count > 0)
        {
            foreach (Match link in PageLink().Matches(notes))
            {
                string tag = link.Groups["tag"].Value;
                if (translations.ContainsKey(tag) && !pages.ContainsKey(tag) &&
                    Uri.TryCreate(link.Groups["url"].Value, UriKind.Absolute, out var page))
                {
                    pages[tag] = page;
                }
            }
        }

        return new ReleaseNotesVariants(notes, translations, pages);
    }

    /// <summary>
    /// The language a release is shown in when a reader wants <paramref name="wanted"/>: that translation when the release
    /// has it, else English.
    /// </summary>
    /// <param name="variants">The release's notes, split.</param>
    /// <param name="wanted">
    /// A catalog tag (the interface language, or the one picked from a language line); <see langword="null"/> or empty
    /// means English. Matched without regard to case.
    /// </param>
    /// <returns><see cref="EnglishTag"/>, or the translation's tag as the release spells it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variants"/> is <see langword="null"/>.</exception>
    public static string ShownLanguage(ReleaseNotesVariants variants, string? wanted)
    {
        ArgumentNullException.ThrowIfNull(variants);
        return string.IsNullOrEmpty(wanted) ? EnglishTag : CanonicalTag(variants, wanted) ?? EnglishTag;
    }

    /// <summary>The Markdown to draw for a release in a language: its translation, or the English when it has none.</summary>
    /// <param name="variants">The release's notes, split.</param>
    /// <param name="language">The language, as <see cref="ShownLanguage"/> answered it.</param>
    /// <returns>The text; the English body as published for <see cref="EnglishTag"/> or a language the release lacks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variants"/> is <see langword="null"/>.</exception>
    public static string TextFor(ReleaseNotesVariants variants, string? language)
    {
        ArgumentNullException.ThrowIfNull(variants);
        return language is not null && CanonicalTag(variants, language) is { } tag ? variants.Translations[tag] : variants.English;
    }

    /// <summary>
    /// Which language a link in a release's notes switches to: the link back to the release's page (a translation's
    /// <c>ENG</c>) means English, a link to one of the release's own translation pages (the English's <c>简体中文</c>)
    /// means that translation. Any other link is an ordinary link.
    /// </summary>
    /// <remarks>
    /// Only a release that carries translations has a language line to switch with: in any other, a link to its own page
    /// stays a link and opens the browser, as before.
    /// </remarks>
    /// <param name="link">The address the reader clicked.</param>
    /// <param name="variants">The notes of the release the link is in, split.</param>
    /// <param name="releasePage">The release's web page (<see cref="ReleaseInfo.PageUrl"/>), or <see langword="null"/> when unknown.</param>
    /// <returns>
    /// <see cref="EnglishTag"/>, the translation's tag as the release spells it, or <see langword="null"/> for a link that
    /// switches nothing and is to be opened as one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="link"/> or <paramref name="variants"/> is <see langword="null"/>.</exception>
    public static string? LanguageOfLink(Uri link, ReleaseNotesVariants variants, Uri? releasePage)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(variants);
        if (!variants.HasTranslations || !link.IsAbsoluteUri)
        {
            return null;
        }

        if (releasePage is not null && SameAddress(link, releasePage))
        {
            return EnglishTag;
        }

        if (!string.Equals(link.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var page = PagePath().Match(link.AbsolutePath);
        return page.Success ? CanonicalTag(variants, page.Groups["tag"].Value) : null;
    }

    /// <summary>
    /// The web page of a release in a language: the translation's page when the English links to one, else the release's
    /// own page. "View on GitHub" opens it, so a reader who reads the notes in Chinese lands on the Chinese page.
    /// </summary>
    /// <param name="variants">The release's notes, split.</param>
    /// <param name="language">The language the notes are shown in (<see cref="ShownLanguage"/>).</param>
    /// <param name="releasePage">The release's web page, or <see langword="null"/> when unknown.</param>
    /// <returns>The page, or <see langword="null"/> when neither is known.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variants"/> is <see langword="null"/>.</exception>
    public static Uri? PageFor(ReleaseNotesVariants variants, string? language, Uri? releasePage)
    {
        ArgumentNullException.ThrowIfNull(variants);
        return language is not null && CanonicalTag(variants, language) is { } tag && variants.Pages.TryGetValue(tag, out var page)
            ? page
            : releasePage;
    }

    /// <summary>The release's own spelling of a tag it has a translation for.</summary>
    /// <param name="variants">The release's notes, split.</param>
    /// <param name="tag">The tag asked for, in any case.</param>
    /// <returns>The key in <see cref="ReleaseNotesVariants.Translations"/>, or <see langword="null"/> when there is no such translation.</returns>
    private static string? CanonicalTag(ReleaseNotesVariants variants, string tag)
    {
        // A tag only ever names a translation; "en" is the visible body and has no key.
        foreach (var key in variants.Translations.Keys)
        {
            if (string.Equals(key, tag, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether two web addresses name the same page: scheme, host and path, ignoring case, a trailing slash, a query and a
    /// fragment (GitHub serves a release page for each spelling).
    /// </summary>
    /// <param name="a">One address.</param>
    /// <param name="b">The other.</param>
    /// <returns><see langword="true"/> for the same page.</returns>
    private static bool SameAddress(Uri a, Uri b) =>
        a.IsAbsoluteUri && b.IsAbsoluteUri &&
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first line of a translation block, trimmed: the marker and the language's catalog tag (<c>zh-Hans</c>, <c>ru</c>),
    /// nothing else. The same pattern as the composer's and the probe's.
    /// </summary>
    [GeneratedRegex(@"^<!-- release-notes (?<tag>[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex OpeningLine();

    /// <summary>
    /// A Markdown link to a translation's page: a file <c>docs/release-notes/&lt;version&gt;/&lt;tag&gt;.md</c> in a GitHub
    /// repository. Bounded: no part of it can match across a blank, a parenthesis or a line.
    /// </summary>
    [GeneratedRegex(@"\]\((?<url>https://github\.com/[^\s()<>]{1,200}/docs/release-notes/[^\s()<>/]{1,40}/(?<tag>[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,3})\.md)\)", RegexOptions.CultureInvariant)]
    private static partial Regex PageLink();

    /// <summary>
    /// The path of a translation's page on GitHub: <c>/&lt;owner&gt;/&lt;repository&gt;/blob/&lt;ref&gt;/docs/release-notes/&lt;version&gt;/&lt;tag&gt;.md</c>.
    /// </summary>
    [GeneratedRegex(@"^/[^/]+/[^/]+/blob/[^/]+/docs/release-notes/[^/]+/(?<tag>[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,3})\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex PagePath();
}
