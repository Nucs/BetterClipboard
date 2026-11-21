using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>A key the panel took from the app while it shows without the keyboard (<see cref="FocuslessPanelInput"/>).</summary>
/// <param name="VirtualKey">The virtual key (left and right modifiers are never reported: they are never taken).</param>
/// <param name="Modifiers">The modifiers held when the key went down.</param>
/// <param name="IsRepeat">Whether the key repeats because it is held down.</param>
/// <param name="Text">
/// The characters the key types with the keyboard layout of the app in front, control characters left out; <see langword="null"/>
/// for a key that types nothing printable, a dead key that waits for the next one, or a shortcut (Ctrl or Alt alone held).
/// </param>
/// <param name="Sequence">Counts the keys of the engagement; the panel hands it back to <see cref="FocuslessPanelInput.Acknowledge"/>.</param>
public readonly record struct PanelKey(uint VirtualKey, HotkeyModifiers Modifiers, bool IsRepeat, string? Text, long Sequence);

/// <summary>Why the panel that shows without the keyboard has lost the user (<see cref="FocuslessPanelInput"/> reports it).</summary>
public enum FocuslessLeave
{
    /// <summary>A mouse button went down outside the panel and the windows it owns (the click goes on to its window).</summary>
    ClickedOutside,

    /// <summary>A window other than the one the panel was opened over, and not one of its own, took the foreground.</summary>
    ForegroundChanged,

    /// <summary>Another desktop took the input (the lock screen, a UAC prompt): no key typed there reaches the hook.</summary>
    DesktopSwitched,

    /// <summary>
    /// The panel's thread took none of its keys for <see cref="FocuslessPanelInput.UnresponsiveAfter"/>: the hook stopped
    /// taking keys, so the keyboard is the app's again rather than lost to a panel that does not answer.
    /// </summary>
    Unresponsive,
}

/// <summary>
/// Gives the panel the keyboard while it shows without the focus (CLAUDE.md §2.59): a low-level keyboard hook takes the keys
/// the panel acts on before the app the user types in sees them, a low-level mouse hook notices a click outside the panel,
/// and WinEvent hooks notice another window taking the foreground or another desktop taking the input. Engaged only while
/// such a panel shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Some controls end their editing as soon as their window loses the focus: a rename in File Explorer, its search
/// box and address bar, a note's name in Obsidian's file tree. A panel that takes the keyboard when it opens ends that
/// editing, and the paste has nowhere to go. Windows' own Win+V leaves the focus where it is; a program outside the system's
/// input stack can do the same only by showing a window that does not activate and taking the keys on their way, as on-screen
/// keyboards do.
/// </para>
/// <para>
/// <b>How.</b> One thread of its own (highest priority: Windows drops a low-level hook whose callback answers too late) runs
/// both low-level hooks and the WinEvent hooks. The keyboard callback decides (<see cref="FocuslessKeyRules"/>), asks the
/// keyboard layout of the app in front what a key types (<c>ToUnicodeEx</c> on this thread, so a dead key waits in this
/// thread's state, never in the app's), hands the key to the panel's callback and returns: never slower than that. A taken
/// key's key-up is taken too, so the app never sees half a key. When a key is taken while Alt is held, the mask key is tapped
/// (<see cref="KeyboardInput.TapMaskKey"/>), so releasing Alt does not open the app's menu bar, which would end the editing.
/// BetterClipboard's own injected keys (its Ctrl+V, the mask taps) pass.
/// </para>
/// <para>
/// <b>Safety.</b> A hook that takes keys could take the session's keyboard, so: the panel engages it only while it shows and
/// stops it (<see cref="StopTaking"/>, at once from any thread) the moment it goes away; the keys pass again as soon as the
/// panel's thread has not taken a key for <see cref="UnresponsiveAfter"/>; a callback never throws into user32. When Windows
/// drops the hook (an overloaded PC), the keys simply reach the app again.
/// </para>
/// <para>
/// <b>Limits.</b> A hook sees keys, not the text an input method composes from them: Chinese, Japanese and Korean input
/// methods type their Latin letters into the search (the panel takes the keyboard for those, CLAUDE.md §2.59). A hook of a
/// process that is not elevated sees no keys typed into a window that runs as administrator, so the panel takes the keyboard
/// over such a window. Caps Lock's state is read once per engagement and then followed.
/// </para>
/// </remarks>
public sealed class FocuslessPanelInput : IDisposable
{
    /// <summary>
    /// How long the panel's thread may leave a key it was handed untaken before the hook gives the keyboard back to the app:
    /// far longer than any list reload, short enough that a panel that hangs does not take the session's keyboard with it.
    /// </summary>
    public static readonly TimeSpan UnresponsiveAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long <see cref="DisengageAsync"/> keeps the hooks for the key-ups of keys the panel took: longer than a paste with
    /// its held keys (a few hundred milliseconds on a busy PC), short enough that a key held down for good cannot keep them.
    /// </summary>
    public static readonly TimeSpan KeyUpWait = TimeSpan.FromSeconds(2);

    /// <summary>The hook thread: every callback, engaging and disengaging run on it, so its state needs no lock.</summary>
    private readonly MessageWindowThread thread;

    /// <summary>The keyboard hook procedure, kept for the object's lifetime: a collected delegate would crash the process.</summary>
    private readonly HookProc keyboardProcedure;

    /// <summary>The mouse hook procedure, kept alive like <see cref="keyboardProcedure"/>.</summary>
    private readonly HookProc mouseProcedure;

    /// <summary>The WinEvent procedure, kept alive: the native side holds only <see cref="winEventPointer"/>.</summary>
    private readonly WinEventProc winEventProcedure;

    /// <summary>The function pointer of <see cref="winEventProcedure"/>.</summary>
    private readonly nint winEventPointer;

    /// <summary>Where taken keys go (called on the hook thread; must only queue them for the panel's thread).</summary>
    private readonly Action<PanelKey> onKey;

    /// <summary>Where a leave goes, with the window concerned (0 when none); called on the hook thread, must only queue it.</summary>
    private readonly Action<FocuslessLeave, nint> onLeave;

    /// <summary>Keys that are down now, as the hook saw them (hook thread): a second key-down of one of them is a repeat.</summary>
    private readonly HashSet<uint> down = [];

    /// <summary>Keys whose key-down was taken (hook thread): their key-ups are taken too.</summary>
    private readonly HashSet<uint> taken = [];

    /// <summary>The installed keyboard hook, or 0.</summary>
    private nint keyboardHook;

    /// <summary>The installed mouse hook, or 0.</summary>
    private nint mouseHook;

    /// <summary>The WinEvent hook for the foreground window, or 0.</summary>
    private nint foregroundHook;

    /// <summary>The WinEvent hook for desktop switches, or 0.</summary>
    private nint desktopHook;

    /// <summary>The panel's window (hook thread): clicks on it, or on a window it owns, are inside.</summary>
    private nint panel;

    /// <summary>The root window the panel was opened over (hook thread): its own windows taking the foreground are not a leave.</summary>
    private nint target;

    /// <summary>Whether the keys are taken now (any thread: written from the panel's thread, read in the callback).</summary>
    private bool taking;

    /// <summary>Left and right Shift, Ctrl, Alt and Windows keys as the hook saw them (hook thread).</summary>
    private bool leftShift, rightShift, leftCtrl, rightCtrl, leftAlt, rightAlt, leftWin, rightWin;

    /// <summary>Whether Caps Lock is on (hook thread): read when engaging, flipped by every new press of the key.</summary>
    private bool capsLock;

    /// <summary>How many keys were handed to the panel in this engagement (hook thread).</summary>
    private long sequence;

    /// <summary>The newest key the panel took (any thread: written by <see cref="Acknowledge"/>).</summary>
    private long acknowledged;

    /// <summary>The acknowledgment the hook saw last, and when the panel last caught up or made progress (hook thread).</summary>
    private long seenAcknowledged;

    /// <summary>Milliseconds (<see cref="Environment.TickCount64"/>) since which the oldest unacknowledged key waits (hook thread).</summary>
    private long waitingSince;

    /// <summary>Starts the hook thread; nothing is hooked until <see cref="EngageAsync"/>.</summary>
    /// <param name="onKey">Receives every taken key on the hook thread; must only queue it for the panel's thread.</param>
    /// <param name="onLeave">Receives every leave on the hook thread; must only queue it for the panel's thread.</param>
    /// <exception cref="ArgumentNullException">A callback is <see langword="null"/>.</exception>
    /// <exception cref="Win32Exception">The hook thread's message window could not be created.</exception>
    public FocuslessPanelInput(Action<PanelKey> onKey, Action<FocuslessLeave, nint> onLeave)
    {
        ArgumentNullException.ThrowIfNull(onKey);
        ArgumentNullException.ThrowIfNull(onLeave);
        this.onKey = onKey;
        this.onLeave = onLeave;
        keyboardProcedure = KeyboardCallback;
        mouseProcedure = MouseCallback;
        winEventProcedure = OnWinEvent;
        winEventPointer = Marshal.GetFunctionPointerForDelegate(winEventProcedure);
        thread = new MessageWindowThread("FocuslessPanelInput", messageOnly: true, priority: ThreadPriority.Highest);
    }

    /// <summary>
    /// Whether a key is down on the keyboard now (<c>GetAsyncKeyState</c>), whichever thread asks: what a panel without the
    /// keyboard reads for the modifiers of a click (its own thread's key state never sees the keys, which go to the app).
    /// </summary>
    /// <param name="virtualKey">The virtual key (the side-less <c>VK_SHIFT</c>, <c>VK_CONTROL</c>, <c>VK_MENU</c> answer for either side).</param>
    /// <returns><see langword="true"/> while it is held down.</returns>
    public static bool IsPhysicallyDown(uint virtualKey) => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    /// <summary>Whether the hooks are installed (the panel shows without the keyboard).</summary>
    public bool IsEngaged => Volatile.Read(ref keyboardHook) != 0;

    /// <summary>Whether the keys are taken now (engaged, not stopped, the panel's thread answering).</summary>
    public bool IsTaking => Volatile.Read(ref taking);

    /// <summary>
    /// Installs the hooks and starts taking keys: the panel shows over <paramref name="targetWindow"/> without the keyboard.
    /// Engaging again while engaged only takes the new windows and starts taking keys again. Never throws.
    /// </summary>
    /// <param name="panelWindow">The panel's window.</param>
    /// <param name="targetWindow">The window the user types in (any of its windows: its root is used).</param>
    /// <returns><see langword="true"/> when the keys are taken now; <see langword="false"/> when the keyboard hook could not be installed (the panel then takes the keyboard instead).</returns>
    public async Task<bool> EngageAsync(nint panelWindow, nint targetWindow)
    {
        try
        {
            return await thread.InvokeAsync(() => Engage(panelWindow, targetWindow)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException or InvalidOperationException)
        {
            AppLog.Warn($"The panel could not take the keys without the focus ({ex.Message}); it takes the keyboard instead.");
            return false;
        }
    }

    /// <summary>
    /// Stops taking keys at once (callable from any thread; the hooks stay until <see cref="DisengageAsync"/>): the panel went
    /// away, or the user left a pinned panel for the app. Keys typed from now on reach the app; the key-up of a key the panel
    /// took is still taken.
    /// </summary>
    public void StopTaking() => Volatile.Write(ref taking, false);

    /// <summary>
    /// Stops taking keys at once and removes the hooks once the key-ups of the keys the panel took have come (at most
    /// <see cref="KeyUpWait"/>): the panel went away, or a pinned panel left the keyboard to the app. Never throws.
    /// </summary>
    /// <remarks>
    /// The wait keeps the app from seeing a key-up without its key-down — the Enter that pasted is usually still down when the
    /// panel goes away, and the paste's own key guard replays its key-up after the paste (CLAUDE.md §2.54), which this hook
    /// then takes. An engagement that starts during the wait (the shortcut pressed again) keeps the hooks.
    /// </remarks>
    /// <returns>A task completing once the hooks are removed, or kept for a new engagement.</returns>
    public async Task DisengageAsync()
    {
        Volatile.Write(ref taking, false);
        try
        {
            long deadline = Environment.TickCount64 + (long)KeyUpWait.TotalMilliseconds;
            while (Environment.TickCount64 < deadline
                && !Volatile.Read(ref taking)
                && await thread.InvokeAsync(() => keyboardHook != 0 && taken.Count > 0).ConfigureAwait(false))
            {
                await Task.Delay(15).ConfigureAwait(false);
            }

            await thread.InvokeAsync(() => Disengage(force: false)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            AppLog.Warn($"Removing the panel's key hooks failed ({ex.Message}).");
        }
    }

    /// <summary>
    /// The panel's thread took key number <paramref name="keySequence"/> (its <see cref="PanelKey.Sequence"/>): it answers, so
    /// the hook keeps taking keys. Callable from any thread.
    /// </summary>
    /// <param name="keySequence">The sequence number of the key just handled.</param>
    public void Acknowledge(long keySequence)
    {
        long current = Volatile.Read(ref acknowledged);
        while (keySequence > current)
        {
            long previous = Interlocked.CompareExchange(ref acknowledged, keySequence, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    /// <summary>Removes the hooks and stops the hook thread.</summary>
    public void Dispose()
    {
        Volatile.Write(ref taking, false);
        try
        {
            thread.InvokeAsync(() => Disengage(force: true)).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            AppLog.Warn($"Removing the panel's key hooks failed: {ex.InnerException?.Message}");
        }

        thread.Dispose();
    }

    /// <summary>
    /// The root window of <paramref name="window"/>'s owner chain (<c>GA_ROOTOWNER</c>): the panel's popups and the target's
    /// dialogs all lead to their main window.
    /// </summary>
    /// <param name="window">A window, or 0.</param>
    /// <returns>Its root owner, the window itself when it has none, or 0.</returns>
    private static nint RootOf(nint window)
    {
        if (window == 0)
        {
            return 0;
        }

        nint root = GetAncestor(window, GA_ROOTOWNER);
        return root == 0 ? window : root;
    }

    /// <summary>Installs the hooks, or takes the new windows when they are installed (hook thread).</summary>
    /// <param name="panelWindow">The panel's window.</param>
    /// <param name="targetWindow">The window the user types in.</param>
    /// <returns><see langword="true"/> when the keys are taken now.</returns>
    private bool Engage(nint panelWindow, nint targetWindow)
    {
        panel = panelWindow;
        target = RootOf(targetWindow);
        if (keyboardHook == 0)
        {
            // A fresh start. Hooks still installed (a disengagement waiting for key-ups) keep what they know: the key-up of a
            // key taken before must still be taken.
            down.Clear();
            taken.Clear();
        }

        // The modifiers as they are now (Win may still be down from Win+V), and Caps Lock's toggle, read once and then followed.
        leftShift = IsDown(VK_LSHIFT);
        rightShift = IsDown(VK_RSHIFT);
        leftCtrl = IsDown(VK_LCONTROL);
        rightCtrl = IsDown(VK_RCONTROL);
        leftAlt = IsDown(VK_LMENU);
        rightAlt = IsDown(VK_RMENU);
        leftWin = IsDown(VK_LWIN);
        rightWin = IsDown(VK_RWIN);
        capsLock = (GetKeyState((int)VK_CAPITAL) & 1) != 0;
        RestartAcknowledgmentClock();

        if (keyboardHook == 0)
        {
            nint module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(keyboardProcedure), module, 0);
            if (keyboardHook == 0)
            {
                AppLog.Warn($"The panel's keyboard hook could not be installed (error {Marshal.GetLastPInvokeError()}); it takes the keyboard instead.");
                return false;
            }

            // Without the mouse hook a click elsewhere in the same window would leave the panel taking keys: the panel then
            // still closes on Esc, on the shortcut and when another window comes to the front. Logged, not fatal.
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, Marshal.GetFunctionPointerForDelegate(mouseProcedure), module, 0);
            if (mouseHook == 0)
            {
                AppLog.Warn($"The panel's mouse hook could not be installed (error {Marshal.GetLastPInvokeError()}); a click outside it may not close it.");
            }

            // This process's own windows are left out: the panel's popups never take the foreground, and Settings coming to
            // the front is a click the mouse hook sees.
            foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, winEventPointer, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            desktopHook = SetWinEventHook(EVENT_SYSTEM_DESKTOPSWITCH, EVENT_SYSTEM_DESKTOPSWITCH, 0, winEventPointer, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        Volatile.Write(ref taking, true);
        return true;

        static bool IsDown(uint virtualKey) => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;
    }

    /// <summary>Removes every hook and forgets the keys (hook thread). Idempotent.</summary>
    /// <param name="force">
    /// Remove them even while keys are taken (the object is disposed); otherwise an engagement that started meanwhile keeps
    /// them (it ran before this on the same thread, so the check is exact).
    /// </param>
    private void Disengage(bool force)
    {
        if (!force && Volatile.Read(ref taking))
        {
            return;
        }

        Volatile.Write(ref taking, false);
        if (keyboardHook != 0)
        {
            // Cleared first: a callback queued before the removal finds no hook and lets its event pass.
            nint installed = keyboardHook;
            Volatile.Write(ref keyboardHook, 0);
            UnhookWindowsHookEx(installed);
        }

        if (mouseHook != 0)
        {
            nint installed = mouseHook;
            mouseHook = 0;
            UnhookWindowsHookEx(installed);
        }

        if (foregroundHook != 0)
        {
            nint installed = foregroundHook;
            foregroundHook = 0;
            UnhookWinEvent(installed);
        }

        if (desktopHook != 0)
        {
            nint installed = desktopHook;
            desktopHook = 0;
            UnhookWinEvent(installed);
        }

        down.Clear();
        taken.Clear();
    }

    /// <summary>Starts the panel's answer clock anew (an engagement starts or resumes): nothing it was handed waits.</summary>
    private void RestartAcknowledgmentClock()
    {
        seenAcknowledged = Volatile.Read(ref acknowledged);
        sequence = seenAcknowledged;
        waitingSince = Environment.TickCount64;
    }

    /// <summary>
    /// Whether the panel's thread has left a key untaken for <see cref="UnresponsiveAfter"/> (hook thread): progress since
    /// the last look restarts the clock.
    /// </summary>
    /// <returns><see langword="true"/> when the panel does not answer.</returns>
    private bool PanelUnresponsive()
    {
        long now = Environment.TickCount64;
        long seen = Volatile.Read(ref acknowledged);
        if (seen != seenAcknowledged)
        {
            seenAcknowledged = seen;
            waitingSince = now;
        }

        return seen < sequence && now - waitingSince > (long)UnresponsiveAfter.TotalMilliseconds;
    }

    /// <summary>The modifiers held now, as the hook followed them.</summary>
    /// <returns>The modifiers.</returns>
    private HotkeyModifiers Held()
    {
        var held = HotkeyModifiers.None;
        if (leftShift || rightShift)
        {
            held |= HotkeyModifiers.Shift;
        }

        if (leftCtrl || rightCtrl)
        {
            held |= HotkeyModifiers.Control;
        }

        if (leftAlt || rightAlt)
        {
            held |= HotkeyModifiers.Alt;
        }

        if (leftWin || rightWin)
        {
            held |= HotkeyModifiers.Win;
        }

        return held;
    }

    /// <summary>Follows a modifier key going down or up (and Caps Lock's toggle).</summary>
    /// <param name="virtualKey">The key.</param>
    /// <param name="isUp">Whether it went up.</param>
    /// <param name="isRepeat">Whether a key-down repeats.</param>
    private void Follow(uint virtualKey, bool isUp, bool isRepeat)
    {
        bool isDown = !isUp;
        switch (virtualKey)
        {
            case VK_LSHIFT: leftShift = isDown; break;
            case VK_RSHIFT: rightShift = isDown; break;
            case VK_LCONTROL: leftCtrl = isDown; break;
            case VK_RCONTROL: rightCtrl = isDown; break;
            case VK_LMENU: leftAlt = isDown; break;
            case VK_RMENU: rightAlt = isDown; break;
            case VK_LWIN: leftWin = isDown; break;
            case VK_RWIN: rightWin = isDown; break;
            case VK_CAPITAL when isDown && !isRepeat:
                // Windows toggles Caps Lock on each new press; a held key repeats without toggling.
                capsLock = !capsLock;
                break;
        }
    }

    /// <summary>
    /// What a key types with the keyboard layout of the app in front and the modifiers held: asked of the layout on this
    /// thread, so a dead key waits in this thread's keyboard state and the next key comes back combined with it.
    /// </summary>
    /// <param name="virtualKey">The key.</param>
    /// <param name="scanCode">Its scan code.</param>
    /// <param name="held">The modifiers held.</param>
    /// <returns>The printable characters, or <see langword="null"/> (none, or a dead key waiting).</returns>
    private unsafe string? Translate(uint virtualKey, uint scanCode, HotkeyModifiers held)
    {
        nint foreground = GetForegroundWindow();
        uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        nint layout = GetKeyboardLayout(foregroundThread);

        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        if ((held & HotkeyModifiers.Shift) != 0)
        {
            state[VK_SHIFT] = 0x80;
            state[leftShift ? VK_LSHIFT : VK_RSHIFT] = 0x80;
        }

        if ((held & HotkeyModifiers.Control) != 0)
        {
            state[VK_CONTROL] = 0x80;
            state[leftCtrl ? VK_LCONTROL : VK_RCONTROL] = 0x80;
        }

        if ((held & HotkeyModifiers.Alt) != 0)
        {
            state[VK_MENU] = 0x80;
            state[rightAlt ? VK_RMENU : VK_LMENU] = 0x80;
        }

        if (capsLock)
        {
            state[VK_CAPITAL] = 0x01;
        }

        char* buffer = stackalloc char[8];
        int count = ToUnicodeEx(virtualKey, scanCode, state, buffer, 8, 0, layout);
        return count <= 0 ? null : FocuslessKeyRules.Printable(new ReadOnlySpan<char>(buffer, count));
    }

    /// <summary>
    /// The low-level keyboard hook (hook thread): takes the panel's keys, follows the modifiers, and does nothing slower than
    /// asking the layout what a key types and queuing it.
    /// </summary>
    /// <param name="nCode">Hook code.</param>
    /// <param name="wParam">Key message.</param>
    /// <param name="lParam">Pointer to the event's <see cref="KBDLLHOOKSTRUCT"/>.</param>
    /// <returns>1 to take the event, otherwise the next hook's result.</returns>
    private unsafe nint KeyboardCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            // A callback that arrives after the hook's removal (queued before it) lets the key pass.
            if (nCode >= 0 && keyboardHook != 0)
            {
                var data = (KBDLLHOOKSTRUCT*)lParam;
                uint message = (uint)wParam;
                bool isUp = message is WM_KEYUP or WM_SYSKEYUP;
                bool own = (data->flags & LLKHF_INJECTED) != 0 && (nint)data->dwExtraInfo == KeyboardInput.OwnInjectionMarker;
                uint virtualKey = data->vkCode;
                if (!own && TakeKey(virtualKey, data->scanCode, isUp))
                {
                    return 1;
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into user32's hook dispatcher.
            AppLog.Error("The panel's keyboard hook failed.", ex);
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>Decides one key event (hook thread): follows it, and takes it when the panel acts on it.</summary>
    /// <param name="virtualKey">The key.</param>
    /// <param name="scanCode">Its scan code (for <see cref="FocuslessKeyRules.PacketKey"/>, the character).</param>
    /// <param name="isUp">Whether the key went up.</param>
    /// <returns><see langword="true"/> to take the event.</returns>
    private bool TakeKey(uint virtualKey, uint scanCode, bool isUp)
    {
        if (isUp)
        {
            down.Remove(virtualKey);
            Follow(virtualKey, isUp: true, isRepeat: false);

            // The key-up of a taken key-down is taken too, also after taking stopped: the app saw no key-down for it.
            return taken.Remove(virtualKey);
        }

        bool isRepeat = !down.Add(virtualKey);
        Follow(virtualKey, isUp: false, isRepeat);
        if (!Volatile.Read(ref taking))
        {
            return false;
        }

        var held = Held();
        if (!FocuslessKeyRules.Takes(virtualKey, held))
        {
            return false;
        }

        if (PanelUnresponsive())
        {
            // The panel has not taken a key for seconds: the keyboard goes back to the app rather than to a panel that hangs.
            Volatile.Write(ref taking, false);
            AppLog.Warn($"The panel took no keys for {UnresponsiveAfter.TotalSeconds:0} s; the keyboard went back to the app.");
            onLeave(FocuslessLeave.Unresponsive, 0);
            return false;
        }

        string? text = virtualKey == FocuslessKeyRules.PacketKey
            ? FocuslessKeyRules.Printable([(char)scanCode])
            : FocuslessKeyRules.MayType(virtualKey, held) ? Translate(virtualKey, scanCode, held) : null;

        // A key taken while Alt is held: the app saw Alt go down, and will see it go up with nothing between, which opens its
        // menu bar (and ends a rename). A tap of the mask key between them keeps that from happening (once per press).
        if ((held & HotkeyModifiers.Alt) != 0 && !isRepeat)
        {
            KeyboardInput.TapMaskKey();
        }

        taken.Add(virtualKey);
        if (Volatile.Read(ref acknowledged) >= sequence)
        {
            // Nothing waits: the panel's answer clock starts with this key.
            waitingSince = Environment.TickCount64;
        }

        sequence++;
        onKey(new PanelKey(virtualKey, held, isRepeat, text, sequence));
        return true;
    }

    /// <summary>
    /// The low-level mouse hook (hook thread): a button pressed outside the panel and the windows it owns is reported, and the
    /// click always goes on to its window. Mouse moves cost one comparison.
    /// </summary>
    /// <param name="nCode">Hook code.</param>
    /// <param name="wParam">Mouse message.</param>
    /// <param name="lParam">Pointer to the event's <see cref="MSLLHOOKSTRUCT"/>.</param>
    /// <returns>The next hook's result (never takes the event).</returns>
    private unsafe nint MouseCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            uint message = (uint)wParam;
            if (nCode >= 0 && mouseHook != 0 && message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                var data = (MSLLHOOKSTRUCT*)lParam;
                nint under = RootOf(WindowFromPoint(data->pt));
                if (under != panel)
                {
                    onLeave(FocuslessLeave.ClickedOutside, under);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("The panel's mouse hook failed.", ex);
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>
    /// A WinEvent (hook thread): another window took the foreground, or another desktop took the input. A foreground window
    /// whose owner chain leads to the target (its dialog, its own popup) is not a leave.
    /// </summary>
    /// <param name="hook">The hook that raised it.</param>
    /// <param name="eventType">The event.</param>
    /// <param name="hwnd">The window concerned.</param>
    /// <param name="idObject">The object (only <c>OBJID_WINDOW</c> counts for the foreground).</param>
    /// <param name="idChild">Unused.</param>
    /// <param name="eventThread">Unused.</param>
    /// <param name="eventTime">Unused.</param>
    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            if (hook == 0)
            {
                return;
            }

            if (hook == desktopHook && eventType == EVENT_SYSTEM_DESKTOPSWITCH)
            {
                onLeave(FocuslessLeave.DesktopSwitched, 0);
                return;
            }

            if (hook == foregroundHook && eventType == EVENT_SYSTEM_FOREGROUND && hwnd != 0 && idObject == OBJID_WINDOW)
            {
                nint root = RootOf(hwnd);
                if (root != target && root != panel)
                {
                    onLeave(FocuslessLeave.ForegroundChanged, root);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("The panel's window event hook failed.", ex);
        }
    }
}
