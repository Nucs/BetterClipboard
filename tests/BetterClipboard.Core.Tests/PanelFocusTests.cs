using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the rule that keeps the keyboard focus in the panel's search box (CLAUDE.md §2.66): a move to the panel's
/// surface is refused or sent back to the search box, a move into a popup or another window is left alone, and a panel
/// that shows without the keyboard never moves the focus itself.
/// </summary>
public sealed class PanelFocusTests
{
    /// <summary>
    /// The cases that matter, one by one: every move while the panel is concealed, into the search box, or out of the
    /// panel's surface is allowed; a move to the surface is refused while the search box has the focus or the panel leaves
    /// the keyboard with the app, and sent to the search box otherwise.
    /// </summary>
    public static TheoryData<PanelFocusTarget, bool, bool, bool, PanelFocusAction> Cases => new()
    {
        // A click on a tab, a header button or a card while the search box has the focus: refused, nothing moves.
        { PanelFocusTarget.Surface, true, false, true, PanelFocusAction.Cancel },

        // A menu that closes hands the focus back to the card it came from, or a window activation picks the first
        // control: the focus is elsewhere already, so it is sent to the search box.
        { PanelFocusTarget.Surface, true, false, false, PanelFocusAction.RedirectToSearchBox },

        // Without the keyboard the move is only refused, whatever has the focus: a redirect would activate the window.
        { PanelFocusTarget.Surface, true, true, true, PanelFocusAction.Cancel },
        { PanelFocusTarget.Surface, true, true, false, PanelFocusAction.Cancel },

        // The search box itself, and the panel's popups (menus, flyouts, viewers, peeks), always take the focus.
        { PanelFocusTarget.SearchBox, true, false, false, PanelFocusAction.Allow },
        { PanelFocusTarget.SearchBox, true, true, false, PanelFocusAction.Allow },
        { PanelFocusTarget.Elsewhere, true, false, true, PanelFocusAction.Allow },
        { PanelFocusTarget.Elsewhere, true, true, true, PanelFocusAction.Allow },

        // A concealed panel guards nothing: its next summon focuses the search box anyway.
        { PanelFocusTarget.Surface, false, false, false, PanelFocusAction.Allow },
        { PanelFocusTarget.Surface, false, true, true, PanelFocusAction.Allow },
    };

    /// <summary>Each case decides as its note says.</summary>
    /// <param name="target">Where the element about to take the focus lies.</param>
    /// <param name="panelShown">Whether the panel is on screen.</param>
    /// <param name="keyboardWithApp">Whether it shows without the keyboard.</param>
    /// <param name="focusInSearchBox">Whether the search box has the focus now.</param>
    /// <param name="expected">What the guard does.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Decide_FollowsTheRule(PanelFocusTarget target, bool panelShown, bool keyboardWithApp, bool focusInSearchBox, PanelFocusAction expected) =>
        Assert.Equal(expected, PanelFocus.Decide(target, panelShown, keyboardWithApp, focusInSearchBox));

    /// <summary>
    /// Every combination of the inputs, against the rule written another way: the guard acts only on the panel's surface
    /// while the panel shows; it never redirects while the panel leaves the keyboard with the app, and never moves a focus
    /// that the search box already has.
    /// </summary>
    [Fact]
    public void Decide_EveryCombination_KeepsTheInvariants()
    {
        int checkedCases = 0;
        foreach (var target in Enum.GetValues<PanelFocusTarget>())
        {
            foreach (bool panelShown in new[] { false, true })
            {
                foreach (bool keyboardWithApp in new[] { false, true })
                {
                    foreach (bool focusInSearchBox in new[] { false, true })
                    {
                        var action = PanelFocus.Decide(target, panelShown, keyboardWithApp, focusInSearchBox);
                        bool guarded = panelShown && target == PanelFocusTarget.Surface;
                        Assert.Equal(guarded, action != PanelFocusAction.Allow);
                        if (keyboardWithApp || focusInSearchBox)
                        {
                            Assert.NotEqual(PanelFocusAction.RedirectToSearchBox, action);
                        }

                        checkedCases++;
                    }
                }
            }
        }

        Assert.Equal(Enum.GetValues<PanelFocusTarget>().Length * 8, checkedCases);
    }
}
