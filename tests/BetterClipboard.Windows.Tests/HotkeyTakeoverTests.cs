using System.Text.Json;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Interop;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for several shortcuts opening the panel and taking them over: the shortcut list parser, one keyboard hook serving
/// several gestures, Explorer's <c>DisabledHotkeys</c> rules and plan, and the installer's <c>--set-hotkeys</c> command.
/// </summary>
public sealed class HotkeyTakeoverTests : IDisposable
{
    private const uint V = 0x56;
    private const uint C = 0x43;

    private readonly string dataDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Deletes the temp data folder.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder must not fail a test.
        }
    }

    /// <summary>
    /// Repeats are dropped by keys, not by text (<c>alt + win + v</c> is <c>Win+Alt+V</c>); texts that are not shortcuts are
    /// reported in order; the canonical texts are what the settings save.
    /// </summary>
    [Fact]
    public void HotkeyList_DropsRepeatsByKeys_AndReportsInvalid()
    {
        var parsed = HotkeyList.Parse(["Win+V", "alt + win + v", "Win+Alt+V", "Ctrl+NoSuchKey", "", "V", "Ctrl+Alt+F9", "win+v"]);

        Assert.Equal(["Win+V", "Alt+Win+V", "Ctrl+Alt+F9"], parsed.CanonicalTexts);
        Assert.Equal(["Ctrl+NoSuchKey", "", "V"], parsed.Invalid);
        Assert.False(parsed.Truncated);
    }

    /// <summary>More distinct shortcuts than the cap: the first ones are kept and the result says some were left out.</summary>
    [Fact]
    public void HotkeyList_CapsTheCount()
    {
        var parsed = HotkeyList.Parse(Enumerable.Range(1, AppSettings.MaxOpenHotkeys + 2).Select(i => $"Ctrl+Alt+F{i}"));

        Assert.Equal(AppSettings.MaxOpenHotkeys, parsed.Gestures.Count);
        Assert.Equal("Ctrl+Alt+F1", parsed.CanonicalTexts[0]);
        Assert.True(parsed.Truncated);
    }

    /// <summary>
    /// One hook, two shortcuts on the same key (Win+V and Alt+Win+V): each press goes to the gesture whose modifiers match,
    /// its repeats and key-up stay with it, and the other gesture never fires.
    /// </summary>
    [Fact]
    public void InterceptorSet_KeepsGesturesOnTheSameKeyApart()
    {
        var set = new HotkeyInterceptorSet([new HotkeyGesture(HotkeyModifiers.Win, V), new HotkeyGesture(HotkeyModifiers.Win | HotkeyModifiers.Alt, V)]);

        Assert.True(set.Handles(V));
        Assert.False(set.Handles(C));

        Assert.Equal(InterceptDecision.SwallowAndFire, set.OnKey(V, true, false, HotkeyModifiers.Win | HotkeyModifiers.Alt, out var fired));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Win | HotkeyModifiers.Alt, V), fired);
        Assert.Equal(InterceptDecision.Swallow, set.OnKey(V, true, false, HotkeyModifiers.Win | HotkeyModifiers.Alt, out _));
        Assert.Equal(InterceptDecision.Swallow, set.OnKey(V, false, false, HotkeyModifiers.Win, out _));

        Assert.Equal(InterceptDecision.SwallowAndFire, set.OnKey(V, true, false, HotkeyModifiers.Win, out fired));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Win, V), fired);
        Assert.Equal(InterceptDecision.Swallow, set.OnKey(V, false, false, HotkeyModifiers.Win, out _));

        // Neither gesture: Win+Shift+V (PowerToys) and a plain V pass, and so does a key-up nobody swallowed.
        Assert.Equal(InterceptDecision.PassThrough, set.OnKey(V, true, false, HotkeyModifiers.Win | HotkeyModifiers.Shift, out fired));
        Assert.Equal(default, fired);
        Assert.Equal(InterceptDecision.PassThrough, set.OnKey(V, false, false, HotkeyModifiers.Win | HotkeyModifiers.Shift, out _));
        Assert.Equal(InterceptDecision.PassThrough, set.OnKey(V, true, false, HotkeyModifiers.None, out _));
        Assert.Equal(InterceptDecision.PassThrough, set.OnKey(V, false, false, HotkeyModifiers.None, out _));
    }

    /// <summary>Our own injected keys pass every interceptor, and a gesture listed twice is intercepted once.</summary>
    [Fact]
    public void InterceptorSet_IgnoresOwnInjection_AndRepeats()
    {
        var winV = new HotkeyGesture(HotkeyModifiers.Win, V);
        var set = new HotkeyInterceptorSet([winV, winV]);

        Assert.Single(set.Gestures);
        Assert.Equal(InterceptDecision.PassThrough, set.OnKey(V, true, isOwnInjection: true, HotkeyModifiers.Win, out _));
        Assert.Equal(InterceptDecision.SwallowAndFire, set.OnKey(V, true, isOwnInjection: false, HotkeyModifiers.Win, out _));
    }

    /// <summary>Only exactly Win plus a letter or digit can be released from Explorer.</summary>
    /// <param name="text">The shortcut.</param>
    /// <param name="key">The expected key, or a space for none.</param>
    [Theory]
    [InlineData("Win+V", 'V')]
    [InlineData("win+c", 'C')]
    [InlineData("Win+1", '1')]
    [InlineData("Alt+Win+V", ' ')]
    [InlineData("Win+Shift+V", ' ')]
    [InlineData("Ctrl+Win+V", ' ')]
    [InlineData("Ctrl+B", ' ')]
    [InlineData("Win+F1", ' ')]
    [InlineData("Win+`", ' ')]
    [InlineData("Win+Space", ' ')]
    public void ReleasableKey_IsExactlyWinPlusLetterOrDigit(string text, char key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        char? expected = key == ' ' ? null : key;
        Assert.Equal(expected, ExplorerHotkeys.ReleasableKey(gesture));
    }

    /// <summary>
    /// Adding keeps the user's characters and their order, never re-adds a key listed in either case (so nothing changes and
    /// Explorer need not restart), and removing drops every spelling of the key.
    /// </summary>
    [Fact]
    public void WithKeys_KeepsOtherEntries()
    {
        Assert.Equal("V", ExplorerHotkeys.WithKeys(null, ['v'], release: true));
        Assert.Equal("XQV", ExplorerHotkeys.WithKeys("XQ", ['V'], release: true));
        Assert.Equal("VX", ExplorerHotkeys.WithKeys("VX", ['V'], release: true));
        Assert.Equal("vX", ExplorerHotkeys.WithKeys("vX", ['V'], release: true));
        Assert.Equal("XVC", ExplorerHotkeys.WithKeys("XV", ['V', 'C', 'c'], release: true));
        Assert.Equal("XQ", ExplorerHotkeys.WithKeys("XvQV", ['V'], release: false));
        Assert.Equal(string.Empty, ExplorerHotkeys.WithKeys("V", ['V'], release: false));
        Assert.Equal("X", ExplorerHotkeys.WithKeys("X", ['V'], release: false));
        Assert.True(ExplorerHotkeys.IsReleased("xv", 'V'));
        Assert.False(ExplorerHotkeys.IsReleased(null, 'V'));
    }

    /// <summary>
    /// The Explorer card's plan: release what is not released yet, give back once everything is (and a stray released
    /// Win+V that is no longer a shortcut), nothing without a Win+letter shortcut — and never the user's own letters.
    /// </summary>
    [Fact]
    public void Plan_CoversEveryState()
    {
        static IReadOnlyList<HotkeyGesture> Shortcuts(params string[] texts) => HotkeyList.Parse(texts).Gestures;

        var fresh = ExplorerHotkeys.Plan(Shortcuts("Win+V"), null);
        Assert.Equal(ExplorerReleaseAction.Release, fresh.Action);
        Assert.Equal(['V'], fresh.Keys);
        Assert.Contains("Win+V is not released", fresh.Status);

        var released = ExplorerHotkeys.Plan(Shortcuts("Win+V", "Ctrl+Alt+F9"), "XV");
        Assert.Equal(ExplorerReleaseAction.GiveBack, released.Action);
        Assert.Equal(['V'], released.Keys);
        Assert.Contains("Win+V is released from Explorer", released.Status);

        var partly = ExplorerHotkeys.Plan(Shortcuts("Win+V", "Win+C"), "V");
        Assert.Equal(ExplorerReleaseAction.Release, partly.Action);
        Assert.Equal(['C'], partly.Keys);
        Assert.Contains("Win+C is not released", partly.Status);
        Assert.Contains("Win+V is released.", partly.Status);

        var both = ExplorerHotkeys.Plan(Shortcuts("Win+V", "Win+C"), "VC");
        Assert.Equal(ExplorerReleaseAction.GiveBack, both.Action);
        Assert.Equal(['V', 'C'], both.Keys);
        Assert.Equal("Win+V and Win+C", both.KeyNames);

        // Win+V left to Windows by the user, but still released: it does nothing, so it is offered back.
        var stray = ExplorerHotkeys.Plan(Shortcuts("Alt+Win+V"), "QV");
        Assert.Equal(ExplorerReleaseAction.GiveBack, stray.Action);
        Assert.Equal(['V'], stray.Keys);
        Assert.Contains("does nothing", stray.Status);

        var strayWithOther = ExplorerHotkeys.Plan(Shortcuts("Win+C"), "VC");
        Assert.Equal(ExplorerReleaseAction.GiveBack, strayWithOther.Action);
        Assert.Equal(['C', 'V'], strayWithOther.Keys);

        // The user's own released letter (Q) is never offered back, and with no Win+letter shortcut there is nothing to do.
        var nothing = ExplorerHotkeys.Plan(Shortcuts("Alt+Win+V", "Ctrl+`"), "Q");
        Assert.Equal(ExplorerReleaseAction.None, nothing.Action);
        Assert.Empty(nothing.Keys);
    }

    /// <summary>Key names read as a sentence.</summary>
    [Fact]
    public void Names_JoinLikeASentence()
    {
        Assert.Equal(string.Empty, ExplorerHotkeys.Names([]));
        Assert.Equal("Win+V", ExplorerHotkeys.Names(['v']));
        Assert.Equal("Win+V and Win+C", ExplorerHotkeys.Names(['V', 'C']));
        Assert.Equal("Win+V, Win+C and Win+1", ExplorerHotkeys.Names(['V', 'C', '1']));
    }

    /// <summary><c>--validate</c> prints the canonical shortcuts and saves nothing.</summary>
    [Fact]
    public void SetHotkeys_Validate_PrintsCanonical_AndSavesNothing()
    {
        var (code, lines) = RunCommand(running: false, "--set-hotkeys", "--validate", "win + alt + v", "Ctrl+Alt+F9", "Windows+Alt+V");

        Assert.Equal(HotkeySetupCommand.ExitOk, code);
        Assert.Equal(["Alt+Win+V", "Ctrl+Alt+F9"], lines);
        Assert.False(File.Exists(SettingsPath));
    }

    /// <summary>
    /// Saving writes the main shortcut and the extras in canonical form and keeps every other setting — also while the app's
    /// own instance is not running, which is when the installer calls it.
    /// </summary>
    [Fact]
    public void SetHotkeys_Saves_AndKeepsOtherSettings()
    {
        File.WriteAllText(SettingsPath, "{ \"OpenHotkey\": \"Win+V\", \"MaxItems\": 77, \"IsCapturePaused\": true }");

        var (code, lines) = RunCommand(running: false, "--SET-HOTKEYS", "Win+C", "ctrl+alt+f9");

        Assert.Equal(HotkeySetupCommand.ExitOk, code);
        Assert.Equal(["Win+C", "Ctrl+Alt+F9"], lines);
        var saved = new SettingsStore(SettingsPath).Load();
        Assert.Equal(["Win+C", "Ctrl+Alt+F9"], saved.OpenHotkeys);
        Assert.Equal(77, saved.MaxItems);
        Assert.True(saved.IsCapturePaused);
        using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal("Win+C", json.RootElement.GetProperty("OpenHotkey").GetString());
    }

    /// <summary>Anything that is not a shortcut fails the whole command (exit 2), names the text, and saves nothing.</summary>
    [Fact]
    public void SetHotkeys_Invalid_FailsWithoutSaving()
    {
        var (code, lines) = RunCommand(running: false, "--set-hotkeys", "Win+V", "Win+Foo", "V");

        Assert.Equal(HotkeySetupCommand.ExitInvalid, code);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("error:", l, StringComparison.Ordinal));
        Assert.Contains("\"Win+Foo\"", lines[0], StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath));

        Assert.Equal(HotkeySetupCommand.ExitInvalid, RunCommand(running: false, "--set-hotkeys").Code);
        Assert.Equal(HotkeySetupCommand.ExitInvalid, RunCommand(running: false, "--set-hotkeys", "--validate").Code);
        var tooMany = Enumerable.Range(1, AppSettings.MaxOpenHotkeys + 1).Select(i => $"Ctrl+Alt+F{i}").Prepend("--set-hotkeys").ToArray();
        Assert.Equal(HotkeySetupCommand.ExitInvalid, RunCommand(running: false, tooMany).Code);
    }

    /// <summary>While BetterClipboard runs with that data folder, saving is refused (it would overwrite the file); validating is not.</summary>
    [Fact]
    public void SetHotkeys_RefusesWhileTheAppRuns()
    {
        var (code, lines) = RunCommand(running: true, "--set-hotkeys", "Alt+Win+V");
        Assert.Equal(HotkeySetupCommand.ExitAppRunning, code);
        Assert.StartsWith("error:", Assert.Single(lines), StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath));

        Assert.Equal(HotkeySetupCommand.ExitOk, RunCommand(running: true, "--set-hotkeys", "--validate", "Alt+Win+V").Code);
    }

    /// <summary>Only a first argument of <c>--set-hotkeys</c> selects the command.</summary>
    [Fact]
    public void SetHotkeys_MatchesOnlyAsFirstArgument()
    {
        Assert.True(HotkeySetupCommand.Matches(["--set-hotkeys", "Win+V"]));
        Assert.False(HotkeySetupCommand.Matches(["--background", "--set-hotkeys"]));
        Assert.False(HotkeySetupCommand.Matches([]));
        Assert.True(HotkeySetupCommand.IsValidateOnly(["--set-hotkeys", "Win+V", "--VALIDATE"]));
        Assert.False(HotkeySetupCommand.IsValidateOnly(["--validate"]));
    }

    /// <summary>The settings file in this test's data folder.</summary>
    private string SettingsPath => Path.Combine(dataDirectory, "settings.json");

    /// <summary>Runs the command against this test's data folder and returns its exit code and output lines.</summary>
    /// <param name="running">What the "is the app running" probe answers.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code and the non-empty output lines.</returns>
    private (int Code, string[] Lines) RunCommand(bool running, params string[] args)
    {
        using var output = new StringWriter { NewLine = "\n" };
        int code = HotkeySetupCommand.Run(args, output, SettingsPath, () => running);
        return (code, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// <see cref="HotkeyService"/> with several shortcuts, registered for real. No keyboard hook is installed
/// (<c>allowHookFallback: false</c>): a hook would see the whole session's keystrokes.
/// </summary>
/// <remarks>
/// <c>RegisterHotKey</c> needs the interactive window station (in a private one, as the clipboard tests use, it fails with
/// 1459 <c>ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION</c>), so these run on the session's own desktop with
/// Ctrl+Alt+Shift+F19–F23: keys no keyboard has, registered for milliseconds, the way <c>tools/probes/probe_hotkeys.cs</c>
/// probes. Where no interactive window station exists (a service session), the test is skipped.
/// </remarks>
public sealed class HotkeyServiceTests
{
    private const int OwnerHotkeyId = 0x7100;
    private const int ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION = 1459;

    /// <summary>
    /// Every shortcut is registered on its own; one another window owns fails with 1409 and stays inactive while the
    /// others work; disposing releases them so their owners could register them.
    /// </summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task Apply_RegistersEachShortcut_AndReportsTakenOnes()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F21", out var taken));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F22", out var free1));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F23", out var free2));

        using var owner = new MessageWindowThread("HotkeyOwner", messageOnly: true);
        var ownerResult = await owner.InvokeAsync(() => NativeMethods.RegisterHotKey(owner.Handle, OwnerHotkeyId, (uint)taken.Modifiers | NativeMethods.MOD_NOREPEAT, taken.VirtualKey) ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        if (ownerResult == ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION)
        {
            Assert.Skip("No interactive window station: RegisterHotKey is unavailable here.");
        }

        Assert.Equal(0, ownerResult);

        IReadOnlyList<HotkeyRegistration> result;
        using (var service = new HotkeyService())
        {
            result = await service.ApplyAsync([free1, taken, free2, free1], allowHookFallback: false);

            Assert.Equal([free1, taken, free2], result.Select(r => r.Gesture));
            Assert.Equal([HotkeyMode.RegisteredHotKey, HotkeyMode.None, HotkeyMode.RegisteredHotKey], result.Select(r => r.Mode));
            Assert.Equal(1409, result[1].Win32Error);
            Assert.Equal(result, service.Registrations);
            Assert.Contains("Used by another app", result[1].DescribeState(), StringComparison.Ordinal);

            // While the service holds them, nobody else can register the free ones.
            Assert.False(await owner.InvokeAsync(() => NativeMethods.RegisterHotKey(owner.Handle, OwnerHotkeyId + 1, (uint)free2.Modifiers | NativeMethods.MOD_NOREPEAT, free2.VirtualKey)));

            // Re-applying a shorter list releases what was dropped.
            var again = await service.ApplyAsync([free2], allowHookFallback: false);
            Assert.Equal(HotkeyMode.RegisteredHotKey, Assert.Single(again).Mode);
            Assert.True(await owner.InvokeAsync(() => NativeMethods.RegisterHotKey(owner.Handle, OwnerHotkeyId + 2, (uint)free1.Modifiers | NativeMethods.MOD_NOREPEAT, free1.VirtualKey)));
        }

        Assert.True(await owner.InvokeAsync(() => NativeMethods.RegisterHotKey(owner.Handle, OwnerHotkeyId + 3, (uint)free2.Modifiers | NativeMethods.MOD_NOREPEAT, free2.VirtualKey)));
        await owner.InvokeAsync(() =>
        {
            for (int id = OwnerHotkeyId; id <= OwnerHotkeyId + 3; id++)
            {
                NativeMethods.UnregisterHotKey(owner.Handle, id);
            }
        });
    }

    /// <summary>
    /// The probe the Settings card waits with: a shortcut another window holds reads as taken, a free one as free and stays
    /// free (the probe lets go at once), and one the service holds itself reads as taken until it is disabled.
    /// </summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task IsTakenElsewhere_TellsHeldFromFree()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F20", out var held));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F19", out var free));
        using var owner = new MessageWindowThread("HotkeyOwner", messageOnly: true);
        var ownerResult = await owner.InvokeAsync(() => NativeMethods.RegisterHotKey(owner.Handle, OwnerHotkeyId, (uint)held.Modifiers | NativeMethods.MOD_NOREPEAT, held.VirtualKey) ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        if (ownerResult == ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION)
        {
            Assert.Skip("No interactive window station: RegisterHotKey is unavailable here.");
        }

        Assert.Equal(0, ownerResult);
        try
        {
            using var service = new HotkeyService();
            Assert.True(await service.IsTakenElsewhereAsync(held));
            Assert.False(await service.IsTakenElsewhereAsync(free));
            Assert.False(await service.IsTakenElsewhereAsync(free));

            await service.ApplyAsync([free], allowHookFallback: false);
            Assert.True(await service.IsTakenElsewhereAsync(free));
            await service.DisableAsync();
            Assert.False(await service.IsTakenElsewhereAsync(free));
        }
        finally
        {
            await owner.InvokeAsync(() => NativeMethods.UnregisterHotKey(owner.Handle, OwnerHotkeyId));
        }
    }

    /// <summary>An empty list is refused: the panel needs at least one key.</summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task Apply_RefusesAnEmptyList()
    {
        using var service = new HotkeyService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(Array.Empty<HotkeyGesture>(), allowHookFallback: false));
        Assert.Empty(service.Registrations);
    }
}
