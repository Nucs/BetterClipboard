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
}

/// <summary>
/// Owns the global "open BetterClipboard" shortcut on a dedicated input thread.
/// </summary>
/// <remarks>
/// <para>
/// <c>RegisterHotKey</c> is tried first. It fails with <c>ERROR_HOTKEY_ALREADY_REGISTERED</c> for combos
/// the shell owns — <c>Win+V</c> is registered by explorer.exe for its clipboard history (verified in
/// CLAUDE.md §1.6) — in which case a low-level keyboard hook intercepts exactly that combination.
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
    private const int HotkeyId = 0xB00C;
    private const uint WM_HOOK_FIRED = WM_APP + 0x20;

    private readonly MessageWindowThread window;
    private HookProc? hookProcedure;
    private nint hookHandle;
    private HotkeyInterceptor? interceptor;
    private bool hotkeyRegistered;

    /// <summary>
    /// Starts the input thread. No shortcut is active until <see cref="ApplyAsync"/>.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The input window could not be created.</exception>
    public HotkeyService()
    {
        window = new MessageWindowThread("Input", messageOnly: true);
        window.MessageHandler = OnMessage;
    }

    /// <summary>
    /// Raised on the input thread when the shortcut is pressed, with the context captured at that
    /// instant (the window to paste into). Marshal to the UI thread before touching UI.
    /// </summary>
    public event EventHandler<ForegroundContext>? Pressed;

    /// <summary>The current registration state.</summary>
    public HotkeyRegistration Current { get; private set; } = new(default, HotkeyMode.None, 0);

    /// <summary>
    /// Replaces the active shortcut.
    /// </summary>
    /// <param name="gesture">The new shortcut.</param>
    /// <param name="allowHookFallback">Allow the low-level-hook takeover when the combination is owned by someone else.</param>
    /// <returns>The resulting registration (check <see cref="HotkeyRegistration.IsActive"/>).</returns>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    public Task<HotkeyRegistration> ApplyAsync(HotkeyGesture gesture, bool allowHookFallback) =>
        window.InvokeAsync(() => Apply(gesture, allowHookFallback));

    /// <summary>
    /// Deactivates the shortcut (e.g. while the settings page records a new one).
    /// </summary>
    /// <returns>A task completing when released.</returns>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    public Task DisableAsync() => window.InvokeAsync(ReleaseCurrent);

    /// <summary>Releases the shortcut/hook and stops the input thread.</summary>
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

    /// <summary>Input-thread implementation of <see cref="ApplyAsync"/>.</summary>
    /// <param name="gesture">Shortcut.</param>
    /// <param name="allowHookFallback">Whether the hook fallback is allowed.</param>
    /// <returns>The registration.</returns>
    private HotkeyRegistration Apply(HotkeyGesture gesture, bool allowHookFallback)
    {
        ReleaseCurrent();
        if (RegisterHotKey(window.Handle, HotkeyId, (uint)gesture.Modifiers | MOD_NOREPEAT, gesture.VirtualKey))
        {
            hotkeyRegistered = true;

            // Logged so "which path is Win+V on?" is answerable from the log alone (e.g. after Explorer
            // released it via DisabledHotkeys, this is the line to look for instead of the hook one).
            AppLog.Info($"{gesture} registered with RegisterHotKey (no keyboard hook needed).");
            return Current = new HotkeyRegistration(gesture, HotkeyMode.RegisteredHotKey, 0);
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == ERROR_HOTKEY_ALREADY_REGISTERED && allowHookFallback)
        {
            interceptor = new HotkeyInterceptor(gesture);
            hookProcedure = HookCallback; // field keeps the delegate (and its native thunk) alive
            hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(hookProcedure), GetModuleHandle(null), 0);
            if (hookHandle != 0)
            {
                AppLog.Info($"{gesture} is owned by another component; intercepting it with a keyboard hook.");
                return Current = new HotkeyRegistration(gesture, HotkeyMode.KeyboardHook, error);
            }

            AppLog.Error($"SetWindowsHookEx failed (error {Marshal.GetLastPInvokeError()}).");
            interceptor = null;
            hookProcedure = null;
        }

        AppLog.Warn($"Could not register {gesture} (error {error}).");
        return Current = new HotkeyRegistration(gesture, HotkeyMode.None, error);
    }

    /// <summary>Unregisters the hotkey and removes the hook (input thread).</summary>
    private void ReleaseCurrent()
    {
        if (hotkeyRegistered)
        {
            UnregisterHotKey(window.Handle, HotkeyId);
            hotkeyRegistered = false;
        }

        if (hookHandle != 0)
        {
            UnhookWindowsHookEx(hookHandle);
            hookHandle = 0;
        }

        interceptor = null;
        hookProcedure = null;
        Current = Current with { Mode = HotkeyMode.None };
    }

    /// <summary>Input-thread message handler.</summary>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">Parameter.</param>
    /// <param name="lParam">Parameter.</param>
    /// <returns>0 when handled, otherwise <see langword="null"/>.</returns>
    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if ((msg == WM_HOTKEY && wParam == HotkeyId) || msg == WM_HOOK_FIRED)
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
            if (nCode >= 0 && interceptor is { } active)
            {
                var data = (KBDLLHOOKSTRUCT*)lParam;
                uint message = (uint)wParam;
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = message is WM_KEYUP or WM_SYSKEYUP;
                if ((down || up) && data->vkCode == active.Gesture.VirtualKey)
                {
                    // Only our own synthetic keys are exempt (see KeyboardInput.OwnInjectionMarker).
                    bool ownInjection = (data->flags & LLKHF_INJECTED) != 0 && (nint)data->dwExtraInfo == KeyboardInput.OwnInjectionMarker;
                    switch (active.OnKey(data->vkCode, down, ownInjection, KeyboardInput.GetHeldModifiers()))
                    {
                        case InterceptDecision.SwallowAndFire:
                            // Chord the held Win/Alt with the mask key so releasing it later neither opens
                            // Start nor focuses a menu bar in the app underneath.
                            if (active.Gesture.UsesWinKey || (active.Gesture.Modifiers & HotkeyModifiers.Alt) != 0)
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
