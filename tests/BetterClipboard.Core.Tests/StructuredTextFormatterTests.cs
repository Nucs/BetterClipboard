using BetterClipboard.Core.TextView;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="StructuredTextFormatter"/>: XML and JSON are re-indented with every token's characters kept, text
/// inside XML elements is never re-flowed, and anything invalid, unsafe or already tidy is left alone.
/// </summary>
public sealed class StructuredTextFormatterTests
{
    /// <summary>Re-indents and returns the result, failing the test when the formatter declines.</summary>
    /// <param name="text">The input.</param>
    /// <param name="language">XML or JSON.</param>
    /// <returns>The formatted text.</returns>
    private static string Format(string text, TextLanguage language)
    {
        Assert.True(StructuredTextFormatter.TryFormat(text, language, out var formatted), "declined");
        return formatted!;
    }

    /// <summary>Minified JSON gets one value per line, two spaces per level; empty containers stay on one line.</summary>
    [Fact]
    public void Json_ReindentsMinified() =>
        Assert.Equal(
            "{\n  \"id\": 4711,\n  \"tags\": [\n    \"a\",\n    \"b\"\n  ],\n  \"none\": {},\n  \"list\": [],\n  \"ok\": true\n}",
            Format("{\"id\":4711,\"tags\":[\"a\",\"b\"],\"none\":{},\"list\":[],\"ok\":true}", TextLanguage.Json));

    /// <summary>Strings keep their escapes and numbers their spelling: only the whitespace between tokens changes.</summary>
    [Fact]
    public void Json_KeepsTokensExactly() =>
        Assert.Equal(
            "{\n  \"e\": \"caf\\u00e9 \\\"q\\\" \\/\",\n  \"n\": 1.50E+2,\n  \"neg\": -0\n}",
            Format("{\"e\":\"caf\\u00e9 \\\"q\\\" \\/\",\"n\":1.50E+2,\"neg\":-0}", TextLanguage.Json));

    /// <summary>
    /// A JSONC comment stays with what it followed (a trailing one on its token's line, an own-line one on its own line);
    /// JSON Lines keep one value per line.
    /// </summary>
    [Fact]
    public void Json_CommentsAndSeveralValues()
    {
        Assert.Equal(
            "{\n  \"a\": 1, // one\n  // own line\n  \"b\": 2 /* two */\n}",
            Format("{\"a\":1, // one\n// own line\n\"b\":2 /* two */}", TextLanguage.Json));
        Assert.Equal("{\n  \"a\": 1\n}\n{\n  \"a\": 2\n}", Format("{\"a\":1}{\"a\":2}", TextLanguage.Json));
    }

    /// <summary>Invalid, already tidy, other languages, empty and oversized inputs are declined.</summary>
    [Fact]
    public void Declines_WhatItCannotOrNeedNotFormat()
    {
        Assert.False(StructuredTextFormatter.TryFormat("{\"a\":", TextLanguage.Json, out _));
        Assert.False(StructuredTextFormatter.TryFormat("{\n  \"a\": 1\n}", TextLanguage.Json, out _));
        Assert.False(StructuredTextFormatter.TryFormat("int x;", TextLanguage.CSharp, out _));
        Assert.False(StructuredTextFormatter.TryFormat(string.Empty, TextLanguage.Json, out _));
        Assert.False(StructuredTextFormatter.TryFormat("<a><b>", TextLanguage.Xml, out _));
        Assert.False(StructuredTextFormatter.TryFormat("[" + new string(' ', StructuredTextFormatter.MaxChars) + "]", TextLanguage.Json, out _));
    }

    /// <summary>
    /// XML: elements that hold only elements get one child per line; an element that holds text (an agent prompt's
    /// instructions) is written exactly as copied, line breaks and indentation included.
    /// </summary>
    [Fact]
    public void Xml_ReindentsStructureAndKeepsText()
    {
        const string Xml = "<heartbeat><automation_id>watch-nightly</automation_id><instructions>\nLine one.\n   Indented line &amp; more.\n</instructions><empty/></heartbeat>";
        Assert.Equal(
            "<heartbeat>\n  <automation_id>watch-nightly</automation_id>\n  <instructions>\nLine one.\n   Indented line &amp; more.\n</instructions>\n  <empty/>\n</heartbeat>",
            Format(Xml, TextLanguage.Xml));
    }

    /// <summary>The owner's tidy heartbeat is already indented: nothing to format, so the switch need not show.</summary>
    [Fact]
    public void Xml_TidyTextIsDeclined() =>
        Assert.False(StructuredTextFormatter.TryFormat(TextLanguageDetectorTests.HeartbeatXml, TextLanguage.Xml, out _));

    /// <summary>Declarations, comments and attributes stay as written; mixed content and xml:space="preserve" stay whole.</summary>
    [Fact]
    public void Xml_DeclarationsCommentsAndMixedContent()
    {
        var formatted = Format("<?xml version='1.0'?><!-- c --><r a='1' b=\"&lt;\"><p>Hello <b>you</b> there</p><pre xml:space=\"preserve\"><x>  a</x></pre></r>", TextLanguage.Xml);
        Assert.Equal(
            "<?xml version='1.0'?>\n<!-- c -->\n<r a='1' b=\"&lt;\">\n  <p>Hello <b>you</b> there</p>\n  <pre xml:space=\"preserve\"><x>  a</x></pre>\n</r>",
            formatted);
    }

    /// <summary>An entity that would read a file (XXE) is never resolved: the text is simply not formattable.</summary>
    [Fact]
    public void Xml_ExternalEntitiesAreNeverResolved()
    {
        const string Xxe = "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x><y>&e;</y></x>";
        Assert.False(StructuredTextFormatter.TryFormat(Xxe, TextLanguage.Xml, out var formatted));
        Assert.Null(formatted);
    }

    /// <summary>Formatting can be cancelled.</summary>
    [Fact]
    public void Formatting_CanBeCancelled()
    {
        var json = "[" + string.Join(",", Enumerable.Range(0, 200_000).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => StructuredTextFormatter.TryFormat(json, TextLanguage.Json, out _, cancelled.Token));
    }
}
