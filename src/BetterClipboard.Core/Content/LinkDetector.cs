namespace BetterClipboard.Core.Content;

/// <summary>
/// Decides whether a clip is "just a link" so it gets the link card and an "open" action.
/// </summary>
/// <remarks>
/// Deliberately strict: only one token, no whitespace, and only web-ish schemes. <see cref="Uri"/> also
/// accepts <c>C:\path</c> as an absolute <c>file:</c> URI and <c>foo:bar</c> as a custom scheme, both of
/// which would make ordinary text look like links — so schemes are allow-listed.
/// </remarks>
public static class LinkDetector
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeFtp, Uri.UriSchemeMailto,
    };

    /// <summary>
    /// Tries to interpret the whole text as a single URL.
    /// </summary>
    /// <param name="text">Candidate text (surrounding whitespace is ignored).</param>
    /// <param name="uri">The parsed URI; bare <c>www.</c> hosts are normalized to <c>https://</c>.</param>
    /// <returns><see langword="true"/> when the text is exactly one allowed URL.</returns>
    public static bool TryGetLink(string? text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
        {
            return false;
        }

        var trimmed = text.Trim();
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || !AllowedSchemes.Contains(parsed.Scheme))
        {
            return false;
        }

        // "http://" alone parses on some runtimes with an empty host; a link without a host is not a link.
        if (parsed.Scheme != Uri.UriSchemeMailto && string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
