using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// The guard that holds back the keys a person types while a paste keeps Ctrl down, and types them after it (CLAUDE.md §2.54):
/// which key events it holds, how it types them again, and — opt-in, since it hooks the session's keyboard — its hook.
/// </summary>
public sealed class PasteKeyGuardTests
{
    /// <summary>The variable that lets the tests install a real low-level keyboard hook outside CI.</summary>
    private const string HookTestsVariable = "BETTERCLIPBOARD_HOOK_TESTS";

    /// <summary>
    /// Key events and whether the guard holds them: every key but BetterClipboard's own injections and the guard's own
    /// replays — a remapper's or a macro's keys are held like the person's.
    /// </summary>
    public static TheoryData<uint, long, bool> Events => new()
    {
        // A key the person pressed.
        { 0u, 0L, true },

        // A key-up the person released.
        { NativeMethods.LLKHF_UP, 0L, true },

        // Not injected: whatever a device puts into its extra info, it is the person's key.
        { 0u, (long)KeyboardInput.OwnInjectionMarker, true },

        // Another program's key (a remapper, a macro).
        { NativeMethods.LLKHF_INJECTED, 0L, true },

        // BetterClipboard's own Ctrl, V, mask key and modifier releases.
        { NativeMethods.LLKHF_INJECTED, (long)KeyboardInput.OwnInjectionMarker, false },
        { NativeMethods.LLKHF_INJECTED | NativeMethods.LLKHF_UP, (long)KeyboardInput.OwnInjectionMarker, false },

        // The guard's own replays of held keys.
        { NativeMethods.LLKHF_INJECTED, (long)KeyboardInput.ReplayMarker, false },
    };

    /// <summary>Each event of <see cref="Events"/> is held or let pass as listed.</summary>
    /// <param name="flags">The event's <c>LLKHF_*</c> flags.</param>
    /// <param name="extraInfo">The event's extra info.</param>
    /// <param name="holds">Whether the guard must hold it back.</param>
    [Theory]
    [MemberData(nameof(Events))]
    public void Holds_EveryKeyButBetterClipboards(uint flags, long extraInfo, bool holds) =>
        Assert.Equal(holds, PasteKeyGuard.Holds(flags, (nint)extraInfo));

    /// <summary>
    /// Held keys are typed again in their order, and a key typed while they go in follows them in a second round — the
    /// person's order, whatever the timing.
    /// </summary>
    [Fact]
    public void Drain_TypesHeldKeysInOrder_ThenThoseTypedMeanwhile()
    {
        var held = new List<HeldKey> { Key(0x41), Key(0x41, up: true) };
        var batches = new List<HeldKey[]>();
        var release = PasteKeyGuard.Drain(held, batch =>
        {
            batches.Add([.. batch]);
            if (batches.Count == 1)
            {
                // The hook runs inside the injection: an Enter typed now is held, after the batch.
                held.Add(Key(0x0D));
            }

            return batch.Count;
        });

        Assert.Equal(2, batches.Count);
        Assert.Equal([Key(0x41), Key(0x41, up: true)], batches[0]);
        Assert.Equal([Key(0x0D)], batches[1]);
        Assert.Equal(new HeldKeysRelease(3, 3), release);
        Assert.Empty(held);
    }

    /// <summary>Keys Windows refused to type again (UIPI) are counted apart, and the list is empty afterwards anyway.</summary>
    [Fact]
    public void Drain_CountsKeysWindowsRefused()
    {
        var held = new List<HeldKey> { Key(0x41), Key(0x41, up: true) };
        Assert.Equal(new HeldKeysRelease(2, 0), PasteKeyGuard.Drain(held, _ => 0));
        Assert.Empty(held);

        // Nothing held: nothing is typed, not even an empty batch.
        int calls = 0;
        Assert.Equal(default, PasteKeyGuard.Drain(held, _ => ++calls));
        Assert.Equal(0, calls);
    }

    /// <summary>
    /// A held key is typed again as it was: its virtual key, scan code, key-up and extended flags — and a character another
    /// program typed as Unicode, as Unicode — each stamped as a replay, so the guard lets it pass.
    /// </summary>
    [Fact]
    public void ReplayEvents_KeepEachKeyAsItWas_MarkedAsReplays()
    {
        var keys = new[]
        {
            new HeldKey(0x58, 0x2D, 0),
            new HeldKey(0xA3, 0x1D, NativeMethods.LLKHF_EXTENDED | NativeMethods.LLKHF_UP),
            new HeldKey(NativeMethods.VK_PACKET, 0xE9, NativeMethods.LLKHF_INJECTED),
        };

        var events = KeyboardInput.ReplayEvents(keys);

        Assert.Equal(3, events.Length);
        Assert.All(events, input =>
        {
            Assert.Equal(NativeMethods.INPUT_KEYBOARD, input.type);
            Assert.Equal(KeyboardInput.ReplayMarker, input.u.ki.dwExtraInfo);
            Assert.False(PasteKeyGuard.Holds(NativeMethods.LLKHF_INJECTED, input.u.ki.dwExtraInfo));
        });

        // An x down.
        Assert.Equal((ushort)0x58, events[0].u.ki.wVk);
        Assert.Equal((ushort)0x2D, events[0].u.ki.wScan);
        Assert.Equal(0u, events[0].u.ki.dwFlags);

        // The right Ctrl up: an extended key.
        Assert.Equal((ushort)0xA3, events[1].u.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY, events[1].u.ki.dwFlags);

        // An é another program typed as Unicode: no virtual key, the character in the scan code.
        Assert.Equal((ushort)0, events[2].u.ki.wVk);
        Assert.Equal((ushort)0xE9, events[2].u.ki.wScan);
        Assert.Equal(NativeMethods.KEYEVENTF_UNICODE, events[2].u.ki.dwFlags);
        Assert.Equal(0, KeyboardInput.TypeAgain([]));
    }

    /// <summary>
    /// The hook goes in with the first engagement and out with the last release, and a release with nothing engaged does
    /// nothing. Opt-in outside CI: it hooks the session's keyboard (keys typed meanwhile are held and typed again).
    /// </summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task EngageAndRelease_InstallAndRemoveTheHook()
    {
        SkipUnlessHooksAllowed();
        using var guard = new PasteKeyGuard();
        Assert.SkipUnless(await guard.EngageAsync(), "No low-level keyboard hook here (no interactive window station).");
        Assert.True(guard.IsEngaged);

        // A second paste joins the engagement; the hook goes with the last release only.
        Assert.True(await guard.EngageAsync());
        Assert.Equal(default, await guard.ReleaseAsync());
        Assert.True(guard.IsEngaged);

        var release = await guard.ReleaseAsync();
        Assert.False(guard.IsEngaged);
        Assert.Equal(release.Held, release.Typed);
        Assert.Equal(default, await guard.ReleaseAsync());
    }

    /// <summary>
    /// An engagement nobody releases (a paste that died) ends by itself, so the guard cannot keep the keyboard. Opt-in outside
    /// CI: it hooks the session's keyboard for a moment.
    /// </summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task Watchdog_ReleasesAnEngagementNobodyReleased()
    {
        SkipUnlessHooksAllowed();
        using var guard = new PasteKeyGuard(TimeSpan.FromMilliseconds(50));
        Assert.SkipUnless(await guard.EngageAsync(), "No low-level keyboard hook here (no interactive window station).");

        // Waited for by condition: the watchdog releases on the guard's own thread.
        Assert.True(SpinWait.SpinUntil(() => !guard.IsEngaged, TimeSpan.FromSeconds(30)), "The watchdog did not release the guard.");
        Assert.Equal(default, await guard.ReleaseAsync());
    }

    /// <summary>
    /// Skips a test that installs a real low-level keyboard hook unless it runs in CI or <see cref="HookTestsVariable"/> is 1:
    /// on a developer's PC it would hold the keys typed meanwhile.
    /// </summary>
    private static void SkipUnlessHooksAllowed() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" || Environment.GetEnvironmentVariable(HookTestsVariable) == "1",
            $"Set {HookTestsVariable}=1 to install a real keyboard hook here (CI runs it).");

    /// <summary>A held key event.</summary>
    /// <param name="virtualKey">The virtual key.</param>
    /// <param name="up">Whether it is a key-up.</param>
    /// <returns>The event.</returns>
    private static HeldKey Key(uint virtualKey, bool up = false) => new(virtualKey, 0, up ? NativeMethods.LLKHF_UP : 0);
}
