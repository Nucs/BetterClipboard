using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Clipboard;

/// <summary>
/// Watches the system clipboard (<c>AddClipboardFormatListener</c>) and turns every change into a
/// <see cref="ClipCapture"/>; also writes history items back to the clipboard for pasting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Runs on its own STA message-window thread. <see cref="Captured"/> is raised on that
/// thread — subscribers must only enqueue (see <c>ClipHistoryService.TryEnqueueCapture</c>) and return,
/// because other clipboard consumers queue behind a slow listener.
/// </para>
/// <para>
/// <b>Debounce and retry.</b> Producers often update the clipboard several times in a burst (Office adds
/// formats in stages), so captures run 60 ms after the last <c>WM_CLIPBOARDUPDATE</c>. If the clipboard is
/// still held open by someone, capture is retried on a 40 ms timer up to 12 times (~0.5 s) — using timers,
/// not sleeps, keeps the message loop responsive.
/// </para>
/// <para>
/// <b>Echo suppression.</b> After <see cref="WriteAsync"/> the clipboard sequence number is remembered; the
/// update notification caused by our own write carries that number and is ignored, so pasting from
/// history does not re-capture the item with BetterClipboard as its source app.
/// </para>
/// </remarks>
public sealed class ClipboardMonitor : IDisposable
{
    private const nuint CaptureTimerId = 1;
    private const uint DebounceMilliseconds = 60;
    private const uint RetryMilliseconds = 40;
    private const int MaxOpenAttempts = 12;

    private readonly Func<CaptureOptions> optionsProvider;
    private readonly SourceAppResolver resolver;
    private readonly TimeProvider time;
    private MessageWindowThread? window;
    private uint lastHandledSequence;
    private uint selfWriteSequence;
    private int openAttempts;

    /// <summary>
    /// Producer attribution snapshotted when the update notification arrived. Resolving later (after the
    /// debounce) is wrong for short-lived producers: a script that copies and exits takes its owner window
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

        window = new MessageWindowThread("Clipboard", messageOnly: true, sta: true);
        window.MessageHandler = OnMessage;
        window.InvokeAsync(() =>
        {
            if (!AddClipboardFormatListener(window.Handle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "AddClipboardFormatListener failed.");
            }

            lastHandledSequence = GetClipboardSequenceNumber();
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

        host.Post(() => RemoveClipboardFormatListener(host.Handle));
        host.Dispose();
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
            // Attribute now, while the producer's owner window (and process) still exist; the latest
            // notification of a burst wins. GetClipboardOwner does not need the clipboard open.
            nint owner = GetClipboardOwner();
            pendingSource = resolver.Resolve(owner != 0 ? owner : GetForegroundWindow());

            // Re-arming the same timer id restarts the debounce window.
            openAttempts = 0;
            SetTimer(host.Handle, CaptureTimerId, DebounceMilliseconds, 0);
            return 0;
        }

        if (msg == WM_TIMER && (nuint)wParam == CaptureTimerId)
        {
            KillTimer(host.Handle, CaptureTimerId);
            CaptureNow(host.Handle);
            return 0;
        }

        return null;
    }

    /// <summary>Reads the clipboard if it changed since the last capture and raises <see cref="Captured"/>.</summary>
    /// <param name="hwnd">Listener window (clipboard opener).</param>
    private void CaptureNow(nint hwnd)
    {
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == lastHandledSequence)
        {
            return;
        }

        if (sequence == selfWriteSequence)
        {
            lastHandledSequence = sequence;
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
                SetTimer(hwnd, CaptureTimerId, RetryMilliseconds, 0);
            }
            else
            {
                lastHandledSequence = sequence;
                AppLog.Warn($"Clipboard stayed locked (held by window 0x{GetOpenClipboardWindow():X}); change #{sequence} not captured.");
            }

            return;
        }

        ClipboardReadResult result;
        try
        {
            result = ClipboardReader.Read(optionsProvider());
        }
        finally
        {
            CloseClipboard();
        }

        lastHandledSequence = sequence;
        pendingSource = null;
        if (result.ExcludedByProducer)
        {
            AppLog.Info($"Skipped a copy marked private by '{source?.ProcessName ?? "unknown"}'.");
            return;
        }

        if (result.Formats.Count == 0)
        {
            return;
        }

        Captured?.Invoke(this, new ClipCapture
        {
            Formats = result.Formats,
            CapturedAtUtc = time.GetUtcNow(),
            Source = source,
            Origin = ClipOrigin.Captured,
        });
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

        selfWriteSequence = GetClipboardSequenceNumber();
    }
}
