using System.Text.Json;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the pin that keeps the panel open after pasting and for the header's buttons Settings can hide (CLAUDE.md
/// §2.55): the settings' defaults, reading and normalizing the stored names, the pin's effect only while it shows, the count
/// of shown buttons behind the panel's minimum width, and what the shortcut does to a pinned panel.
/// </summary>
public sealed class PanelButtonsTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// A new install shows every button and does not pin the panel; so does a settings file written before either member
    /// existed, which also loads without being set aside.
    /// </summary>
    [Fact]
    public void Defaults_EveryButtonShown_PanelNotPinned()
    {
        var fresh = new AppSettings();
        Assert.False(fresh.KeepPanelOpen);
        Assert.Empty(fresh.HiddenPanelButtons);
        Assert.False(fresh.PanelStaysOpen);

        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"MaxItems\": 50, \"ShowGroupsPane\": true }");
        var loaded = new SettingsStore(path).Load();
        Assert.False(loaded.KeepPanelOpen);
        Assert.Empty(loaded.HiddenPanelButtons);
        Assert.True(loaded.ShowGroupsPane);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.corrupt*"));
    }

    /// <summary>
    /// The pin and the hidden buttons survive a save and a reload (the owner's choice: the pin is remembered until
    /// unpinned, also after a restart), while the derived "stays open" is never written to the file.
    /// </summary>
    [Fact]
    public void PinAndHiddenButtons_RoundTripThroughTheFile()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { KeepPanelOpen = true, HiddenPanelButtons = ["clear", "Updates"] });

        var reloaded = new SettingsStore(path).Load();
        Assert.True(reloaded.KeepPanelOpen);
        Assert.True(reloaded.PanelStaysOpen);
        Assert.Equal(["Updates", "Clear"], reloaded.HiddenPanelButtons);

        var json = File.ReadAllText(path);
        Assert.Contains("\"KeepPanelOpen\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PanelStaysOpen", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hand-edited or newer version's list loses what no button answers to — the Settings button, a typo, a number, a
    /// blank, a null — and keeps the rest once each, canonical and in the header's order, idempotently, without setting the
    /// file aside.
    /// </summary>
    [Fact]
    public void HiddenButtons_AreNormalizedLeniently()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"HiddenPanelButtons\": [\"clear\", \" PAUSE \", \"pause\", \"Settings\", \"Pin\", \"3\", \"\", null, \"KeepOpen\"] }");
        var loaded = new SettingsStore(path).Load();
        Assert.Equal(["KeepOpen", "Pause", "Clear"], loaded.HiddenPanelButtons);
        Assert.Equal(loaded.HiddenPanelButtons, loaded.Normalize().HiddenPanelButtons);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.corrupt*"));

        Assert.Empty(new AppSettings { HiddenPanelButtons = null! }.Normalize().HiddenPanelButtons);
    }

    /// <summary>Names are read ignoring case and surrounding spaces; anything that is not exactly a button's name is refused.</summary>
    /// <param name="name">The stored name.</param>
    /// <param name="expected">The button it names, or <see langword="null"/> for none.</param>
    [Theory]
    [InlineData("KeepOpen", PanelButton.KeepOpen)]
    [InlineData("keepopen", PanelButton.KeepOpen)]
    [InlineData(" Updates ", PanelButton.Updates)]
    [InlineData("PAUSE", PanelButton.Pause)]
    [InlineData("groups", PanelButton.Groups)]
    [InlineData("Clear", PanelButton.Clear)]
    [InlineData("Settings", null)]
    [InlineData("Pin", null)]
    [InlineData("3", null)]
    [InlineData("Pause, Clear", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryParse_NamesOnly(string? name, PanelButton? expected)
    {
        bool known = PanelButtons.TryParse(name, out var button);
        Assert.Equal(expected is not null, known);
        if (expected is { } named)
        {
            Assert.Equal(named, button);
        }
    }

    /// <summary>
    /// The panel stays open after pasting only while it is pinned and the pin shows: hiding the pin ends the mode, other
    /// hidden buttons do not touch it, and showing the pin again brings the stored pin back.
    /// </summary>
    [Fact]
    public void StaysOpen_OnlyWhilePinnedAndThePinShows()
    {
        var pinned = new AppSettings { KeepPanelOpen = true };
        Assert.True(pinned.PanelStaysOpen);
        Assert.True((pinned with { HiddenPanelButtons = ["Pause", "Clear", "Updates", "Groups"] }).PanelStaysOpen);
        Assert.False((pinned with { HiddenPanelButtons = ["KeepOpen"] }).PanelStaysOpen);
        Assert.False((pinned with { HiddenPanelButtons = [" keepopen "] }).PanelStaysOpen);

        var hiddenThenShown = pinned with { HiddenPanelButtons = PanelButtons.WithShown(["KeepOpen"], PanelButton.KeepOpen, shown: true) };
        Assert.True(hiddenThenShown.PanelStaysOpen);
        Assert.False(new AppSettings { KeepPanelOpen = false }.PanelStaysOpen);
    }

    /// <summary>
    /// Ticking or unticking one box changes that button alone, keeps the list canonical whatever order the boxes were
    /// clicked in, and repeating a change changes nothing.
    /// </summary>
    [Fact]
    public void WithShown_ChangesOneButtonAndStaysCanonical()
    {
        string[] hidden = [];
        hidden = PanelButtons.WithShown(hidden, PanelButton.Clear, shown: false);
        hidden = PanelButtons.WithShown(hidden, PanelButton.KeepOpen, shown: false);
        hidden = PanelButtons.WithShown(hidden, PanelButton.Clear, shown: false);
        Assert.Equal(["KeepOpen", "Clear"], hidden);

        hidden = PanelButtons.WithShown(hidden, PanelButton.Updates, shown: false);
        Assert.Equal(["KeepOpen", "Updates", "Clear"], hidden);

        hidden = PanelButtons.WithShown(hidden, PanelButton.KeepOpen, shown: true);
        Assert.Equal(["Updates", "Clear"], hidden);
        Assert.Equal(["Updates", "Clear"], PanelButtons.WithShown(hidden, PanelButton.Pause, shown: true));

        // A list from a hand-edited file is cleaned on the way.
        Assert.Equal(["Pause", "Groups"], PanelButtons.WithShown(["groups", "Settings", "x"], PanelButton.Pause, shown: false));
        Assert.Empty(PanelButtons.WithShown(null, PanelButton.Groups, shown: true));
    }

    /// <summary>
    /// The header counts the Settings button and every button not hidden, the update button only when this copy has an
    /// update system (not the Microsoft Store edition); the count makes the panel's minimum width.
    /// </summary>
    [Fact]
    public void CountShown_MakesTheMinimumWidth()
    {
        Assert.Equal(6, PanelButtons.CountShown([], hasUpdateButton: true));
        Assert.Equal(5, PanelButtons.CountShown([], hasUpdateButton: false));
        Assert.Equal(5, PanelButtons.CountShown(["Updates"], hasUpdateButton: true));
        Assert.Equal(5, PanelButtons.CountShown(["Updates"], hasUpdateButton: false));
        Assert.Equal(1, PanelButtons.CountShown(PanelButtons.All.Select(b => b.ToString()).ToArray(), hasUpdateButton: true));
        Assert.Equal(4, PanelButtons.CountShown(["keepopen", "Clear", "nonsense"], hasUpdateButton: true));
        Assert.Equal(6, PanelButtons.CountShown(null, hasUpdateButton: true));

        Assert.Equal(432, AppSettings.MinFlyoutWidthFor(PanelButtons.CountShown([], hasUpdateButton: true)));
        Assert.Equal(396, AppSettings.MinFlyoutWidthFor(PanelButtons.CountShown(["KeepOpen"], hasUpdateButton: true)));
        Assert.Equal(360, AppSettings.MinFlyoutWidthFor(PanelButtons.CountShown(["KeepOpen", "Clear"], hasUpdateButton: true)));
        Assert.Equal(AppSettings.MinFlyoutWidth, AppSettings.MinFlyoutWidthFor(1));
    }

    /// <summary>
    /// Every hideable button has a name a stored list can hold, the header's order is the enum's, and Settings is not among
    /// them (it always shows).
    /// </summary>
    [Fact]
    public void All_ListsTheHideableButtonsInHeaderOrder()
    {
        Assert.Equal([PanelButton.KeepOpen, PanelButton.Updates, PanelButton.Pause, PanelButton.Groups, PanelButton.Clear], PanelButtons.All);
        foreach (var button in PanelButtons.All)
        {
            Assert.True(PanelButtons.TryParse(button.ToString(), out var parsed));
            Assert.Equal(button, parsed);
        }

        Assert.False(PanelButtons.TryParse("Settings", out _));
    }

    /// <summary>
    /// The shortcut opens a closed panel; closes an open one that is in front (pinned or not) or that is not pinned; and
    /// brings a pinned panel that waits behind another app forward (the owner's choice, 2026-11-20).
    /// </summary>
    /// <param name="isOpen">Whether the panel is on screen.</param>
    /// <param name="staysOpen">Whether it is pinned.</param>
    /// <param name="inFront">Whether it has the keyboard.</param>
    /// <param name="expected">What the shortcut does.</param>
    [Theory]
    [InlineData(false, false, false, PanelShortcutAction.Show)]
    [InlineData(false, true, false, PanelShortcutAction.Show)]
    [InlineData(false, false, true, PanelShortcutAction.Show)]
    [InlineData(false, true, true, PanelShortcutAction.Show)]
    [InlineData(true, false, true, PanelShortcutAction.Close)]
    [InlineData(true, false, false, PanelShortcutAction.Close)]
    [InlineData(true, true, true, PanelShortcutAction.Close)]
    [InlineData(true, true, false, PanelShortcutAction.BringForward)]
    public void Shortcut_BringsAPinnedPanelForward(bool isOpen, bool staysOpen, bool inFront, PanelShortcutAction expected) =>
        Assert.Equal(expected, PanelShortcut.Decide(isOpen, staysOpen, inFront));

    /// <summary>The stored members' names are the ones the file keeps (renaming them would silently reset both settings).</summary>
    [Fact]
    public void FileNames_AreStable()
    {
        var json = JsonSerializer.Serialize(new AppSettings { KeepPanelOpen = true, HiddenPanelButtons = ["Pause"] });
        Assert.Contains("\"KeepPanelOpen\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"HiddenPanelButtons\":[\"Pause\"]", json, StringComparison.Ordinal);
    }
}
