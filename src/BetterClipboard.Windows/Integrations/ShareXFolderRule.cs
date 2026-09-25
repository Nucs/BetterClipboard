using System.Text;
using System.Text.RegularExpressions;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// One place ShareX saves screenshots to: a fixed <see cref="Root"/> folder plus the ShareX name pattern
/// of the subfolders below it (<c>%y-%mo</c> by default, i.e. <c>Screenshots\2026-09</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pattern and not just "anything under the root":</b> users point ShareX at broad folders
/// (<c>Pictures</c>, a synced drive). A plain recursive match would then import every image that any app
/// saves there — phone-photo sync, downloads — as a "ShareX screenshot". Matching the pattern ShareX will
/// expand keeps the import to the folders ShareX actually writes.
/// </para>
/// <para>
/// <b>How the pattern is read:</b> ShareX replaces <c>%</c> tokens by plain, case-sensitive text substitution
/// (its <c>NameParser</c>, studied for interoperability — none of its GPL code is used). Date and time
/// tokens become digit classes (<c>%y</c> → 4 digits, <c>%mo</c> → 2, …). Free-text tokens (window title,
/// process name, random or incrementing values) match any text within one folder name. A token's
/// <c>{…}</c> argument is skipped, and anything else, including unknown <c>%x</c> sequences, is literal.
/// Imperfect by design: a free-text token can match a folder ShareX did not create, but only below the
/// configured root and only in the pattern's shape.
/// </para>
/// </remarks>
public sealed record ShareXFolderRule
{
    /// <summary>ShareX name tokens valid in folder patterns, longest first so <c>%mon2</c> wins over <c>%mon</c> and <c>%mo</c>.</summary>
    /// <remarks>
    /// <c>%n</c> (new line) is deliberately absent: ShareX expands it only in text, never in paths, so in a
    /// folder pattern it stays literal.
    /// </remarks>
    private static readonly (string Token, string Regex)[] Tokens =
    [
        ("radjective", FreeText), ("ranimal", FreeText), ("remoji", FreeText), ("height", FreeText), ("width", FreeText),
        ("unix", @"\d+"), ("guid", FreeText), ("GUID", FreeText), ("mon2", FreeText), ("mon", FreeText),
        ("iAa", FreeText), ("iaA", FreeText), ("rna", FreeText), ("uln", FreeText),
        ("yy", @"\d{2}"), ("mo", @"\d{2}"), ("w2", FreeText), ("wy", @"\d{1,2}"), ("ms", @"\d{3}"), ("mi", @"\d{2}"),
        ("pm", "(?:AM|PM)"), ("pn", FreeText), ("ia", FreeText), ("iA", FreeText), ("ib", FreeText), ("iB", FreeText),
        ("ix", FreeText), ("iX", FreeText), ("rn", FreeText), ("ra", FreeText), ("rx", FreeText), ("rX", FreeText),
        ("rf", FreeText), ("un", FreeText), ("cn", FreeText),
        ("y", @"\d{4}"), ("d", @"\d{2}"), ("h", @"\d{1,2}"), ("s", @"\d{2}"), ("t", FreeText), ("w", FreeText), ("i", FreeText),
    ];

    /// <summary>Any text inside one folder name (possibly empty: ShareX substitutes "" for unknown values such as the image width).</summary>
    private const string FreeText = @"[^\\]*";

    /// <summary>Compiled <see cref="RelativePattern"/>, matched against a file's folder relative to <see cref="Root"/>.</summary>
    private readonly Regex matcher;

    /// <summary>
    /// Creates a rule.
    /// </summary>
    /// <param name="root">Absolute folder ShareX's pattern starts in (made full, trailing separator removed).</param>
    /// <param name="relativePattern">ShareX name pattern of the subfolders below <paramref name="root"/>; empty = files directly in it.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is not an absolute path.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ShareXFolderRule(string root, string relativePattern)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relativePattern);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The root of a ShareX folder rule must be an absolute path.", nameof(root));
        }

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RelativePattern = relativePattern.Replace('/', '\\').Trim('\\');

        // NonBacktracking: patterns come from a user-editable file, so no input may make matching slow.
        matcher = new Regex(ToRegex(RelativePattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    /// <summary>Absolute folder the pattern starts in (what gets watched, recursively).</summary>
    public string Root { get; }

    /// <summary>Subfolder pattern below <see cref="Root"/> with <c>\</c> separators and no leading/trailing separator.</summary>
    public string RelativePattern { get; }

    /// <summary>
    /// Whether ShareX could have saved <paramref name="filePath"/> under this rule: the file lies below
    /// <see cref="Root"/> in a folder the pattern can expand to.
    /// </summary>
    /// <param name="filePath">Absolute file path.</param>
    /// <returns><see langword="true"/> on a match.</returns>
    public bool Contains(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (directory is null)
        {
            return false;
        }

        directory = Path.TrimEndingDirectorySeparator(directory);
        if (string.Equals(directory, Root, StringComparison.OrdinalIgnoreCase))
        {
            return matcher.IsMatch(string.Empty);
        }

        return directory.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && matcher.IsMatch(directory[(Root.Length + 1)..]);
    }

    /// <summary>Rules are equal when root (case-insensitive, like Windows paths) and pattern (case-sensitive, like ShareX tokens) are.</summary>
    /// <param name="other">Other rule.</param>
    /// <returns>Whether both describe the same folders.</returns>
    public bool Equals(ShareXFolderRule? other) =>
        other is not null
        && string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RelativePattern, other.RelativePattern, StringComparison.Ordinal);

    /// <summary>Hash consistent with <see cref="Equals(ShareXFolderRule?)"/>.</summary>
    /// <returns>The hash.</returns>
    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Root), StringComparer.Ordinal.GetHashCode(RelativePattern));

    /// <summary>
    /// The folders to watch for a set of rules: each distinct root once, and no root that lies inside another
    /// (a recursive watch of the outer one already covers it).
    /// </summary>
    /// <param name="rules">Rules.</param>
    /// <returns>The minimal set, in first-seen order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> MinimalRoots(IEnumerable<ShareXFolderRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var roots = rules.Select(rule => rule.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return roots
            .Where(root => !roots.Any(other => !string.Equals(other, root, StringComparison.OrdinalIgnoreCase)
                && root.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>
    /// Translates a ShareX folder pattern into an anchored regular expression over a relative folder path
    /// (see the class remarks for the token mapping).
    /// </summary>
    /// <param name="relativePattern">Pattern with <c>\</c> separators.</param>
    /// <returns>The expression, e.g. <c>^\d{4}-\d{2}$</c> for <c>%y-%mo</c>.</returns>
    internal static string ToRegex(string relativePattern)
    {
        var regex = new StringBuilder("^");
        int i = 0;
        while (i < relativePattern.Length)
        {
            if (relativePattern[i] == '%' && MatchToken(relativePattern, i + 1) is { } token)
            {
                regex.Append(token.Regex);
                i += 1 + token.Token.Length;

                // "{10}", "{3,2}", "{C:\words.txt}": ShareX reads these as the token's argument, not as text.
                if (i < relativePattern.Length && relativePattern[i] == '{')
                {
                    int close = relativePattern.IndexOf('}', i);
                    if (close > i)
                    {
                        i = close + 1;
                    }
                }

                continue;
            }

            regex.Append(Regex.Escape(relativePattern[i].ToString()));
            i++;
        }

        return regex.Append('$').ToString();
    }

    /// <summary>
    /// Index of the first <c>%</c> that starts a ShareX name token, or -1. A <c>%</c> that starts no token
    /// (<c>D:\100%\Shots</c>) is literal text, so it must not end the fixed root: cutting there would widen
    /// the watch to the whole drive.
    /// </summary>
    /// <param name="pattern">Expanded folder pattern.</param>
    /// <returns>The index, or -1 when the pattern is all literal.</returns>
    internal static int FirstTokenIndex(string pattern)
    {
        for (int i = pattern.IndexOf('%', StringComparison.Ordinal); i >= 0; i = pattern.IndexOf('%', i + 1))
        {
            if (MatchToken(pattern, i + 1) is not null)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The longest token starting at <paramref name="start"/> (case-sensitive, like ShareX's replacement).</summary>
    /// <param name="pattern">Pattern.</param>
    /// <param name="start">Index just after the <c>%</c>.</param>
    /// <returns>The token, or <see langword="null"/> when the <c>%</c> is literal.</returns>
    private static (string Token, string Regex)? MatchToken(string pattern, int start)
    {
        foreach (var candidate in Tokens)
        {
            if (string.CompareOrdinal(pattern, start, candidate.Token, 0, candidate.Token.Length) == 0
                && start + candidate.Token.Length <= pattern.Length)
            {
                return candidate;
            }
        }

        return null;
    }
}
