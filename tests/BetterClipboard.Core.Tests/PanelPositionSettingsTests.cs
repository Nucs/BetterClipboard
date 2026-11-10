using System.Text.Json;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// The panel position setting (CLAUDE.md §2.38): <see cref="AppSettings.Placement"/> and
/// <see cref="AppSettings.CenterOnMainScreen"/> — defaults, files from before, and what older versions can read.
/// </summary>
public sealed class PanelPositionSettingsTests : IDisposable
{
    /// <summary>The three placement names every released version reads; a fourth would make them quarantine the file.</summary>
    private static readonly string[] ReleasedPlacementNames = ["NearCaret", "NearCursor", "CenterScreen"];

    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// Every combination the Settings list can store — placement and main screen — as written to the file: the placement
    /// is one of the three names released versions know, the main-screen choice a member of its own.
    /// </summary>
    public static TheoryData<FlyoutPlacement, bool> StoredChoices => new()
    {
        { FlyoutPlacement.NearCaret, false },
        { FlyoutPlacement.NearCursor, false },
        { FlyoutPlacement.CenterScreen, true },
        { FlyoutPlacement.CenterScreen, false },
    };

    /// <summary>A new install opens the panel next to the text cursor, like Win+V; the main screen is off.</summary>
    [Fact]
    public void Defaults_NextToTheTextCursor()
    {
        var settings = new SettingsStore(Path.Combine(temp.Path, "settings.json")).Load();
        Assert.Equal(FlyoutPlacement.NearCaret, settings.Placement);
        Assert.False(settings.CenterOnMainScreen);
    }

    /// <summary>
    /// A file from before the main screen existed (a centered panel then went to the screen of the active window) still
    /// centers on the current screen: the missing member reads as off.
    /// </summary>
    [Fact]
    public void FileFromBefore_CentersOnTheCurrentScreen()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"Placement\": \"CenterScreen\", \"MaxItems\": 50 }");

        var settings = new SettingsStore(path).Load();

        Assert.Equal(FlyoutPlacement.CenterScreen, settings.Placement);
        Assert.False(settings.CenterOnMainScreen);
        Assert.Equal(50, settings.MaxItems);
    }

    /// <summary>
    /// Each choice survives a save and a load, and the file holds the placement under a name released versions know, so
    /// going back to an older version keeps every setting (it centers on the current screen instead of the main one).
    /// </summary>
    /// <param name="placement">The stored placement.</param>
    /// <param name="mainScreen">The stored main-screen choice.</param>
    [Theory]
    [MemberData(nameof(StoredChoices))]
    public void EveryChoice_RoundTrips_UnderANameOlderVersionsKnow(FlyoutPlacement placement, bool mainScreen)
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();

        store.Update(s => s with { Placement = placement, CenterOnMainScreen = mainScreen });

        var reloaded = new SettingsStore(path).Load();
        Assert.Equal(placement, reloaded.Placement);
        Assert.Equal(mainScreen, reloaded.CenterOnMainScreen);

        using var file = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Contains(file.RootElement.GetProperty(nameof(AppSettings.Placement)).GetString(), ReleasedPlacementNames);
        Assert.Equal(mainScreen, file.RootElement.GetProperty(nameof(AppSettings.CenterOnMainScreen)).GetBoolean());
    }

    /// <summary>
    /// The placement keeps exactly the three members released versions know. A fourth one, written by name, would make
    /// every older version set the whole settings file aside as corrupt (the reason the main screen is a member of its
    /// own): a new choice needs a member of <see cref="AppSettings"/>, not of <see cref="FlyoutPlacement"/>.
    /// </summary>
    [Fact]
    public void Placement_KeepsItsThreeReleasedNames()
    {
        Assert.Equal(ReleasedPlacementNames, Enum.GetNames<FlyoutPlacement>());
    }

    /// <summary>
    /// The main-screen choice is kept even while another placement is chosen (the list writes both halves together, so
    /// nothing reads it then), and a hand-edited unknown placement still falls back to the text cursor.
    /// </summary>
    [Fact]
    public void MainScreen_IsKeptAsWritten_AndAnUnknownPlacementFallsBack()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();

        store.Update(s => s with { Placement = (FlyoutPlacement)42, CenterOnMainScreen = true });

        Assert.Equal(FlyoutPlacement.NearCaret, store.Current.Placement);
        Assert.True(store.Current.CenterOnMainScreen);
        Assert.True(new SettingsStore(path).Load().CenterOnMainScreen);
    }
}
