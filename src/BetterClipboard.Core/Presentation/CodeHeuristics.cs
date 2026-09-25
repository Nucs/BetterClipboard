namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Cheap guess whether text is source code / structured data, so the card can use a monospace font
/// (indentation and alignment then stay readable in the preview).
/// </summary>
/// <remarks>
/// Only cosmetic — a wrong guess changes the font of a preview, never the pasted content. It inspects at
/// most the first 40 lines to stay O(1) on huge clips.
/// </remarks>
public static class CodeHeuristics
{
    private static readonly string[] CodeStarts = ["{", "}", "<", "//", "#", "using ", "import ", "var ", "let ", "const ", "def ", "public ", "private ", "SELECT ", "function ", "class "];

    /// <summary>
    /// Returns whether <paramref name="text"/> looks like code or structured data.
    /// </summary>
    /// <param name="text">Preview text (newlines normalized to <c>\n</c>).</param>
    /// <returns><see langword="true"/> when at least a third of the inspected lines look code-like.</returns>
    public static bool LooksLikeCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int inspected = 0;
        int codeLike = 0;
        foreach (var raw in text.Split('\n').Take(40))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            inspected++;
            var trimmed = line.TrimStart();
            bool indented = line.Length - trimmed.Length >= 2 || line.StartsWith('\t');
            bool punctuated = line.EndsWith(';') || line.EndsWith('{') || line.EndsWith('}') || line.EndsWith("=>", StringComparison.Ordinal) || line.EndsWith(',');
            bool keyword = CodeStarts.Any(k => trimmed.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            if (indented || punctuated || keyword)
            {
                codeLike++;
            }
        }

        // A single line needs a strong signal: prose ends with commas too, so require a keyword/brace start.
        if (inspected == 1)
        {
            var only = text.Trim();
            return only.EndsWith(';') || CodeStarts.Take(4).Any(k => only.StartsWith(k, StringComparison.Ordinal));
        }

        return inspected > 0 && codeLike * 3 >= inspected;
    }
}
