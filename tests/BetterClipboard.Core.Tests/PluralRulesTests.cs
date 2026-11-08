using BetterClipboard.Core.Localization;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// CLDR plural rules as catalogs write them: the parser, the categories of whole counts in languages with one to four
/// forms, CLDR's own samples, and the refusals that keep a rule copied wrong out of a release.
/// </summary>
public sealed class PluralRulesTests
{
    /// <summary>English (CLDR): one is exactly 1 with no decimals; everything else is other.</summary>
    private static readonly Dictionary<string, string> English = new()
    {
        ["one"] = "i = 1 and v = 0 @integer 1",
        ["other"] = "@integer 0, 2~16, 100, 1000, 10000, 100000, 1000000, … @decimal 0.0~1.5, 10.0, 100.0, 1000.0, …",
    };

    /// <summary>Russian (CLDR): one for 1, 21, 31 …; few for 2–4, 22–24 …; many for 0, 5–20, 25–30 ….</summary>
    private static readonly Dictionary<string, string> Russian = new()
    {
        ["one"] = "v = 0 and i % 10 = 1 and i % 100 != 11 @integer 1, 21, 31, 41, 51, 61, 71, 81, 101, 1001, …",
        ["few"] = "v = 0 and i % 10 = 2..4 and i % 100 != 12..14 @integer 2~4, 22~24, 32~34, 42~44, 52~54, 62, 102, 1002, …",
        ["many"] = "v = 0 and i % 10 = 0 or v = 0 and i % 10 = 5..9 or v = 0 and i % 100 = 11..14 @integer 0, 5~19, 100, 1000, 10000, 100000, 1000000, …",
        ["other"] = "   @decimal 0.0~1.5, 10.0, 100.0, 1000.0, 10000.0, 100000.0, 1000000.0, …",
    };

    /// <summary>Hebrew (CLDR 42+): one for 1, two for 2, other for the rest.</summary>
    private static readonly Dictionary<string, string> Hebrew = new()
    {
        ["one"] = "i = 1 and v = 0 or i = 0 and v != 0 @integer 1 @decimal 0.0~0.9, 0.00~0.05",
        ["two"] = "i = 2 and v = 0 @integer 2",
        ["other"] = " @integer 0, 3~17, 100, 1000, 10000, 100000, 1000000, … @decimal 1.0~2.5, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, …",
    };

    /// <summary>
    /// Spanish (CLDR 42+): one for 1; many for whole millions (written with the compact exponent too, which whole
    /// counts never have); other for the rest.
    /// </summary>
    private static readonly Dictionary<string, string> Spanish = new()
    {
        ["one"] = "n = 1 @integer 1 @decimal 1.0, 1.00, 1.000, 1.0000",
        ["many"] = "e = 0 and i != 0 and i % 1000000 = 0 and v = 0 or e != 0..5 @integer 1000000, 1c6, 2c6, 3c6, 4c6, 5c6, 6c6, …",
        ["other"] = " @integer 0, 2~16, 100, 1000, 10000, 100000, 1c3, 2c3, 3c3, 4c3, 5c3, 6c3, …",
    };

    /// <summary>Arabic (CLDR): six categories, with modulo ranges.</summary>
    private static readonly Dictionary<string, string> Arabic = new()
    {
        ["zero"] = "n = 0 @integer 0",
        ["one"] = "n = 1 @integer 1",
        ["two"] = "n = 2 @integer 2",
        ["few"] = "n % 100 = 3..10 @integer 3~10, 103~110, 1003, …",
        ["many"] = "n % 100 = 11..99 @integer 11~26, 111, 1011, …",
        ["other"] = " @integer 100~102, 200~202, 300~302, 400~402, 500~502, 600, 1000, 10000, 100000, 1000000, …",
    };

    /// <summary>Counts and the category each language puts them in.</summary>
    public static TheoryData<string, long, PluralCategory> Categories => new()
    {
        { "en", 1, PluralCategory.One },
        { "en", 0, PluralCategory.Other },
        { "en", 2, PluralCategory.Other },
        { "en", 21, PluralCategory.Other },
        { "en", -1, PluralCategory.One },
        { "ru", 1, PluralCategory.One },
        { "ru", 21, PluralCategory.One },
        { "ru", 11, PluralCategory.Many },
        { "ru", 2, PluralCategory.Few },
        { "ru", 24, PluralCategory.Few },
        { "ru", 12, PluralCategory.Many },
        { "ru", 5, PluralCategory.Many },
        { "ru", 0, PluralCategory.Many },
        { "ru", 111, PluralCategory.Many },
        { "ru", 1001, PluralCategory.One },
        { "he", 1, PluralCategory.One },
        { "he", 2, PluralCategory.Two },
        { "he", 3, PluralCategory.Other },
        { "he", 0, PluralCategory.Other },
        { "es", 1, PluralCategory.One },
        { "es", 1000000, PluralCategory.Many },
        { "es", 2000000, PluralCategory.Many },
        { "es", 1000001, PluralCategory.Other },
        { "es", 0, PluralCategory.Other },
        { "ar", 0, PluralCategory.Zero },
        { "ar", 2, PluralCategory.Two },
        { "ar", 103, PluralCategory.Few },
        { "ar", 111, PluralCategory.Many },
        { "ar", 100, PluralCategory.Other },
    };

    /// <summary>Rules that must not parse, and why.</summary>
    public static TheoryData<string, string> Broken => new()
    {
        { "single", "i = 1" },
        { "one", "i = " },
        { "one", "x = 1" },
        { "one", "i % 0 = 1" },
        { "one", "i = 3..1" },
        { "one", "i = 1 and" },
        { "one", "i = 1 i = 2" },
        { "one", "i < 1" },
        { "one", "i = 1 @fraction 1" },
        { "one", "i = 1 @integer one" },
        { "other", "i = 1" },
        { "few", "" },
    };

    /// <summary>Each language's rules put each count in the expected category (see <see cref="Categories"/>).</summary>
    /// <param name="language">The language's tag (one of this class's rule sets).</param>
    /// <param name="count">The count.</param>
    /// <param name="expected">Its category.</param>
    [Theory]
    [MemberData(nameof(Categories))]
    public void Select_PutsCountsInTheirCategory(string language, long count, PluralCategory expected) =>
        Assert.Equal(expected, PluralRules.Parse(RulesOf(language)).Select(count));

    /// <summary>
    /// Every integer sample CLDR gives with a rule lands in that rule's category: the check that catches a rule copied
    /// wrong into a catalog, run here on languages with two, three, four and six forms.
    /// </summary>
    /// <param name="language">The language's tag.</param>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("he")]
    [InlineData("es")]
    [InlineData("ar")]
    public void EveryIntegerSample_LandsInItsCategory(string language)
    {
        var rules = PluralRules.Parse(RulesOf(language));
        int checkedSamples = 0;
        foreach (var category in Enum.GetValues<PluralCategory>())
        {
            foreach (long sample in rules.IntegerSamples(category))
            {
                Assert.True(rules.Select(sample) == category, $"{language}: {sample} should be {category}, is {rules.Select(sample)}");
                checkedSamples++;
            }
        }

        Assert.True(checkedSamples >= 5, $"{language}: only {checkedSamples} samples");
    }

    /// <summary>
    /// The categories a language uses are its rules' plus other, in CLDR's order whatever order the catalog writes them
    /// in: exactly the forms a complete catalog gives each plural string.
    /// </summary>
    [Fact]
    public void Categories_FollowCldrOrder_OtherLast()
    {
        Assert.Equal(new[] { PluralCategory.One, PluralCategory.Other }, PluralRules.Parse(English).Categories);
        Assert.Equal(new[] { PluralCategory.One, PluralCategory.Few, PluralCategory.Many, PluralCategory.Other }, PluralRules.Parse(Russian).Categories);
        Assert.Equal(Enum.GetValues<PluralCategory>(), PluralRules.Parse(Arabic.Reverse().ToDictionary()).Categories);
        Assert.Equal(new[] { PluralCategory.Other }, PluralRules.Parse(new Dictionary<string, string>()).Categories);
        Assert.Same(PluralRules.OtherOnly, PluralRules.Parse(new Dictionary<string, string>()));
        Assert.Equal(PluralCategory.Other, PluralRules.OtherOnly.Select(1));
    }

    /// <summary>
    /// CLDR's operators all parse: <c>in</c> / <c>not in</c>, <c>=</c> / <c>!=</c>, <c>is</c> / <c>is not</c>,
    /// <c>within</c>, <c>mod</c> and <c>%</c>, value lists and ranges, and operands of decimals (0 for whole counts).
    /// </summary>
    [Fact]
    public void EveryOperatorParses()
    {
        var rules = PluralRules.Parse(new Dictionary<string, string>
        {
            ["zero"] = "n is 0",
            ["one"] = "n in 1, 21 and i is not 11",
            ["two"] = "n mod 10 within 2..2 and n not in 12, 112",
            ["few"] = "n % 10 = 3..4,9 and n % 100 not within 10..19 and f = 0 and t = 0 and w = 0 and c = 0",
            ["many"] = "n != 0..9 and v = 0",
        });

        Assert.Equal(PluralCategory.Zero, rules.Select(0));
        Assert.Equal(PluralCategory.One, rules.Select(21));
        Assert.Equal(PluralCategory.Two, rules.Select(32));
        Assert.Equal(PluralCategory.Many, rules.Select(12));
        Assert.Equal(PluralCategory.Few, rules.Select(29));
        Assert.Equal(PluralCategory.Many, rules.Select(15));
        Assert.Equal(PluralCategory.Other, rules.Select(5));
    }

    /// <summary>
    /// Extreme counts never throw: the most negative long has no absolute value of its own and is read as the largest
    /// one (…807 ends in 7: Russian many).
    /// </summary>
    [Fact]
    public void ExtremeCounts_DoNotThrow()
    {
        var rules = PluralRules.Parse(Russian);
        Assert.Equal(PluralCategory.Many, rules.Select(long.MaxValue));
        Assert.Equal(PluralCategory.Many, rules.Select(long.MinValue));
    }

    /// <summary>A rule that does not follow CLDR's grammar is refused with a message naming its category.</summary>
    /// <param name="category">The category it is written under.</param>
    /// <param name="rule">The rule.</param>
    [Theory]
    [MemberData(nameof(Broken))]
    public void BrokenRules_AreRefused(string category, string rule)
    {
        var error = Assert.Throws<FormatException>(() => PluralRules.Parse(new Dictionary<string, string> { [category] = rule }));
        Assert.Contains(category, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Category names are CLDR's own, exactly; keys and catalogs write them lowercase.</summary>
    [Fact]
    public void CategoryNames_RoundTrip()
    {
        foreach (var category in Enum.GetValues<PluralCategory>())
        {
            Assert.True(PluralRules.TryParseCategory(PluralRules.Name(category), out var parsed));
            Assert.Equal(category, parsed);
        }

        Assert.False(PluralRules.TryParseCategory("One", out _));
        Assert.False(PluralRules.TryParseCategory("plural", out _));
        Assert.False(PluralRules.TryParseCategory(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluralRules.Name((PluralCategory)42));
    }

    /// <summary>The rule set of one of this class's languages.</summary>
    /// <param name="language">The tag.</param>
    /// <returns>Its rules as a catalog writes them.</returns>
    private static Dictionary<string, string> RulesOf(string language) => language switch
    {
        "en" => English,
        "ru" => Russian,
        "he" => Hebrew,
        "es" => Spanish,
        "ar" => Arabic,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "No such rule set here."),
    };
}
