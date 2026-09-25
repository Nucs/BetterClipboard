using BetterClipboard.Core.Cli;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <c>bclip</c>'s human output: content commands stay byte-exact, everything else is complete
/// lines (a redirected <c>bclip pin 5</c> once ended without a line break), and exit codes follow grep.
/// </summary>
public sealed class CliOutputTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <c>get</c>/<c>wait</c> print the payload verbatim; confirmations always end with a newline and an
    /// absent message prints nothing (not an empty line).
    /// </summary>
    [Fact]
    public void Content_IsVerbatim_MessagesAreLines()
    {
        var content = new CliResponse { Ok = true, Content = new CliContent { Format = "text", MediaType = "text/plain", Text = "a\nb" } };
        Assert.Equal("a\nb", CliOutput.Render(CliCommands.Get, content, Now));
        Assert.Equal("x", CliOutput.Render(CliCommands.Wait, new CliResponse { Ok = true, Item = new CliItem { Id = 1, Preview = "p", Text = "x" } }, Now));

        Assert.Equal("Pinned item 5.\n", CliOutput.Render(CliCommands.Pin, new CliResponse { Ok = true, Message = "Pinned item 5." }, Now));
        Assert.Equal(string.Empty, CliOutput.Render(CliCommands.Put, new CliResponse { Ok = true }, Now));
    }

    /// <summary>Table rows: id with <c>*</c> when pinned, kind, age, source, first line with ↵ for more.</summary>
    [Fact]
    public void Table_And_Matches()
    {
        var items = new[]
        {
            new CliItem { Id = 12, Kind = "text", Preview = "BC-TEST first\nsecond", Pinned = true, Source = "Notepad", LastUsed = Now.AddMinutes(-3) },
            new CliItem { Id = 9, Kind = "link", Preview = "https://example.com", LastUsed = Now.AddHours(-2) },
        };
        var lines = CliOutput.Render(CliCommands.List, new CliResponse { Ok = true, Items = items }, Now).Split('\n');
        Assert.Equal(3, lines.Length); // two rows + the empty string after the final newline
        Assert.StartsWith("    12*  text", lines[0], StringComparison.Ordinal);
        Assert.Contains("Notepad", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("BC-TEST first ↵", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("      9  link", lines[1], StringComparison.Ordinal);
        Assert.Equal(string.Empty, lines[2]);

        var grep = new CliResponse { Ok = true, Items = [new CliItem { Id = 7, Matches = [new CliMatch(3, "ERROR: x"), new CliMatch(9, "ERROR: y")] }] };
        Assert.Equal("7:3: ERROR: x\n7:9: ERROR: y\n", CliOutput.Render(CliCommands.Grep, grep, Now));

        var status = CliOutput.Render(CliCommands.Status, new CliResponse { Ok = true, Status = new CliStatus { Version = "0.2.0", Items = 3, TotalBytes = 2048 } }, Now);
        Assert.StartsWith("BetterClipboard 0.2.0 (protocol v1)\n", status, StringComparison.Ordinal);
        Assert.EndsWith("\n", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Forgotten", status, StringComparison.Ordinal);
        var withForgotten = CliOutput.Render(CliCommands.Status, new CliResponse { Ok = true, Status = new CliStatus { Version = "0.2.0", Forgotten = 2 } }, Now);
        Assert.Contains("Forgotten: 2 items never recorded", withForgotten, StringComparison.Ordinal);
    }

    /// <summary>Exit codes: 0 ok, 1 nothing found / timed out, 2 bad request, 4 other failures.</summary>
    [Fact]
    public void ExitCodes_FollowGrep()
    {
        Assert.Equal(CliExitCodes.Ok, CliExitCodes.For(new CliResponse { Ok = true }));
        Assert.Equal(CliExitCodes.NothingFound, CliExitCodes.For(CliResponse.Fail(CliErrorCodes.NotFound, "none")));
        Assert.Equal(CliExitCodes.NothingFound, CliExitCodes.For(CliResponse.Fail(CliErrorCodes.Timeout, "late")));
        Assert.Equal(CliExitCodes.Usage, CliExitCodes.For(CliResponse.Fail(CliErrorCodes.BadRequest, "bad")));
        Assert.Equal(CliExitCodes.Error, CliExitCodes.For(CliResponse.Fail(CliErrorCodes.ClipboardBusy, "busy")));
        Assert.Equal("1.5 KB", CliOutput.FormatBytes(1536));
    }
}
