namespace BetterClipboard.Core.Presentation;

/// <summary>What the panel's shortcut (Win+V) does, decided by <see cref="PanelShortcut.Decide"/>.</summary>
public enum PanelShortcutAction
{
    /// <summary>Open the panel: placed by the placement setting, its view reset, the paste target the window in front.</summary>
    Show,

    /// <summary>
    /// Give the keyboard to the panel that stays open behind the app the user types in, where it stands, without moving
    /// it or resetting its view (the owner's choice, 2026-11-20); its next paste goes into that app.
    /// </summary>
    BringForward,

    /// <summary>Close the panel, like Win+V does when pressed again; the keyboard goes back to where the user was.</summary>
    Close,
}

/// <summary>
/// The rule for the shortcut while the panel may be pinned open (CLAUDE.md §2.55): pressed with no panel up, it opens one;
/// pressed while the panel is in front, it closes it; pressed while a pinned panel waits behind another app, it brings that
/// panel forward instead of closing it — the panel is there to be used again, and a second press then closes it.
/// </summary>
/// <remarks>Pure, so the rule is unit-tested; <c>AppController.OnHotkey</c> asks it with the panel's state at the key.</remarks>
public static class PanelShortcut
{
    /// <summary>Decides what the shortcut does now.</summary>
    /// <param name="isOpen">Whether the panel is on screen (<c>ClipboardFlyout.IsOpen</c>).</param>
    /// <param name="staysOpen">Whether it stays open after pasting (pinned and the pin shown, <see cref="Settings.AppSettings.PanelStaysOpen"/>).</param>
    /// <param name="inFront">Whether the panel, or a popup of its own, has the keyboard.</param>
    /// <returns>
    /// <see cref="PanelShortcutAction.Show"/> when the panel is not open; <see cref="PanelShortcutAction.BringForward"/> when
    /// it stays open but another window is in front; otherwise <see cref="PanelShortcutAction.Close"/>. An open panel that is
    /// not pinned and not in front (a popup of its own keeps it up) closes, as before pinning existed.
    /// </returns>
    public static PanelShortcutAction Decide(bool isOpen, bool staysOpen, bool inFront) =>
        !isOpen ? PanelShortcutAction.Show
        : staysOpen && !inFront ? PanelShortcutAction.BringForward
        : PanelShortcutAction.Close;
}
