using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the built-in password-manager ignore catalog: its hygiene, first-run seeding, keeping the
/// user's own entries, deletions that stick, and names a later release adds reaching existing users once.
/// </summary>
public sealed class KnownPasswordManagersTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>
    /// Every name is already normalized (no <c>.exe</c>, no path, no padding — matching compares normalized
    /// names, so anything else would never match), names are unique across products, products are unique.
    /// </summary>
    [Fact]
    public void Catalog_IsWellFormed()
    {
        Assert.True(KnownPasswordManagers.All.Count >= 40);
        Assert.Equal(KnownPasswordManagers.All.Count, KnownPasswordManagers.All.Select(a => a.Product).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var app in KnownPasswordManagers.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Product));
            Assert.NotEmpty(app.ProcessNames);
            foreach (var name in app.ProcessNames)
            {
                Assert.Equal(name, CaptureRules.NormalizeProcessName(name));
            }
        }

        var names = KnownPasswordManagers.ProcessNames;
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(KnownPasswordManagers.All.SelectMany(a => a.ProcessNames), names);
        Assert.Contains(KnownPasswordManagers.All, a => a.Kind == CredentialAppKind.Authenticator);
    }

    /// <summary>Fresh settings ignore every catalogued app and remember having offered them.</summary>
    [Fact]
    public void FreshSettings_IgnoreEveryKnownApp()
    {
        var settings = new AppSettings().Normalize();
        Assert.Equal(KnownPasswordManagers.ProcessNames, settings.IgnoredApps);
        Assert.Equal(KnownPasswordManagers.ProcessNames, settings.SeededIgnoredApps);

        var rules = CaptureRules.FromSettings(settings);
        Assert.True(rules.IsIgnored(new SourceAppInfo("KeePassXC", @"C:\Program Files\KeePassXC\KeePassXC.exe", "KeePassXC")));
        Assert.True(rules.IsIgnored(new SourceAppInfo("protonpass", null, "Proton Pass")));
        Assert.True(rules.IsIgnored(new SourceAppInfo("Authy Desktop", null, "Authy Desktop")));
        Assert.False(rules.IsIgnored(new SourceAppInfo("notepad", @"C:\Windows\notepad.exe", "Notepad")));

        // Idempotent: normalizing again changes nothing.
        Assert.Equal(settings.IgnoredApps, settings.Normalize().IgnoredApps);
    }

    /// <summary>
    /// The user's own entries stay first and untouched; a catalog name already covered by a user entry
    /// in another spelling ("keepass.EXE") is not added a second time.
    /// </summary>
    [Fact]
    public void ExistingList_IsKeptAndExtended()
    {
        var settings = new AppSettings { IgnoredApps = ["BC-TEST Vault", "keepass.EXE"] }.Normalize();
        Assert.Equal(["BC-TEST Vault", "keepass.EXE"], settings.IgnoredApps.Take(2));
        Assert.Contains("Bitwarden", settings.IgnoredApps);
        Assert.DoesNotContain("KeePass", settings.IgnoredApps);
        Assert.Equal(2 + KnownPasswordManagers.ProcessNames.Count - 1, settings.IgnoredApps.Count);
        Assert.Contains("KeePass", settings.SeededIgnoredApps); // offered (covered by the user's entry)
    }

    /// <summary>A deleted catalog entry is never merged back in by later loads or saves.</summary>
    [Fact]
    public void DeletedEntries_StayDeleted()
    {
        var seeded = new AppSettings().Normalize();
        var edited = (seeded with { IgnoredApps = [.. seeded.IgnoredApps.Where(a => a != "Bitwarden")] }).Normalize();
        Assert.DoesNotContain("Bitwarden", edited.IgnoredApps);
        Assert.DoesNotContain("Bitwarden", edited.Normalize().IgnoredApps);
    }

    /// <summary>
    /// A later release that adds names reaches users who already went through seeding — exactly the new
    /// names, not the ones they deleted meanwhile.
    /// </summary>
    [Fact]
    public void NamesAddedLater_ArriveOnce()
    {
        var (first, seen) = KnownPasswordManagers.Seed(["Mine"], [], ["BC-TEST-A", "BC-TEST-B"]);
        Assert.Equal(["Mine", "BC-TEST-A", "BC-TEST-B"], first);

        // The user deletes A; the next release's catalog also lists C.
        var (second, seenAgain) = KnownPasswordManagers.Seed(["Mine", "BC-TEST-B"], seen, ["BC-TEST-A", "BC-TEST-B", "BC-TEST-C"]);
        Assert.Equal(["Mine", "BC-TEST-B", "BC-TEST-C"], second);
        Assert.Equal(["BC-TEST-A", "BC-TEST-B", "BC-TEST-C"], seenAgain);

        // Nothing new: the same instances come back (no churn on every load).
        var (third, seenThird) = KnownPasswordManagers.Seed(second, seenAgain, ["BC-TEST-A", "BC-TEST-B", "BC-TEST-C"]);
        Assert.Same(second, third);
        Assert.Same(seenAgain, seenThird);
    }

    /// <summary>The explicit "Add known password managers" action restores deleted names too, without duplicates.</summary>
    [Fact]
    public void AddMissing_RestoresEverythingOnce()
    {
        var restored = KnownPasswordManagers.AddMissing(["Mine", "KEEPASS.exe"], out int added);
        Assert.Equal("Mine", restored[0]);
        Assert.Equal(KnownPasswordManagers.ProcessNames.Count - 1, added);
        Assert.DoesNotContain("KeePass", restored);

        KnownPasswordManagers.AddMissing(restored, out int again);
        Assert.Equal(0, again);
    }

    /// <summary>
    /// End to end through <see cref="SettingsStore"/>: a v0.1-style file (no seeding record) gains the
    /// catalog on load, a deletion is saved together with the record, and it survives a restart.
    /// </summary>
    [Fact]
    public void SettingsFile_SeedsOnce_AndRemembersDeletions()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ \"IgnoredApps\": [ \"BC-TEST Vault\" ], \"MaxItems\": 500 }");

        var store = new SettingsStore(path);
        var loaded = store.Load();
        Assert.Equal("BC-TEST Vault", loaded.IgnoredApps[0]);
        Assert.Contains("KeePassXC", loaded.IgnoredApps);

        store.Update(s => s with { IgnoredApps = [.. s.IgnoredApps.Where(a => a != "KeePassXC")] });
        Assert.Contains("SeededIgnoredApps", File.ReadAllText(path), StringComparison.Ordinal);

        var restarted = new SettingsStore(path).Load();
        Assert.DoesNotContain("KeePassXC", restarted.IgnoredApps);
        Assert.Contains("BC-TEST Vault", restarted.IgnoredApps);
        Assert.Contains("Bitwarden", restarted.IgnoredApps);
        Assert.Equal(500, restarted.MaxItems);
    }
}
