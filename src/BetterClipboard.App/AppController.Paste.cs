using System.Diagnostics;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Model;
using BetterClipboard.Windows.Input;

namespace BetterClipboard.App;

/// <summary>
/// Pasting from the panel (CLAUDE.md §2.52): the item onto the clipboard and Ctrl+V into the window the user came from, with
/// nothing else between the user's Enter and the paste.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is ordered this way.</b> Reported 2026-11-15: "I Win+V, go up 1/2 cards to the pinned ones and press enter. there
/// is a delay of 400ms-1000ms." Measured with <c>tools/probes/probe_paste.cs</c> in a Windows 11 test desktop: the old order —
/// read the item, write the clipboard, put the whole panel away, then activate the target and wait 40 ms — sent Ctrl+V 86 to
/// 206 ms after Enter in a warm process, and 141 to 1,420 ms after it once the app's memory had been trimmed and its pages had
/// left RAM (what the owner's PC, 138 GB committed on 64 GB, does to it between uses). The log showed where: "panel away 84 to
/// 1,258 ms" — hiding and showing the window, resetting its view for the next summon, a layout pass of the list, all on the UI
/// thread, all page-faulting back in, all before the Ctrl+V.
/// </para>
/// <para>
/// <b>The order now</b> (<see cref="DeliverAsync"/>): the item's formats were read while the user looked at the card
/// (<see cref="PrefetchFormats"/>); the panel is cloaked (DWM drops it at its next frame) and nothing else; off the UI thread,
/// the clipboard is written, the target activated, and Ctrl+V sent once the target had its settle time; the UI thread stays
/// idle until the target has taken the paste; and only then come the rest of putting the panel away
/// (<see cref="Views.ClipboardFlyout.FinishPuttingAway"/>) and the item's bump to the top.
/// </para>
/// </remarks>
public sealed partial class AppController
{
    /// <summary>
    /// The largest item whose formats are read ahead (<see cref="PrefetchFormats"/>): text and small pictures, which are
    /// what people paste in a hurry. A screenshot's PNG and bitmap (tens of MB) would stay in memory for as long as its card
    /// is selected; they are read at the paste instead, as before.
    /// </summary>
    private const long PrefetchLimitBytes = 4L * 1024 * 1024;

    /// <summary>
    /// How long after the Ctrl+V the UI thread stays free before the panel's put-away runs: the target takes its activation
    /// and its paste meanwhile, and its activation can wait on this process's windows (Windows tells the window it
    /// deactivated, and waits for it) — a UI thread busy laying out the hidden list would hold the paste back.
    /// </summary>
    private static readonly TimeSpan FinishAfterPaste = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The formats of the card selected in the panel, read ahead (<see cref="PrefetchFormats"/>): the entry, its content hash
    /// at the time (an entry updated in place since, a Notepad++ tab for one, must be read again), and the read itself.
    /// <see langword="null"/> when nothing is read ahead. UI thread only.
    /// </summary>
    private (long Id, string ContentHash, Task<IReadOnlyList<ClipFormatData>> Formats)? prefetched;

    /// <summary>Whether a paste was logged this session (the first one always is, see <see cref="LogPaste"/>).</summary>
    private bool pasteLogged;

    /// <summary>
    /// Starts reading the formats of the card the panel just selected, so a paste of it finds them in memory: the read (a
    /// query of the encrypted store, tens to hundreds of milliseconds once the app's memory was trimmed) happens while the
    /// user looks at the card, not after the Enter. One card at a time: selecting another replaces it.
    /// </summary>
    /// <remarks>
    /// Skipped for items over <see cref="PrefetchLimitBytes"/>, and while the store is not open. A read that fails is
    /// forgotten quietly: the paste then reads again and reports what goes wrong.
    /// </remarks>
    /// <param name="entry">The selected card's entry.</param>
    internal void PrefetchFormats(ClipEntry entry)
    {
        if (history is null || entry.SizeBytes > PrefetchLimitBytes)
        {
            return;
        }

        if (prefetched is { } slot && slot.Id == entry.Id && slot.ContentHash == entry.ContentHash)
        {
            return;
        }

        var read = history.GetFormatsAsync(entry.Id);

        // Observed, so a read that fails while nobody pastes is not reported as an unobserved task exception.
        _ = read.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        prefetched = (entry.Id, entry.ContentHash, read);
    }

    /// <summary>Lets go of the formats read ahead (the panel was put away: whatever it read is not needed until the next use).</summary>
    internal void ForgetPrefetchedFormats() => prefetched = null;

    /// <summary>
    /// Replays an entry onto the clipboard and (per settings) pastes it into the window the user came from.
    /// </summary>
    /// <param name="entry">The history entry.</param>
    /// <param name="plainText">Strip formatting ("paste as plain text").</param>
    /// <param name="paste">Inject Ctrl+V into the target (otherwise only copy).</param>
    /// <param name="timeline">
    /// The paste's timeline, started at the key that asked for it; <see langword="null"/> starts one now (a click, a menu).
    /// </param>
    /// <returns>A task completing when done; failures are logged, never thrown (fire-and-forget safe).</returns>
    public async Task PasteAsync(ClipEntry entry, bool plainText, bool paste = true, PasteTimeline? timeline = null)
    {
        timeline ??= PasteTimeline.StartNow();
        try
        {
            var formats = ReplayFormats.Prepare(await TakeFormatsAsync(entry), plainText);
            timeline.Mark("formats");
            if (formats.Count == 0)
            {
                AppLog.Warn($"Entry {entry.Id} has nothing to paste{(plainText ? " as plain text" : string.Empty)}.");
                flyout?.Dismiss(restoreFocus: true);
                return;
            }

            await DeliverAsync(formats, paste, timeline);

            // After the paste: the bump reaches the history worker, whose change event then refreshes the hidden list.
            if (Settings.Current.MoveToTopOnPaste)
            {
                _ = History.MarkUsedAsync(entry.Id);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Pasting entry {entry.Id} failed.", ex);
            flyout?.Dismiss(restoreFocus: true);
        }
    }

    /// <summary>
    /// The entry's stored formats: the ones read ahead for it when they are still its content (<see cref="PrefetchFormats"/>),
    /// otherwise read now.
    /// </summary>
    /// <param name="entry">The entry to paste.</param>
    /// <returns>Its formats, in their stored order.</returns>
    /// <exception cref="Exception">The store could not be read (what <c>ClipHistoryService.GetFormatsAsync</c> throws).</exception>
    private async Task<IReadOnlyList<ClipFormatData>> TakeFormatsAsync(ClipEntry entry)
    {
        if (prefetched is { } slot && slot.Id == entry.Id && slot.ContentHash == entry.ContentHash)
        {
            try
            {
                return await slot.Formats;
            }
            catch (Exception ex)
            {
                // Read again below: a failure that persists is reported by that read, with the paste's own context.
                AppLog.Warn($"Reading entry {entry.Id} ahead failed ({ex.GetType().Name}); reading it again for the paste.");
            }
        }

        return await History.GetFormatsAsync(entry.Id);
    }

    /// <summary>
    /// Puts <paramref name="formats"/> on the clipboard and, when asked and allowed, pastes them into the window the user
    /// came from — every step that is not needed for the Ctrl+V after it (see the class remarks).
    /// </summary>
    /// <remarks>
    /// <para>
    /// UI thread: the panel is cloaked and marked away (<see cref="Views.ClipboardFlyout.PutAwayForPaste"/>), so its own
    /// deactivation does nothing more. Then off the UI thread, which handles that deactivation (WinUI's own work, page faults
    /// included) in parallel: the clipboard is written first, while nothing else competes with it; then the target is
    /// activated (this process still owns the foreground, so any of its threads may hand it on) and gets
    /// <see cref="PasteInjector.SettleTime"/> before the Ctrl+V. The UI thread then stays free for
    /// <see cref="FinishAfterPaste"/> before the panel's put-away finishes (<see cref="Views.ClipboardFlyout.FinishPuttingAway"/>)
    /// — at once when nothing was pasted.
    /// </para>
    /// <para>
    /// When a step fails, the target still gets the foreground back (from here, if the delivery failed before activating it),
    /// and the exception reaches the caller, which logs it.
    /// </para>
    /// </remarks>
    /// <param name="formats">The formats to put on the clipboard (already prepared for a paste).</param>
    /// <param name="paste">Inject Ctrl+V (otherwise only copy, and give the target its focus back).</param>
    /// <param name="timeline">The paste's timeline.</param>
    /// <returns>A task completing once the paste was sent (or refused) and the panel put away.</returns>
    /// <exception cref="IOException">The clipboard stayed locked by another application.</exception>
    /// <exception cref="InvalidOperationException">The clipboard monitor is not running.</exception>
    /// <exception cref="OutOfMemoryException">A global memory block for the clipboard could not be allocated.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Emptying the clipboard failed.</exception>
    private async Task DeliverAsync(IReadOnlyList<ClipFormatData> formats, bool paste, PasteTimeline timeline)
    {
        nint target = pasteTarget?.TargetWindow ?? 0;
        bool inject = paste && Settings.Current.PasteOnSelect && target != 0;
        var panel = flyout;
        var writer = monitor ?? throw new InvalidOperationException("The clipboard monitor is not running.");
        panel?.PutAwayForPaste();
        timeline.Mark("panel away");
        bool activated = false;
        PasteOutcome? outcome = null;
        try
        {
            outcome = await Task.Run(async () =>
            {
                await writer.WriteAsync(formats).ConfigureAwait(false);
                timeline.Mark("clipboard");
                long activatedAt = 0;
                if (target != 0)
                {
                    ForegroundHelper.Activate(target);
                    activatedAt = Stopwatch.GetTimestamp();
                    activated = true;
                    timeline.Mark("target");
                }

                return inject
                    ? await PasteInjector.PasteIntoWithOutcomeAsync(target, activatedAt, timeline).ConfigureAwait(false)
                    : (PasteOutcome?)null;
            });
        }
        finally
        {
            // A delivery that failed before the hand-over still owes the user their window (the panel is already gone).
            if (!activated && target != 0)
            {
                ForegroundHelper.Activate(target);
            }

            // The target takes its activation and the Ctrl+V while this thread stays free (FinishAfterPaste), then the rest
            // of putting the panel away: the view reset for the next summon, the remembered tab saved, memory given back later.
            if (outcome == PasteOutcome.Pasted)
            {
                await Task.Delay(FinishAfterPaste);
            }

            panel?.FinishPuttingAway();
        }

        if (outcome == PasteOutcome.TargetElevated && elevatedPasteNotified.Add(target))
        {
            tray?.ShowNotification(
                UiText.Translate("Copied: press Ctrl+V to paste"),
                UiText.Translate("That window runs as administrator, and Windows does not let BetterClipboard type into it. The item is on the clipboard."));
        }

        if (outcome == PasteOutcome.Pasted)
        {
            LogPaste(timeline);
        }
    }

    /// <summary>
    /// Logs how long a paste took from the key to the Ctrl+V, stage by stage — the session's first one always, later ones
    /// only when slow (<see cref="PasteTimeline.SlowPaste"/>) — so a "pasting takes a moment" report can be checked against
    /// the log. Content-free: stage names and times.
    /// </summary>
    /// <param name="timeline">The finished paste's timeline.</param>
    private void LogPaste(PasteTimeline timeline)
    {
        var total = timeline.Total;
        if (!PasteTimeline.ShouldLog(total, firstOfSession: !pasteLogged))
        {
            return;
        }

        // Part of a log line, so English whatever the interface language.
        AppLog.Info($"Pasted: Ctrl+V sent {PasteTimeline.Milliseconds(total)} ms after the key ({timeline.DescribeStages()}){(pasteLogged ? " (slow)" : " (first paste)")}.");
        pasteLogged = true;
    }
}
