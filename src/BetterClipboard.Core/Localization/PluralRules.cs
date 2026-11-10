using System.Globalization;

namespace BetterClipboard.Core.Localization;

/// <summary>
/// The plural categories of Unicode CLDR, in CLDR's order. A language uses <see cref="Other"/> and any of the others its
/// rules name: English one and other; Chinese only other; Russian one, few, many and other; Hebrew one, two and other.
/// </summary>
public enum PluralCategory
{
    /// <summary>Zero (Arabic, Latvian, Welsh).</summary>
    Zero,

    /// <summary>One (English "1 item"; Russian also 21, 31, …).</summary>
    One,

    /// <summary>Two (Hebrew, Arabic, Slovenian).</summary>
    Two,

    /// <summary>Few (Russian 2–4, 22–24, …; Polish, Czech, Arabic).</summary>
    Few,

    /// <summary>Many (Russian 5–20, 25–30, …; Spanish and French for millions).</summary>
    Many,

    /// <summary>Every count no other rule takes; every language has it.</summary>
    Other,
}

/// <summary>
/// One language's CLDR cardinal plural rules: which <see cref="PluralCategory"/> a count belongs to, so a catalog can
/// word "1 item", "2 items" and "5 items" with the forms its language needs (<see cref="UiText.Plural(long, FormattableString, FormattableString)"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the rules come from.</b> Each catalog's <c>"$language"</c> header carries its language's rules in CLDR's own
/// syntax, copied from CLDR's plural rules chart (<c>"one": "i = 1 and v = 0 @integer 1"</c>), so a new language needs
/// no code here. The samples after <c>@integer</c> / <c>@decimal</c> are CLDR's examples of each category; the parser
/// keeps the integer ones (<see cref="IntegerSamples"/>) and the tests check that every sample lands in its category,
/// which catches a rule copied wrong.
/// </para>
/// <para>
/// <b>Counts are whole numbers.</b> The interface pluralizes item, file and prompt counts only, so the operands are those
/// of an integer: <c>n</c> and <c>i</c> are the absolute value, and <c>v</c>, <c>w</c>, <c>f</c>, <c>t</c>, <c>c</c> and
/// <c>e</c> are 0. Rules about decimals parse and simply never match a decimal. Footgun: a future caller that formats a
/// fraction ("1.5 MB") must not pluralize through this class; CLDR words fractions with other categories.
/// </para>
/// <para>Instances are immutable and shared by every thread.</para>
/// </remarks>
public sealed class PluralRules
{
    /// <summary>The categories in CLDR's order, the order a count is tested in.</summary>
    private static readonly PluralCategory[] Order = [PluralCategory.Zero, PluralCategory.One, PluralCategory.Two, PluralCategory.Few, PluralCategory.Many];

    /// <summary>The parsed condition of each category the language names (never <see cref="PluralCategory.Other"/>).</summary>
    private readonly Dictionary<PluralCategory, Condition> conditions;

    /// <summary>The integer samples of each category, <see cref="PluralCategory.Other"/> included when its rule has some.</summary>
    private readonly Dictionary<PluralCategory, IReadOnlyList<long>> samples;

    /// <summary>Creates a rule set from parsed parts; only <see cref="Parse"/> does.</summary>
    /// <param name="conditions">Category → condition, without <see cref="PluralCategory.Other"/>.</param>
    /// <param name="samples">Category → integer samples.</param>
    private PluralRules(Dictionary<PluralCategory, Condition> conditions, Dictionary<PluralCategory, IReadOnlyList<long>> samples)
    {
        this.conditions = conditions;
        this.samples = samples;
        Categories = [.. Order.Where(conditions.ContainsKey), PluralCategory.Other];
    }

    /// <summary>
    /// The rules of a language with only <see cref="PluralCategory.Other"/> (Chinese, Japanese, Korean): one form for
    /// every count. Also what a header without <c>"plurals"</c> gets, which the tests do not allow in a shipped catalog.
    /// </summary>
    public static PluralRules OtherOnly { get; } = new([], []);

    /// <summary>
    /// The categories this language uses, in CLDR's order, <see cref="PluralCategory.Other"/> last: exactly the forms a
    /// complete catalog gives each plural string.
    /// </summary>
    public IReadOnlyList<PluralCategory> Categories { get; }

    /// <summary>
    /// Parses a language's rules as they are written in a catalog header: category name → CLDR condition, optionally
    /// followed by samples (<c>"i = 1 and v = 0 @integer 1"</c>). <c>"other"</c> may be given for its samples; its
    /// condition must then be empty, because other is whatever no rule takes.
    /// </summary>
    /// <param name="rules">The rules by category name (<c>zero</c>, <c>one</c>, <c>two</c>, <c>few</c>, <c>many</c>, <c>other</c>).</param>
    /// <returns>The rule set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">A category name is unknown, or a condition or a sample list does not parse.</exception>
    public static PluralRules Parse(IReadOnlyDictionary<string, string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var conditions = new Dictionary<PluralCategory, Condition>();
        var samples = new Dictionary<PluralCategory, IReadOnlyList<long>>();
        foreach (var (name, text) in rules)
        {
            if (!TryParseCategory(name, out var category))
            {
                throw new FormatException($"\"{name}\" is not a CLDR plural category (zero, one, two, few, many, other).");
            }

            // Everything from the first '@' on is samples: "@integer 1, 21, 31, …" and "@decimal …".
            string rule = text ?? string.Empty;
            int at = rule.IndexOf('@', StringComparison.Ordinal);
            string condition = (at < 0 ? rule : rule[..at]).Trim();
            samples[category] = at < 0 ? [] : ParseIntegerSamples(rule[at..], name);
            if (category == PluralCategory.Other)
            {
                if (condition.Length > 0)
                {
                    throw new FormatException("The \"other\" category takes no condition: it is every count no other rule takes.");
                }

                continue;
            }

            if (condition.Length == 0)
            {
                throw new FormatException($"The \"{name}\" rule has no condition.");
            }

            conditions[category] = new ConditionParser(condition, name).ParseAll();
        }

        return conditions.Count == 0 && samples.Count == 0 ? OtherOnly : new PluralRules(conditions, samples);
    }

    /// <summary>The category of a count: the first of CLDR's order whose rule matches, else <see cref="PluralCategory.Other"/>.</summary>
    /// <param name="count">The count; a negative one is read as its absolute value, as CLDR's <c>n</c> is.</param>
    /// <returns>The category.</returns>
    public PluralCategory Select(long count)
    {
        // long.MinValue has no positive twin; it is no count anyone shows, and it must not throw.
        long value = count == long.MinValue ? long.MaxValue : Math.Abs(count);
        foreach (var category in Order)
        {
            if (conditions.TryGetValue(category, out var condition) && condition.Matches(value))
            {
                return category;
            }
        }

        return PluralCategory.Other;
    }

    /// <summary>
    /// CLDR's integer examples of a category, as written after <c>@integer</c> in the catalog (ranges expanded up to
    /// 100 values each, the ellipsis and compact forms such as <c>1c6</c> left out): what the tests check the rules
    /// against.
    /// </summary>
    /// <param name="category">The category.</param>
    /// <returns>The samples; empty when the catalog gave none.</returns>
    public IReadOnlyList<long> IntegerSamples(PluralCategory category) => samples.TryGetValue(category, out var list) ? list : [];

    /// <summary>The CLDR name of a category, as catalogs write it after <c>#</c> (<c>one</c>, <c>other</c>).</summary>
    /// <param name="category">The category.</param>
    /// <returns>Its lowercase name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="category"/> is not a defined category.</exception>
    public static string Name(PluralCategory category) => category switch
    {
        PluralCategory.Zero => "zero",
        PluralCategory.One => "one",
        PluralCategory.Two => "two",
        PluralCategory.Few => "few",
        PluralCategory.Many => "many",
        PluralCategory.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Not a plural category."),
    };

    /// <summary>Reads a category by its CLDR name (exact, lowercase: catalogs and keys are written that way).</summary>
    /// <param name="name">The name.</param>
    /// <param name="category">The category, when the name is one.</param>
    /// <returns>Whether it is.</returns>
    public static bool TryParseCategory(string? name, out PluralCategory category)
    {
        switch (name)
        {
            case "zero": category = PluralCategory.Zero; return true;
            case "one": category = PluralCategory.One; return true;
            case "two": category = PluralCategory.Two; return true;
            case "few": category = PluralCategory.Few; return true;
            case "many": category = PluralCategory.Many; return true;
            case "other": category = PluralCategory.Other; return true;
            default: category = PluralCategory.Other; return false;
        }
    }

    /// <summary>
    /// Reads the integer samples of a rule's sample part (<c>"@integer 0, 2~16, 100, … @decimal 0.0~1.5, …"</c>): the
    /// values and ranges after <c>@integer</c>, up to the next <c>@</c>.
    /// </summary>
    /// <param name="text">The sample part, starting at its first <c>@</c>.</param>
    /// <param name="name">The category's name, for messages.</param>
    /// <returns>The integer samples (a range adds at most its first 100 values).</returns>
    /// <exception cref="FormatException">A sample section is not <c>@integer</c> or <c>@decimal</c>, or an integer sample does not parse.</exception>
    private static List<long> ParseIntegerSamples(string text, string name)
    {
        var values = new List<long>();
        foreach (string section in text.Split('@', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = section.Trim();
            if (trimmed.StartsWith("decimal", StringComparison.Ordinal))
            {
                continue;
            }

            if (!trimmed.StartsWith("integer", StringComparison.Ordinal))
            {
                throw new FormatException($"The \"{name}\" rule's samples start with @{trimmed.Split(' ')[0]}, not @integer or @decimal.");
            }

            foreach (string item in trimmed["integer".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // "…" (or "...") says "and so on"; "1c6" is CLDR's compact notation for a million with an exponent,
                // which counts in this app never have.
                if (item is "…" or "..." || IsCompact(item))
                {
                    continue;
                }

                string[] range = item.Split('~');
                if (range.Length is < 1 or > 2
                    || !long.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out long first)
                    || (range.Length == 2 && !long.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                {
                    throw new FormatException($"The \"{name}\" rule's sample \"{item}\" is not a whole number or a range a~b.");
                }

                long last = range.Length == 2 ? long.Parse(range[1], NumberStyles.None, CultureInfo.InvariantCulture) : first;
                if (last < first)
                {
                    throw new FormatException($"The \"{name}\" rule's sample range \"{item}\" ends before it starts.");
                }

                for (long value = first; value <= last && value - first < 100; value++)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    /// <summary>
    /// Whether a sample is in CLDR's compact notation: digits, <c>c</c> or <c>e</c>, digits (<c>1c6</c> is a million
    /// written with an exponent). Anything else that is not a number stays an error.
    /// </summary>
    /// <param name="item">One sample.</param>
    /// <returns>Whether it is compact.</returns>
    private static bool IsCompact(string item)
    {
        int exponent = item.IndexOfAny(['c', 'e']);
        return exponent > 0 && exponent < item.Length - 1
            && item.AsSpan(0, exponent).IndexOfAnyExcept("0123456789.") < 0
            && item.AsSpan(exponent + 1).IndexOfAnyExcept("0123456789") < 0;
    }

    /// <summary>A parsed condition: relations joined by <c>and</c> inside groups joined by <c>or</c>.</summary>
    /// <param name="AnyOf">The <c>or</c> alternatives, each a list of relations that must all hold.</param>
    private sealed record Condition(IReadOnlyList<IReadOnlyList<Relation>> AnyOf)
    {
        /// <summary>Whether a whole, non-negative count satisfies the condition.</summary>
        /// <param name="value">The count's absolute value.</param>
        /// <returns>Whether it does.</returns>
        public bool Matches(long value) => AnyOf.Any(all => all.All(relation => relation.Holds(value)));
    }

    /// <summary>
    /// One relation: an operand (optionally modulo a value) compared with a list of values and ranges, by
    /// <c>in</c> / <c>=</c> (whole values in the list), <c>within</c> (anywhere between a range's ends) or <c>is</c>.
    /// </summary>
    /// <param name="Operand">The operand letter (<c>n</c>, <c>i</c>, <c>v</c>, <c>w</c>, <c>f</c>, <c>t</c>, <c>c</c>, <c>e</c>).</param>
    /// <param name="Modulus">The modulus, or 0 for none.</param>
    /// <param name="Negated">Whether the relation is <c>not in</c>, <c>!=</c>, <c>is not</c> or <c>not within</c>.</param>
    /// <param name="Ranges">The values and ranges (a single value is a range from itself to itself).</param>
    private sealed record Relation(char Operand, long Modulus, bool Negated, IReadOnlyList<(long From, long To)> Ranges)
    {
        /// <summary>Whether the relation holds for a whole, non-negative count.</summary>
        /// <param name="value">The count's absolute value.</param>
        /// <returns>Whether it does.</returns>
        /// <remarks>
        /// For a whole count, <c>in</c> and <c>within</c> agree (the difference is whether a fraction between a range's
        /// ends counts), and the operands of decimals are all 0.
        /// </remarks>
        public bool Holds(long value)
        {
            long operand = Operand is 'n' or 'i' ? value : 0;
            if (Modulus > 0)
            {
                operand %= Modulus;
            }

            bool inList = Ranges.Any(range => operand >= range.From && operand <= range.To);
            return inList != Negated;
        }
    }

    /// <summary>
    /// A recursive-descent parser for CLDR's condition grammar (UTS #35, "Plural rules syntax"): <c>or</c> of <c>and</c>
    /// of relations; a relation is <c>expr ('not'? 'in' | '=' | '!=' | 'is' 'not'? | 'not'? 'within') range_list</c>,
    /// and <c>expr</c> is an operand with an optional <c>mod</c> / <c>%</c> value.
    /// </summary>
    private sealed class ConditionParser
    {
        /// <summary>The condition's tokens: words, numbers, and the symbols <c>= != % , ..</c>.</summary>
        private readonly List<string> tokens;

        /// <summary>The category's name, for messages.</summary>
        private readonly string name;

        /// <summary>The next token's index.</summary>
        private int position;

        /// <summary>Tokenizes a condition.</summary>
        /// <param name="condition">The condition text (samples already cut off).</param>
        /// <param name="name">The category's name, for messages.</param>
        /// <exception cref="FormatException">The text holds a character the grammar has no use for.</exception>
        public ConditionParser(string condition, string name)
        {
            this.name = name;
            tokens = Tokenize(condition, name);
        }

        /// <summary>Parses the whole condition.</summary>
        /// <returns>The condition.</returns>
        /// <exception cref="FormatException">The condition does not follow the grammar, or has text after its end.</exception>
        public Condition ParseAll()
        {
            var anyOf = new List<IReadOnlyList<Relation>> { ParseAnd() };
            while (Accept("or"))
            {
                anyOf.Add(ParseAnd());
            }

            if (position < tokens.Count)
            {
                throw Error($"unexpected \"{tokens[position]}\"");
            }

            return new Condition(anyOf);
        }

        /// <summary>Parses relations joined by <c>and</c>.</summary>
        /// <returns>The relations.</returns>
        private List<Relation> ParseAnd()
        {
            var all = new List<Relation> { ParseRelation() };
            while (Accept("and"))
            {
                all.Add(ParseRelation());
            }

            return all;
        }

        /// <summary>Parses one relation.</summary>
        /// <returns>The relation.</returns>
        /// <exception cref="FormatException">The relation does not follow the grammar.</exception>
        private Relation ParseRelation()
        {
            string operand = Next("an operand");
            if (operand.Length != 1 || "nivwftce".IndexOf(operand[0], StringComparison.Ordinal) < 0)
            {
                throw Error($"\"{operand}\" is not an operand (n, i, v, w, f, t, c, e)");
            }

            long modulus = 0;
            if (Accept("mod") || Accept("%"))
            {
                modulus = ParseValue();
                if (modulus == 0)
                {
                    throw Error("modulo 0");
                }
            }

            bool negated;
            if (Accept("is"))
            {
                negated = Accept("not");
                long single = ParseValue();
                return new Relation(operand[0], modulus, negated, [(single, single)]);
            }

            if (Accept("="))
            {
                negated = false;
            }
            else if (Accept("!="))
            {
                negated = true;
            }
            else
            {
                negated = Accept("not");
                if (!Accept("in") && !Accept("within"))
                {
                    throw Error("expected is, in, within, = or !=");
                }
            }

            var ranges = new List<(long, long)> { ParseRange() };
            while (Accept(","))
            {
                ranges.Add(ParseRange());
            }

            return new Relation(operand[0], modulus, negated, ranges);
        }

        /// <summary>Parses a value or a range <c>a..b</c>.</summary>
        /// <returns>The range (a value is a range from itself to itself).</returns>
        /// <exception cref="FormatException">The value or range does not parse, or ends before it starts.</exception>
        private (long From, long To) ParseRange()
        {
            long from = ParseValue();
            if (!Accept(".."))
            {
                return (from, from);
            }

            long to = ParseValue();
            return to >= from ? (from, to) : throw Error($"range {from}..{to} ends before it starts");
        }

        /// <summary>Parses a non-negative whole value.</summary>
        /// <returns>The value.</returns>
        /// <exception cref="FormatException">The next token is not a whole number.</exception>
        private long ParseValue()
        {
            string token = Next("a number");
            return long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
                ? value
                : throw Error($"\"{token}\" is not a whole number");
        }

        /// <summary>Consumes the next token when it is the given one.</summary>
        /// <param name="token">The expected token.</param>
        /// <returns>Whether it was.</returns>
        private bool Accept(string token)
        {
            if (position < tokens.Count && tokens[position] == token)
            {
                position++;
                return true;
            }

            return false;
        }

        /// <summary>Consumes and returns the next token.</summary>
        /// <param name="what">What the grammar expects there, for the message.</param>
        /// <returns>The token.</returns>
        /// <exception cref="FormatException">The condition ended.</exception>
        private string Next(string what) =>
            position < tokens.Count ? tokens[position++] : throw Error($"expected {what} at the end");

        /// <summary>A parse error naming the category.</summary>
        /// <param name="problem">What is wrong.</param>
        /// <returns>The exception to throw.</returns>
        private FormatException Error(string problem) => new($"The \"{name}\" rule does not parse: {problem}.");

        /// <summary>Splits a condition into words, numbers and the grammar's symbols.</summary>
        /// <param name="condition">The condition text.</param>
        /// <param name="name">The category's name, for messages.</param>
        /// <returns>The tokens.</returns>
        /// <exception cref="FormatException">A character outside the grammar.</exception>
        private static List<string> Tokenize(string condition, string name)
        {
            var list = new List<string>();
            for (int i = 0; i < condition.Length;)
            {
                char c = condition[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
                {
                    // A word or a number: a run of letters or a run of digits (ranges are split at "..").
                    int start = i;
                    bool digits = char.IsAsciiDigit(c);
                    while (i < condition.Length && (digits ? char.IsAsciiDigit(condition[i]) : char.IsAsciiLetterLower(condition[i])))
                    {
                        i++;
                    }

                    list.Add(condition[start..i]);
                }
                else if (c == '.' && i + 1 < condition.Length && condition[i + 1] == '.')
                {
                    list.Add("..");
                    i += 2;
                }
                else if (c == '!' && i + 1 < condition.Length && condition[i + 1] == '=')
                {
                    list.Add("!=");
                    i += 2;
                }
                else if (c is '=' or '%' or ',')
                {
                    list.Add(c.ToString());
                    i++;
                }
                else
                {
                    throw new FormatException($"The \"{name}\" rule does not parse: \"{c}\" has no meaning in a CLDR condition.");
                }
            }

            return list;
        }
    }
}
