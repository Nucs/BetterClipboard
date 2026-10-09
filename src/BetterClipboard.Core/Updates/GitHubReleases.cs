using System.Globalization;
using System.Text.Json;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// Reads the answer of GitHub's "list releases" API (<c>GET /repos/{owner}/{repo}/releases</c>) into
/// <see cref="ReleaseInfo"/> values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defensive by design:</b> the text comes from the network. A member of the wrong type, a tag that is not a version, a
/// draft or an address that is not http(s) drops that release or file instead of failing the whole answer; only text that
/// is not JSON at all, or not a list of releases, throws. Every count and every text length is bounded, so a hostile or
/// broken answer cannot make the app hold megabytes of "release notes".
/// </para>
/// <para>
/// The shape used (checked against the live API on 2026-10-09): each release has <c>tag_name</c>, <c>name</c>,
/// <c>body</c> (Markdown), <c>html_url</c>, <c>published_at</c>, <c>draft</c>, <c>prerelease</c> and <c>assets</c>; each
/// asset has <c>name</c>, <c>size</c>, <c>browser_download_url</c> and <c>digest</c> (<c>sha256:</c> + 64 hex digits,
/// which GitHub computes at upload).
/// </para>
/// </remarks>
public static class GitHubReleases
{
    /// <summary>The most releases read from one answer (the newest come first in the API's order).</summary>
    public const int MaxReleases = 100;

    /// <summary>The most files read per release.</summary>
    public const int MaxAssets = 100;

    /// <summary>The longest release notes kept, in characters; longer notes are cut here (BetterClipboard's own are about 7,000).</summary>
    public const int MaxNotesLength = 200_000;

    /// <summary>The longest tag, title or file name kept, in characters.</summary>
    private const int MaxNameLength = 256;

    /// <summary>The longest address kept, in characters.</summary>
    private const int MaxUrlLength = 2048;

    /// <summary>The UTF-8 byte order mark (EF BB BF), written as bytes so that no invisible character sits in this source.</summary>
    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Parses the API's answer: a JSON array of releases (a single release object is accepted too).
    /// </summary>
    /// <param name="json">The answer's bytes (UTF-8, with or without a byte order mark).</param>
    /// <returns>
    /// The releases that are published and carry a version tag, in the answer's order (GitHub lists the newest first).
    /// Drafts and releases whose tag is not a version are left out. May be empty.
    /// </returns>
    /// <exception cref="FormatException">The bytes are not JSON, or the JSON is neither an array nor an object.</exception>
    public static IReadOnlyList<ReleaseInfo> Parse(ReadOnlySpan<byte> json)
    {
        // Utf8JsonReader refuses a byte order mark; a cached answer written by another tool may carry one.
        if (json.StartsWith(Utf8ByteOrderMark))
        {
            json = json[3..];
        }

        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 });
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The release list is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var releases = new List<ReleaseInfo>();
            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var element in root.EnumerateArray())
                    {
                        if (releases.Count >= MaxReleases)
                        {
                            break;
                        }

                        if (TryReadRelease(element) is { } release)
                        {
                            releases.Add(release);
                        }
                    }

                    break;
                case JsonValueKind.Object:
                    if (TryReadRelease(root) is { } single)
                    {
                        releases.Add(single);
                    }

                    break;
                default:
                    throw new FormatException("The release list is neither a JSON array nor a JSON object.");
            }

            return releases;
        }
    }

    /// <summary>Reads one release, or returns <see langword="null"/> when it must be left out (see <see cref="Parse"/>).</summary>
    /// <param name="element">A JSON value from the answer.</param>
    /// <returns>The release, or <see langword="null"/>.</returns>
    private static ReleaseInfo? TryReadRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || ReadBoolean(element, "draft"))
        {
            return null;
        }

        var tag = ReadString(element, "tag_name", MaxNameLength);
        if (tag is null || !SemanticVersion.TryParse(tag, out var version))
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        if (element.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (assets.Count >= MaxAssets)
                {
                    break;
                }

                if (TryReadAsset(item) is { } asset)
                {
                    assets.Add(asset);
                }
            }
        }

        return new ReleaseInfo(
            version,
            tag,
            ReadString(element, "name", MaxNameLength) ?? string.Empty,
            ReadString(element, "body", MaxNotesLength) ?? string.Empty,
            ReadHttpUrl(element, "html_url"),
            ReadDate(element, "published_at"),
            ReadBoolean(element, "prerelease") || version.IsPrerelease,
            assets);
    }

    /// <summary>Reads one attached file, or returns <see langword="null"/> when it has no name or no usable address.</summary>
    /// <param name="element">A JSON value from a release's <c>assets</c>.</param>
    /// <returns>The file, or <see langword="null"/>.</returns>
    private static ReleaseAsset? TryReadAsset(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = ReadString(element, "name", MaxNameLength);
        var url = ReadHttpUrl(element, "browser_download_url");
        if (string.IsNullOrWhiteSpace(name) || url is null)
        {
            return null;
        }

        long size = element.TryGetProperty("size", out var sizeValue) && sizeValue.ValueKind == JsonValueKind.Number &&
                    sizeValue.TryGetInt64(out long parsed) && parsed > 0
            ? parsed
            : 0;
        return new ReleaseAsset(name, size, url, ReadSha256Digest(element));
    }

    /// <summary>
    /// Reads GitHub's asset digest (<c>sha256:</c> + 64 hex digits) as lower-case hex.
    /// </summary>
    /// <param name="element">An asset object.</param>
    /// <returns>The digest, or <see langword="null"/> when absent, of another algorithm or malformed.</returns>
    private static string? ReadSha256Digest(JsonElement element)
    {
        const string Prefix = "sha256:";
        var digest = ReadString(element, "digest", 128);
        if (digest is null || !digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[Prefix.Length..];
        return Sha256Sums.IsSha256Hex(hex) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>Reads a string member, cut to <paramref name="maxLength"/> characters.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="maxLength">The most characters kept.</param>
    /// <returns>The text, or <see langword="null"/> when the member is missing or not a string.</returns>
    private static string? ReadString(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return text is null ? null : text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>Reads a boolean member; anything but a JSON <c>true</c> is <see langword="false"/>.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The member's name.</param>
    /// <returns>The value.</returns>
    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Reads an absolute http or https address.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The member's name.</param>
    /// <returns>The address, or <see langword="null"/> when missing, relative, too long or of another scheme.</returns>
    private static Uri? ReadHttpUrl(JsonElement element, string name)
    {
        var text = ReadString(element, name, MaxUrlLength + 1);
        if (text is null || text.Length > MaxUrlLength || !Uri.TryCreate(text, UriKind.Absolute, out var url))
        {
            return null;
        }

        return url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp ? url : null;
    }

    /// <summary>Reads an ISO 8601 time (<c>2026-10-03T05:44:30Z</c>).</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The member's name.</param>
    /// <returns>The time, or <see langword="null"/> when missing or not a time.</returns>
    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        var text = ReadString(element, name, 64);
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }
}
