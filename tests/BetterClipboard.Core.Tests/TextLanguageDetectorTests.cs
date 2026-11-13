using BetterClipboard.Core.TextView;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="TextLanguageDetector"/>: each language is recognized from a typical copy of it, and prose, notes
/// and prompts stay Markdown — the fallback the owner chose, which must also win whenever a guess is not clear.
/// </summary>
public sealed class TextLanguageDetectorTests
{
    /// <summary>An agent prompt shaped like the owner's Codex heartbeat (invented BC-TEST content).</summary>
    internal const string HeartbeatXml =
        "<heartbeat>\n  <automation_id>watch-nightly-build</automation_id>\n" +
        "  <current_time_iso>2026-11-12T16:04:57.290Z</current_time_iso>\n  <instructions>\n" +
        "Monitor the BC-TEST release pipeline for replies about the nightly build. Check BOTH queues: build-4711 and build-4712.\n" +
        "Use the connected mail tool if available; otherwise read the signed-in session only.\n" +
        "Verified links: https://example.com/builds/4711 and https://example.com/builds/4712 .\n" +
        "Stay quiet while there is no meaningful change.\n  </instructions>\n</heartbeat>";

    /// <summary>Samples of code and data, each with the language it must be recognized as.</summary>
    public static TheoryData<string, TextLanguage> CodeSamples => new()
    {
        { HeartbeatXml, TextLanguage.Xml },
        { "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>", TextLanguage.Xml },
        { "<Button Content=\"BC-TEST\" Click=\"OnClick\" />", TextLanguage.Xml },
        { "<!DOCTYPE html>\n<html><body><p>BC-TEST</p></body></html>", TextLanguage.Html },
        { "<div class=\"card\">\n  <span>BC-TEST</span>\n</div>", TextLanguage.Html },
        { "{\"automation_id\":\"watch-nightly-build\",\"queues\":[4711,4712],\"quiet\":true}", TextLanguage.Json },
        { "[\n  { \"id\": 1 },\n  { \"id\": 2 }\n]", TextLanguage.Json },
        { "// BC-TEST settings\n{\n  \"editor.fontSize\": 14, // comment\n  \"files.eol\": \"\\n\",\n}", TextLanguage.Json },
        { "{\"a\":1}\n{\"a\":2}\n{\"a\":3}", TextLanguage.Json },
        {
            "using System;\nusing System.Linq;\n\nnamespace BcTest.Sample;\n\npublic sealed class Greeter\n{\n    public string Name { get; init; }\n\n" +
            "    public void Greet() => Console.WriteLine($\"Hello {Name}\");\n}", TextLanguage.CSharp
        },
        {
            "public async Task<int> CountAsync(CancellationToken token)\n{\n    var items = await store.ReadAsync(token);\n    return items.Count;\n}",
            TextLanguage.CSharp
        },
        {
            "package com.example.bctest;\n\nimport java.util.List;\n\npublic class Main {\n    public static void main(String[] args) {\n" +
            "        System.out.println(\"BC-TEST\");\n    }\n}", TextLanguage.Java
        },
        { "fun main() {\n    val names = listOf(\"a\", \"b\")\n    println(names)\n}\n\ndata class User(val name: String)", TextLanguage.Kotlin },
        { "const fs = require('fs');\nconst data = fs.readFileSync('x.txt', 'utf8');\nconsole.log(data.length);", TextLanguage.JavaScript },
        {
            "interface User {\n  name: string;\n  age: number;\n}\n\nexport const nameOf = (user: User): string => user.name;",
            TextLanguage.TypeScript
        },
        {
            "import os\n\n\ndef main() -> None:\n    for name in os.listdir('.'):\n        print(name)\n\n\nif __name__ == \"__main__\":\n    main()",
            TextLanguage.Python
        },
        { "require 'json'\n\nclass Report\n  attr_reader :rows\n\n  def initialize\n    @rows = []\n  end\nend", TextLanguage.Ruby },
        { "<?php\necho \"BC-TEST\";\n$x = 1;", TextLanguage.Php },
        { "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tx := 42\n\tfmt.Println(x)\n}", TextLanguage.Go },
        { "use std::collections::HashMap;\n\nfn main() {\n    let mut map = HashMap::new();\n    map.insert(1, \"a\");\n    println!(\"{:?}\", map);\n}", TextLanguage.Rust },
        { "#include <iostream>\n\nint main() {\n    std::cout << \"BC-TEST\" << std::endl;\n    return 0;\n}", TextLanguage.Cpp },
        {
            "$files = Get-ChildItem -Path $env:TEMP -Recurse\n$files | Where-Object { $_.Length -gt 1MB } | Select-Object Name\nWrite-Host \"done\"",
            TextLanguage.PowerShell
        },
        { "param(\n    [string]$Name = 'BC-TEST'\n)\nWrite-Output $Name", TextLanguage.PowerShell },
        { "#!/bin/bash\nset -e\necho \"BC-TEST\"", TextLanguage.Shell },
        { "sudo apt-get update && sudo apt-get install -y git\nexport PATH=\"$HOME/bin:$PATH\"\ngit clone https://example.com/x.git 2>&1 | tail -n 3", TextLanguage.Shell },
        { "@echo off\nsetlocal\nset NAME=BC-TEST\nif exist %NAME% goto done\n:done\nendlocal", TextLanguage.Batch },
        { "SELECT id, name\nFROM users\nWHERE age > 30\nORDER BY name;", TextLanguage.Sql },
        { "CREATE TABLE notes (\n  id INTEGER PRIMARY KEY,\n  body TEXT NOT NULL\n);", TextLanguage.Sql },
        { ".card {\n  color: #333;\n  margin: 0 auto;\n}\n\n@media (max-width: 600px) {\n  .card { display: none; }\n}", TextLanguage.Css },
        { "name: build\non:\n  push:\n    branches: [main]\njobs:\n  build:\n    runs-on: windows-latest\n    steps:\n      - uses: actions/checkout@v5", TextLanguage.Yaml },
        { "[core]\n\tautocrlf = true\n[user]\n\tname = BC-TEST\n\temail = bc-test@example.com", TextLanguage.Ini },
        { "diff --git a/x.txt b/x.txt\n--- a/x.txt\n+++ b/x.txt\n@@ -1,2 +1,2 @@\n-old\n+new\n same", TextLanguage.Diff },
        { "--- a/x.txt\n+++ b/x.txt\n@@ -1 +1 @@\n-old\n+new", TextLanguage.Diff },
        { "#!/usr/bin/env python3\nprint('BC-TEST')", TextLanguage.Python },
    };

    /// <summary>Texts that are prose, notes or prompts: they must be Markdown, even when they mention code words.</summary>
    public static TheoryData<string> ProseSamples => new()
    {
        "BC-TEST hello world",
        "Please look at the failing build and tell me what changed since yesterday. The logs are in the usual place.",
        "Select the items from the list where the price is high, then sort them by name and send me the result.",
        "Note: the meeting moved to Thursday.\nAgenda: budget, hiring, the release.\nOwner: BC-TEST",
        "TODO:\n- fix the bug in the parser\n- write the tests\n- update the docs",
        "# Release plan\n\nWe ship on **Friday**. See [the board](https://example.com/board).\n\n- freeze Monday\n- test Wednesday",
        "Fix the bug in `Foo.Bar()` where it throws when the list is empty.\n\n```csharp\nvar x = list.First();\n```\n\nIt should return null instead.",
        "2026-11-13 10:00:01 INFO Started the BC-TEST service\n2026-11-13 10:00:02 WARN Slow response from the queue\n2026-11-13 10:00:05 INFO Done",
        "<your name> wrote:\nThanks for the update, I will check it tomorrow morning and get back to you.",
        "[1] First reference to a paper about the topic.\n[2] Second reference, from the same group of authors.",
        "Dear team,\n\nThe build is green again. Thanks to everyone who helped over the weekend.\n\nBest,\nBC-TEST",
        "if you can, call me back after five; I am in meetings until then.",
        "",
        "   \n\t ",
    };

    /// <summary>Every sample of code and data is recognized as its language.</summary>
    /// <param name="text">The sample.</param>
    /// <param name="expected">Its language.</param>
    [Theory]
    [MemberData(nameof(CodeSamples))]
    public void Detect_RecognizesCode(string text, TextLanguage expected) => Assert.Equal(expected, TextLanguageDetector.Detect(text));

    /// <summary>Prose, notes, logs and prompts are Markdown, also with words that code uses.</summary>
    /// <param name="text">The sample.</param>
    [Theory]
    [MemberData(nameof(ProseSamples))]
    public void Detect_ProseIsMarkdown(string text) => Assert.Equal(TextLanguage.Markdown, TextLanguageDetector.Detect(text));

    /// <summary>No text is Markdown, and a huge text is judged by its head without reading all of it.</summary>
    [Fact]
    public void Detect_NullAndHugeTexts()
    {
        Assert.Equal(TextLanguage.Markdown, TextLanguageDetector.Detect(null));

        // 20 MB of C#: only the head is read, so this is quick; the answer is the head's.
        var huge = "using System;\nnamespace BcTest;\n" + string.Concat(Enumerable.Repeat("public sealed class A { public int X { get; set; } }\n", 400_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(TextLanguage.CSharp, TextLanguageDetector.Detect(huge));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    /// <summary>A minified JSON line longer than the sample is still JSON: its head reads without an error.</summary>
    [Fact]
    public void Detect_HugeJsonIsJudgedByItsHead()
    {
        var json = "[" + string.Join(",", Enumerable.Range(0, 100_000).Select(i => $"{{\"id\":{i},\"name\":\"BC-TEST {i}\"}}")) + "]";
        Assert.True(json.Length > 4 * TextLanguageDetector.SampleChars);
        Assert.Equal(TextLanguage.Json, TextLanguageDetector.Detect(json));
    }

    /// <summary>Prose lines weigh as prose, code-shaped and comment lines do not.</summary>
    /// <param name="line">The line.</param>
    /// <param name="expected">Its prose weight.</param>
    [Theory]
    [InlineData("The build is green again and the release goes out on Friday.", 1)]
    [InlineData("Thanks, see you.", 0.5)]
    [InlineData("var total = items.Sum(x => x.Price);", 0)]
    [InlineData("// Increment the counter for each item in the list we read", 0)]
    [InlineData("# Install the dependencies first and then run the tests", 0)]
    [InlineData("public void Run() {", 0)]
    public void ProseWeight_TellsSentencesFromCode(string line, double expected) => Assert.Equal(expected, TextLanguageDetector.ProseWeight(line));

    /// <summary>The JSON check accepts JSON with comments and several values, and refuses bracketed prose.</summary>
    /// <param name="text">The text.</param>
    /// <param name="expected">Whether it is JSON.</param>
    [Theory]
    [InlineData("{}", true)]
    [InlineData("[1, 2, 3,]", true)]
    [InlineData("{\"a\": 1} {\"b\": 2}", true)]
    [InlineData("[x] done", false)]
    [InlineData("{ not json }", false)]
    [InlineData("[", false)]
    public void IsJson_FollowsTheReader(string text, bool expected) => Assert.Equal(expected, TextLanguageDetector.IsJson(text, isWhole: true));
}
