namespace BetterClipboard.Core.Updates;

/// <summary>
/// Reads a release's <c>SHA256SUMS.txt</c>: one line per file in <c>sha256sum</c>'s format, <c>&lt;64 hex digits&gt;</c>,
/// spaces, an optional <c>*</c> (binary mode), then the file's name.
/// </summary>
/// <remarks>
/// The same file <c>install.ps1</c> checks a download against (its <c>Get-AssetSha256</c>), so the app's updater and the
/// installer accept and refuse exactly the same downloads. The file comes from the same release as the package it
/// vouches for: it protects against a damaged or swapped download, not against a release that was itself replaced.
/// </remarks>
public static class Sha256Sums
{
    /// <summary>The length of a SHA-256 in hex digits.</summary>
    public const int HexLength = 64;

    /// <summary>
    /// Finds the SHA-256 listed for <paramref name="fileName"/>.
    /// </summary>
    /// <param name="text">The content of <c>SHA256SUMS.txt</c>; may be <see langword="null"/>.</param>
    /// <param name="fileName">The file's name, without a folder (compared exactly, as <c>sha256sum -c</c> does).</param>
    /// <param name="sha256">The hash as 64 lower-case hex digits when the method returns <see langword="true"/>; otherwise empty.</param>
    /// <returns>
    /// <see langword="true"/> when a well-formed line names the file. A file that is listed twice with different hashes is
    /// not found: such a list vouches for nothing.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is null or blank.</exception>
    public static bool TryFind(string? text, string fileName, out string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        sha256 = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string? found = null;
        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.Length <= HexLength || !IsSha256Hex(line[..HexLength]))
            {
                continue;
            }

            // After the hash: at least one blank, then an optional '*' glued to the name.
            var rest = line[HexLength..];
            if (!char.IsWhiteSpace(rest[0]))
            {
                continue;
            }

            rest = rest.TrimStart();
            if (rest.StartsWith("*"))
            {
                rest = rest[1..];
            }

            if (!rest.SequenceEqual(fileName))
            {
                continue;
            }

            var hash = line[..HexLength].ToString().ToLowerInvariant();
            if (found is not null && found != hash)
            {
                return false;
            }

            found = hash;
        }

        if (found is null)
        {
            return false;
        }

        sha256 = found;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is exactly 64 hex digits (either case).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for a SHA-256 in hex.</returns>
    public static bool IsSha256Hex(ReadOnlySpan<char> text)
    {
        if (text.Length != HexLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
