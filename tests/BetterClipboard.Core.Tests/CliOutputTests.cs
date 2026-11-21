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

    /// <summary>
    /// <c>--sort size</c> adds a size column after the kind, and an item in a group gets <c>+</c> after its id (like a
    /// pinned one's <c>*</c>): the two kinds the size budget does not count.
    /// </summary>
    [Fact]
    public void Table_SizesAndGroupMarks()
    {
        var items = new[]
        {
            new CliItem { Id = 40, Kind = "image", SizeBytes = 37 * 1024 * 1024, Pinned = true, Groups = 1, Preview = "BC-TEST shot", LastUsed = Now },
            new CliItem { Id = 7, Kind = "text", SizeBytes = 900, Groups = 2, Preview = "BC-TEST text", LastUsed = Now },
            new CliItem { Id = 3, Kind = "text", SizeBytes = 2048, Preview = "BC-TEST plain", LastUsed = Now },
        };
        var lines = CliOutput.Render(CliCommands.List, new CliResponse { Ok = true, Items = items }, Now, showSizes: true).Split('\n');
        Assert.StartsWith("   40*+  image         37 MB ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("     7+  text          900 B ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("      3  text           2 KB ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("     7+  text      just now", CliOutput.Render(CliCommands.List, new CliResponse { Ok = true, Items = [items[1]] }, Now), StringComparison.Ordinal);
    }

    /// <summary>status shows the history's limits: the budget used, what is counted and kept, the largest item, the file.</summary>
    [Fact]
    public void Status_ShowsTheBudget()
    {
        var budget = new CliBudget
        {
            UsedBytes = 15_783_867_597, BudgetBytes = 16L << 30, PercentUsed = 91.9, CountedItems = 3214, MaxItems = 5000,
            ProtectedItems = 12, ProtectedBytes = 300L << 20, KeepDays = 1, LargestItemBytes = 39_007_028, DatabaseBytes = 19_541_812_019,
        };
        var status = CliOutput.Render(CliCommands.Status, new CliResponse { Ok = true, Status = new CliStatus { Version = "0.2.12", Budget = budget } }, Now);
        Assert.Contains("Budget:   14.7 GB of 16 GB used (91.9 %); once full, the oldest items are removed to make room\n", status, StringComparison.Ordinal);
        Assert.Contains("Counted:  3,214 of 5,000 items, removed after 1 day unused; 12 pinned or grouped items (300 MB) not counted\n", status, StringComparison.Ordinal);
        Assert.Contains("Largest:  37.2 MB (bclip list --sort size)\n", status, StringComparison.Ordinal);
        Assert.Contains("On disk:  18.2 GB (the history file, with its search index and thumbnails)\n", status, StringComparison.Ordinal);

        var unlimited = CliOutput.Render(CliCommands.Status, new CliResponse { Ok = true, Status = new CliStatus { Version = "0.2.12", Budget = new CliBudget { UsedBytes = 2048, CountedItems = 1, ProtectedItems = 1, ProtectedBytes = 10 } } }, Now);
        Assert.Contains("Budget:   none (2 KB stored)\n", unlimited, StringComparison.Ordinal);
        Assert.Contains("Counted:  1 item, no item limit, kept until a limit removes them; 1 pinned or grouped item (10 B) not counted\n", unlimited, StringComparison.Ordinal);
    }

    /// <summary>A text item that is nothing but paths shows "path" in the kind column, so a files listing explains itself.</summary>
    [Fact]
    public void Table_ShowsPathForPathTexts()
    {
        var items = new[]
        {
            new CliItem { Id = 3, Kind = "text", Paths = 1, Preview = @"C:\BC-TEST\a.txt", LastUsed = Now },
            new CliItem { Id = 2, Kind = "files", Preview = "a.txt", LastUsed = Now },
        };
        var lines = CliOutput.Render(CliCommands.List, new CliResponse { Ok = true, Items = items }, Now).Split('\n');
        Assert.StartsWith("      3  path ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("      2  files ", lines[1], StringComparison.Ordinal);
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
