namespace BetterClipboard.Core.Presentation;

/// <summary>
/// Where an element that is about to take the keyboard focus lies, as the panel's focus guard sees it
/// (<see cref="PanelFocus.Decide"/>). The app classifies the element by walking up WinUI's visual tree from it.
/// </summary>
public enum PanelFocusTarget
{
    /// <summary>The search box, or a part of it (its text box, its clear button): the one place the panel's focus belongs.</summary>
    SearchBox,

    /// <summary>
    /// Anything else of the panel's own window: a tab, a header button, a group icon, a card, the list, its scroll viewer,
    /// or one of the elements WinUI wraps the window's content in (where it parks the focus after a click on something that
    /// takes none, such as a tooltip). Focus that lands here leaves typing with a control that ignores it.
    /// </summary>
    Surface,

    /// <summary>
    /// Outside the panel's surface: one of its popups (a menu, a flyout, a dialog, a viewer, a peek, the search box's own
    /// Cut/Copy/Paste menu), or another window of the app on the same thread (Settings). These take the focus by design.
    /// </summary>
    Elsewhere,
}

/// <summary>What the panel's focus guard does with a focus move (<see cref="PanelFocus.Decide"/>).</summary>
public enum PanelFocusAction
{
    /// <summary>Let the focus move: it goes to the search box, a popup, or another window.</summary>
    Allow,

    /// <summary>
    /// Refuse the move: the focus stays where it is. Used while the search box has it (nothing to repair), and while the
    /// panel shows without the keyboard, where moving the focus anywhere, even to the search box, would hand the panel's
    /// window Win32's focus and activate it in the background.
    /// </summary>
    Cancel,

    /// <summary>
    /// Send the move to the search box instead: the panel has the keyboard and the focus is somewhere else already (a
    /// menu that is closing hands it back to the element it came from, a window activation picks the first control), so
    /// refusing would leave it there.
    /// </summary>
    RedirectToSearchBox,
}

/// <summary>
/// The rule behind "the search box never loses the keyboard focus, unless a popup opens" (the owner's rule, 2026-11-21:
/// "We are not supposed to lose focus from the search box (unless a panel/modal opens)"; CLAUDE.md §2.66).
/// </summary>
/// <remarks>
/// <para>
/// The panel's keys work from any of its elements (its key model listens on the root of its tree), but typed text goes to
/// the focused control. Until 2026-11-21 every control of the panel took the focus when clicked — a tab, a header button,
/// a group icon, a card — and Tab moved it on, so the search stopped following the keyboard until the box was clicked
/// again. The rule keeps every element of the panel's surface from taking the focus, and leaves the focus alone wherever
/// the panel's popups take it: they hand it back when they close.
/// </para>
/// <para>
/// Pure, so the rule is unit-tested; <c>ClipboardFlyout.SearchFocus.cs</c> asks it for every focus move WinUI announces
/// (<c>FocusManager.GettingFocus</c>) and carries the answer out.
/// </para>
/// </remarks>
public static class PanelFocus
{
    /// <summary>Decides what happens to a focus move the panel's window announces.</summary>
    /// <param name="target">Where the element about to take the focus lies.</param>
    /// <param name="panelShown">
    /// Whether the panel is on screen. A concealed panel guards nothing: its next summon gives the search box the keyboard
    /// (or leaves it with the app) anyway, and WinUI shuffles focus while the window is hidden and shown between uses.
    /// </param>
    /// <param name="keyboardWithApp">
    /// Whether the panel shows without the keyboard (Settings › Panel › <i>Leave the keyboard with your app</i>): its keys
    /// come through a hook, whatever WinUI's focus is, and a focus move must never activate its window.
    /// </param>
    /// <param name="focusInSearchBox">Whether the element that has the focus now lies in the search box.</param>
    /// <returns>
    /// <see cref="PanelFocusAction.Allow"/> for a concealed panel and for any target but the panel's surface;
    /// <see cref="PanelFocusAction.Cancel"/> for the surface while the panel shows without the keyboard or the search box
    /// has the focus; <see cref="PanelFocusAction.RedirectToSearchBox"/> for the surface otherwise.
    /// </returns>
    public static PanelFocusAction Decide(PanelFocusTarget target, bool panelShown, bool keyboardWithApp, bool focusInSearchBox)
    {
        if (!panelShown || target != PanelFocusTarget.Surface)
        {
            return PanelFocusAction.Allow;
        }

        // Without the keyboard, a redirect would be a focus move like the one refused (WinUI gives its window Win32's focus,
        // an activation in the background); with the focus in the search box already, there is nothing to move it to.
        return keyboardWithApp || focusInSearchBox ? PanelFocusAction.Cancel : PanelFocusAction.RedirectToSearchBox;
    }
}
