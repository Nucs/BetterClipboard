using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for <see cref="UiAccess"/> and <see cref="UiAccessStart"/> (CLAUDE.md §2.63): what a process without Windows' UI
/// access finds at its start (this test process: nothing to lower), and the log line every kind of start gives. A process
/// that has UI access exists only after <c>install.ps1 -UIAccess</c> signed a copy in Program Files, so that half was
/// verified in a test desktop.
/// </summary>
public sealed class UiAccessTests
{
    /// <summary>
    /// A process Windows did not give UI access keeps its token as it is: the level before and after is this process's own,
    /// nothing failed, and it does not count as started with UI access.
    /// </summary>
    [Fact]
    public void ThisProcess_HasNoUiAccess_AndKeepsItsLevel()
    {
        var start = UiAccess.LowerIntegrityIfGranted();

        Assert.False(start.HadUiAccess);
        Assert.Null(start.Error);
        Assert.Equal(ProcessIntegrity.Current, start.LevelBefore);
        Assert.Equal(start.LevelBefore, start.LevelAfter);
        Assert.Equal(false, start.UiAccessAfter);
        Assert.Same(start, UiAccess.AtStart);
        Assert.False(UiAccess.StartedWithUiAccess);
        Assert.Null(start.Describe());
    }

    /// <summary>
    /// The log line for each kind of start: none without UI access; the token's read failing; UI access at High lowered to
    /// Medium (an administrator's account), at Medium already (a standard account), and a lowering that failed. Never
    /// content: levels and error numbers only.
    /// </summary>
    /// <param name="hadUiAccess">Whether the process had UI access.</param>
    /// <param name="before">The level before.</param>
    /// <param name="after">The level after.</param>
    /// <param name="error">The error, or <see langword="null"/>.</param>
    /// <param name="expected">The line, or <see langword="null"/> for none.</param>
    [Theory]
    [InlineData(false, ProcessIntegrity.Medium, ProcessIntegrity.Medium, null, null)]
    [InlineData(false, null, null, 5, "Could not read whether this copy runs with Windows' UI access (error 5).")]
    [InlineData(true, ProcessIntegrity.High, ProcessIntegrity.Medium, null, "This copy runs with Windows' UI access: its panel can open above Start and Search (its integrity level lowered from High to Medium, so programs it starts run as usual).")]
    [InlineData(true, ProcessIntegrity.Medium, ProcessIntegrity.Medium, null, "This copy runs with Windows' UI access: its panel can open above Start and Search (at the Medium integrity level).")]
    [InlineData(true, ProcessIntegrity.High, ProcessIntegrity.High, 1314, "This copy runs with Windows' UI access: its panel can open above Start and Search (at the High integrity level; lowering the level failed (error 1314), so programs it starts get UI access and its level too).")]
    [InlineData(true, 0x4000, null, null, "This copy runs with Windows' UI access: its panel can open above Start and Search (its integrity level lowered from 0x4000 to unknown, so programs it starts run as usual).")]
    public void Describe_NamesWhatHappened(bool hadUiAccess, int? before, int? after, int? error, string? expected)
    {
        var start = new UiAccessStart(hadUiAccess, before, after, hadUiAccess && before == after, error);

        Assert.Equal(expected, start.Describe());
    }
}
