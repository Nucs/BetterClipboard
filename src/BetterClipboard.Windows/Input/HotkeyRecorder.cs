using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>What the shortcut recorder's keyboard hook does with one keyboard event.</summary>
public enum RecorderDecision
{
    /// <summary>Let the event through: it types into the box, or it is not the recorder's business.</summary>
    PassThrough,

    /// <summary>Swallow the event: it is (part of) the shortcut being recorded, so no app and no other shortcut sees it.</summary>
    Swallow,
}

/// <summary>
/// Pure decision logic of Settings' shortcut recorder, kept free of Win32 calls so it is unit-testable: while the shortcut
/// box listens, the first key pressed with Ctrl, Alt or Win — or a key that works alone, such as F9, Pause or a media key —
/// becomes the shortcut instead of reaching anything else; every other key types into the box as usual.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the main key is swallowed, never a modifier</b>, like <see cref="HotkeyInterceptor"/>: the rest of the system
/// keeps a consistent modifier state, and the held modifiers are read from it when the main key goes down. A swallowed
/// key's auto-repeats and its key-up are swallowed too (an orphan key-up would reach an app as a stray "K released"); a
/// key-up whose key-down was not swallowed always passes, so a key held before the box started listening is released
/// normally.
/// </para>
/// <para>
/// <b>What still reaches the box:</b> letters, digits, punctuation, Space and the editing keys without Ctrl, Alt or Win —
/// so a shortcut's name can still be typed, corrected with Backspace, and added with Enter — and every gesture
/// <see cref="HotkeyGesture.Problem"/> refuses: Alt+Tab and Alt+F4 keep working while the box has focus, and Ctrl+C,
/// Ctrl+X and Ctrl+V copy, cut and paste the box's text. Events BetterClipboard injected itself always pass.
/// </para>
/// </remarks>
public sealed class HotkeyCapture
{
    /// <summary>Main keys whose key-down was swallowed and whose key-up is still to come (rarely more than one).</summary>
    private readonly List<uint> swallowedKeys = new(2);

    /// <summary>
    /// Whether a swallowed key is still down: its key-up must still be swallowed, so the recorder's hook stays installed
    /// until it arrives (see <see cref="HotkeyRecorder.StopAsync"/>).
    /// </summary>
    public bool IsHoldingKeys => swallowedKeys.Count > 0;

    /// <summary>
    /// Decides the fate of one keyboard event.
    /// </summary>
    /// <param name="virtualKey">Virtual-key code of the event.</param>
    /// <param name="isKeyDown">Key-down (including auto-repeat) vs key-up.</param>
    /// <param name="isOwnInjection">Event was synthesized by BetterClipboard itself (<see cref="KeyboardInput.OwnInjectionMarker"/>).</param>
    /// <param name="heldModifiers">Modifiers down at the time of the event (read only for key-downs while listening).</param>
    /// <param name="listening">
    /// Whether the box takes shortcuts right now: <see langword="false"/> while its window is not in the foreground, or while
    /// the recorder stops — then only the key-ups of keys already swallowed are swallowed.
    /// </param>
    /// <param name="captured">The recorded shortcut when this key-down completed one; otherwise <see langword="null"/>.</param>
    /// <returns>Whether to swallow the event or let it through.</returns>
    public RecorderDecision OnKey(uint virtualKey, bool isKeyDown, bool isOwnInjection, HotkeyModifiers heldModifiers, bool listening, out HotkeyGesture? captured)
    {
        captured = null;
        if (isOwnInjection || HotkeyGesture.IsModifierKey(virtualKey))
        {
            return RecorderDecision.PassThrough;
        }

        if (!isKeyDown)
        {
            return swallowedKeys.Remove(virtualKey) ? RecorderDecision.Swallow : RecorderDecision.PassThrough;
        }

        if (swallowedKeys.Contains(virtualKey))
        {
            return RecorderDecision.Swallow; // auto-repeat of the key that made the shortcut
        }

        if (!listening)
        {
            return RecorderDecision.PassThrough;
        }

        // Typing and editing keys without Ctrl, Alt or Win stay with the box, so names can still be typed and edited.
        var kind = HotkeyGesture.KindOf(virtualKey);
        bool strong = (heldModifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;
        if (kind == HotkeyKeyKind.Unusable || (!strong && kind is not (HotkeyKeyKind.Function or HotkeyKeyKind.Standalone)))
        {
            return RecorderDecision.PassThrough;
        }

        // A gesture no shortcut may have keeps its usual meaning, also in the box: Alt+Tab switches windows, Ctrl+V pastes.
        if (!HotkeyGesture.TryCreate(heldModifiers, virtualKey, out var gesture, out _))
        {
            return RecorderDecision.PassThrough;
        }

        swallowedKeys.Add(virtualKey);
        captured = gesture;
        return RecorderDecision.Swallow;
    }

    /// <summary>
    /// Forgets the swallowed keys, after the hook was removed: their key-ups went to the system, and a later recording must
    /// not swallow a key-up whose key-down it never saw (the app below would keep that key "pressed").
    /// </summary>
    public void Reset() => swallowedKeys.Clear();
}

/// <summary>
/// Records the shortcut a person presses in Settings' shortcut box: a low-level keyboard hook, installed only while the box
/// listens, that turns the first key pressed with Ctrl, Alt or Win (or a key that works alone) into a
/// <see cref="HotkeyGesture"/> — whoever else owns that key — and lets every other key type into the box.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hook.</b> The box's own key events never see a shortcut another program owns: Explorer takes Win+E before any
/// window gets it, and BetterClipboard's own shortcuts open the panel. A <c>WH_KEYBOARD_LL</c> hook is called before the
/// system checks registered hotkeys, so it sees (and keeps) every combination except Ctrl+Alt+Delete; Win+L still locks the
/// PC. Hooks installed later are called first, so <see cref="RestartAsync"/> re-installs this one after
/// <see cref="HotkeyService"/> re-installs its own takeover hook.
/// </para>
/// <para>
/// <b>Safety.</b> A hook that swallowed keys by mistake would take the whole session's keyboard, so: it is installed only
/// between <see cref="StartAsync"/> and <see cref="StopAsync"/>; it takes nothing while the owner window is not in the
/// foreground (checked on every key, so a missed focus change cannot leak it into another app); it swallows only the main
/// key of a recorded shortcut (<see cref="HotkeyCapture"/>); and it lives on its own thread, so a busy UI thread cannot
/// make Windows drop it. When BetterClipboard exits, Windows removes it with the process.
/// </para>
/// </remarks>
public sealed class HotkeyRecorder : IDisposable
{
    /// <summary>Posted from the hook to the recorder's window when a shortcut was recorded (wParam = modifiers, lParam = key).</summary>
    private const uint WM_CAPTURED = WM_APP + 0x30;

    /// <summary>Posted from the hook when stopping and the last swallowed key went up: the hook can go.</summary>
    private const uint WM_DRAINED = WM_APP + 0x31;

    /// <summary>
    /// How long a stop waits for the key-up of a swallowed key before removing the hook anyway: long enough to let go of a
    /// shortcut, short enough that a stuck stop cannot hold the session's keyboard.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly MessageWindowThread window;
    private readonly nint ownerWindow;
    private readonly HotkeyCapture capture = new();
    private HookProc? hookProcedure;
    private nint hookHandle;
    private TaskCompletionSource? drained;
    private Timer? drainTimer;
    private volatile bool listening;

    /// <summary>
    /// Starts the recorder's thread. Nothing is hooked until <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="ownerWindow">The window that holds the shortcut box: the recorder takes keys only while it is the foreground window.</param>
    /// <exception cref="ArgumentException"><paramref name="ownerWindow"/> is 0.</exception>
    /// <exception cref="Win32Exception">The recorder's message window could not be created.</exception>
    public HotkeyRecorder(nint ownerWindow)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException("The recorder needs the window that holds the shortcut box.", nameof(ownerWindow));
        }

        this.ownerWindow = ownerWindow;
        window = new MessageWindowThread("HotkeyRecorder", messageOnly: true);
        window.MessageHandler = OnMessage;
    }

    /// <summary>
    /// Raised on the recorder's thread when a shortcut was pressed in the box (the key is already swallowed). Marshal to the
    /// UI thread before touching UI. The gesture is always one <see cref="HotkeyGesture.Problem"/> accepts.
    /// </summary>
    public event EventHandler<HotkeyGesture>? Captured;

    /// <summary>Whether the box takes shortcuts right now (between <see cref="StartAsync"/> and <see cref="StopAsync"/>).</summary>
    public bool IsListening => listening;

    /// <summary>
    /// Installs the hook (when not installed yet) and starts taking shortcuts. Call it when the shortcut box gets focus while
    /// its window is active. Cancels a stop that is still waiting for a key-up.
    /// </summary>
    /// <returns>A task completing when the hook is installed.</returns>
    /// <exception cref="Win32Exception">The hook could not be installed (the task faults with it).</exception>
    /// <exception cref="ObjectDisposedException">The recorder was disposed.</exception>
    public Task StartAsync() => window.InvokeAsync(() =>
    {
        listening = true;
        FinishDrain();
        if (hookHandle == 0)
        {
            Install();
        }
    });

    /// <summary>
    /// Stops taking shortcuts and removes the hook — at once, or as soon as the key of a just-recorded shortcut is released
    /// (at most <see cref="DrainTimeout"/>), so its key-up never reaches another app alone. Call it when the box loses focus or
    /// its window is deactivated or closed.
    /// </summary>
    /// <returns>A task completing when the hook is removed.</returns>
    /// <exception cref="ObjectDisposedException">The recorder was disposed.</exception>
    public Task StopAsync() => window.InvokeAsync(() =>
    {
        listening = false;
        if (hookHandle == 0 || !capture.IsHoldingKeys)
        {
            Uninstall();
            return Task.CompletedTask;
        }

        // A swallowed key is still down: keep the hook until its key-up arrives (WM_DRAINED), or the timeout passes.
        drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        drainTimer ??= new Timer(_ => window.Post(() =>
        {
            if (!listening)
            {
                Uninstall();
            }
        }), null, DrainTimeout, Timeout.InfiniteTimeSpan);
        return drained.Task;
    }).Unwrap();

    /// <summary>
    /// Re-installs the hook while the box listens, so it is called before a hook installed after it — BetterClipboard's own
    /// takeover hook, which <see cref="HotkeyService"/> re-installs whenever the shortcuts change. Without this, pressing a
    /// taken-over shortcut such as Win+V in the box would open the panel instead of recording it.
    /// </summary>
    /// <returns>A task completing when done (nothing happens while the box does not listen).</returns>
    /// <exception cref="Win32Exception">The new hook could not be installed; the old one stays (the task faults with it).</exception>
    /// <exception cref="ObjectDisposedException">The recorder was disposed.</exception>
    public Task RestartAsync() => window.InvokeAsync(() =>
    {
        if (!listening || hookHandle == 0)
        {
            return;
        }

        // The new hook goes in before the old one goes out, so no key slips through in between. For that instant both call
        // the same callback; a key the newer one swallows never reaches the older, and the decisions are idempotent.
        var previous = hookHandle;
        var procedure = hookProcedure!;
        var handle = SetWindowsHookEx(WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(procedure), GetModuleHandle(null), 0);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The shortcut recorder's keyboard hook could not be re-installed.");
        }

        hookHandle = handle;
        UnhookWindowsHookEx(previous);
    });

    /// <summary>Removes the hook at once (a key still held then reaches the system without its key-down) and stops the thread.</summary>
    public void Dispose()
    {
        try
        {
            window.InvokeAsync(() =>
            {
                listening = false;
                Uninstall();
            }).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            AppLog.Warn($"Removing the shortcut recorder's hook failed: {ex.InnerException?.Message}");
        }

        window.Dispose();
    }

    /// <summary>Installs the hook (recorder thread).</summary>
    /// <exception cref="Win32Exception"><c>SetWindowsHookEx</c> failed.</exception>
    private void Install()
    {
        hookProcedure = HookCallback; // the field keeps the delegate (and its native thunk) alive while hooked
        hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(hookProcedure), GetModuleHandle(null), 0);
        if (hookHandle == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            hookProcedure = null;
            listening = false;
            throw new Win32Exception(error, "The shortcut recorder's keyboard hook could not be installed.");
        }
    }

    /// <summary>Removes the hook, forgets swallowed keys and completes a pending stop (recorder thread).</summary>
    private void Uninstall()
    {
        if (hookHandle != 0)
        {
            UnhookWindowsHookEx(hookHandle);
            hookHandle = 0;
        }

        hookProcedure = null;
        capture.Reset();
        FinishDrain();
    }

    /// <summary>Completes a pending <see cref="StopAsync"/> and drops its timer (recorder thread).</summary>
    private void FinishDrain()
    {
        drainTimer?.Dispose();
        drainTimer = null;
        drained?.TrySetResult();
        drained = null;
    }

    /// <summary>Recorder-thread message handler: raises <see cref="Captured"/> and finishes stops, outside the hook callback.</summary>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">For <see cref="WM_CAPTURED"/>: the modifiers.</param>
    /// <param name="lParam">For <see cref="WM_CAPTURED"/>: the virtual key.</param>
    /// <returns>0 when handled, otherwise <see langword="null"/>.</returns>
    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_CAPTURED)
        {
            Captured?.Invoke(this, new HotkeyGesture((HotkeyModifiers)(uint)wParam, (uint)lParam));
            return 0;
        }

        if (msg == WM_DRAINED)
        {
            if (!listening && !capture.IsHoldingKeys)
            {
                Uninstall();
            }

            return 0;
        }

        return null;
    }

    /// <summary>The low-level keyboard hook; decides, maybe taps the mask key, posts, returns — nothing slower.</summary>
    /// <param name="nCode">Hook code.</param>
    /// <param name="wParam">Key message.</param>
    /// <param name="lParam">Pointer to <see cref="KBDLLHOOKSTRUCT"/>.</param>
    /// <returns>1 to swallow, otherwise the next hook's result.</returns>
    private unsafe nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var data = (KBDLLHOOKSTRUCT*)lParam;
                uint message = (uint)wParam;
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = message is WM_KEYUP or WM_SYSKEYUP;
                if (down || up)
                {
                    bool ownInjection = (data->flags & LLKHF_INJECTED) != 0 && (nint)data->dwExtraInfo == KeyboardInput.OwnInjectionMarker;

                    // Checked per key, not trusted from focus events: the box takes nothing while another app is in front.
                    bool takes = listening && down && GetForegroundWindow() == ownerWindow;
                    var held = takes ? KeyboardInput.GetHeldModifiers() : HotkeyModifiers.None;
                    var decision = capture.OnKey(data->vkCode, down, ownInjection, held, takes, out var captured);
                    if (captured is { } gesture)
                    {
                        // Chord the held Win/Alt with the mask key so releasing it neither opens Start nor focuses a menu bar.
                        if (gesture.UsesWinKey || (gesture.Modifiers & HotkeyModifiers.Alt) != 0)
                        {
                            KeyboardInput.TapMaskKey();
                        }

                        PostMessage(window.Handle, WM_CAPTURED, (nint)(uint)gesture.Modifiers, (nint)gesture.VirtualKey);
                    }

                    if (!listening && !capture.IsHoldingKeys)
                    {
                        PostMessage(window.Handle, WM_DRAINED, 0, 0);
                    }

                    if (decision == RecorderDecision.Swallow)
                    {
                        return 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into user32's hook dispatcher.
            AppLog.Error("Shortcut recorder hook callback failed.", ex);
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }
}
