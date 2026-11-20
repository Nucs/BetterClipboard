using System.Text.Json;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the order of the panel header's buttons (Settings › Shortcut › Buttons in the panel, its up and down arrows;
/// CLAUDE.md §2.58): the order shown from the stored one, where a move puts a button and which moves are possible, what is
/// stored, and the setting's reading, normalizing and file name. The Settings button is in no order: it always ends the
/// header.
/// </summary>
public sealed class PanelButtonOrderTests : IDisposable
{
    /// <summary>Every button Settings lists in a copy with an update system.</summary>
    private static readonly Func<PanelButton, bool> Everything = _ => true;

    /// <summary>The Microsoft Store edition's list: no update button (its updates come from the Store).</summary>
    private static readonly Func<PanelButton, bool> NoUpdates = button => button != PanelButton.Updates;

    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// A new install, and a settings file from before the order existed, keep the buttons in the default order and store
    /// nothing; the file loads without being set aside.
    /// </summary>
    [Fact]
    public void Defaults_TheDefaultOrder_StoredEmpty()
    {
        Assert.Empty(new AppSettings().PanelButtonOrder);
        Assert.Equal(PanelButtons.All, PanelButtons.Arrange(null));
        Assert.Equal(PanelButtons.All, PanelButtons.Arrange([]));

        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"HiddenPanelButtons\": [\"Pause\"] }");
        var loaded = new SettingsStore(path).Load();
        Assert.Empty(loaded.PanelButtonOrder);
        Assert.Equal(PanelButtons.All, PanelButtons.Arrange(loaded.PanelButtonOrder));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.corrupt*"));
    }

    /// <summary>
    /// A stored order is shown as stored, completed with the buttons it lacks, each right after its predecessor in the
    /// default order wherever the user put that one; names are read leniently, and repeats, "Settings" and unknown names are
    /// skipped.
    /// </summary>
    [Fact]
    public void Arrange_CompletesTheStoredOrder()
    {
        Assert.Equal(
            [PanelButton.Clear, PanelButton.Groups, PanelButton.Pause, PanelButton.Updates, PanelButton.KeepOpen],
            PanelButtons.Arrange(["Clear", "Groups", "Pause", "Updates", "KeepOpen"]));

        // KeepOpen (first by default) goes first; Updates after KeepOpen; Groups after Pause, wherever Pause stands.
        Assert.Equal(
            [PanelButton.KeepOpen, PanelButton.Updates, PanelButton.Clear, PanelButton.Pause, PanelButton.Groups],
            PanelButtons.Arrange(["Clear", "Pause"]));

        // Groups and Pause as stored; Clear follows Groups, its predecessor by default, wherever Groups now stands.
        Assert.Equal(
            [PanelButton.KeepOpen, PanelButton.Updates, PanelButton.Groups, PanelButton.Clear, PanelButton.Pause],
            PanelButtons.Arrange([" groups ", "Settings", "GROUPS", "Pause", "Pin", "3", "", null!]));
    }

    /// <summary>
    /// Up puts a button right before the nearest listed button above it, down right after the nearest one below it; the top
    /// button cannot go up, the bottom one cannot go down, and a move that cannot be made returns the same order.
    /// </summary>
    [Fact]
    public void Move_OnePlaceEachWay()
    {
        var order = PanelButtons.All;
        Assert.Equal(
            [PanelButton.KeepOpen, PanelButton.Pause, PanelButton.Updates, PanelButton.Groups, PanelButton.Clear],
            PanelButtons.Move(order, PanelButton.Pause, later: false, Everything));
        Assert.Equal(
            [PanelButton.KeepOpen, PanelButton.Updates, PanelButton.Groups, PanelButton.Pause, PanelButton.Clear],
            PanelButtons.Move(order, PanelButton.Pause, later: true, Everything));

        Assert.False(PanelButtons.CanMove(order, PanelButton.KeepOpen, later: false, Everything));
        Assert.Same(order, PanelButtons.Move(order, PanelButton.KeepOpen, later: false, Everything));
        Assert.False(PanelButtons.CanMove(order, PanelButton.Clear, later: true, Everything));
        Assert.Same(order, PanelButtons.Move(order, PanelButton.Clear, later: true, Everything));
        Assert.True(PanelButtons.CanMove(order, PanelButton.KeepOpen, later: true, Everything));
        Assert.True(PanelButtons.CanMove(order, PanelButton.Clear, later: false, Everything));
    }

    /// <summary>
    /// Where Settings does not list the update button (the Microsoft Store edition), a move steps over it so every press
    /// visibly moves the row, and the update button keeps its place between the others; with it last, the button before it
    /// can no longer go down.
    /// </summary>
    [Fact]
    public void Move_StepsOverAButtonSettingsDoesNotList()
    {
        var order = PanelButtons.All; // KeepOpen, Updates, Pause, Groups, Clear
        Assert.Equal(
            [PanelButton.Pause, PanelButton.KeepOpen, PanelButton.Updates, PanelButton.Groups, PanelButton.Clear],
            PanelButtons.Move(order, PanelButton.Pause, later: false, NoUpdates));
        Assert.Equal(
            [PanelButton.Updates, PanelButton.Pause, PanelButton.KeepOpen, PanelButton.Groups, PanelButton.Clear],
            PanelButtons.Move(order, PanelButton.KeepOpen, later: true, NoUpdates));

        IReadOnlyList<PanelButton> updatesLast = [PanelButton.KeepOpen, PanelButton.Pause, PanelButton.Groups, PanelButton.Clear, PanelButton.Updates];
        Assert.False(PanelButtons.CanMove(updatesLast, PanelButton.Clear, later: true, NoUpdates));
        Assert.True(PanelButtons.CanMove(updatesLast, PanelButton.Clear, later: true, Everything));
    }

    /// <summary>
    /// A button not in the order (an order a test builds by hand) cannot move, and moving it returns the order unchanged.
    /// </summary>
    [Fact]
    public void Move_AButtonNotInTheOrder_ChangesNothing()
    {
        IReadOnlyList<PanelButton> partial = [PanelButton.Pause, PanelButton.Clear];
        Assert.False(PanelButtons.CanMove(partial, PanelButton.Groups, later: true, Everything));
        Assert.Same(partial, PanelButtons.Move(partial, PanelButton.Groups, later: false, Everything));
    }

    /// <summary>
    /// The default order is stored as nothing (so a later version's default reaches users who never moved a button), any
    /// other order as every name; every one of the 120 orders survives being stored and read back.
    /// </summary>
    [Fact]
    public void ToStored_EmptyForTheDefault_AndRoundTripsEveryOrder()
    {
        Assert.Empty(PanelButtons.ToStored(PanelButtons.All));
        Assert.Equal(["Pause", "KeepOpen", "Updates", "Groups", "Clear"],
            PanelButtons.ToStored([PanelButton.Pause, PanelButton.KeepOpen, PanelButton.Updates, PanelButton.Groups, PanelButton.Clear]));

        int orders = 0;
        foreach (var order in Permutations(PanelButtons.All.ToList()))
        {
            orders++;
            Assert.Equal(order, PanelButtons.Arrange(PanelButtons.ToStored(order)));
            Assert.Equal(order, PanelButtons.Arrange(PanelButtons.NormalizeOrder(PanelButtons.ToStored(order))));
        }

        Assert.Equal(120, orders);
    }

    /// <summary>
    /// Random arrow presses always leave every button exactly once; a press moves the button exactly when
    /// <see cref="PanelButtons.CanMove"/> says it can, by one listed place; the update button keeps its place among the
    /// others where it is not listed; and pressing the other arrow right after undoes the move as Settings' list shows it.
    /// </summary>
    /// <param name="storeEdition">Whether the update button is unlisted (the Microsoft Store edition).</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomPresses_KeepEveryButtonOnce(bool storeEdition)
    {
        var isListed = storeEdition ? NoUpdates : Everything;
        var random = new Random(20261120 + (storeEdition ? 1 : 0));
        IReadOnlyList<PanelButton> order = PanelButtons.All;
        for (int press = 0; press < 2000; press++)
        {
            var button = PanelButtons.All[random.Next(PanelButtons.All.Count)];
            bool later = random.Next(2) == 1;
            bool can = PanelButtons.CanMove(order, button, later, isListed);
            var moved = PanelButtons.Move(order, button, later, isListed);

            Assert.Equal(PanelButtons.All.Count, moved.Distinct().Count());
            Assert.Equal(PanelButtons.All.OrderBy(b => b), moved.OrderBy(b => b));
            if (!can || !isListed(button))
            {
                Assert.Equal(order, moved);
                continue;
            }

            Assert.NotEqual(order, moved);

            // One listed place: the listed buttons' order changes by swapping the button with its listed neighbor.
            var listedBefore = order.Where(isListed).ToList();
            var listedAfter = moved.Where(isListed).ToList();
            int at = listedBefore.IndexOf(button);
            int to = at + (later ? 1 : -1);
            (listedBefore[at], listedBefore[to]) = (listedBefore[to], listedBefore[at]);
            Assert.Equal(listedBefore, listedAfter);

            // An unlisted button keeps the listed button it followed (or stays first).
            if (storeEdition)
            {
                int before = order.ToList().IndexOf(PanelButton.Updates);
                int after = moved.ToList().IndexOf(PanelButton.Updates);
                var followedBefore = before == 0 ? (PanelButton?)null : order[before - 1];
                var followedAfter = after == 0 ? (PanelButton?)null : moved[after - 1];
                if (followedBefore != button && followedAfter != button)
                {
                    Assert.Equal(followedBefore, followedAfter);
                }
            }

            // The other arrow undoes the move as the user sees it (the listed buttons' order). Where every button is listed
            // that is the whole order; an unlisted button the press stepped over may end up on the other side of the moved
            // one (it shows nowhere, so only its place relative to the other unlisted ones would matter, and there are none).
            var undone = PanelButtons.Move(moved, button, !later, isListed);
            Assert.Equal(order.Where(isListed), undone.Where(isListed));
            if (!storeEdition)
            {
                Assert.Equal(order, undone);
            }

            order = moved;
        }
    }

    /// <summary>
    /// A hand-edited order keeps the known names, canonical, once each, in the stored order — "Settings", typos, numbers,
    /// blanks and nulls dropped — idempotently, without setting the file aside; a null list is empty.
    /// </summary>
    [Fact]
    public void StoredOrder_IsNormalizedLeniently()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"PanelButtonOrder\": [\" clear \", \"Settings\", \"pause\", \"CLEAR\", \"Pin\", \"3\", \"\", null, \"Updates\"] }");
        var loaded = new SettingsStore(path).Load();
        Assert.Equal(["Clear", "Pause", "Updates"], loaded.PanelButtonOrder);
        Assert.Equal(loaded.PanelButtonOrder, loaded.Normalize().PanelButtonOrder);

        // KeepOpen is first by default; Groups follows Pause, its predecessor by default.
        Assert.Equal(
            [PanelButton.KeepOpen, PanelButton.Clear, PanelButton.Pause, PanelButton.Groups, PanelButton.Updates],
            PanelButtons.Arrange(loaded.PanelButtonOrder));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.corrupt*"));

        Assert.Empty(new AppSettings { PanelButtonOrder = null! }.Normalize().PanelButtonOrder);
    }

    /// <summary>
    /// A move saved through the settings file comes back in the same order, and moving everything back stores nothing again;
    /// the hidden list keeps its own canonical order whatever the arrangement.
    /// </summary>
    [Fact]
    public void Order_RoundTripsThroughTheFile()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();
        var moved = PanelButtons.Move(PanelButtons.All, PanelButton.Clear, later: false, Everything);
        store.Update(s => s with { PanelButtonOrder = PanelButtons.ToStored(moved), HiddenPanelButtons = ["Clear", "KeepOpen"] });

        var reloaded = new SettingsStore(path).Load();
        Assert.Equal(moved, PanelButtons.Arrange(reloaded.PanelButtonOrder));
        Assert.Equal(["KeepOpen", "Clear"], reloaded.HiddenPanelButtons);

        var back = PanelButtons.Move(moved, PanelButton.Clear, later: true, Everything);
        store.Update(s => s with { PanelButtonOrder = PanelButtons.ToStored(back) });
        Assert.Empty(new SettingsStore(path).Load().PanelButtonOrder);
    }

    /// <summary>
    /// The member's name is the one the file keeps: renaming it would silently put every user's buttons back in the default
    /// order.
    /// </summary>
    [Fact]
    public void FileName_IsStable()
    {
        var json = JsonSerializer.Serialize(new AppSettings { PanelButtonOrder = ["Pause", "KeepOpen"] });
        Assert.Contains("\"PanelButtonOrder\":[\"Pause\",\"KeepOpen\"]", json, StringComparison.Ordinal);
    }

    /// <summary>Every order of a list, each once.</summary>
    /// <param name="items">The items.</param>
    /// <returns>The orders.</returns>
    private static IEnumerable<IReadOnlyList<PanelButton>> Permutations(List<PanelButton> items)
    {
        if (items.Count <= 1)
        {
            yield return items.ToArray();
            yield break;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, k) => k != i).ToList();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }
}
