using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Clipboard;

/// <summary>
/// Capture accounting since <see cref="ClipboardMonitor.Start"/> — the evidence behind "did we miss a copy?".
/// </summary>
/// <remarks>
/// Once no notification is pending, every notification is accounted exactly once:
/// <c>Notifications == Read + SelfWrites + LockedOut − Recovered + Superseded</c>.
/// </remarks>
/// <param name="Notifications">Change notifications received (one per producer clipboard session).</param>
/// <param name="Read">Clipboard states read (captured, or skipped as private/empty), watchdog recoveries included.</param>
/// <param name="Captured">States raised as <see cref="ClipboardMonitor.Captured"/>.</param>
/// <param name="Superseded">
/// Changes replaced by a newer change before they could be read — the only way a copy can be lost. A
/// producer that updates one copy in stages (formats added in a second session) also lands here without
/// losing anything, so this is an upper bound on losses, not a count of them.
/// </param>
/// <param name="SelfWrites">Notifications caused by our own <see cref="ClipboardMonitor.WriteAsync"/> (ignored on purpose).</param>
/// <param name="LockedOut">Changes given up because another application kept the clipboard open for about half a second.</param>
/// <param name="Recovered">Changes found by the watchdog without any notification (the listener had silently stopped); each one re-registers the listener.</param>
public readonly record struct ClipboardMonitorStatistics(
    long Notifications, long Read, long Captured, long Superseded, long SelfWrites, long LockedOut, long Recovered);

/// <summary>
/// Watches the system clipboard (<c>AddClipboardFormatListener</c>) and turns every change into a
/// <see cref="ClipCapture"/>; also writes history items back to the clipboard for pasting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mechanism — the same one Win+V uses.</b> Windows' clipboard-history service (cbdhsvc), WinRT's
/// <c>Clipboard.ContentChanged</c> and Chromium's web <c>clipboardchange</c> event all sit on this
/// listener: an event per change, no polling. There is no user-mode API that hands out a snapshot of
/// <i>every</i> clipboard state — every consumer reads the clipboard <i>after</i> being told it changed.
/// The legacy viewer chain (<c>SetClipboardViewer</c>/<c>WM_DRAWCLIPBOARD</c>) is often believed to be
/// synchronous with the producer; measured on Windows 11 build 26200 it is not (the producer's
/// <c>CloseClipboard</c> returned in 0.01 ms while the viewer slept 150 ms), and in a 40-copy zero-delay
/// burst both mechanisms received 40 notifications yet read only 2 distinct states. The chain is also
/// fragile (a crashed member cuts off everyone after it), so it is not used. Details: CLAUDE.md.
/// </para>
/// <para>
/// <b>Minimizing the miss window.</b> What can be done is shrinking the time between a change and the read:
/// the change is read <b>immediately</b> in the notification handler (no debounce — the earlier 60 ms
/// debounce silently merged any two copies made within 60 ms), on a thread running at
/// <see cref="ThreadPriority.AboveNormal"/> so it wakes promptly under load, and every notification is
/// accounted for in <see cref="Statistics"/> so a miss is visible instead of silent. Producers that update
/// one copy in stages are harmless: each stage carries the same text, hashes to the same entry, and the
/// history's "latest copy wins" merge keeps the final, complete set of formats.
/// </para>
/// <para>
/// <b>Retry.</b> If another consumer holds the clipboard open when we try to read, capture is retried on a
/// 10 ms timer up to 50 times (~0.5 s) — timers, not sleeps, keep the message loop responsive, and a newer
/// notification cancels the retry and reads the newest state at once.
/// </para>
/// <para>
/// <b>Watchdog.</b> Every 2 s the monitor compares <c>GetClipboardSequenceNumber</c> (no clipboard access,
/// essentially free) with the last state it handled. A change that stays unhandled for a whole period with
/// no notification means the listener stopped delivering; the watchdog captures it, re-registers the
/// listener and logs a warning. <c>WM_TIMER</c> is only generated when no posted message is waiting, so a
/// queued notification always wins the race.
/// </para>
/// <para>
/// <b>Echo suppression.</b> After <see cref="WriteAsync"/> the clipboard sequence number is remembered; the
/// update notification caused by our own write carries that number and is ignored, so pasting from
/// history does not re-capture the item with BetterClipboard as its source app.
/// </para>
/// <para>
/// <b>Threading.</b> Runs on its own STA message-window thread. <see cref="Captured"/> is raised on that
/// thread — subscribers must only enqueue (see <c>ClipHistoryService.TryEnqueueCapture</c>) and return,
/// because a slow handler widens the miss window for the next copy.
/// </para>
/// </remarks>
public sealed class ClipboardMonitor : IDisposable, IClipboardWriter
{
    private const nuint RetryTimerId = 1;
    private const nuint WatchdogTimerId = 2;
    private const uint RetryMilliseconds = 10;
    private const int MaxOpenAttempts = 50;
    private const uint DefaultWatchdogMilliseconds = 2000;

    private readonly Func<CaptureOptions> optionsProvider;
    private readonly SourceAppResolver resolver;
    private readonly TimeProvider time;
    private MessageWindowThread? window;
    private uint lastHandledSequence;
    private uint selfWriteSequence;
    private int openAttempts;

    /// <summary>Notifications received whose state has not been settled yet (read, skipped or given up).</summary>
    private int pendingNotifications;

    /// <summary>An unhandled sequence number the watchdog saw on its previous tick (0 = none).</summary>
    private uint watchdogSuspect;

    // Statistics: written on the monitor thread only, read from any thread through Interlocked.
    private long notificationCount;
    private long readCount;
    private long capturedCount;
    private long supersededCount;
    private long selfWriteCount;
    private long lockedOutCount;
    private long recoveredCount;

    /// <summary>
    /// Producer attribution snapshotted when the update notification arrived. Resolving later (after a
    /// retry) is wrong for short-lived producers: a script that copies and exits takes its owner window
    /// with it, and the foreground fallback would then name whatever is in front by then (often us).
    /// </summary>
    private SourceAppInfo? pendingSource;

    /// <summary>
    /// Creates a monitor. Nothing happens until <see cref="Start"/>.
    /// </summary>
    /// <param name="optionsProvider">Returns the current capture options; called on the monitor thread per capture.</param>
    /// <param name="resolver">Resolves the producing application.</param>
    /// <param name="time">Clock for capture timestamps; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public ClipboardMonitor(Func<CaptureOptions> optionsProvider, SourceAppResolver resolver, TimeProvider? time = null)
    {
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on the monitor thread for every recordable clipboard change.</summary>
    public event EventHandler<ClipCapture>? Captured;

    /// <summary>A consistent-enough snapshot of the capture accounting (each counter is read atomically).</summary>
    public ClipboardMonitorStatistics Statistics => new(
        Interlocked.Read(ref notificationCount),
        Interlocked.Read(ref readCount),
        Interlocked.Read(ref capturedCount),
        Interlocked.Read(ref supersededCount),
        Interlocked.Read(ref selfWriteCount),
        Interlocked.Read(ref lockedOutCount),
        Interlocked.Read(ref recoveredCount));

    /// <summary>
    /// Tests only: a desktop in a private window station for the monitor thread, so tests exercise the real
    /// Win32 clipboard without touching the user's clipboard or Win+V history. Forces an MTA thread (see
    /// <see cref="MessageWindowThread"/>); production leaves it 0.
    /// </summary>
    internal nint IsolatedDesktop { get; init; }

    /// <summary>Tests only: watchdog period (production: 2 s).</summary>
    internal uint WatchdogMilliseconds { get; init; } = DefaultWatchdogMilliseconds;

    /// <summary>
    /// Creates the listener window and subscribes to clipboard updates. The content present at start is
    /// <b>not</b> captured (it was copied before we were watching; the Windows-history import covers it).
    /// </summary>
    /// <exception cref="Win32Exception">The listener window or subscription could not be created.</exception>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        if (window is not null)
        {
            throw new InvalidOperationException("The clipboard monitor is already running.");
        }

        var host = IsolatedDesktop == 0
            ? new MessageWindowThread("Clipboard", messageOnly: true, sta: true, priority: ThreadPriority.AboveNormal)
            : new MessageWindowThread("Clipboard", messageOnly: true, sta: false, priority: ThreadPriority.AboveNormal, desktop: IsolatedDesktop);
        window = host;
        host.MessageHandler = OnMessage;
        host.InvokeAsync(() =>
        {
            if (!AddClipboardFormatListener(host.Handle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "AddClipboardFormatListener failed.");
            }

            lastHandledSequence = GetClipboardSequenceNumber();
            SetTimer(host.Handle, WatchdogTimerId, WatchdogMilliseconds, 0);
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Replaces the clipboard content with <paramref name="formats"/> (used to paste a history item).
    /// </summary>
    /// <remarks>
    /// Runs on the monitor thread and may block it for up to ~0.3 s while another app holds the clipboard.
    /// Formats are placed in list order (consumers treat earlier formats as more descriptive).
    /// </remarks>
    /// <param name="formats">Formats to place; privacy markers and GDI handle formats are skipped.</param>
    /// <returns>A task completing once the clipboard is closed again.</returns>
    /// <exception cref="InvalidOperationException">The monitor is not running.</exception>
    /// <exception cref="IOException">The clipboard stayed locked by another application.</exception>
    /// <exception cref="Win32Exception">Emptying the clipboard failed.</exception>
    /// <exception cref="OutOfMemoryException">A global memory block could not be allocated.</exception>
    public Task WriteAsync(IReadOnlyList<ClipFormatData> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var host = window ?? throw new InvalidOperationException("The clipboard monitor is not running.");
        return host.InvokeAsync(() => WriteCore(host.Handle, formats));
    }

    /// <summary>Unsubscribes and destroys the listener window.</summary>
    public void Dispose()
    {
        var host = Interlocked.Exchange(ref window, null);
        if (host is null)
        {
            return;
        }

        host.Post(() =>
        {
            KillTimer(host.Handle, WatchdogTimerId);
            KillTimer(host.Handle, RetryTimerId);
            RemoveClipboardFormatListener(host.Handle);
        });
        host.Dispose();
    }

    /// <summary>
    /// Tests only: silently unsubscribes the listener (as if Windows had stopped delivering), so the
    /// watchdog's recovery can be verified.
    /// </summary>
    /// <returns>A task completing once unsubscribed.</returns>
    /// <exception cref="InvalidOperationException">The monitor is not running.</exception>
    internal Task SimulateListenerLossAsync()
    {
        var host = window ?? throw new InvalidOperationException("The clipboard monitor is not running.");
        return host.InvokeAsync(() => RemoveClipboardFormatListener(host.Handle));
    }

    /// <summary>Monitor-thread message handler.</summary>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">Parameter.</param>
    /// <param name="lParam">Parameter.</param>
    /// <returns>0 when handled, otherwise <see langword="null"/>.</returns>
    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        var host = window;
        if (host is null)
        {
            return null;
        }

        if (msg == WM_CLIPBOARDUPDATE)
        {
            Interlocked.Increment(ref notificationCount);
            pendingNotifications++;

            // Attribute now, while the producer's owner window (and process) still exist; the latest
            // notification wins. GetClipboardOwner does not need the clipboard open.
            nint owner = GetClipboardOwner();
            pendingSource = resolver.Resolve(owner != 0 ? owner : GetForegroundWindow());

            // A newer change makes a pending retry moot: read the newest state right now.
            KillTimer(host.Handle, RetryTimerId);
            openAttempts = 0;
            CaptureNow(host.Handle);
            return 0;
        }

        if (msg == WM_TIMER && (nuint)wParam == RetryTimerId)
        {
            KillTimer(host.Handle, RetryTimerId);
            CaptureNow(host.Handle);
            return 0;
        }

        if (msg == WM_TIMER && (nuint)wParam == WatchdogTimerId)
        {
            CheckWatchdog(host.Handle);
            return 0;
        }

        return null;
    }

    /// <summary>Reads the clipboard if it changed since the last capture and raises <see cref="Captured"/>.</summary>
    /// <param name="hwnd">Listener window (clipboard opener).</param>
    private void CaptureNow(nint hwnd)
    {
        // Cheap pre-check without opening the clipboard.
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == lastHandledSequence)
        {
            // Every pending notification announced a state an earlier read had already overtaken (that
            // read saw the newer content): nothing new to read, but those intermediate states are gone.
            Interlocked.Add(ref supersededCount, pendingNotifications);
            pendingNotifications = 0;
            pendingSource = null;
            return;
        }

        if (sequence == selfWriteSequence)
        {
            lastHandledSequence = sequence;
            pendingSource = null;
            Settle(ref selfWriteCount);
            return;
        }

        // Prefer the attribution taken at notification time; resolving here (before opening, so the
        // clipboard is never held while we query processes) is only the fallback.
        var source = pendingSource;
        if (source is null)
        {
            nint owner = GetClipboardOwner();
            source = resolver.Resolve(owner != 0 ? owner : GetForegroundWindow());
        }

        if (!OpenClipboard(hwnd))
        {
            if (++openAttempts < MaxOpenAttempts)
            {
                SetTimer(hwnd, RetryTimerId, RetryMilliseconds, 0);
            }
            else
            {
                lastHandledSequence = sequence;
                openAttempts = 0;
                pendingSource = null;
                Settle(ref lockedOutCount);
                AppLog.Warn($"Clipboard stayed locked (held by window 0x{GetOpenClipboardWindow():X}); change #{sequence} not captured.");
            }

            return;
        }

        ClipboardReadResult result;
        try
        {
            // Authoritative number: nobody can change the clipboard while we hold it open (delayed renders
            // do not bump it), so this is exactly the state being read even if another change slipped in
            // after the pre-check.
            sequence = GetClipboardSequenceNumber();
            result = ClipboardReader.Read(optionsProvider());
        }
        finally
        {
            CloseClipboard();
        }

        lastHandledSequence = sequence;
        openAttempts = 0;
        pendingSource = null;
        Settle(ref readCount);
        if (result.ExcludedByProducer)
        {
            AppLog.Info($"Skipped a copy marked private by '{source?.ProcessName ?? "unknown"}'.");
            return;
        }

        if (result.Formats.Count == 0)
        {
            return;
        }

        Interlocked.Increment(ref capturedCount);
        Captured?.Invoke(this, new ClipCapture
        {
            Formats = result.Formats,
            CapturedAtUtc = time.GetUtcNow(),
            Source = source,
            Origin = ClipOrigin.Captured,
        });
    }

    /// <summary>
    /// Closes the books on the notifications received so far: the newest is accounted under
    /// <paramref name="outcome"/>, every older one was overwritten before it could be read.
    /// </summary>
    /// <param name="outcome">Counter for what happened to the state just handled.</param>
    private void Settle(ref long outcome)
    {
        Interlocked.Increment(ref outcome);
        if (pendingNotifications > 1)
        {
            Interlocked.Add(ref supersededCount, pendingNotifications - 1);
        }

        pendingNotifications = 0;
    }

    /// <summary>Watchdog tick: recovers a change the listener never announced (monitor thread).</summary>
    /// <param name="hwnd">Listener window.</param>
    private void CheckWatchdog(nint hwnd)
    {
        // A retry is in flight, so the listener demonstrably works; the retry settles that change.
        if (pendingNotifications > 0)
        {
            watchdogSuspect = 0;
            return;
        }

        uint sequence = GetClipboardSequenceNumber();
        if (sequence == lastHandledSequence || sequence == selfWriteSequence)
        {
            watchdogSuspect = 0;
            return;
        }

        if (sequence != watchdogSuspect)
        {
            // First sighting: a producer may still hold the clipboard open (EmptyClipboard bumps the
            // number before CloseClipboard notifies), so give the listener one full period.
            watchdogSuspect = sequence;
            return;
        }

        watchdogSuspect = 0;
        Interlocked.Increment(ref recoveredCount);
        AppLog.Warn($"Clipboard change #{sequence} arrived without a notification; recovering it and re-registering the listener.");
        RemoveClipboardFormatListener(hwnd);
        if (!AddClipboardFormatListener(hwnd))
        {
            AppLog.Warn($"Re-registering the clipboard listener failed (error {Marshal.GetLastPInvokeError()}).");
        }

        nint owner = GetClipboardOwner();
        pendingSource = resolver.Resolve(owner != 0 ? owner : GetForegroundWindow());
        openAttempts = 0;
        CaptureNow(hwnd);
    }

    /// <summary>Places formats on the clipboard (monitor thread).</summary>
    /// <param name="hwnd">Owner window for <c>EmptyClipboard</c>.</param>
    /// <param name="formats">Formats in placement order.</param>
    private void WriteCore(nint hwnd, IReadOnlyList<ClipFormatData> formats)
    {
        bool opened = false;
        for (int attempt = 0; attempt < 20 && !(opened = OpenClipboard(hwnd)); attempt++)
        {
            Thread.Sleep(15);
        }

        if (!opened)
        {
            throw new IOException($"The clipboard is locked by another application (window 0x{GetOpenClipboardWindow():X}).");
        }

        try
        {
            if (!EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "EmptyClipboard failed.");
            }

            foreach (var format in formats)
            {
                if (ClipFormatNames.IsPrivacyMarker(format.Name))
                {
                    continue;
                }

                uint id = ClipboardFormatRegistry.GetOrRegisterId(format.Name);
                if (id == 0 || ClipboardFormatRegistry.IsHandleFormat(id))
                {
                    continue;
                }

                nint memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(1, format.Data.Length));
                if (memory == 0)
                {
                    throw new OutOfMemoryException($"GlobalAlloc failed for {format.Data.Length:N0} bytes ({format.Name}).");
                }

                nint pointer = GlobalLock(memory);
                if (pointer == 0)
                {
                    GlobalFree(memory);
                    throw new OutOfMemoryException($"GlobalLock failed ({format.Name}).");
                }

                try
                {
                    Marshal.Copy(format.Data, 0, pointer, format.Data.Length);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                // Ownership passes to the system only on success; on failure the block is still ours to free.
                if (SetClipboardData(id, memory) == 0)
                {
                    int error = Marshal.GetLastPInvokeError(); // read before GlobalFree overwrites it
                    GlobalFree(memory);
                    AppLog.Warn($"SetClipboardData failed for '{format.Name}' (error {error}).");
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

        // After CloseClipboard, not before: closing bumps the number again (Windows adds its synthesized
        // formats), and the notification for this write carries the final value. Our own notification is
        // posted to this thread, so it cannot be handled before this assignment.
        selfWriteSequence = GetClipboardSequenceNumber();
    }
}
