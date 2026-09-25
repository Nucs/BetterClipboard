using System.Globalization;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for presentation helpers (relative time wording and code detection).
/// </summary>
public sealed class PresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 15, 30, 0, TimeSpan.Zero);

    /// <summary>Each time bucket produces its expected wording (UTC zone, invariant culture).</summary>
    /// <param name="minutesAgo">Offset from now in minutes.</param>
    /// <param name="expected">Expected text.</param>
    [Theory]
    [InlineData(0, "just now")]
    [InlineData(-5, "just now")]
    [InlineData(3, "3 min ago")]
    [InlineData(59, "59 min ago")]
    [InlineData(150, "2 h ago")]
    [InlineData(60 * 20, "Yesterday 19:30")]
    [InlineData(60 * 24 * 3, "Tue 15:30")]
    [InlineData(60 * 24 * 40, "16 Aug")]
    [InlineData(60 * 24 * 400, "21 Aug 2025")]
    public void RelativeTime_Buckets(int minutesAgo, string expected)
    {
        var text = RelativeTimeFormatter.Format(Now.AddMinutes(-minutesAgo), Now, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
        Assert.Equal(expected, text);
    }

    /// <summary>Typical code and data snippets are recognized.</summary>
    /// <param name="text">Snippet.</param>
    [Theory]
    [InlineData("public void Foo()\n{\n    Bar();\n}")]
    [InlineData("{\n  \"a\": 1,\n  \"b\": 2\n}")]
    [InlineData("SELECT *\nFROM clips\nWHERE id = 1;")]
    [InlineData("var x = 42;")]
    public void Code_IsDetected(string text)
    {
        Assert.True(CodeHeuristics.LooksLikeCode(text));
    }

    /// <summary>Ordinary prose stays in the proportional font.</summary>
    /// <param name="text">Prose.</param>
    [Theory]
    [InlineData("Hello there, how are you?")]
    [InlineData("Meeting notes\nWe agreed to ship on Friday\nEveryone happy")]
    [InlineData("")]
    public void Prose_IsNotCode(string text)
    {
        Assert.False(CodeHeuristics.LooksLikeCode(text));
    }
}
