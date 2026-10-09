using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the panel's arrow-key rules (<see cref="PanelNavigation"/>): Left and Right through the tabs in a circle, Up
/// and Down through the rows, Up from the top row into the groups column and Up and Down inside it, and when Left and
/// Right belong to the search box's text instead.
/// </summary>
public sealed class PanelNavigationTests
{
    /// <summary>
    /// Right walks through the tabs and from the last one back to the first; Left the other way, from the first tab to
    /// the last — the circle the tabs are navigated in.
    /// </summary>
    [Fact]
    public void CycleTab_WrapsAroundAtBothEnds()
    {
        int[] forward = [1, 2, 3, 4, 0];
        int tab = 0;
        foreach (int expected in forward)
        {
            tab = PanelNavigation.CycleTab(tab, 5, forward: true, isRepeat: false);
            Assert.Equal(expected, tab);
        }

        int[] backward = [4, 3, 2, 1, 0, 4];
        foreach (int expected in backward)
        {
            tab = PanelNavigation.CycleTab(tab, 5, forward: false, isRepeat: false);
            Assert.Equal(expected, tab);
        }
    }

    /// <summary>
    /// A key that repeats because it is held down moves like a press between the ends but stops at them: holding Right
    /// must not spin through the tabs for as long as it is held. The next new press wraps.
    /// </summary>
    [Fact]
    public void CycleTab_HeldKeyStopsAtTheEnds()
    {
        Assert.Equal(3, PanelNavigation.CycleTab(2, 5, forward: true, isRepeat: true));
        Assert.Equal(4, PanelNavigation.CycleTab(3, 5, forward: true, isRepeat: true));
        Assert.Equal(4, PanelNavigation.CycleTab(4, 5, forward: true, isRepeat: true));
        Assert.Equal(0, PanelNavigation.CycleTab(4, 5, forward: true, isRepeat: false));

        Assert.Equal(0, PanelNavigation.CycleTab(1, 5, forward: false, isRepeat: true));
        Assert.Equal(0, PanelNavigation.CycleTab(0, 5, forward: false, isRepeat: true));
        Assert.Equal(4, PanelNavigation.CycleTab(0, 5, forward: false, isRepeat: false));
    }

    /// <summary>
    /// Without a selected tab among those that show (it was just hidden), Right starts at the first tab and Left at the
    /// last; one tab alone stays selected; no tab at all answers -1.
    /// </summary>
    /// <param name="current">The selected position (outside the tabs: none).</param>
    /// <param name="count">How many tabs show.</param>
    /// <param name="forward">Right (<see langword="true"/>) or Left.</param>
    /// <param name="expected">The position to select.</param>
    [Theory]
    [InlineData(-1, 5, true, 0)]
    [InlineData(-1, 5, false, 4)]
    [InlineData(5, 5, true, 0)]
    [InlineData(99, 5, false, 4)]
    [InlineData(0, 1, true, 0)]
    [InlineData(0, 1, false, 0)]
    [InlineData(0, 0, true, -1)]
    [InlineData(-1, 0, false, -1)]
    [InlineData(2, -3, true, -1)]
    public void CycleTab_WithoutSelectionOrTabs(int current, int count, bool forward, int expected)
    {
        Assert.Equal(expected, PanelNavigation.CycleTab(current, count, forward, isRepeat: false));
        Assert.Equal(expected, PanelNavigation.CycleTab(current, count, forward, isRepeat: true));
    }

    /// <summary>
    /// Left and Right switch tabs with an empty search box, once an arrow key steered the panel, and when a tab or a
    /// button has the keyboard; they are the text's while the box holds text that is being edited, and always with a
    /// modifier key. While the keys walk the groups column, Right leaves it and Left is swallowed.
    /// </summary>
    /// <param name="right">The Right key (<see langword="true"/>) or Left.</param>
    /// <param name="hasModifier">Ctrl, Shift, Alt or Win is held.</param>
    /// <param name="inGroups">The keys walk the groups column.</param>
    /// <param name="inTextBox">The key was typed into the search box.</param>
    /// <param name="hasSearchText">The search box holds text.</param>
    /// <param name="isNavigating">An arrow key steered the panel last.</param>
    /// <param name="expected">What the key does.</param>
    [Theory]
    [InlineData(true, false, false, true, false, false, HorizontalArrowAction.NextTab)]      // empty box: nothing to edit
    [InlineData(false, false, false, true, false, false, HorizontalArrowAction.PreviousTab)]
    [InlineData(true, false, false, true, true, false, HorizontalArrowAction.NotHandled)]    // editing "foo": the caret moves
    [InlineData(false, false, false, true, true, false, HorizontalArrowAction.NotHandled)]
    [InlineData(true, false, false, true, true, true, HorizontalArrowAction.NextTab)]        // "foo", then Down: tabs again
    [InlineData(false, false, false, true, true, true, HorizontalArrowAction.PreviousTab)]
    [InlineData(true, false, false, false, true, false, HorizontalArrowAction.NextTab)]      // a tab has the keyboard
    [InlineData(false, false, false, false, false, false, HorizontalArrowAction.PreviousTab)]
    [InlineData(true, true, false, true, false, true, HorizontalArrowAction.NotHandled)]     // Ctrl+Right, Shift+Right, …
    [InlineData(false, true, false, false, false, true, HorizontalArrowAction.NotHandled)]
    [InlineData(true, true, true, true, true, true, HorizontalArrowAction.NotHandled)]
    [InlineData(true, false, true, true, true, false, HorizontalArrowAction.LeaveGroups)]    // in the column: Right is the way out
    [InlineData(true, false, true, false, false, true, HorizontalArrowAction.LeaveGroups)]
    [InlineData(false, false, true, true, true, false, HorizontalArrowAction.Swallow)]       // nothing lies further left
    [InlineData(false, false, true, true, false, true, HorizontalArrowAction.Swallow)]
    public void HorizontalArrow_BelongsToTabsTextOrGroups(
        bool right, bool hasModifier, bool inGroups, bool inTextBox, bool hasSearchText, bool isNavigating, HorizontalArrowAction expected)
    {
        Assert.Equal(expected, PanelNavigation.HorizontalArrow(right, hasModifier, inGroups, inTextBox, hasSearchText, isNavigating));
    }

    /// <summary>
    /// Up and Down move one row, the page keys <see cref="PanelNavigation.PageStep"/> rows, all clamped to the list; with
    /// no row selected they select the first one (Page Down the fifth), and an empty list has no row to select.
    /// </summary>
    /// <param name="selected">The selected row (negative: none).</param>
    /// <param name="count">Rows in the list.</param>
    /// <param name="delta">The key's movement.</param>
    /// <param name="expected">The row to select, or -1.</param>
    [Theory]
    [InlineData(0, 10, 1, 1)]
    [InlineData(3, 10, -1, 2)]
    [InlineData(9, 10, 1, 9)]
    [InlineData(0, 10, -1, 0)]
    [InlineData(8, 10, 5, 9)]
    [InlineData(2, 10, -5, 0)]
    [InlineData(4, 10, 5, 9)]
    [InlineData(-1, 10, 1, 0)]
    [InlineData(-1, 10, -1, 0)]
    [InlineData(-1, 10, 5, 4)]
    [InlineData(-7, 3, 5, 2)]
    [InlineData(0, 1, 1, 0)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(-1, 0, -1, -1)]
    [InlineData(5, 10, int.MaxValue, 9)]
    [InlineData(5, 10, int.MinValue, 0)]
    public void MoveInList_MovesAndClamps(int selected, int count, int delta, int expected)
    {
        // Without a groups column to walk into, every key stays in the list, new press or held.
        Assert.Equal(new ListMove(expected, false), PanelNavigation.MoveInList(selected, count, delta, isRepeat: false, groupsReachable: false));
        Assert.Equal(new ListMove(expected, false), PanelNavigation.MoveInList(selected, count, delta, isRepeat: true, groupsReachable: false));
    }

    /// <summary>
    /// Only a new press of Up on the top row walks into the groups column — and only while there is a column with groups
    /// to walk. A held Up stops at the top row (holding it to return from far down a list must not run on through the
    /// groups), Page Up never leaves the list, and Up from below the top row or without a selection moves in the list.
    /// </summary>
    [Fact]
    public void MoveInList_EntersTheGroupsColumnOnlyOnANewPressOfUpAtTheTop()
    {
        Assert.Equal(new ListMove(-1, true), PanelNavigation.MoveInList(0, 10, -1, isRepeat: false, groupsReachable: true));

        Assert.Equal(new ListMove(0, false), PanelNavigation.MoveInList(0, 10, -1, isRepeat: true, groupsReachable: true));
        Assert.Equal(new ListMove(0, false), PanelNavigation.MoveInList(0, 10, -1, isRepeat: false, groupsReachable: false));
        Assert.Equal(new ListMove(0, false), PanelNavigation.MoveInList(0, 10, -PanelNavigation.PageStep, isRepeat: false, groupsReachable: true));
        Assert.Equal(new ListMove(0, false), PanelNavigation.MoveInList(1, 10, -1, isRepeat: false, groupsReachable: true));
        Assert.Equal(new ListMove(0, false), PanelNavigation.MoveInList(-1, 10, -1, isRepeat: false, groupsReachable: true));
        Assert.Equal(new ListMove(1, false), PanelNavigation.MoveInList(0, 10, 1, isRepeat: false, groupsReachable: true));

        // An empty list (an empty group, a search without matches) has no row to stand on: Up still reaches the column.
        Assert.Equal(new ListMove(-1, true), PanelNavigation.MoveInList(-1, 0, -1, isRepeat: false, groupsReachable: true));
        Assert.Equal(new ListMove(-1, false), PanelNavigation.MoveInList(-1, 0, -1, isRepeat: true, groupsReachable: true));
        Assert.Equal(new ListMove(-1, false), PanelNavigation.MoveInList(-1, 0, 1, isRepeat: false, groupsReachable: true));
    }

    /// <summary>
    /// The keys walk into the column on its lowest group and move through it by position — 0 is the clipboard icon (the
    /// whole history), then the groups from the top — stopping at both ends: only Right leaves the column.
    /// </summary>
    [Fact]
    public void Groups_EnterAtTheLowest_AndStopAtBothEnds()
    {
        Assert.Equal(3, PanelNavigation.EnterGroups(3));
        Assert.Equal(0, PanelNavigation.EnterGroups(0));
        Assert.Equal(0, PanelNavigation.EnterGroups(-2));

        // Up from the lowest of three groups: the two above, the clipboard icon, and there it stays.
        int[] up = [2, 1, 0, 0];
        int position = PanelNavigation.EnterGroups(3);
        foreach (int expected in up)
        {
            position = PanelNavigation.MoveInGroups(position, 3, -1);
            Assert.Equal(expected, position);
        }

        // Down again to the lowest group, and there it stays too (it never returns to the list by itself).
        int[] down = [1, 2, 3, 3];
        foreach (int expected in down)
        {
            position = PanelNavigation.MoveInGroups(position, 3, 1);
            Assert.Equal(expected, position);
        }

        Assert.Equal(4, PanelNavigation.MoveInGroups(9, 12, -PanelNavigation.PageStep));
        Assert.Equal(12, PanelNavigation.MoveInGroups(9, 12, PanelNavigation.PageStep));
        Assert.Equal(0, PanelNavigation.MoveInGroups(2, 12, -PanelNavigation.PageStep));
    }

    /// <summary>
    /// Positions and counts that no longer fit (a group deleted under the keys, a column that emptied) are clamped
    /// instead of trusted, and extreme movements do not overflow.
    /// </summary>
    /// <param name="position">The position before the key.</param>
    /// <param name="groupCount">Groups in the column.</param>
    /// <param name="delta">The key's movement.</param>
    /// <param name="expected">The position after it.</param>
    [Theory]
    [InlineData(99, 3, -1, 2)]
    [InlineData(-4, 3, 1, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(2, -1, 1, 0)]
    [InlineData(2, 3, int.MaxValue, 3)]
    [InlineData(2, 3, int.MinValue, 0)]
    public void Groups_ClampWhatNoLongerFits(int position, int groupCount, int delta, int expected)
    {
        Assert.Equal(expected, PanelNavigation.MoveInGroups(position, groupCount, delta));
    }
}

/// <summary>
/// Tests for <see cref="PointerSelectionGate"/>: the mouse selects the row it is over only after the cursor really moved
/// since the keyboard, a reload or a summon set the selection.
/// </summary>
public sealed class PointerSelectionGateTests
{
    /// <summary>The system's drag threshold at 100 %, which the panel uses.</summary>
    private const int Threshold = 4;

    /// <summary>
    /// After the keyboard set the selection, pointer events at the cursor's unchanged spot — or within the threshold of it
    /// — select nothing (a row that scrolled under a mouse lying still, a mouse nudged while typing); past the threshold
    /// on either axis the mouse takes over and keeps the selection wherever it goes, back on the old spot included.
    /// </summary>
    [Fact]
    public void Suspend_IgnoresPointerEventsUntilTheCursorLeavesItsSpot()
    {
        var gate = new PointerSelectionGate();
        gate.Suspend(500, 300, Threshold);
        Assert.False(gate.IsPointerActive);

        Assert.False(gate.Observe(500, 300));
        Assert.False(gate.Observe(504, 300));
        Assert.False(gate.Observe(496, 304));
        Assert.False(gate.Observe(500, 296));
        Assert.False(gate.IsPointerActive);

        Assert.True(gate.Observe(505, 300));
        Assert.True(gate.IsPointerActive);
        Assert.True(gate.Observe(500, 300));
        Assert.True(gate.Observe(-2000, 9000));
    }

    /// <summary>Vertical travel counts like horizontal travel, in both directions.</summary>
    /// <param name="x">The cursor's horizontal position after the move.</param>
    /// <param name="y">Its vertical position.</param>
    /// <param name="moved">Whether that is far enough from (500, 300) to count as moved.</param>
    [Theory]
    [InlineData(500, 305, true)]
    [InlineData(500, 295, true)]
    [InlineData(495, 300, true)]
    [InlineData(503, 303, false)]
    [InlineData(496, 297, false)]
    public void Observe_CountsTravelOnEitherAxis(int x, int y, bool moved)
    {
        var gate = new PointerSelectionGate();
        gate.Suspend(500, 300, Threshold);
        Assert.Equal(moved, gate.Observe(x, y));
    }

    /// <summary>
    /// Every key press or reload hands the selection back: the mouse stops selecting again, and must leave the spot it is
    /// on then — not the one it left earlier.
    /// </summary>
    [Fact]
    public void Suspend_TakesTheSelectionBackEveryTime()
    {
        var gate = new PointerSelectionGate();
        gate.Suspend(100, 100, Threshold);
        Assert.True(gate.Observe(140, 100));

        gate.Suspend(140, 100, Threshold);
        Assert.False(gate.IsPointerActive);
        Assert.False(gate.Observe(140, 100));
        Assert.False(gate.Observe(143, 101));

        // Back near the first spot: far from the new one, so it counts.
        Assert.True(gate.Observe(100, 100));
    }

    /// <summary>
    /// A gate that was never told where the cursor is — a new one, or one suspended while the position could not be read
    /// — takes the first position it sees as the spot to leave: one event is ignored rather than a guess taken for movement.
    /// </summary>
    [Fact]
    public void WithoutAKnownSpot_TheFirstPositionSeenBecomesIt()
    {
        var fresh = new PointerSelectionGate();
        Assert.False(fresh.IsPointerActive);
        Assert.False(fresh.Observe(10, 10));
        Assert.False(fresh.Observe(10, 10));
        Assert.True(fresh.Observe(11, 10)); // a new gate has no threshold yet: any change counts

        var gate = new PointerSelectionGate();
        gate.Suspend(100, 100, Threshold);
        Assert.True(gate.Observe(200, 100));
        gate.Suspend(Threshold);
        Assert.False(gate.IsPointerActive);
        Assert.False(gate.Observe(700, 700));
        Assert.False(gate.Observe(703, 700));
        Assert.True(gate.Observe(706, 700));
    }

    /// <summary>
    /// A turn of the wheel hands the selection to the mouse without any movement; the next key press takes it back.
    /// </summary>
    [Fact]
    public void Activate_HandsTheSelectionToTheMouseWithoutMovement()
    {
        var gate = new PointerSelectionGate();
        gate.Suspend(50, 50, Threshold);
        gate.Activate();
        Assert.True(gate.IsPointerActive);
        Assert.True(gate.Observe(50, 50));

        gate.Suspend(50, 50, Threshold);
        Assert.False(gate.Observe(50, 50));
    }

    /// <summary>
    /// A negative threshold counts as 0 (every change of position is movement, the same position is not), and positions
    /// at the far ends of the coordinate range do not overflow the comparison.
    /// </summary>
    [Fact]
    public void Thresholds_AndExtremeCoordinates()
    {
        var gate = new PointerSelectionGate();
        gate.Suspend(7, 7, -20);
        Assert.False(gate.Observe(7, 7));
        Assert.True(gate.Observe(7, 8));

        gate.Suspend(int.MinValue, int.MaxValue, Threshold);
        Assert.False(gate.Observe(int.MinValue, int.MaxValue));
        Assert.True(gate.Observe(int.MaxValue, int.MaxValue));

        gate.Suspend(0, int.MaxValue, Threshold);
        Assert.True(gate.Observe(0, int.MinValue));
    }
}

/// <summary>
/// Tests for the remembered tab (<see cref="AppSettings.LastTab"/>): its default, persistence by name, and the lenient
/// reading that keeps a name this version does not know from costing more than the remembered tab.
/// </summary>
public sealed class LastTabSettingsTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// The panel starts in "All" — also with a settings file written before the tab was remembered — and a chosen tab
    /// persists by its name, without the derived filter value reaching the file.
    /// </summary>
    [Fact]
    public void LastTab_DefaultsToAll_AndPersistsByName()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"MaxItems\": 50 }");
        var store = new SettingsStore(path);
        Assert.Equal(AppSettings.DefaultTab, store.Load().LastTab);
        Assert.Equal("All", AppSettings.DefaultTab);
        Assert.Equal(ClipFilter.All, store.Current.LastTabFilter);
        Assert.Equal(ClipFilter.All, new AppSettings().LastTabFilter);

        store.Update(s => s with { LastTab = ClipFilter.ClaudeCode.ToString() });

        var reloaded = new SettingsStore(path).Load();
        Assert.Equal("ClaudeCode", reloaded.LastTab);
        Assert.Equal(ClipFilter.ClaudeCode, reloaded.LastTabFilter);
        var json = File.ReadAllText(path);
        Assert.Contains("\"LastTab\": \"ClaudeCode\"", json);
        Assert.DoesNotContain("LastTabFilter", json);
    }

    /// <summary>
    /// A tab name is read ignoring case and surrounding spaces; anything else — blank, unknown, a number, a list of
    /// names — is "All", never an exception and never some other tab.
    /// </summary>
    /// <param name="name">The stored text.</param>
    /// <param name="expected">The tab it stands for.</param>
    [Theory]
    [InlineData("All", ClipFilter.All)]
    [InlineData("Pinned", ClipFilter.Pinned)]
    [InlineData("pinned", ClipFilter.Pinned)]
    [InlineData("  Files ", ClipFilter.Files)]
    [InlineData("CLAUDECODE", ClipFilter.ClaudeCode)]
    [InlineData("PowerShell", ClipFilter.PowerShell)]
    [InlineData("Everything", ClipFilter.Everything)]
    [InlineData(null, ClipFilter.All)]
    [InlineData("", ClipFilter.All)]
    [InlineData("   ", ClipFilter.All)]
    [InlineData("Pwsh", ClipFilter.All)]          // the tab's label, not its filter's name
    [InlineData("Screenshots", ClipFilter.All)]
    [InlineData("3", ClipFilter.All)]             // Enum.TryParse would say Images
    [InlineData("99", ClipFilter.All)]
    [InlineData("-1", ClipFilter.All)]
    [InlineData("Pinned, Text", ClipFilter.All)]  // Enum.TryParse would combine them into Images
    public void ParseTab_ReadsNamesLeniently(string? name, ClipFilter expected)
    {
        Assert.Equal(expected, AppSettings.ParseTab(name));
    }

    /// <summary>Every tab's filter comes back from its own name, so no tab can be chosen that the next start forgets.</summary>
    [Fact]
    public void ParseTab_RoundTripsEveryFilter()
    {
        foreach (var filter in Enum.GetValues<ClipFilter>())
        {
            Assert.Equal(filter, AppSettings.ParseTab(filter.ToString()));
            Assert.Equal(filter, new AppSettings { LastTab = filter.ToString() }.Normalize().LastTabFilter);
        }
    }

    /// <summary>
    /// Loading and saving leave a canonical name in the file: another spelling is corrected, and a name no tab answers to
    /// (a typo, a tab of a newer version) or a JSON null becomes "All" — without setting the file aside as corrupt, which
    /// is what an enum member written by name would cost.
    /// </summary>
    [Fact]
    public void Normalize_KeepsACanonicalName_AndUnknownNamesAreHarmless()
    {
        Assert.Equal("Run", new AppSettings { LastTab = " run " }.Normalize().LastTab);
        Assert.Equal("All", new AppSettings { LastTab = "TabOfTheFuture" }.Normalize().LastTab);
        Assert.Equal("All", new AppSettings { LastTab = null! }.Normalize().LastTab);

        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"LastTab\": \"TabOfTheFuture\", \"MaxItems\": 77 }");
        var loaded = new SettingsStore(path).Load();
        Assert.Equal("All", loaded.LastTab);
        Assert.Equal(77, loaded.MaxItems); // the rest of the file was read
        Assert.Empty(Directory.GetFiles(temp.Path, "settings.json.corrupt-*"));

        File.WriteAllText(path, "{ \"LastTab\": null, \"MaxItems\": 78 }");
        loaded = new SettingsStore(path).Load();
        Assert.Equal(ClipFilter.All, loaded.LastTabFilter);
        Assert.Equal(78, loaded.MaxItems);
    }
}
