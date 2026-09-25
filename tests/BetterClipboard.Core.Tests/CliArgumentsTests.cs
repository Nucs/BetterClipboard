using BetterClipboard.Core.Cli;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the <c>bclip</c> grammar: commands and aliases, positional arguments, options in both
/// forms, <c>--since</c>, and the errors a script or agent gets for bad input.
/// </summary>
public sealed class CliArgumentsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>No arguments, <c>help</c> and <c>-h</c> show help; <c>help grep</c> picks the topic.</summary>
    [Fact]
    public void Help_Variants()
    {
        Assert.True(Parse().ShowHelp);
        Assert.True(Parse("help").ShowHelp);
        Assert.Equal(CliCommands.Grep, Parse("help", "grep").HelpTopic);
        Assert.Equal(CliCommands.Get, Parse("get", "--help").HelpTopic);
        Assert.True(Parse("--version").ShowVersion);
        Assert.Contains("bclip grep", CliArguments.Help(CliCommands.Grep), StringComparison.Ordinal);
        Assert.Contains("Exit codes", CliArguments.Help(null), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every command has its own help topic, and every help screen ends with a line break (it used to end
    /// mid-line, gluing the shell prompt onto the last line).
    /// </summary>
    [Fact]
    public void Help_EveryCommandHasATopic()
    {
        string[] commands =
        [
            CliCommands.List, CliCommands.Search, CliCommands.Grep, CliCommands.Get, CliCommands.Copy, CliCommands.Put,
            CliCommands.Pin, CliCommands.Unpin, CliCommands.Delete, CliCommands.Wait, CliCommands.Status,
        ];
        foreach (var command in commands)
        {
            var help = CliArguments.Help(command);
            Assert.Contains($"bclip {command}", help, StringComparison.Ordinal);
            Assert.DoesNotContain("Usage: bclip <command>", help, StringComparison.Ordinal); // not the overview
            Assert.EndsWith("\n", help, StringComparison.Ordinal);
        }

        Assert.EndsWith("4 other error.\n", CliArguments.Help(null), StringComparison.Ordinal);
    }

    /// <summary>Options in short, long and <c>--name=value</c> form, anywhere after the command.</summary>
    [Fact]
    public void List_WithOptions()
    {
        var invocation = Parse("ls", "-n", "5", "--filter=pinned", "--json", "--offset", "10");
        Assert.Null(invocation.Error);
        Assert.Equal(CliCommands.List, invocation.Request!.Command);
        Assert.Equal(5, invocation.Request.Limit);
        Assert.Equal(10, invocation.Request.Offset);
        Assert.Equal("pinned", invocation.Request.Filter);
        Assert.True(invocation.Json);
    }

    /// <summary>Search joins its words; grep takes exactly one pattern, <c>--</c> allows leading dashes.</summary>
    [Fact]
    public void Search_And_Grep()
    {
        Assert.Equal("invoice 2026", Parse("search", "invoice", "2026").Request!.Query);
        var grep = Parse("grep", "-i", "err(or)?", "--full");
        Assert.Equal("err(or)?", grep.Request!.Query);
        Assert.True(grep.Request.IgnoreCase);
        Assert.True(grep.Request.Full);
        Assert.Equal("-v", Parse("grep", "--", "-v").Request!.Query);
        Assert.NotNull(Parse("grep", "two", "patterns").Error);
        Assert.NotNull(Parse("grep").Error);
        Assert.NotNull(Parse("search").Error);
    }

    /// <summary>Items are addressed by id or --recent, never both; ids must be positive integers.</summary>
    [Fact]
    public void Get_Targets()
    {
        Assert.Null(Parse("get").Request!.Id);
        Assert.Equal(42, Parse("cat", "42").Request!.Id);
        Assert.Equal(2, Parse("get", "-r", "2").Request!.Recent);
        Assert.NotNull(Parse("get", "42", "--recent", "2").Error);
        Assert.NotNull(Parse("get", "abc").Error);
        Assert.NotNull(Parse("get", "0").Error);
        Assert.NotNull(Parse("get", "1", "2").Error);
        var saved = Parse("get", "7", "-o", "shot.png", "--format", "png");
        Assert.Equal("shot.png", saved.OutputFile);
        Assert.Equal("png", saved.Request!.Format);
    }

    /// <summary>put takes its words, or stdin when given none or <c>-</c>.</summary>
    [Fact]
    public void Put_TextOrStdin()
    {
        var words = Parse("put", "hello", "world");
        Assert.Equal("hello world", words.Request!.Text);
        Assert.False(words.ReadStdin);
        Assert.True(Parse("put").ReadStdin);
        Assert.True(Parse("put", "-").ReadStdin);
    }

    /// <summary>Relative and absolute --since values.</summary>
    [Fact]
    public void Since_RelativeAndAbsolute()
    {
        Assert.Equal(Now.AddMinutes(-10), Parse("list", "--since", "10m").Request!.Since);
        Assert.Equal(Now.AddHours(-1.5), Parse("list", "-s", "1.5h").Request!.Since);
        Assert.Equal(Now.AddDays(-14), Parse("list", "-s", "2w").Request!.Since);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero), Parse("list", "-s", "2026-09-25T08:00Z").Request!.Since);
        Assert.NotNull(Parse("list", "--since", "yesterday-ish").Error);
    }

    /// <summary>Unknown commands, unknown options, missing values and bad numbers are usage errors.</summary>
    [Fact]
    public void Errors_AreReported()
    {
        Assert.NotNull(Parse("frobnicate").Error);
        Assert.NotNull(Parse("list", "--bogus").Error);
        Assert.NotNull(Parse("list", "-n").Error);
        Assert.NotNull(Parse("list", "-n", "0").Error);
        Assert.NotNull(Parse("list", "-f", "videos").Error);
        Assert.NotNull(Parse("status", "extra").Error);
        Assert.Equal(3, Parse("wait", "-t", "3").Request!.TimeoutSeconds);
    }

    /// <summary>Parses with the fixed clock.</summary>
    /// <param name="args">Arguments.</param>
    /// <returns>The invocation.</returns>
    private static CliInvocation Parse(params string[] args) => CliArguments.Parse(args, Now);
}
