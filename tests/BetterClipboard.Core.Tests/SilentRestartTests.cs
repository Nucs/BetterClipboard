using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// When BetterClipboard restarts itself after Settings (CLAUDE.md §2.46): only after Settings was used, never while
/// anything of the app shows or an update or a command-line request runs, never when the new process would ask for a
/// language, and only after Settings has been closed a few seconds and the user paused a moment.
/// </summary>
public sealed class SilentRestartTests
{
    /// <summary>A moment that allows the restart: Settings used and closed long ago, nothing showing, the user idle.</summary>
    private static readonly SilentRestartState Quiet = new()
    {
        SettingsUsed = true,
        SinceSettingsClosed = TimeSpan.FromSeconds(30),
        SinceInput = TimeSpan.FromSeconds(30),
    };

    /// <summary>All quiet: restart now, with no reason to wait.</summary>
    [Fact]
    public void Quiet_RestartsNow()
    {
        Assert.Equal(SilentRestartDecision.Now, SilentRestartRules.Decide(Quiet));
        Assert.Null(SilentRestartRules.WaitingFor(Quiet));
        Assert.Null(SilentRestartRules.CallOffReason(Quiet));
    }

    /// <summary>No restart is wanted without Settings, while exiting, or when the new process would show the language picker.</summary>
    [Fact]
    public void NotWanted_WithoutSettings_WhileExiting_OrWhenAPickerWouldShow()
    {
        Assert.Equal(SilentRestartDecision.NotNeeded, SilentRestartRules.Decide(Quiet with { SettingsUsed = false }));
        Assert.Equal(SilentRestartDecision.NotNeeded, SilentRestartRules.Decide(Quiet with { Exiting = true }));
        Assert.Equal(SilentRestartDecision.NotNeeded, SilentRestartRules.Decide(Quiet with { LanguageWouldBeAsked = true }));
        Assert.Equal(SilentRestartRules.NotNeededReason, SilentRestartRules.WaitingFor(Quiet with { SettingsUsed = false }));
    }

    /// <summary>Each thing that holds the restart back makes it wait, and is named in the log's words.</summary>
    /// <param name="change">Which fact is not quiet.</param>
    /// <param name="reason">The reason expected.</param>
    [Theory]
    [MemberData(nameof(Blockers))]
    public void Waits_ForEachBlocker(string change, string reason)
    {
        var state = Apply(Quiet, change);
        Assert.Equal(SilentRestartDecision.Wait, SilentRestartRules.Decide(state));
        Assert.Equal(reason, SilentRestartRules.WaitingFor(state));
    }

    /// <summary>The blockers and the reason each one is logged with.</summary>
    public static TheoryData<string, string> Blockers => new()
    {
        { "settings open", "Settings is open" },
        { "picker open", "the language picker is open" },
        { "panel open", "the panel is open" },
        { "update", "an update is being installed" },
        { "bclip", "a command-line request is being served" },
        { "settings just closed", "Settings closed only a moment ago" },
        { "key held", "a key or mouse button is held down" },
        { "input just now", "keyboard or mouse was used a moment ago" },
    };

    /// <summary>
    /// The edges of the two pauses: exactly the pause is enough, a millisecond less is not.
    /// </summary>
    [Fact]
    public void Pauses_AreInclusive()
    {
        Assert.Equal(SilentRestartDecision.Now, SilentRestartRules.Decide(Quiet with { SinceSettingsClosed = SilentRestartRules.AfterSettingsClosed }));
        Assert.Equal(SilentRestartDecision.Wait, SilentRestartRules.Decide(Quiet with { SinceSettingsClosed = SilentRestartRules.AfterSettingsClosed - TimeSpan.FromMilliseconds(1) }));
        Assert.Equal(SilentRestartDecision.Now, SilentRestartRules.Decide(Quiet with { SinceInput = SilentRestartRules.QuietInput }));
        Assert.Equal(SilentRestartDecision.Wait, SilentRestartRules.Decide(Quiet with { SinceInput = SilentRestartRules.QuietInput - TimeSpan.FromMilliseconds(1) }));
    }

    /// <summary>
    /// The most lasting reason is the one named when several hold: an open panel outranks a moment-ago key press, so the
    /// log does not blame typing for a wait that the open panel causes.
    /// </summary>
    [Fact]
    public void SeveralBlockers_NameTheMostLasting()
    {
        var state = Quiet with { PanelOpen = true, SinceInput = TimeSpan.Zero, KeysOrButtonsHeld = true };
        Assert.Equal("the panel is open", SilentRestartRules.WaitingFor(state));
    }

    /// <summary>
    /// Once the new process holds the shortcuts, only something that opened (or an update or a request) calls the restart
    /// off; input and the pauses no longer do, since every press reaches the new process then.
    /// </summary>
    [Fact]
    public void CallOff_OnlyForWhatOpened()
    {
        var typing = Quiet with { SinceInput = TimeSpan.Zero, KeysOrButtonsHeld = true, SinceSettingsClosed = TimeSpan.Zero };
        Assert.Null(SilentRestartRules.CallOffReason(typing));
        Assert.Equal("the panel opened", SilentRestartRules.CallOffReason(Quiet with { PanelOpen = true }));
        Assert.Equal("Settings opened", SilentRestartRules.CallOffReason(Quiet with { SettingsOpen = true }));
        Assert.Equal("the language picker opened", SilentRestartRules.CallOffReason(Quiet with { LanguagePickerOpen = true }));
        Assert.Equal("an update began", SilentRestartRules.CallOffReason(Quiet with { UpdateInProgress = true }));
        Assert.Equal("a command-line request came in", SilentRestartRules.CallOffReason(Quiet with { CommandLineRequests = 1 }));
        Assert.Equal("the app is exiting", SilentRestartRules.CallOffReason(Quiet with { Exiting = true }));
    }

    /// <summary>A null state is refused by every rule.</summary>
    [Fact]
    public void NullState_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => SilentRestartRules.Decide(null!));
        Assert.Throws<ArgumentNullException>(() => SilentRestartRules.WaitingFor(null!));
        Assert.Throws<ArgumentNullException>(() => SilentRestartRules.CallOffReason(null!));
    }

    /// <summary>Changes one fact of a quiet moment.</summary>
    /// <param name="state">The quiet moment.</param>
    /// <param name="change">Which fact to change (the names <see cref="Blockers"/> uses).</param>
    /// <returns>The changed state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="change"/> is not one of the known names.</exception>
    private static SilentRestartState Apply(SilentRestartState state, string change) => change switch
    {
        "settings open" => state with { SettingsOpen = true },
        "picker open" => state with { LanguagePickerOpen = true },
        "panel open" => state with { PanelOpen = true },
        "update" => state with { UpdateInProgress = true },
        "bclip" => state with { CommandLineRequests = 2 },
        "settings just closed" => state with { SinceSettingsClosed = TimeSpan.FromSeconds(1) },
        "key held" => state with { KeysOrButtonsHeld = true },
        "input just now" => state with { SinceInput = TimeSpan.FromMilliseconds(500) },
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown change."),
    };
}
