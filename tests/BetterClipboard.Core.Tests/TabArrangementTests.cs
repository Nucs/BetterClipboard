using System.Text.RegularExpressions;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Arranging the panel's tabs from their right-click menu (CLAUDE.md §2.40): <see cref="TabArrangement"/> — the order
/// shown, the moves, what is stored — and its settings, <see cref="AppSettings.TabOrder"/> and
/// <see cref="AppSettings.HiddenTabs"/>.
/// </summary>
public sealed class TabArrangementTests : IDisposable
{
    /// <summary>The panel's default order, as the XAML declares the tabs (checked against it by <see cref="Xaml_DeclaresOneTabPerFilter_InTheTestedOrder"/>).</summary>
    private static readonly string[] Defaults =
        ["All", "Pinned", "Text", "Images", "Links", "Files", "ShareX", "Snipping", "NotepadPlusPlus", "Run", "PowerShell", "Cmd", "ClaudeCode", "Codex", "Everything"];

    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// Shown-ness patterns for the property check: every tab, the six built-in tabs only, and the other apps' tabs
    /// missing in a checkerboard.
    /// </summary>
    public static TheoryData<string> ShownPatterns => new()
    {
        "111111111111111",
        "111111000000000",
        "101010100101010",
        "110000111110011",
        "100000000000001",
    };

    /// <summary>No stored order (a new install, or the default restored) shows the tabs in the default order.</summary>
    [Fact]
    public void Arrange_WithoutAStoredOrder_IsTheDefault()
    {
        Assert.Equal(Defaults, TabArrangement.Arrange(Defaults, null));
        Assert.Equal(Defaults, TabArrangement.Arrange(Defaults, []));
    }

    /// <summary>A stored order that names every tab is shown as stored.</summary>
    [Fact]
    public void Arrange_KeepsAFullStoredOrder()
    {
        string[] stored = ["All", "Files", "Pinned", "Codex", "Text", "Images", "Links", "ShareX", "Snipping", "NotepadPlusPlus", "Run", "PowerShell", "Cmd", "ClaudeCode", "Everything"];
        Assert.Equal(stored, TabArrangement.Arrange(Defaults, stored));
    }

    /// <summary>
    /// A tab the stored order lacks — one a newer version added after the order was stored — goes right after its
    /// predecessor in the default order, wherever the user put that one.
    /// </summary>
    [Fact]
    public void Arrange_PutsAMissingTabAfterItsDefaultPredecessor()
    {
        // Stored by a version without the Snipping, Notepad++ and Everything tabs, Files moved to the front.
        string[] stored = ["All", "Files", "Pinned", "Text", "Images", "Links", "ShareX", "Run", "PowerShell", "Cmd", "ClaudeCode", "Codex"];

        var arranged = TabArrangement.Arrange(Defaults, stored);

        Assert.Equal(["All", "Files", "Pinned", "Text", "Images", "Links", "ShareX", "Snipping", "NotepadPlusPlus", "Run", "PowerShell", "Cmd", "ClaudeCode", "Codex", "Everything"], arranged);
    }

    /// <summary>
    /// A stored order of just a few tabs (hand-edited) keeps those in front in its order and lets each missing tab follow
    /// its default predecessor; every tab appears once.
    /// </summary>
    [Fact]
    public void Arrange_CompletesAPartialOrder()
    {
        var arranged = TabArrangement.Arrange(Defaults, ["Codex", "Pinned"]).ToList();

        Assert.Equal(Defaults.Length, arranged.Count);
        Assert.Equal(Defaults.Length, arranged.Distinct().Count());
        Assert.Equal("All", arranged[0]);

        // Text..Files follow Pinned (their default predecessor chain); Everything follows Codex.
        Assert.Equal(arranged.IndexOf("Pinned") + 1, arranged.IndexOf("Text"));
        Assert.Equal(arranged.IndexOf("Codex") + 1, arranged.IndexOf("Everything"));
        Assert.True(arranged.IndexOf("Codex") < arranged.IndexOf("Pinned"));
    }

    /// <summary>
    /// Names are matched ignoring case and spaces and spelled as the defaults spell them; repeats, blanks, null and names
    /// no tab answers to are skipped, never an exception.
    /// </summary>
    [Fact]
    public void Arrange_ReadsStoredNamesLeniently()
    {
        string?[] stored = ["all", " pinned ", "PINNED", "Bogus", null, "", "3", "Pinned, Text", "TEXT"];

        var arranged = TabArrangement.Arrange(Defaults, stored!);

        Assert.Equal(Defaults, arranged);
    }

    /// <summary>"All" is first whatever the stored order says: stored in the middle, or not stored at all.</summary>
    [Fact]
    public void Arrange_KeepsAllFirst()
    {
        Assert.Equal("All", TabArrangement.Arrange(Defaults, ["Pinned", "Text", "All", "Images"])[0]);
        Assert.Equal("All", TabArrangement.Arrange(Defaults, ["Codex", "Files"])[0]);

        // The others keep the stored order behind it.
        Assert.Equal(["All", "Text", "Pinned"], TabArrangement.Arrange(["All", "Pinned", "Text"], ["Text", "Pinned", "All"]));
    }

    /// <summary>Move right puts the tab right after its shown neighbor; Move left right before it.</summary>
    [Fact]
    public void Move_OnePlaceEitherWay()
    {
        Assert.Equal(["All", "Text", "Pinned", "Images"], TabArrangement.Move(["All", "Pinned", "Text", "Images"], "Pinned", toRight: true, AllShown));
        Assert.Equal(["All", "Pinned", "Images", "Text"], TabArrangement.Move(["All", "Pinned", "Text", "Images"], "Images", toRight: false, AllShown));
    }

    /// <summary>
    /// Tabs that do not show (hidden, or an app's tab that is not there) are passed over, and keep their places: a tab
    /// not shown today comes back where it was.
    /// </summary>
    [Fact]
    public void Move_SkipsTabsThatDoNotShow_AndLeavesThemInPlace()
    {
        string[] order = ["All", "Links", "Files", "ShareX", "Snipping", "Run", "PowerShell"];
        bool Shown(string tab) => tab is not ("ShareX" or "Snipping");

        // Run left: before Files, past the two that do not show; they stay after Files.
        Assert.Equal(["All", "Links", "Run", "Files", "ShareX", "Snipping", "PowerShell"], TabArrangement.Move(order, "Run", toRight: false, Shown));

        // Files right: after Run, past the two that do not show; they stay before Run.
        Assert.Equal(["All", "Links", "ShareX", "Snipping", "Run", "Files", "PowerShell"], TabArrangement.Move(order, "Files", toRight: true, Shown));
    }

    /// <summary>
    /// Nothing passes "All": the first shown tab after it cannot move left, also when tabs that do not show lie between.
    /// </summary>
    [Fact]
    public void Move_NothingPassesAll()
    {
        string[] order = ["All", "Pinned", "Text"];
        Assert.False(TabArrangement.CanMove(order, "Pinned", toRight: false, AllShown));
        Assert.Same(order, TabArrangement.Move(order, "Pinned", toRight: false, AllShown));

        // Pinned hidden: Text is now the first shown tab after All.
        Assert.False(TabArrangement.CanMove(order, "Text", toRight: false, tab => tab != "Pinned"));
    }

    /// <summary>"All" itself never moves, either way, whatever the case of its name.</summary>
    [Fact]
    public void Move_AllNeverMoves()
    {
        string[] order = ["All", "Pinned", "Text"];
        Assert.False(TabArrangement.CanMove(order, "All", toRight: true, AllShown));
        Assert.False(TabArrangement.CanMove(order, "all", toRight: true, AllShown));
        Assert.False(TabArrangement.CanMove(order, "All", toRight: false, AllShown));
        Assert.Same(order, TabArrangement.Move(order, "All", toRight: true, AllShown));
    }

    /// <summary>The last shown tab cannot move right, also when tabs that do not show come after it.</summary>
    [Fact]
    public void Move_LastShownTabStays()
    {
        string[] order = ["All", "Pinned", "Text", "Everything"];
        Assert.False(TabArrangement.CanMove(order, "Everything", toRight: true, AllShown));
        Assert.False(TabArrangement.CanMove(order, "Text", toRight: true, tab => tab != "Everything"));
        Assert.True(TabArrangement.CanMove(order, "Text", toRight: true, AllShown));
    }

    /// <summary>A tab the order does not hold moves nowhere, and the order comes back unchanged (the same list).</summary>
    [Fact]
    public void Move_UnknownTab_ChangesNothing()
    {
        string[] order = ["All", "Pinned", "Text"];
        Assert.False(TabArrangement.CanMove(order, "Bogus", toRight: true, AllShown));
        Assert.Same(order, TabArrangement.Move(order, "Bogus", toRight: false, AllShown));
    }

    /// <summary>A move to the right undone by a move to the left gives the order back, also across tabs that do not show.</summary>
    [Fact]
    public void Move_RightThenLeft_IsTheOriginalOrder()
    {
        bool Shown(string tab) => tab is not ("ShareX" or "Snipping" or "Everything");
        foreach (var tab in Defaults.Where(t => t != "All" && Shown(t)))
        {
            if (!TabArrangement.CanMove(Defaults, tab, toRight: true, Shown))
            {
                continue;
            }

            var moved = TabArrangement.Move(Defaults, tab, toRight: true, Shown);
            var back = TabArrangement.Move(moved, tab, toRight: false, Shown);

            // The tab is back right after its old left neighbor; tabs that do not show may have shifted past it.
            Assert.Equal(Defaults.Where(Shown), back.Where(Shown));
        }
    }

    /// <summary>
    /// For every tab, either direction and several patterns of tabs not shown: <see cref="TabArrangement.CanMove"/> says
    /// whether <see cref="TabArrangement.Move"/> changes the order; a move keeps every tab exactly once, moves the tab by
    /// one place among the shown tabs, and leaves the relative order of every other tab alone.
    /// </summary>
    /// <param name="pattern">One character per default tab: 1 shown, 0 not.</param>
    [Theory]
    [MemberData(nameof(ShownPatterns))]
    public void Move_AgreesWithCanMove_AndMovesOneShownPlace(string pattern)
    {
        var shown = Defaults.Where((_, i) => pattern[i] == '1').ToHashSet();
        bool Shown(string tab) => shown.Contains(tab);

        foreach (var tab in Defaults)
        {
            foreach (bool toRight in new[] { false, true })
            {
                bool can = TabArrangement.CanMove(Defaults, tab, toRight, Shown);
                var moved = TabArrangement.Move(Defaults, tab, toRight, Shown);
                Assert.Equal(can, !ReferenceEquals(moved, Defaults));
                Assert.Equal(Defaults.Order(), moved.Order());
                Assert.Equal(Defaults.Where(t => t != tab), moved.Where(t => t != tab));
                Assert.Equal("All", moved[0]);
                if (can && Shown(tab))
                {
                    var before = Defaults.Where(Shown).ToList();
                    var after = moved.Where(Shown).ToList();
                    Assert.Equal(before.IndexOf(tab) + (toRight ? 1 : -1), after.IndexOf(tab));
                }
            }
        }
    }

    /// <summary>Every tab but "All" can be hidden.</summary>
    [Fact]
    public void CanHide_EveryTabButAll()
    {
        Assert.False(TabArrangement.CanHide("All"));
        Assert.False(TabArrangement.CanHide("all"));
        Assert.All(Defaults.Skip(1), tab => Assert.True(TabArrangement.CanHide(tab)));
    }

    /// <summary>The default order is stored as an empty list (so a later default reaches the user); any other whole.</summary>
    [Fact]
    public void ToStored_EmptyForTheDefault_WholeOtherwise()
    {
        Assert.Empty(TabArrangement.ToStored(Defaults, Defaults));
        Assert.Empty(TabArrangement.ToStored(Defaults.Select(t => t.ToLowerInvariant()).ToArray(), Defaults));

        var moved = TabArrangement.Move(Defaults, "Files", toRight: false, AllShown);
        Assert.Equal(moved, TabArrangement.ToStored(moved, Defaults));

        // Moved back: the default again, stored empty.
        Assert.Empty(TabArrangement.ToStored(TabArrangement.Move(moved, "Files", toRight: true, AllShown), Defaults));
    }

    /// <summary>A new install, and a settings file from before these members, have neither an order nor hidden tabs.</summary>
    [Fact]
    public void Settings_DefaultAndFileFromBefore_AreEmpty()
    {
        var fresh = new SettingsStore(Path.Combine(temp.Path, "fresh.json")).Load();
        Assert.Empty(fresh.TabOrder);
        Assert.Empty(fresh.HiddenTabs);

        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"LastTab\": \"Files\", \"MaxItems\": 50 }");
        var old = new SettingsStore(path).Load();
        Assert.Empty(old.TabOrder);
        Assert.Empty(old.HiddenTabs);
        Assert.Equal(50, old.MaxItems);
    }

    /// <summary>
    /// Both lists keep the names a tab answers to, spelled as their filter, each once, in the stored order; unknown names
    /// are dropped; "All" is never hidden (a hand-edited file cannot hide it). Normalizing twice changes nothing.
    /// </summary>
    [Fact]
    public void Settings_Normalize_KeepsKnownNamesOnce_AndNeverHidesAll()
    {
        var settings = new AppSettings
        {
            TabOrder = ["files", " Pinned ", "Bogus", "FILES", "All", "", null!, "3", "Codex"],
            HiddenTabs = ["all", "cmd", "Cmd", "Nope", "ShareX"],
        }.Normalize();

        Assert.Equal(["Files", "Pinned", "All", "Codex"], settings.TabOrder);
        Assert.Equal(["Cmd", "ShareX"], settings.HiddenTabs);

        var again = settings.Normalize();
        Assert.Equal(settings.TabOrder, again.TabOrder);
        Assert.Equal(settings.HiddenTabs, again.HiddenTabs);
    }

    /// <summary>An arranged order and the hidden tabs survive a save and a load.</summary>
    [Fact]
    public void Settings_RoundTrip()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();
        var order = TabArrangement.ToStored(TabArrangement.Move(Defaults, "Codex", toRight: false, AllShown), Defaults);
        store.Update(s => s with { TabOrder = order, HiddenTabs = ["Images", "Run"] });

        var loaded = new SettingsStore(path).Load();

        Assert.Equal(order, loaded.TabOrder);
        Assert.Equal(["Images", "Run"], loaded.HiddenTabs);
        Assert.Equal(TabArrangement.Arrange(Defaults, order), TabArrangement.Arrange(Defaults, loaded.TabOrder));
    }

    /// <summary>
    /// <see cref="AppSettings.TryParseTab"/> takes a filter's name in any case and with spaces around it, and refuses
    /// everything else — numbers and comma-separated lists included — without reading it as "All".
    /// </summary>
    [Fact]
    public void TryParseTab_NamesOnly()
    {
        Assert.True(AppSettings.TryParseTab(" claudecode ", out var claude));
        Assert.Equal(ClipFilter.ClaudeCode, claude);
        Assert.True(AppSettings.TryParseTab("All", out var all));
        Assert.Equal(ClipFilter.All, all);
        foreach (var text in new[] { null, "", "  ", "3", "Pinned, Text", "Bogus" })
        {
            Assert.False(AppSettings.TryParseTab(text, out _));
        }
    }

    /// <summary>
    /// The panel's XAML declares exactly one tab per <see cref="ClipFilter"/>, tagged with the filter's name, in the order
    /// these tests use as the default. The panel reads its default order from the XAML, and the settings store tab names
    /// as filter names, so a tab tagged otherwise could never be arranged or hidden.
    /// </summary>
    [Fact]
    public void Xaml_DeclaresOneTabPerFilter_InTheTestedOrder()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "BetterClipboard.App", "Views", "ClipboardFlyout.xaml"));
        var tags = Regex.Matches(xaml, @"<SelectorBarItem\b[^>]*?\bTag=""([^""]+)""", RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(Defaults, tags);
        Assert.Equal(Enum.GetNames<ClipFilter>().Order(), tags.Order());
    }

    /// <summary>Every tab shows: the visibility test for the plain cases.</summary>
    /// <param name="tab">A tab's name.</param>
    /// <returns><see langword="true"/>.</returns>
    private static bool AllShown(string tab) => true;

    /// <summary>
    /// The repository root: the nearest folder above the test binaries that holds <c>BetterClipboard.sln</c> (as in
    /// <c>ThirdPartyCatalogTests</c>).
    /// </summary>
    /// <returns>The root folder.</returns>
    /// <exception cref="InvalidOperationException">The tests do not run from inside the repository.</exception>
    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "BetterClipboard.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"BetterClipboard.sln was not found above {AppContext.BaseDirectory}.");
    }
}
