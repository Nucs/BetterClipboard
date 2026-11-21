using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Runs alone: its tests install a real low-level keyboard hook that takes the session's keys for a moment, and inject keys
/// (F24, which no keyboard has, and the mask key), which a hook of another test running beside them would see.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PanelKeyboardHookCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Panel keyboard hook (takes the session's keys for a moment)";
}

/// <summary>
/// The panel that shows without taking the keyboard from the app the user types in (CLAUDE.md §2.59): which keys its hook
/// takes and which of them may type (<see cref="FocuslessKeyRules"/>), and — opt-in outside CI, since it takes the
/// session's keys — the hook itself (<see cref="FocuslessPanelInput"/>).
/// </summary>
[Collection(PanelKeyboardHookCollection.Name)]
public sealed class FocuslessPanelInputTests
{
    /// <summary>The variable that lets the tests install a real low-level keyboard hook outside CI.</summary>
    private const string HookTestsVariable = "BETTERCLIPBOARD_HOOK_TESTS";

    /// <summary>F24 (<c>VK_F24</c>): a key the panel takes, which no keyboard has and no app acts on when it passes.</summary>
    private const uint F24 = 0x87;

    /// <summary>
    /// The mask key, which the panel's hook always lets pass: typed after the keys under test, it shows that their fate is
    /// decided (low-level hooks see events in order), so a key that never reached the observer was taken.
    /// </summary>
    private const uint Sentinel = KeyboardInput.MaskKey;

    /// <summary>
    /// Keys, the modifiers held, and whether the panel takes the key-down: nearly everything, so that no key the user means for
    /// the panel lands in the control they want to paste into — but never the modifiers, anything with Win, Windows' own
    /// switches, the lock, media and input-method keys, or the mask key.
    /// </summary>
    public static TheoryData<uint, HotkeyModifiers, bool> Takes => new()
    {
        // Letters, digits, Enter, Tab, Delete, F5 (which would refresh File Explorer and end a rename), a Unicode character
        // another program typed (VK_PACKET), and the panel's own Alt shortcuts.
        { 0x41, HotkeyModifiers.None, true },
        { 0x41, HotkeyModifiers.Shift, true },
        { 0x41, HotkeyModifiers.Control, true },
        { 0x41, HotkeyModifiers.Control | HotkeyModifiers.Alt, true },
        { 0x37, HotkeyModifiers.None, true },
        { 0x0D, HotkeyModifiers.None, true },
        { 0x09, HotkeyModifiers.None, true },
        { 0x09, HotkeyModifiers.Shift, true },
        { 0x2E, HotkeyModifiers.None, true },
        { 0x08, HotkeyModifiers.Control, true },
        { 0x74, HotkeyModifiers.None, true },
        { 0x87, HotkeyModifiers.None, true },
        { FocuslessKeyRules.PacketKey, HotkeyModifiers.None, true },
        { 0x43, HotkeyModifiers.Alt, true },
        { 0x1B, HotkeyModifiers.None, true },
        { 0x1B, HotkeyModifiers.Shift, true },

        // Windows' switches: Alt+Tab (also Ctrl+Alt+Tab), Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc.
        { 0x09, HotkeyModifiers.Alt, false },
        { 0x09, HotkeyModifiers.Alt | HotkeyModifiers.Control, false },
        { 0x1B, HotkeyModifiers.Alt, false },
        { 0x1B, HotkeyModifiers.Control, false },
        { 0x1B, HotkeyModifiers.Control | HotkeyModifiers.Shift, false },

        // Anything with a Windows key held: Win+V is the panel's own shortcut, handled by its shortcut service.
        { 0x56, HotkeyModifiers.Win, false },
        { 0x44, HotkeyModifiers.Win | HotkeyModifiers.Shift, false },

        // The modifiers themselves, either side.
        { 0x10, HotkeyModifiers.None, false },
        { 0xA0, HotkeyModifiers.None, false },
        { 0xA3, HotkeyModifiers.Control, false },
        { 0xA5, HotkeyModifiers.Alt, false },
        { 0x5B, HotkeyModifiers.None, false },

        // Caps Lock, Num Lock, Scroll Lock, Print Screen, Sleep, browser, volume, media and launch keys.
        { 0x14, HotkeyModifiers.None, false },
        { 0x90, HotkeyModifiers.None, false },
        { 0x91, HotkeyModifiers.None, false },
        { 0x2C, HotkeyModifiers.None, false },
        { 0x5F, HotkeyModifiers.None, false },
        { 0xA6, HotkeyModifiers.None, false },
        { 0xAF, HotkeyModifiers.None, false },
        { 0xB3, HotkeyModifiers.None, false },
        { 0xB7, HotkeyModifiers.None, false },

        // Input-method keys (Kana/Hangul, Hanja/Kanji, Convert, a key an input method took, Hankaku/Zenkaku) and the mask key.
        { 0x15, HotkeyModifiers.None, false },
        { 0x19, HotkeyModifiers.None, false },
        { 0x1C, HotkeyModifiers.None, false },
        { 0xE5, HotkeyModifiers.None, false },
        { 0xF3, HotkeyModifiers.None, false },
        { KeyboardInput.MaskKey, HotkeyModifiers.None, false },
    };

    /// <summary>
    /// Keys, the modifiers held, and whether the layout is asked what they type: plain, with Shift, or with AltGr (Ctrl+Alt,
    /// which types € or @ on many layouts) — never with Ctrl alone, Alt alone or Win, which are shortcuts.
    /// </summary>
    public static TheoryData<uint, HotkeyModifiers, bool> Typing => new()
    {
        { 0x41, HotkeyModifiers.None, true },
        { 0x41, HotkeyModifiers.Shift, true },
        { 0x45, HotkeyModifiers.Control | HotkeyModifiers.Alt, true },
        { 0x45, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, true },
        { 0x41, HotkeyModifiers.Control, false },
        { 0x41, HotkeyModifiers.Alt, false },
        { 0x41, HotkeyModifiers.Win, false },
        { 0x41, HotkeyModifiers.Control | HotkeyModifiers.Shift, false },

        // Enter may be asked: the layout answers a carriage return, which is not printable, so nothing is typed.
        { 0x0D, HotkeyModifiers.None, true },
        { 0x10, HotkeyModifiers.Shift, false },
        { 0x14, HotkeyModifiers.None, false },
        { KeyboardInput.MaskKey, HotkeyModifiers.None, false },
    };

    /// <summary>What a layout answered for a key, and the printable part the panel searches for (<see langword="null"/>: none).</summary>
    public static TheoryData<string, string?> Printables => new()
    {
        { "a", "a" },
        { "", null },
        { "\r", null },
        { "\u001B", null },
        { "\u0000", null },
        { "a\tb", "ab" },
        { "€", "€" },
        { "\U0001F600", "\U0001F600" },
        { "´x", "´x" },
    };

    /// <summary>The modifier keys are exactly Shift, Ctrl, Alt and the Windows keys, side-less or either side.</summary>
    [Fact]
    public void IsModifier_KnowsTheModifierKeys()
    {
        foreach (uint key in new uint[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
        {
            Assert.True(FocuslessKeyRules.IsModifier(key), $"0x{key:X2} is a modifier.");
        }

        foreach (uint key in new uint[] { 0x41, 0x0D, 0x14, 0x5D, 0x9F, 0xA6, KeyboardInput.MaskKey })
        {
            Assert.False(FocuslessKeyRules.IsModifier(key), $"0x{key:X2} is not a modifier.");
        }
    }

    /// <summary>Which key-downs the panel takes from the app.</summary>
    /// <param name="virtualKey">The key.</param>
    /// <param name="held">The modifiers held.</param>
    /// <param name="expected">Whether the panel takes it.</param>
    [Theory]
    [MemberData(nameof(Takes))]
    public void Takes_NearlyEverything_ButWindowsKeys(uint virtualKey, HotkeyModifiers held, bool expected) =>
        Assert.Equal(expected, FocuslessKeyRules.Takes(virtualKey, held));

    /// <summary>Which taken keys the keyboard layout is asked to type.</summary>
    /// <param name="virtualKey">The key.</param>
    /// <param name="held">The modifiers held.</param>
    /// <param name="expected">Whether it may type.</param>
    [Theory]
    [MemberData(nameof(Typing))]
    public void MayType_PlainShiftOrAltGr(uint virtualKey, HotkeyModifiers held, bool expected) =>
        Assert.Equal(expected, FocuslessKeyRules.MayType(virtualKey, held));

    /// <summary>The printable part of what a key typed.</summary>
    /// <param name="typed">What the layout answered.</param>
    /// <param name="expected">The printable characters, or <see langword="null"/>.</param>
    [Theory]
    [MemberData(nameof(Printables))]
    public void Printable_LeavesControlCharactersOut(string typed, string? expected) =>
        Assert.Equal(expected, FocuslessKeyRules.Printable(typed));

    /// <summary>
    /// While engaged, the hook takes a key the panel acts on, with its key-up, and hands the key-down over once, numbered;
    /// stopped, it lets keys pass to the app again. Opt-in outside CI: it takes the session's keys for a moment.
    /// </summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task Engaged_TakesTheKeyWithItsKeyUp_AndStoppedLetsKeysPass()
    {
        SkipUnlessHooksAllowed();

        // Installed first, so Windows calls it after the panel's hook: it sees only what the panel's hook let pass.
        using var observer = await KeyObserver.StartAsync();
        Assert.SkipUnless(observer is not null, "No low-level keyboard hook here (no interactive window station).");
        var keys = new ConcurrentQueue<PanelKey>();
        using var input = new FocuslessPanelInput(keys.Enqueue, (_, _) => { });
        Assert.SkipUnless(await input.EngageAsync(0, 0), "No low-level keyboard hook here (no interactive window station).");
        Assert.True(input.IsEngaged);
        Assert.True(input.IsTaking);

        Type(F24, up: false);
        Type(F24, up: true);
        Type(Sentinel, up: false);
        Type(Sentinel, up: true);
        Assert.True(observer.WaitFor(Sentinel, up: true, count: 1), "The sentinel key never reached the observer.");
        Assert.Equal(0, observer.Count(F24, up: false));
        Assert.Equal(0, observer.Count(F24, up: true));
        var key = Assert.Single(keys);
        Assert.Equal(F24, key.VirtualKey);
        Assert.False(key.IsRepeat);
        Assert.Null(key.Text);
        Assert.Equal(1, key.Sequence);
        input.Acknowledge(key.Sequence);

        // Stopped: the same key reaches the app, both halves, and nothing more is handed to the panel.
        input.StopTaking();
        Assert.False(input.IsTaking);
        Type(F24, up: false);
        Type(F24, up: true);
        Type(Sentinel, up: false);
        Type(Sentinel, up: true);
        Assert.True(observer.WaitFor(Sentinel, up: true, count: 2), "The second sentinel never reached the observer.");
        Assert.Equal(1, observer.Count(F24, up: false));
        Assert.Equal(1, observer.Count(F24, up: true));
        Assert.Single(keys);
    }

    /// <summary>
    /// The key-up of a key the panel took is taken too after taking stopped, and disengaging waits for it before the hooks go:
    /// the app never sees half a key. Opt-in outside CI: it takes the session's keys for a moment.
    /// </summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task Disengage_WaitsForTheKeyUpOfATakenKey_AndTakesIt()
    {
        SkipUnlessHooksAllowed();
        using var observer = await KeyObserver.StartAsync();
        Assert.SkipUnless(observer is not null, "No low-level keyboard hook here (no interactive window station).");
        var keys = new ConcurrentQueue<PanelKey>();
        using var input = new FocuslessPanelInput(keys.Enqueue, (_, _) => { });
        Assert.SkipUnless(await input.EngageAsync(0, 0), "No low-level keyboard hook here (no interactive window station).");

        Type(F24, up: false);
        Assert.True(SpinWait.SpinUntil(() => !keys.IsEmpty, TimeSpan.FromSeconds(10)), "The panel was never handed F24.");

        // The panel goes away while F24 is still down: taking stops at once, the hooks stay for its key-up.
        var disengaging = input.DisengageAsync();
        Assert.False(input.IsTaking);
        Type(F24, up: true);
        await disengaging;
        Assert.False(input.IsEngaged);

        Type(Sentinel, up: false);
        Type(Sentinel, up: true);
        Assert.True(observer.WaitFor(Sentinel, up: true, count: 1), "The sentinel key never reached the observer.");
        Assert.Equal(0, observer.Count(F24, up: false));
        Assert.Equal(0, observer.Count(F24, up: true));
    }

    /// <summary>
    /// A panel whose thread takes none of the keys it was handed for <see cref="FocuslessPanelInput.UnresponsiveAfter"/> loses
    /// them: the next key reaches the app, the panel is told why, and the hook stops taking keys. Opt-in outside CI: it takes
    /// the session's keys for about two seconds.
    /// </summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task APanelThatDoesNotAnswer_GetsNoMoreKeys()
    {
        SkipUnlessHooksAllowed();
        using var observer = await KeyObserver.StartAsync();
        Assert.SkipUnless(observer is not null, "No low-level keyboard hook here (no interactive window station).");
        var keys = new ConcurrentQueue<PanelKey>();
        var leaves = new ConcurrentQueue<FocuslessLeave>();
        using var input = new FocuslessPanelInput(keys.Enqueue, (leave, _) => leaves.Enqueue(leave));
        Assert.SkipUnless(await input.EngageAsync(0, 0), "No low-level keyboard hook here (no interactive window station).");

        // F24 now and then, never acknowledged, until the hook gives up on the panel (or a generous deadline passes).
        var deadline = DateTime.UtcNow + FocuslessPanelInput.UnresponsiveAfter + TimeSpan.FromSeconds(13);
        while (!leaves.Contains(FocuslessLeave.Unresponsive) && DateTime.UtcNow < deadline)
        {
            Type(F24, up: false);
            Type(F24, up: true);
            await Task.Delay(100);
        }

        Assert.Contains(FocuslessLeave.Unresponsive, leaves);
        Assert.False(input.IsTaking);
        Assert.NotEmpty(keys);

        // The key that found the panel unresponsive went on to the app.
        Type(Sentinel, up: false);
        Type(Sentinel, up: true);
        Assert.True(observer.WaitFor(Sentinel, up: true, count: 1), "The sentinel key never reached the observer.");
        Assert.True(observer.Count(F24, up: false) >= 1, "No F24 reached the app after the panel stopped answering.");
    }

    /// <summary>
    /// Skips a test that installs a real low-level keyboard hook unless it runs in CI or <see cref="HookTestsVariable"/> is 1:
    /// on a developer's PC the panel's hook would take the keys typed meanwhile.
    /// </summary>
    private static void SkipUnlessHooksAllowed() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" || Environment.GetEnvironmentVariable(HookTestsVariable) == "1",
            $"Set {HookTestsVariable}=1 to install a real keyboard hook here (CI runs it).");

    /// <summary>Types one key event as another program would (not stamped as BetterClipboard's own, which the hook lets pass).</summary>
    /// <param name="virtualKey">The key.</param>
    /// <param name="up">Whether it is the key-up.</param>
    private static void Type(uint virtualKey, bool up) =>
        Assert.Equal(1, KeyboardInput.TypeAgain([new HeldKey(virtualKey, 0, up ? NativeMethods.LLKHF_UP : 0)]));

    /// <summary>
    /// A low-level keyboard hook of the test's own, installed before the panel's: Windows calls the newest hook first, so this
    /// one sees only the key events the panel's hook let pass on.
    /// </summary>
    private sealed class KeyObserver : IDisposable
    {
        /// <summary>The thread whose message loop runs the hook (a low-level hook is called on the thread that installed it).</summary>
        private readonly MessageWindowThread thread = new("KeyObserver", messageOnly: true, priority: ThreadPriority.Highest);

        /// <summary>The hook procedure, kept alive while the hook is installed.</summary>
        private readonly NativeMethods.HookProc procedure;

        /// <summary>Every event seen, oldest first.</summary>
        private readonly ConcurrentQueue<(uint Key, bool Up)> seen = new();

        /// <summary>The installed hook, or 0.</summary>
        private nint hook;

        /// <summary>Creates the thread; <see cref="StartAsync"/> installs the hook.</summary>
        private KeyObserver() => procedure = Callback;

        /// <summary>Starts an observer.</summary>
        /// <returns>The observer, or <see langword="null"/> when no low-level hook can be installed here.</returns>
        public static async Task<KeyObserver?> StartAsync()
        {
            var observer = new KeyObserver();
            bool installed = await observer.thread.InvokeAsync(() =>
            {
                observer.hook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(observer.procedure), NativeMethods.GetModuleHandle(null), 0);
                return observer.hook != 0;
            });
            if (!installed)
            {
                observer.Dispose();
                return null;
            }

            return observer;
        }

        /// <summary>How many events of a key, going down or up, reached the observer.</summary>
        /// <param name="virtualKey">The key.</param>
        /// <param name="up">Key-ups rather than key-downs.</param>
        /// <returns>The count.</returns>
        public int Count(uint virtualKey, bool up) => seen.Count(e => e.Key == virtualKey && e.Up == up);

        /// <summary>Waits until at least <paramref name="count"/> such events have reached the observer (at most 10 s).</summary>
        /// <param name="virtualKey">The key.</param>
        /// <param name="up">Key-ups rather than key-downs.</param>
        /// <param name="count">How many to wait for.</param>
        /// <returns><see langword="true"/> when they came.</returns>
        public bool WaitFor(uint virtualKey, bool up, int count) =>
            SpinWait.SpinUntil(() => Count(virtualKey, up) >= count, TimeSpan.FromSeconds(10));

        /// <summary>Removes the hook and stops the thread.</summary>
        public void Dispose()
        {
            thread.InvokeAsync(() =>
            {
                if (hook != 0)
                {
                    NativeMethods.UnhookWindowsHookEx(hook);
                    hook = 0;
                }
            }).Wait(TimeSpan.FromSeconds(5));
            thread.Dispose();
        }

        /// <summary>Records a key event and passes it on: an observer, never a taker.</summary>
        /// <param name="nCode">Hook code.</param>
        /// <param name="wParam">Key message.</param>
        /// <param name="lParam">Pointer to the event's <see cref="NativeMethods.KBDLLHOOKSTRUCT"/>.</param>
        /// <returns>The next hook's result.</returns>
        private nint Callback(int nCode, nint wParam, nint lParam)
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                seen.Enqueue((data.vkCode, (data.flags & NativeMethods.LLKHF_UP) != 0));
            }

            return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
        }
    }
}
