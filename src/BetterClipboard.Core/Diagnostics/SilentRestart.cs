namespace BetterClipboard.Core.Diagnostics;

/// <summary>
/// What the app knows when it asks whether to restart silently now (CLAUDE.md §2.46): the facts
/// <see cref="SilentRestartRules.Decide"/> weighs. Captured on the UI thread in one go, so the decision sees one moment.
/// </summary>
/// <remarks>
/// Settings leaves memory behind that nothing but a new process gives back (XAML's templates and resources, the
/// window-size surfaces later opens reuse: +50 to 60 MB, CLAUDE.md §2.45). The owner's choice (2026-11-12): restart
/// "right after Settings closes", with no switch, and nobody may see or feel it.
/// </remarks>
public sealed record SilentRestartState
{
    /// <summary>Settings was opened since this process started: the only reason to restart.</summary>
    public required bool SettingsUsed { get; init; }

    /// <summary>The app is exiting (or already restarting): nothing more to decide.</summary>
    public bool Exiting { get; init; }

    /// <summary>
    /// A new process would show the language picker (no language chosen yet): a restart must never show a window, so it
    /// does not happen at all.
    /// </summary>
    public bool LanguageWouldBeAsked { get; init; }

    /// <summary>Settings is open (again): its memory is in use.</summary>
    public bool SettingsOpen { get; init; }

    /// <summary>The first start's language picker is open.</summary>
    public bool LanguagePickerOpen { get; init; }

    /// <summary>The clipboard panel is on screen (presented), with or without a popup of its own.</summary>
    public bool PanelOpen { get; init; }

    /// <summary>An approved update downloads, is verified or installed: its installer owns the next restart.</summary>
    public bool UpdateInProgress { get; init; }

    /// <summary>Command-line requests being served (a <c>bclip wait</c> would lose its answer).</summary>
    public int CommandLineRequests { get; init; }

    /// <summary>How long ago Settings closed.</summary>
    public TimeSpan SinceSettingsClosed { get; init; }

    /// <summary>How long ago the user last touched keyboard or mouse (anywhere in the session).</summary>
    public TimeSpan SinceInput { get; init; }

    /// <summary>A modifier key or a mouse button is held down right now (a shortcut or a drag in progress).</summary>
    public bool KeysOrButtonsHeld { get; init; }
}

/// <summary>What <see cref="SilentRestartRules.Decide"/> answers.</summary>
public enum SilentRestartDecision
{
    /// <summary>No restart is wanted (Settings was not used, the app exits, or a restart would show the language picker).</summary>
    NotNeeded,

    /// <summary>A restart is wanted, but not now: ask again at the next check.</summary>
    Wait,

    /// <summary>Restart now.</summary>
    Now,
}

/// <summary>
/// When BetterClipboard restarts itself after Settings was used (CLAUDE.md §2.46): a few seconds after Settings closes,
/// once nothing of the app is on screen and the user paused for a moment. Pure: the caller gathers the facts and owns
/// the timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the pauses.</b> The switch between the two processes takes about a second. The new process holds the shortcuts
/// from its first moment and the old one hands over the tray icon, so nothing is lost or seen even then; the pauses keep
/// the switch away from a moment the user is busy with the app anyway. <see cref="AfterSettingsClosed"/>: a quick reopen
/// of Settings (closing it by mistake) does not restart. <see cref="QuietInput"/>: someone typing, clicking or holding a
/// key is in the middle of something; the restart waits for the next short pause.
/// </para>
/// <para>
/// <b>Never</b> while Settings, the language picker or the panel shows, while an update installs (its installer
/// restarts the app itself), or while a command-line request is served. And never when the new process would ask for a
/// language: that would open a window.
/// </para>
/// </remarks>
public static class SilentRestartRules
{
    /// <summary>How long after Settings closed the restart waits at least.</summary>
    public static readonly TimeSpan AfterSettingsClosed = TimeSpan.FromSeconds(5);

    /// <summary>How long nobody must have touched keyboard or mouse.</summary>
    public static readonly TimeSpan QuietInput = TimeSpan.FromSeconds(2);

    /// <summary>How often the app asks again while the answer is <see cref="SilentRestartDecision.Wait"/>.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>Decides whether to restart now, later or not at all (see the class remarks).</summary>
    /// <param name="state">The facts of this moment.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    public static SilentRestartDecision Decide(SilentRestartState state) =>
        WaitingFor(state) switch
        {
            null => SilentRestartDecision.Now,
            NotNeededReason => SilentRestartDecision.NotNeeded,
            _ => SilentRestartDecision.Wait,
        };

    /// <summary>
    /// What a wanted restart waits for, in English for the log ("the panel is open"); <see langword="null"/> when it can
    /// happen now, and <see cref="NotNeededReason"/> when no restart is wanted at all.
    /// </summary>
    /// <param name="state">The facts of this moment.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    public static string? WaitingFor(SilentRestartState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.SettingsUsed || state.Exiting || state.LanguageWouldBeAsked)
        {
            return NotNeededReason;
        }

        // Ordered from the most to the least lasting reason, so the log names what really holds the restart back.
        return state.SettingsOpen ? "Settings is open"
            : state.LanguagePickerOpen ? "the language picker is open"
            : state.PanelOpen ? "the panel is open"
            : state.UpdateInProgress ? "an update is being installed"
            : state.CommandLineRequests > 0 ? "a command-line request is being served"
            : state.SinceSettingsClosed < AfterSettingsClosed ? "Settings closed only a moment ago"
            : state.KeysOrButtonsHeld ? "a key or mouse button is held down"
            : state.SinceInput < QuietInput ? "keyboard or mouse was used a moment ago"
            : null;
    }

    /// <summary>
    /// Whether a restart already under way must be called off (in English for the log), asked once the new process holds
    /// the shortcuts: something of the app opened while it started (a Win+V before the hook held it, a tray click), or an
    /// update began. Input and pauses no longer matter then: every press already reaches the new process.
    /// </summary>
    /// <param name="state">The facts of this moment.</param>
    /// <returns>Why it is called off, or <see langword="null"/> to go on.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    public static string? CallOffReason(SilentRestartState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Exiting ? "the app is exiting"
            : state.SettingsOpen ? "Settings opened"
            : state.LanguagePickerOpen ? "the language picker opened"
            : state.PanelOpen ? "the panel opened"
            : state.UpdateInProgress ? "an update began"
            : state.CommandLineRequests > 0 ? "a command-line request came in"
            : null;
    }

    /// <summary>The <see cref="WaitingFor"/> answer that means no restart is wanted.</summary>
    public const string NotNeededReason = "no restart is wanted";
}
