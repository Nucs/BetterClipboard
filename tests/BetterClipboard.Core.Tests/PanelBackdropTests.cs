using System.Text.Json;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the panel's Background transparency (Settings › App, CLAUDE.md §2.58): the default is Windows' own acrylic, the
/// scale runs from the most frosted to the clearest glass without ever leaving the glass, every level keeps the theme's
/// colors, and the setting's default, clamping, round trip and file name.
/// </summary>
public sealed class PanelBackdropTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// The default level is Windows' own look, exactly the controller's values the backdrop lab measured on Windows 11 25H2
    /// (the "Default" acrylic): a panel left at 50 looks as it did before the slider existed.
    /// </summary>
    [Fact]
    public void Default_IsWindowsOwnAcrylic()
    {
        Assert.Equal(50, PanelBackdrop.DefaultLevel);
        Assert.True(PanelBackdrop.IsWindowsLook(PanelBackdrop.DefaultLevel));
        Assert.Equal(new AcrylicLook(0xFF2C2C2C, 0.15f, 0.96f, 0xFF2C2C2C), PanelBackdrop.WindowsDark);
        Assert.Equal(new AcrylicLook(0xFFFCFCFC, 0f, 0.85f, 0xFFF9F9F9), PanelBackdrop.WindowsLight);
        Assert.Equal(PanelBackdrop.WindowsDark, PanelBackdrop.LookFor(PanelBackdrop.DefaultLevel, light: false));
        Assert.Equal(PanelBackdrop.WindowsLight, PanelBackdrop.LookFor(PanelBackdrop.DefaultLevel, light: true));

        foreach (int level in new[] { 0, 5, 45, 55, 100 })
        {
            Assert.False(PanelBackdrop.IsWindowsLook(level));
        }
    }

    /// <summary>
    /// Higher levels are never less see-through: as the level rises through every step, the tint and the luminosity layer
    /// never grow, in both themes; and every step of 5 changes at least one of them.
    /// </summary>
    /// <param name="light">The theme.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scale_IsMonotonic(bool light)
    {
        var previous = PanelBackdrop.LookFor(PanelBackdrop.MinLevel, light);
        for (int level = PanelBackdrop.MinLevel + 1; level <= PanelBackdrop.MaxLevel; level++)
        {
            var look = PanelBackdrop.LookFor(level, light);
            Assert.True(look.TintOpacity <= previous.TintOpacity + 1e-6f, $"tint grew at {level}");
            Assert.True(look.LuminosityOpacity <= previous.LuminosityOpacity + 1e-6f, $"luminosity grew at {level}");
            previous = look;
        }

        for (int level = PanelBackdrop.MinLevel; level < PanelBackdrop.MaxLevel; level += PanelBackdrop.Step)
        {
            var here = PanelBackdrop.LookFor(level, light);
            var next = PanelBackdrop.LookFor(level + PanelBackdrop.Step, light);
            Assert.True(next.TintOpacity < here.TintOpacity || next.LuminosityOpacity < here.LuminosityOpacity, $"no change from {level}");
        }
    }

    /// <summary>
    /// The glass stays glass at both ends (the owner's "it always stays blur"): the most frosted level has more luminosity
    /// layer than Windows' look and less than 1, where the backdrop lab measured a solid color; the clearest has less than
    /// Windows' look and keeps some, so the panel's text stays readable; the tint never rises above Windows' on the clear side
    /// nor falls below it on the frosted one.
    /// </summary>
    /// <param name="light">The theme.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ends_StayGlass(bool light)
    {
        var windows = light ? PanelBackdrop.WindowsLight : PanelBackdrop.WindowsDark;
        var frosted = PanelBackdrop.LookFor(PanelBackdrop.MinLevel, light);
        var clearest = PanelBackdrop.LookFor(PanelBackdrop.MaxLevel, light);

        Assert.True(frosted.LuminosityOpacity > windows.LuminosityOpacity, "the most frosted glass is more frosted than Windows' own");
        Assert.True(frosted.LuminosityOpacity < 1f, "a luminosity layer of 1 is a solid color, not glass");
        Assert.True(frosted.TintOpacity >= windows.TintOpacity);

        Assert.True(clearest.LuminosityOpacity < windows.LuminosityOpacity, "the clearest glass is clearer than Windows' own");
        Assert.True(clearest.LuminosityOpacity > 0f, "the clearest glass keeps some frost");
        Assert.True(clearest.TintOpacity <= windows.TintOpacity);
    }

    /// <summary>
    /// Each half of the slider changes what shows through in equal steps: the lab measured that it falls in a straight line
    /// with the luminosity opacity, so every step of 5 on one side of the default changes the luminosity by the same amount.
    /// </summary>
    /// <param name="light">The theme.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachHalf_StepsEvenly(bool light)
    {
        foreach (var (from, to) in new[] { (PanelBackdrop.MinLevel, PanelBackdrop.DefaultLevel), (PanelBackdrop.DefaultLevel, PanelBackdrop.MaxLevel) })
        {
            float first = PanelBackdrop.LookFor(from, light).LuminosityOpacity - PanelBackdrop.LookFor(from + PanelBackdrop.Step, light).LuminosityOpacity;
            Assert.True(first > 0f, $"no change from {from}");
            for (int level = from; level < to; level += PanelBackdrop.Step)
            {
                float step = PanelBackdrop.LookFor(level, light).LuminosityOpacity - PanelBackdrop.LookFor(level + PanelBackdrop.Step, light).LuminosityOpacity;
                Assert.Equal(first, step, 1e-5f);
            }
        }
    }

    /// <summary>
    /// Every level, out-of-range ones included, gives opacities within 0..1 and the theme's own tint and fallback colors:
    /// the controller stops following the theme for every value once one is set, so the colors must always come along.
    /// </summary>
    [Fact]
    public void EveryLevel_KeepsTheThemesColors()
    {
        for (int level = -20; level <= 120; level++)
        {
            foreach (bool light in new[] { false, true })
            {
                var windows = light ? PanelBackdrop.WindowsLight : PanelBackdrop.WindowsDark;
                var look = PanelBackdrop.LookFor(level, light);
                Assert.InRange(look.TintOpacity, 0f, 1f);
                Assert.InRange(look.LuminosityOpacity, 0f, 1f);
                Assert.Equal(windows.TintColor, look.TintColor);
                Assert.Equal(windows.FallbackColor, look.FallbackColor);
                Assert.Equal(PanelBackdrop.LookFor(PanelBackdrop.Normalize(level), light), look);
            }
        }
    }

    /// <summary>Levels outside 0..100 are clamped; the default is 50; a value in range is kept as given.</summary>
    /// <param name="stored">The stored level.</param>
    /// <param name="expected">The level used.</param>
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(47, 47)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, 100)]
    public void Normalize_Clamps(int stored, int expected)
    {
        Assert.Equal(expected, PanelBackdrop.Normalize(stored));
        Assert.Equal(expected, new AppSettings { BackgroundTransparency = stored }.Normalize().BackgroundTransparency);
    }

    /// <summary>
    /// A new install, and a settings file from before the setting existed, are at the default (Windows' look); a level saved
    /// survives a reload, a hand-edited one out of range is clamped without the file being set aside, and the member's name
    /// is the one the file keeps.
    /// </summary>
    [Fact]
    public void Setting_DefaultsRoundTripsAndKeepsItsName()
    {
        Assert.Equal(PanelBackdrop.DefaultLevel, new AppSettings().BackgroundTransparency);

        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"Theme\": \"Dark\" }");
        Assert.Equal(PanelBackdrop.DefaultLevel, new SettingsStore(path).Load().BackgroundTransparency);

        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { BackgroundTransparency = 85 });
        Assert.Equal(85, new SettingsStore(path).Load().BackgroundTransparency);
        Assert.Contains("\"BackgroundTransparency\": 85", File.ReadAllText(path), StringComparison.Ordinal);

        File.WriteAllText(path, "{ \"BackgroundTransparency\": 999 }");
        Assert.Equal(PanelBackdrop.MaxLevel, new SettingsStore(path).Load().BackgroundTransparency);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.corrupt*"));

        Assert.Contains("\"BackgroundTransparency\":30", JsonSerializer.Serialize(new AppSettings { BackgroundTransparency = 30 }), StringComparison.Ordinal);
    }
}
