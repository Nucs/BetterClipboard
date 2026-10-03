using System.Text.Json;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the shortcuts that open the panel in settings: the main one plus the extra ones (<see cref="AppSettings.OpenHotkeys"/>),
/// how they are normalized, saved and read back, and what files from before the extras look like.
/// </summary>
public sealed class HotkeySettingsTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Out of the box only Win+V opens the panel.</summary>
    [Fact]
    public void Defaults_AreWinVAlone()
    {
        var settings = new AppSettings().Normalize();
        Assert.Equal(["Win+V"], settings.OpenHotkeys);
        Assert.Empty(settings.ExtraOpenHotkeys);
    }

    /// <summary>
    /// The extras lose blanks, repeats (by text, ignoring case and spaces) and copies of the main shortcut, keep their
    /// order, and stop at the cap — first come, first kept.
    /// </summary>
    [Fact]
    public void Normalize_CleansTheExtras()
    {
        var settings = new AppSettings
        {
            OpenHotkey = " Win+V ",
            ExtraOpenHotkeys = ["", "  ", "win + v", "Alt+Win+V", "ALT+WIN+V", " Ctrl+Alt+F9 ", null!],
        }.Normalize();

        Assert.Equal("Win+V", settings.OpenHotkey);
        Assert.Equal(["Alt+Win+V", "Ctrl+Alt+F9"], settings.ExtraOpenHotkeys);
        Assert.Equal(["Win+V", "Alt+Win+V", "Ctrl+Alt+F9"], settings.OpenHotkeys);

        var many = new AppSettings { ExtraOpenHotkeys = Enumerable.Range(1, 20).Select(i => $"Ctrl+Alt+F{i}").ToArray() }.Normalize();
        Assert.Equal(AppSettings.MaxOpenHotkeys, many.OpenHotkeys.Count);
        Assert.Equal("Ctrl+Alt+F1", many.ExtraOpenHotkeys[0]);
        Assert.Equal($"Ctrl+Alt+F{AppSettings.MaxOpenHotkeys - 1}", many.ExtraOpenHotkeys[^1]);
    }

    /// <summary>A blank main shortcut falls back to Win+V; a missing extras member (JSON null) reads as none.</summary>
    [Fact]
    public void Normalize_BlankMain_IsWinV_AndNullExtras_AreNone()
    {
        var settings = new AppSettings { OpenHotkey = "  ", ExtraOpenHotkeys = null! }.Normalize();
        Assert.Equal(["Win+V"], settings.OpenHotkeys);
    }

    /// <summary>
    /// <see cref="AppSettings.WithOpenHotkeys"/> makes the first shortcut the main one and the rest the extras; an empty
    /// list is refused (the panel would have no key).
    /// </summary>
    [Fact]
    public void WithOpenHotkeys_SplitsMainAndExtras()
    {
        var settings = new AppSettings().WithOpenHotkeys(["Alt+Win+V", "Ctrl+Alt+F9"]).Normalize();
        Assert.Equal("Alt+Win+V", settings.OpenHotkey);
        Assert.Equal(["Ctrl+Alt+F9"], settings.ExtraOpenHotkeys);

        var single = settings.WithOpenHotkeys(["Win+V"]).Normalize();
        Assert.Equal(["Win+V"], single.OpenHotkeys);
        Assert.Empty(single.ExtraOpenHotkeys);

        Assert.Throws<ArgumentException>(() => settings.WithOpenHotkeys([]));
        Assert.Throws<ArgumentNullException>(() => settings.WithOpenHotkeys(null!));
    }

    /// <summary>Text comparison ignores case and spaces, but not modifier order (the gesture parser handles that).</summary>
    [Fact]
    public void SameHotkeyText_IgnoresCaseAndSpacesOnly()
    {
        Assert.True(AppSettings.SameHotkeyText("win + v", "Win+V"));
        Assert.True(AppSettings.SameHotkeyText(null, " "));
        Assert.False(AppSettings.SameHotkeyText("Win+Alt+V", "Alt+Win+V"));
        Assert.False(AppSettings.SameHotkeyText("Win+V", "Win+C"));
    }

    /// <summary>
    /// The extras are saved and read back; the derived <see cref="AppSettings.OpenHotkeys"/> is never written, so a version
    /// from before the extras sees only members it knows; and a file from before them reads with none.
    /// </summary>
    [Fact]
    public void Extras_RoundTrip_AndOlderFilesStillLoad()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"OpenHotkey\": \"Ctrl+`\", \"MaxItems\": 50 }");
        var store = new SettingsStore(path);
        Assert.Equal(["Ctrl+`"], store.Load().OpenHotkeys);

        store.Update(s => s.WithOpenHotkeys(["Alt+Win+V", "Ctrl+Alt+F9", "Win+C"]));

        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.Equal("Alt+Win+V", json.RootElement.GetProperty("OpenHotkey").GetString());
            Assert.Equal(["Ctrl+Alt+F9", "Win+C"], json.RootElement.GetProperty("ExtraOpenHotkeys").EnumerateArray().Select(e => e.GetString()!).ToArray());
            Assert.False(json.RootElement.TryGetProperty("OpenHotkeys", out _));
            Assert.Equal(50, json.RootElement.GetProperty("MaxItems").GetInt32());
        }

        Assert.Equal(["Alt+Win+V", "Ctrl+Alt+F9", "Win+C"], new SettingsStore(path).Load().OpenHotkeys);
    }
}
