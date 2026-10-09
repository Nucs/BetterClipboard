using System.Globalization;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// A version in Semantic Versioning 2.0 form (<c>1.2.3</c>, <c>1.2.3-rc.1</c>, <c>1.2.3+build</c>): what a release tag
/// (<c>v0.2.6</c>) and the running app's own version are compared as, to decide whether a release is newer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="Version"/>.</b> The app's builds carry pre-release parts (<c>0.2.6-dev.52539eb</c> for a build
/// installed before its release), and <see cref="Version"/> cannot parse them. Comparing only the numbers would call such
/// a build equal to the released <c>0.2.6</c>, so the release would never be offered to it.
/// </para>
/// <para>
/// <b>Order</b> is SemVer's: the three numbers first; then a version without a pre-release part is newer than one with it
/// (<c>0.2.6</c> &gt; <c>0.2.6-dev.1</c>); pre-release parts compare identifier by identifier, numbers below words, and a
/// longer list wins a tie. Build metadata (<c>+sha</c>) is dropped when parsing and never compared.
/// </para>
/// <para>
/// Equality (<see cref="Equals(SemanticVersion)"/>) agrees with <see cref="CompareTo"/>: numeric identifiers are stored
/// without leading zeros, so <c>1.0.0-rc.01</c> and <c>1.0.0-rc.1</c> are the same value.
/// </para>
/// </remarks>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    /// <summary>The longest text <see cref="TryParse"/> looks at; a longer one is not a version (a sanity bound for network input).</summary>
    private const int MaxLength = 128;

    /// <summary>The pre-release part without its dash, identifiers joined by dots; <see langword="null"/> or empty for a release.</summary>
    private readonly string? prerelease;

    /// <summary>
    /// Creates a version from its parts.
    /// </summary>
    /// <param name="major">The first number (incompatible changes).</param>
    /// <param name="minor">The second number (features).</param>
    /// <param name="patch">The third number (fixes).</param>
    /// <param name="prerelease">
    /// The pre-release part without its dash (<c>rc.1</c>), or empty for a release. Must already be well formed: use
    /// <see cref="TryParse"/> for text from outside.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A number is negative.</exception>
    public SemanticVersion(int major, int minor, int patch, string? prerelease = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
        this.prerelease = string.IsNullOrEmpty(prerelease) ? null : prerelease;
    }

    /// <summary>The first number.</summary>
    public int Major { get; }

    /// <summary>The second number.</summary>
    public int Minor { get; }

    /// <summary>The third number.</summary>
    public int Patch { get; }

    /// <summary>The pre-release part without its dash (<c>dev.52539eb</c>); empty for a release.</summary>
    public string Prerelease => prerelease ?? string.Empty;

    /// <summary>
    /// Whether this is a pre-release (it has a pre-release part). Pre-releases are never offered as updates, and a
    /// pre-release build is older than the release of the same three numbers.
    /// </summary>
    public bool IsPrerelease => prerelease is not null;

    /// <summary>Whether <paramref name="left"/> and <paramref name="right"/> are the same version.</summary>
    /// <param name="left">One version.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

    /// <summary>Whether <paramref name="left"/> and <paramref name="right"/> are different versions.</summary>
    /// <param name="left">One version.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);

    /// <summary>Whether <paramref name="left"/> is older than <paramref name="right"/>.</summary>
    /// <param name="left">One version.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when older.</returns>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> is newer than <paramref name="right"/>.</summary>
    /// <param name="left">One version.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when newer.</returns>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> is older than or the same as <paramref name="right"/>.</summary>
    /// <param name="left">One version.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when not newer.</returns>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> is newer than or the same as <paramref name="right"/>.</summary>
    /// <param name="left">One version.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when not older.</returns>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Reads a version from text such as <c>0.2.6</c>, <c>v0.2.6</c>, <c>0.2.6-dev.52539eb</c> or
    /// <c>0.2.6+52539eb1c0</c> (a tag name, or the app's informational version).
    /// </summary>
    /// <remarks>
    /// Strict about the shape, because the text comes from the network: exactly three numbers of at most nine digits each,
    /// pre-release identifiers of ASCII letters, digits and dashes, nothing else. A leading <c>v</c> or <c>V</c> and
    /// surrounding whitespace are accepted. A four-part version (<c>0.2.5.20261003</c>, a Chocolatey package fix) is not
    /// a release version and is refused.
    /// </remarks>
    /// <param name="text">The text; may be <see langword="null"/>.</param>
    /// <param name="version">The version when the method returns <see langword="true"/>; the default (0.0.0) otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a version.</returns>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length is 0 or > MaxLength)
        {
            return false;
        }

        if (span[0] is 'v' or 'V')
        {
            span = span[1..];
        }

        // Build metadata never takes part in a comparison: cut it before anything else.
        int plus = span.IndexOf('+');
        if (plus >= 0)
        {
            if (!IsIdentifierList(span[(plus + 1)..]))
            {
                return false;
            }

            span = span[..plus];
        }

        ReadOnlySpan<char> pre = default;
        int dash = span.IndexOf('-');
        if (dash >= 0)
        {
            pre = span[(dash + 1)..];
            span = span[..dash];
            if (!IsIdentifierList(pre))
            {
                return false;
            }
        }

        Span<Range> parts = stackalloc Range[4];
        if (span.Split(parts, '.') != 3 ||
            !TryParseNumber(span[parts[0]], out int major) ||
            !TryParseNumber(span[parts[1]], out int minor) ||
            !TryParseNumber(span[parts[2]], out int patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, NormalizePrerelease(pre));
        return true;
    }

    /// <summary>
    /// Compares by SemVer's order (see the type's remarks).
    /// </summary>
    /// <param name="other">The version to compare with.</param>
    /// <returns>Negative when this version is older, zero when equal, positive when newer.</returns>
    public int CompareTo(SemanticVersion other)
    {
        int byNumbers = Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
        if (byNumbers != 0)
        {
            return byNumbers;
        }

        // A release is newer than any of its pre-releases.
        if (prerelease is null || other.prerelease is null)
        {
            return (prerelease is null ? 1 : 0) - (other.prerelease is null ? 1 : 0);
        }

        return ComparePrerelease(prerelease, other.prerelease);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion other) =>
        Major == other.Major && Minor == other.Minor && Patch == other.Patch &&
        string.Equals(prerelease, other.prerelease, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, prerelease);

    /// <summary>
    /// The version as text without a leading <c>v</c>: <c>0.2.6</c> or <c>0.2.6-dev.52539eb</c>. This is the form the
    /// release's file names use (<c>BetterClipboard-0.2.6-win-x64.zip</c>).
    /// </summary>
    /// <returns>The text.</returns>
    public override string ToString() => prerelease is null
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{prerelease}");

    /// <summary>Parses one of the three numbers: ASCII digits only, at most nine (so it always fits an <see cref="int"/>).</summary>
    /// <param name="text">The digits.</param>
    /// <param name="value">The number.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is such a number.</returns>
    private static bool TryParseNumber(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 9)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a dot-separated list of non-empty identifiers made of ASCII letters, digits and
    /// dashes (the shape of a pre-release part and of build metadata).
    /// </summary>
    /// <param name="text">The text after the dash or the plus sign.</param>
    /// <returns><see langword="true"/> when well formed.</returns>
    private static bool IsIdentifierList(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        bool empty = true;
        foreach (char c in text)
        {
            if (c == '.')
            {
                if (empty)
                {
                    return false;
                }

                empty = true;
            }
            else if (char.IsAsciiLetterOrDigit(c) || c == '-')
            {
                empty = false;
            }
            else
            {
                return false;
            }
        }

        return !empty;
    }

    /// <summary>
    /// Strips leading zeros from the numeric identifiers of a pre-release part, so equal versions have equal text.
    /// </summary>
    /// <param name="prerelease">A well-formed pre-release part, or empty.</param>
    /// <returns>The normalized part, or <see langword="null"/> for an empty one.</returns>
    private static string? NormalizePrerelease(ReadOnlySpan<char> prerelease)
    {
        if (prerelease.IsEmpty)
        {
            return null;
        }

        var identifiers = prerelease.ToString().Split('.');
        for (int i = 0; i < identifiers.Length; i++)
        {
            if (IsNumeric(identifiers[i]))
            {
                var trimmed = identifiers[i].TrimStart('0');
                identifiers[i] = trimmed.Length == 0 ? "0" : trimmed;
            }
        }

        return string.Join('.', identifiers);
    }

    /// <summary>Compares two normalized pre-release parts identifier by identifier.</summary>
    /// <param name="left">One part.</param>
    /// <param name="right">The other.</param>
    /// <returns>Negative, zero or positive, like <see cref="CompareTo"/>.</returns>
    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumeric = IsNumeric(a[i]);
            bool bNumeric = IsNumeric(b[i]);
            int result;
            if (aNumeric && bNumeric)
            {
                // Both are digits without leading zeros: the longer one is the larger number, and equal lengths compare
                // digit by digit. No parsing, so an identifier of any length (a date, a build counter) cannot overflow.
                result = a[i].Length != b[i].Length ? a[i].Length.CompareTo(b[i].Length) : string.CompareOrdinal(a[i], b[i]);
            }
            else if (aNumeric != bNumeric)
            {
                // Numbers sort below words.
                result = aNumeric ? -1 : 1;
            }
            else
            {
                result = string.CompareOrdinal(a[i], b[i]);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>Whether an identifier is made of digits only.</summary>
    /// <param name="identifier">A non-empty identifier.</param>
    /// <returns><see langword="true"/> for a numeric identifier.</returns>
    private static bool IsNumeric(string identifier)
    {
        foreach (char c in identifier)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
