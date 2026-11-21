using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>A key event <see cref="PasteKeyGuard"/> held back, as its low-level keyboard hook saw it.</summary>
/// <param name="VirtualKey">The virtual key (<c>VK_PACKET</c> for a character another program typed as Unicode).</param>
/// <param name="ScanCode">The scan code; for <c>VK_PACKET</c>, the character's UTF-16 code unit.</param>
/// <param name="Flags">The hook's <c>LLKHF_*</c> flags: key-up, extended key, injected.</param>
public readonly record struct HeldKey(uint VirtualKey, uint ScanCode, uint Flags)
{
    /// <summary>Whether the event was a key-up.</summary>
    public bool IsUp => (Flags & LLKHF_UP) != 0;

    /// <summary>Whether the key is an extended one (right Ctrl, the arrows, the numpad's Enter, …).</summary>
    public bool IsExtended => (Flags & LLKHF_EXTENDED) != 0;
}

/// <summary>What one <see cref="PasteKeyGuard.ReleaseAsync"/> did with the keys typed while it was engaged.</summary>
/// <param name="Held">How many key events the guard held back.</param>
/// <param name="Typed">How many of them it typed again; fewer when Windows refused them (UIPI: a window running as
/// administrator came to the front meanwhile).</param>
public readonly record struct HeldKeysRelease(int Held, int Typed);

/// <summary>
/// Holds back the keys a person types while a paste keeps Ctrl down, and types them again once Ctrl is up, in their order:
/// a key that reached the window meanwhile would arrive as Ctrl+key — an Enter typed to submit the pasted text as Ctrl+Enter
/// (CLAUDE.md §2.54).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Since 2026-11-17 a paste keeps Ctrl down until the window took the V (<see cref="InputThreadWatch"/>), and every
/// key event that goes through the system meanwhile is a Ctrl+key. Measured in a test desktop with keyboard hooks as slow as
/// on the owner's PC (100 ms per key event, about 20 programs hook the keyboard there): an x typed 200 ms after the Enter
/// reached the terminal as Ctrl+X 4 times of 4 — and 3 of 4 with the one-call Ctrl+V of before, since Windows let it in
/// between the events of that one <c>SendInput</c> call while the slow hooks ran.
/// </para>
/// <para>
/// <b>How.</b> A <c>WH_KEYBOARD_LL</c> hook on a thread of its own, installed only from just before the paste's keys go in
/// until Ctrl is up again. The newest hook is called first, so it sees each key before other programs' hooks and before any
/// window. BetterClipboard's own keys (<see cref="KeyboardInput.OwnInjectionMarker"/>) and the guard's own replays
/// (<see cref="KeyboardInput.ReplayMarker"/>) pass; every other key event — the person's, or another program's — is kept, and
/// typed again after Ctrl came up (<see cref="KeyboardInput.TypeAgain"/>). Keys typed while those are typed again are held
/// too and follow them, so the order is the person's.
/// </para>
/// <para>
/// <b>Safety.</b> A hook that keeps keys could take the session's keyboard, so: it is engaged around one paste only and the
/// caller releases it on every path; should the caller never come back, it releases itself after
/// <see cref="MaximumEngagement"/>; it keeps at most <see cref="MaximumHeldKeys"/> events and lets the rest pass; and its
/// callback does nothing but remember the event. When Windows drops the hook (a callback later than the hooks' timeout on an
/// overloaded PC), keys reach the window as they did without it. Footgun: the keys typed again arrive as injected keys — a
/// program that ignores injected input sees them so, and a window running as administrator that came to the front meanwhile
/// does not get them (logged).
/// </para>
/// </remarks>
public sealed class PasteKeyGuard : IDisposable
{
    /// <summary>
    /// The longest a guard stays engaged without its release: longer than any paste's hold (the injection's hooks, then at most
    /// <see cref="InputThreadWatch.HoldCap"/>), short enough that a lost release cannot keep the keyboard for long.
    /// </summary>
    public static readonly TimeSpan MaximumEngagement = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The most key events one engagement keeps: far more than anyone types in a few seconds; past it, keys pass, so memory
    /// and the replay stay bounded whatever happens.
    /// </summary>
    public const int MaximumHeldKeys = 512;

    /// <summary>Serializes the creation of <see cref="shared"/>.</summary>
    private static readonly Lock SharedLock = new();

    /// <summary>The process's guard, created at the first paste; <see langword="null"/> until then, or while creating it fails.</summary>
    private static PasteKeyGuard? shared;

    /// <summary>The guard's thread: the hook's callback, engaging and releasing all run on it, so its state needs no lock.</summary>
    private readonly MessageWindowThread thread;

    /// <summary>The hook procedure, created once and kept for the guard's lifetime: a collected delegate would crash the process.</summary>
    private readonly HookProc hookProcedure;

    /// <summary>How long an engagement may last before the guard releases itself.</summary>
    private readonly TimeSpan maximumEngagement;

    /// <summary>The key events held back, oldest first (guard thread only).</summary>
    private readonly List<HeldKey> held = [];

    /// <summary>The installed hook, or 0 (guard thread only).</summary>
    private nint hookHandle;

    /// <summary>How many callers engaged the guard and did not release it yet (guard thread only).</summary>
    private int depth;

    /// <summary>Counts engagements, so a watchdog of an earlier one never releases a later one (guard thread only).</summary>
    private int engagement;

    /// <summary>Releases an engagement its caller never released (guard thread only).</summary>
    private Timer? watchdog;

    /// <summary>Starts the guard's thread; nothing is hooked until <see cref="EngageAsync"/>.</summary>
    /// <exception cref="Win32Exception">The guard's message window could not be created.</exception>
    public PasteKeyGuard()
        : this(MaximumEngagement)
    {
    }

    /// <summary>Starts the guard's thread with another longest engagement (tests).</summary>
    /// <param name="maximumEngagement">How long an engagement may last before the guard releases itself.</param>
    /// <exception cref="Win32Exception">The guard's message window could not be created.</exception>
    internal PasteKeyGuard(TimeSpan maximumEngagement)
    {
        this.maximumEngagement = maximumEngagement;
        hookProcedure = HookCallback;

        // Above every other thread of the app: Windows drops a low-level hook whose callback answers too late.
        thread = new MessageWindowThread("PasteKeyGuard", messageOnly: true, priority: ThreadPriority.Highest);
    }

    /// <summary>Whether the hook is installed right now (it holds keys back).</summary>
    public bool IsEngaged => Volatile.Read(ref hookHandle) != 0;

    /// <summary>
    /// The process's guard, created at the first call; <see langword="null"/> when it cannot be created (the paste then goes
    /// on without one, as before 2026-11-17). A later call tries again.
    /// </summary>
    /// <returns>The guard, or <see langword="null"/>.</returns>
    public static PasteKeyGuard? TryGetShared()
    {
        lock (SharedLock)
        {
            if (shared is null)
            {
                try
                {
                    shared = new PasteKeyGuard();
                }
                catch (Win32Exception ex)
                {
                    AppLog.Warn($"The paste's key guard could not start ({ex.Message}); keys typed while a paste holds Ctrl may arrive as Ctrl+key.");
                }
            }

            return shared;
        }
    }

    /// <summary>
    /// Installs the hook (when no other paste has it installed already): from now on, key events other than BetterClipboard's
    /// own are held back until <see cref="ReleaseAsync"/>. Never throws: a guard that cannot engage only means the keys typed
    /// meanwhile are not held back.
    /// </summary>
    /// <returns><see langword="true"/> when the guard holds keys back now; then the caller must call <see cref="ReleaseAsync"/>.</returns>
    public async Task<bool> EngageAsync()
    {
        try
        {
            return await thread.InvokeAsync(Engage).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException or InvalidOperationException)
        {
            AppLog.Warn($"The paste's key guard could not engage ({ex.Message}); keys typed while the paste holds Ctrl may arrive as Ctrl+key.");
            return false;
        }
    }

    /// <summary>
    /// Ends the caller's engagement: when it was the last one, types the held keys again in their order (with those typed
    /// meanwhile) and removes the hook. Call it after Ctrl is up, on every path that engaged. Never throws.
    /// </summary>
    /// <returns>How many key events were held back and how many were typed again; zeros when nothing was held.</returns>
    public async Task<HeldKeysRelease> ReleaseAsync()
    {
        try
        {
            return await thread.InvokeAsync(() => Release(watchdogFor: null)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            AppLog.Warn($"The paste's key guard could not be released ({ex.Message}).");
            return default;
        }
    }

    /// <summary>Removes the hook at once — keys held then are typed again first — and stops the guard's thread.</summary>
    public void Dispose()
    {
        try
        {
            thread.InvokeAsync(() =>
            {
                depth = 1;
                return Release(watchdogFor: null);
            }).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            AppLog.Warn($"Removing the paste's key guard failed: {ex.InnerException?.Message}");
        }

        thread.Dispose();
    }

    /// <summary>
    /// Types held keys again until none is left: a key typed while a batch goes in is held too (the hook's callback runs on
    /// this thread, inside the injection) and goes in the next round, after the batch.
    /// </summary>
    /// <param name="held">The held keys, oldest first; empty afterwards.</param>
    /// <param name="typeAgain">Injects one batch; returns how many of its events went in.</param>
    /// <returns>How many key events were held, and how many went in.</returns>
    internal static HeldKeysRelease Drain(List<HeldKey> held, Func<IReadOnlyList<HeldKey>, int> typeAgain)
    {
        int heldCount = 0;
        int typed = 0;
        while (held.Count > 0)
        {
            var batch = held.ToArray();
            held.Clear();
            heldCount += batch.Length;
            typed += typeAgain(batch);
        }

        return new HeldKeysRelease(heldCount, typed);
    }

    /// <summary>
    /// Whether the guard holds back an event with these flags and extra info: every key event except BetterClipboard's own
    /// injections and the guard's own replays.
    /// </summary>
    /// <param name="flags">The event's <c>LLKHF_*</c> flags.</param>
    /// <param name="extraInfo">The event's <c>dwExtraInfo</c>.</param>
    /// <returns><see langword="true"/> to hold it back.</returns>
    internal static bool Holds(uint flags, nint extraInfo) =>
        (flags & LLKHF_INJECTED) == 0 || (extraInfo != KeyboardInput.OwnInjectionMarker && extraInfo != KeyboardInput.ReplayMarker);

    /// <summary>Installs the hook for a new engagement, or joins the one in place (guard thread).</summary>
    /// <returns><see langword="true"/>: the guard holds keys back.</returns>
    /// <exception cref="Win32Exception"><c>SetWindowsHookEx</c> failed (no interactive window station, for one).</exception>
    private bool Engage()
    {
        if (depth++ > 0)
        {
            return true;
        }

        held.Clear();
        nint handle = SetWindowsHookEx(WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(hookProcedure), GetModuleHandle(null), 0);
        if (handle == 0)
        {
            depth = 0;
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The paste's key guard hook could not be installed.");
        }

        Volatile.Write(ref hookHandle, handle);
        int id = ++engagement;

        // Should the caller never release it (a paste that died between its finally's lines), the guard lets go by itself.
        watchdog = new Timer(_ => thread.Post(() => Release(watchdogFor: id)), null, maximumEngagement, Timeout.InfiniteTimeSpan);
        return true;
    }

    /// <summary>
    /// Ends one engagement; at the last one types the held keys again and removes the hook (guard thread).
    /// </summary>
    /// <param name="watchdogFor">
    /// The engagement whose watchdog fired, which ends the engagement whatever its depth; <see langword="null"/> for a caller's
    /// release.
    /// </param>
    /// <returns>What happened to the held keys; zeros while another caller still holds the guard, or when nothing was held.</returns>
    private HeldKeysRelease Release(int? watchdogFor)
    {
        if (watchdogFor is { } id)
        {
            if (id != engagement || hookHandle == 0)
            {
                // A watchdog of an engagement that was released already.
                return default;
            }

            AppLog.Warn($"The paste's key guard was not released within {maximumEngagement.TotalMilliseconds:0} ms; it let go by itself.");
            depth = 1;
        }

        if (depth == 0 || --depth > 0)
        {
            return default;
        }

        watchdog?.Dispose();
        watchdog = null;

        // The keys go in while the hook is still installed: keys typed during this are held and follow them. Only an empty
        // list removes it — no callback can run between the check and the removal, since callbacks run on this thread.
        var release = Drain(held, KeyboardInput.TypeAgain);
        nint handle = hookHandle;
        Volatile.Write(ref hookHandle, 0);
        if (handle != 0)
        {
            UnhookWindowsHookEx(handle);
        }

        if (release.Typed < release.Held)
        {
            AppLog.Warn($"Of {release.Held} key events typed while a paste held Ctrl down, {release.Held - release.Typed} could not be typed again (a window running as administrator came to the front?).");
        }

        return release;
    }

    /// <summary>The low-level keyboard hook: remembers a key event it holds back, and does nothing slower.</summary>
    /// <param name="nCode">Hook code.</param>
    /// <param name="wParam">Key message.</param>
    /// <param name="lParam">Pointer to the event's <see cref="KBDLLHOOKSTRUCT"/>.</param>
    /// <returns>1 to hold the event back, otherwise the next hook's result.</returns>
    private unsafe nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            // A callback that arrives after the hook's removal (queued before it) lets the key pass.
            if (nCode >= 0 && hookHandle != 0 && held.Count < MaximumHeldKeys)
            {
                var data = (KBDLLHOOKSTRUCT*)lParam;
                if (Holds(data->flags, (nint)data->dwExtraInfo))
                {
                    held.Add(new HeldKey(data->vkCode, data->scanCode, data->flags));
                    return 1;
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into user32's hook dispatcher.
            AppLog.Error("The paste's key guard hook failed.", ex);
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }
}
