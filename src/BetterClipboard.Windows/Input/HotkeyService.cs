using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>How the global shortcut is currently wired.</summary>
public enum HotkeyMode
{
    /// <summary>Not active (registration failed or disabled).</summary>
    None,

    /// <summary>Registered with <c>RegisterHotKey</c> — the cheap, conflict-free path.</summary>
    RegisteredHotKey,

    /// <summary>
    /// Intercepted with a <c>WH_KEYBOARD_LL</c> hook because another component (Explorer, for Win+V)
    /// owns the combination.
    /// </summary>
    KeyboardHook,
}

/// <summary>
/// Result of applying a shortcut.
/// </summary>
/// <param name="Gesture">The shortcut.</param>
/// <param name="Mode">How it is wired.</param>
/// <param name="Win32Error">The <c>RegisterHotKey</c> error that forced the fallback or failure (0 when registered directly).</param>
public sealed record HotkeyRegistration(HotkeyGesture Gesture, HotkeyMode Mode, int Win32Error)
{
    /// <summary>Whether pressing the shortcut will open BetterClipboard.</summary>
    public bool IsActive => Mode != HotkeyMode.None;

    /// <summary>
    /// Human-readable status for the settings page.
    /// </summary>
    /// <returns>A one-line description.</returns>
    public string Describe() => Mode switch
    {
        HotkeyMode.RegisteredHotKey => $"{Gesture} is registered.",
        HotkeyMode.KeyboardHook => $"{Gesture} is owned by Windows — intercepted with a keyboard hook (taken over).",
        _ when Win32Error == ERROR_HOTKEY_ALREADY_REGISTERED => $"{Gesture} is already used by another app. Pick another shortcut or allow the keyboard-hook takeover.",
        _ => $"{Gesture} could not be registered (error {Win32Error}).",
    };

    /// <summary>
    /// The same status without the shortcut's name, for a row that shows the name next to it (Settings' shortcut list).
    /// </summary>
    /// <returns>A short sentence.</returns>
    public string DescribeState() => Mode switch
    {
        HotkeyMode.RegisteredHotKey => "Registered.",
        HotkeyMode.KeyboardHook => "Owned by Windows or another app: taken over with a keyboard hook while BetterClipboard runs.",
        _ when Win32Error == ERROR_HOTKEY_ALREADY_REGISTERED => "Used by another app. Turn on the takeover below, or pick another shortcut.",
        _ => $"Could not be registered (error {Win32Error}).",
    };
}

/// <summary>
/// Owns the global "open BetterClipboard" shortcuts — one or several — on a dedicated input thread.
/// </summary>
/// <remarks>
/// <para>
/// <c>RegisterHotKey</c> is tried first for each shortcut. It fails with <c>ERROR_HOTKEY_ALREADY_REGISTERED</c> for
/// combos the shell or another app owns — <c>Win+V</c> is registered by explorer.exe for its clipboard history
/// (verified in CLAUDE.md §1.6) — in which case one low-level keyboard hook intercepts exactly those combinations.
/// The hook lives only as long as this service, so quitting BetterClipboard instantly gives Win+V back to
/// Windows; nothing is changed on disk.
/// </para>
/// <para>
/// The hook callback runs for every keystroke in the session and therefore does the bare minimum:
/// decide, maybe tap the mask key, post a message, return. The actual work (capturing the foreground
/// context, raising <see cref="Pressed"/>) happens after the callback returns.
/// </para>
/// </remarks>
public sealed class HotkeyService : IDisposable
{
    /// <summary>
    /// <c>RegisterHotKey</c> id of the first shortcut; shortcut <c>i</c> uses this plus <c>i</c>. Ids are per window, so the
    /// range only has to stay below 0xC000 (the range <c>GlobalAddAtom</c> ids use for shared DLLs).
    /// </summary>
    private const int FirstHotkeyId = 0xB00C;

    /// <summary>Id of <see cref="IsTakenElsewhereAsync"/>'s probe: below the shortcuts' range, so it never collides with one.</summary>
    private const int ProbeHotkeyId = 0xB000;
    private const uint WM_HOOK_FIRED = WM_APP + 0x20;

    private readonly MessageWindowThread window;
    private readonly List<int> registeredIds = [];
    private HookProc? hookProcedure;
    private nint hookHandle;
    private HotkeyInterceptorSet? interceptors;

    /// <summary>
    /// Starts the input thread. No shortcut is active until <see cref="ApplyAsync(IReadOnlyList{HotkeyGesture}, bool)"/>.
    /// </summary>
    /// <remarks>
    /// The thread lives on the session's own desktop: <c>RegisterHotKey</c> needs the interactive window station (in a
    /// private one it fails with 1459, <c>ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION</c>), so unlike the clipboard listener
    /// this service cannot be isolated for tests.
    /// </remarks>
    /// <exception cref="System.ComponentModel.Win32Exception">The input window could not be created.</exception>
    public HotkeyService()
    {
        window = new MessageWindowThread("Input", messageOnly: true);
        window.MessageHandler = OnMessage;
    }

    /// <summary>
    /// Raised on the input thread when any of the shortcuts is pressed, with the context captured at that
    /// instant (the window to paste into). Marshal to the UI thread before touching UI.
    /// </summary>
    public event EventHandler<ForegroundContext>? Pressed;

    /// <summary>
    /// The registration state of each shortcut, in the order applied; empty before the first apply. Replaced as a whole,
    /// so a reader on another thread always sees one consistent list.
    /// </summary>
    public IReadOnlyList<HotkeyRegistration> Registrations { get; private set; } = [];

    /// <summary>
    /// Replaces the active shortcut with a single one.
    /// </summary>
    /// <param name="gesture">The new shortcut.</param>
    /// <param name="allowHookFallback">Allow the low-level-hook takeover when the combination is owned by someone else.</param>
    /// <returns>The resulting registration (check <see cref="HotkeyRegistration.IsActive"/>).</returns>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    public async Task<HotkeyRegistration> ApplyAsync(HotkeyGesture gesture, bool allowHookFallback) =>
        (await ApplyAsync([gesture], allowHookFallback).ConfigureAwait(false))[0];

    /// <summary>
    /// Replaces the active shortcuts: every gesture is registered, and those another app or Windows owns are taken over
    /// by one keyboard hook when <paramref name="allowHookFallback"/> allows it.
    /// </summary>
    /// <param name="gestures">The shortcuts, in order; repeats are applied once. Callers parse settings with <see cref="HotkeyList"/>.</param>
    /// <param name="allowHookFallback">Allow the low-level-hook takeover when a combination is owned by someone else.</param>
    /// <returns>One registration per distinct gesture, in order (check <see cref="HotkeyRegistration.IsActive"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gestures"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="gestures"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    public Task<IReadOnlyList<HotkeyRegistration>> ApplyAsync(IReadOnlyList<HotkeyGesture> gestures, bool allowHookFallback)
    {
        ArgumentNullException.ThrowIfNull(gestures);
        if (gestures.Count == 0)
        {
            throw new ArgumentException("At least one shortcut is needed.", nameof(gestures));
        }

        // Copied before the hop to the input thread: the caller may change its list meanwhile.
        var distinct = gestures.Distinct().ToArray();
        return window.InvokeAsync(() => Apply(distinct, allowHookFallback));
    }

    /// <summary>
    /// Deactivates every shortcut (e.g. while Explorer restarts to take a shortcut back, so it can register it).
    /// </summary>
    /// <returns>A task completing when released.</returns>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    public Task DisableAsync() => window.InvokeAsync(ReleaseCurrent);

    /// <summary>
    /// Whether someone else has <paramref name="gesture"/> registered right now: a <c>RegisterHotKey</c> probe on the input
    /// window, released at once when it succeeds.
    /// </summary>
    /// <remarks>
    /// Used to wait until a restarted Explorer has registered a shortcut it was given back. Shortcuts this service holds
    /// itself also count as taken, so call <see cref="DisableAsync"/> first. The probe holds the shortcut for
    /// microseconds; a registration attempted in exactly that moment would fail, which is why callers poll rather than
    /// probe in a tight loop.
    /// </remarks>
    /// <param name="gesture">The shortcut to probe.</param>
    /// <returns><see langword="true"/> when it is registered by another window (error 1409); <see langword="false"/> when it is free (or fails otherwise).</returns>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    public Task<bool> IsTakenElsewhereAsync(HotkeyGesture gesture) => window.InvokeAsync(() =>
    {
        if (RegisterHotKey(window.Handle, ProbeHotkeyId, (uint)gesture.Modifiers | MOD_NOREPEAT, gesture.VirtualKey))
        {
            UnregisterHotKey(window.Handle, ProbeHotkeyId);
            return false;
        }

        return Marshal.GetLastPInvokeError() == ERROR_HOTKEY_ALREADY_REGISTERED;
    });

    /// <summary>Releases the shortcuts/hook and stops the input thread.</summary>
    public void Dispose()
    {
        try
        {
            window.InvokeAsync(ReleaseCurrent).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            AppLog.Warn($"Releasing the hotkey failed: {ex.InnerException?.Message}");
        }

        window.Dispose();
    }

    /// <summary>Input-thread implementation of <see cref="ApplyAsync(IReadOnlyList{HotkeyGesture}, bool)"/>.</summary>
    /// <param name="gestures">Distinct shortcuts.</param>
    /// <param name="allowHookFallback">Whether the hook fallback is allowed.</param>
    /// <returns>The registrations, in order.</returns>
    private IReadOnlyList<HotkeyRegistration> Apply(IReadOnlyList<HotkeyGesture> gestures, bool allowHookFallback)
    {
        ReleaseCurrent();
        var results = new HotkeyRegistration[gestures.Count];
        var hooked = new List<int>();
        for (int i = 0; i < gestures.Count; i++)
        {
            var gesture = gestures[i];
            int id = FirstHotkeyId + i;
            if (RegisterHotKey(window.Handle, id, (uint)gesture.Modifiers | MOD_NOREPEAT, gesture.VirtualKey))
            {
                registeredIds.Add(id);

                // Logged so "which path is Win+V on?" is answerable from the log alone (e.g. after Explorer
                // released it via DisabledHotkeys, this is the line to look for instead of the hook one).
                AppLog.Info($"{gesture} registered with RegisterHotKey (no keyboard hook needed).");
                results[i] = new HotkeyRegistration(gesture, HotkeyMode.RegisteredHotKey, 0);
                continue;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == ERROR_HOTKEY_ALREADY_REGISTERED && allowHookFallback)
            {
                // Decided after the loop: one hook serves every shortcut that needs it, and if it cannot be installed
                // they all fail together.
                hooked.Add(i);
                results[i] = new HotkeyRegistration(gesture, HotkeyMode.KeyboardHook, error);
                continue;
            }

            AppLog.Warn($"Could not register {gesture} (error {error}).");
            results[i] = new HotkeyRegistration(gesture, HotkeyMode.None, error);
        }

        if (hooked.Count > 0)
        {
            interceptors = new HotkeyInterceptorSet(hooked.Select(i => gestures[i]));
            hookProcedure = HookCallback; // field keeps the delegate (and its native thunk) alive
            hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(hookProcedure), GetModuleHandle(null), 0);
            if (hookHandle != 0)
            {
                foreach (int i in hooked)
                {
                    AppLog.Info($"{gestures[i]} is owned by another component; intercepting it with a keyboard hook.");
                }
            }
            else
            {
                AppLog.Error($"SetWindowsHookEx failed (error {Marshal.GetLastPInvokeError()}).");
                interceptors = null;
                hookProcedure = null;
                foreach (int i in hooked)
                {
                    AppLog.Warn($"Could not register {gestures[i]} (error {ERROR_HOTKEY_ALREADY_REGISTERED}).");
                    results[i] = results[i] with { Mode = HotkeyMode.None };
                }
            }
        }

        Registrations = results;
        return results;
    }

    /// <summary>Unregisters the hotkeys and removes the hook (input thread).</summary>
    private void ReleaseCurrent()
    {
        foreach (int id in registeredIds)
        {
            UnregisterHotKey(window.Handle, id);
        }

        registeredIds.Clear();
        if (hookHandle != 0)
        {
            UnhookWindowsHookEx(hookHandle);
            hookHandle = 0;
        }

        interceptors = null;
        hookProcedure = null;
        Registrations = Registrations.Select(r => r with { Mode = HotkeyMode.None }).ToArray();
    }

    /// <summary>Input-thread message handler.</summary>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">Parameter (the hotkey id for <c>WM_HOTKEY</c>).</param>
    /// <param name="lParam">Parameter.</param>
    /// <returns>0 when handled, otherwise <see langword="null"/>.</returns>
    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        // Only ids this service registered: anything else posting WM_HOTKEY to the window is not a shortcut of ours.
        if ((msg == WM_HOTKEY && registeredIds.Contains((int)wParam)) || msg == WM_HOOK_FIRED)
        {
            Pressed?.Invoke(this, ForegroundContext.Capture());
            return 0;
        }

        return null;
    }

    /// <summary>The low-level keyboard hook; keep it trivial (see class remarks).</summary>
    /// <param name="nCode">Hook code.</param>
    /// <param name="wParam">Key message.</param>
    /// <param name="lParam">Pointer to <see cref="KBDLLHOOKSTRUCT"/>.</param>
    /// <returns>1 to swallow, otherwise the next hook's result.</returns>
    private unsafe nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && interceptors is { } active)
            {
                var data = (KBDLLHOOKSTRUCT*)lParam;
                uint message = (uint)wParam;
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = message is WM_KEYUP or WM_SYSKEYUP;
                if ((down || up) && active.Handles(data->vkCode))
                {
                    // Only our own synthetic keys are exempt (see KeyboardInput.OwnInjectionMarker).
                    bool ownInjection = (data->flags & LLKHF_INJECTED) != 0 && (nint)data->dwExtraInfo == KeyboardInput.OwnInjectionMarker;
                    switch (active.OnKey(data->vkCode, down, ownInjection, KeyboardInput.GetHeldModifiers(), out var fired))
                    {
                        case InterceptDecision.SwallowAndFire:
                            // Chord the held Win/Alt with the mask key so releasing it later neither opens
                            // Start nor focuses a menu bar in the app underneath.
                            if (fired.UsesWinKey || (fired.Modifiers & HotkeyModifiers.Alt) != 0)
                            {
                                KeyboardInput.TapMaskKey();
                            }

                            PostMessage(window.Handle, WM_HOOK_FIRED, 0, 0);
                            return 1;
                        case InterceptDecision.Swallow:
                            return 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into user32's hook dispatcher.
            AppLog.Error("Keyboard hook callback failed.", ex);
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }
}
