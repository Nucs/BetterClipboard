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
    /// Whether this session has logged that a choice only copied because "Paste after choosing an item" is off
    /// (<see cref="LogWhyNotPasted"/>): said once, since with the setting off every choice copies by design.
    /// </summary>
    private bool pasteOffLogged;

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
    /// <see cref="PasteInjector.SettleTime"/> before the Ctrl+V — longer when its input thread is busy, and Ctrl stays down
    /// until that thread took the V (CLAUDE.md §2.54). The UI thread then stays free for <see cref="FinishAfterPaste"/> before
    /// the panel's put-away finishes (<see cref="Views.ClipboardFlyout.FinishPuttingAway"/>) — at once when nothing was pasted.
    /// </para>
    /// <para>
    /// For a paste, keys the user types from the start until Ctrl is up again are held back and typed after the pasted text
    /// (<see cref="PasteKeyGuard"/>): a fast "Enter, Enter" submits what was pasted, and nothing lands in the panel on its way
    /// out or as Ctrl+key.
    /// </para>
    /// <para>
    /// When a step fails, the target still gets the foreground back (from here, if the delivery failed before activating it),
    /// and the exception reaches the caller, which logs it.
    /// </para>
    /// <para>
    /// A paste that only copies says why in the log (<see cref="LogWhyNotPasted"/>: the setting is off, or no window was
    /// captured), and so does one whose window closed meanwhile (<see cref="PasteOutcome.NoTarget"/>); the injector logs its
    /// own refusals. Content-free.
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
        bool pasteOnSelect = Settings.Current.PasteOnSelect;
        bool inject = paste && pasteOnSelect && target != 0;

        // A choice that was meant to paste and only copies says why (content-free): until 2026-11-18 these ended without a
        // line, and one lost paste in the 0.2.12 QA could not be explained afterwards (CLAUDE.md §6).
        if (paste && !inject)
        {
            LogWhyNotPasted(pasteOnSelect);
        }

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
                // From the Enter until the paste's Ctrl is up again, keys the user types are held back and typed after the
                // pasted text, in their order (CLAUDE.md §2.54): typed now, they would go to the panel on its way out (it
                // still has the keyboard until the target is activated), or reach the target before the paste. The
                // injector joins this engagement. Not for a window that runs as administrator: the paste into it is
                // refused, and Windows would drop the held keys typed into it again (UIPI), where the user's own reach it.
                var guard = inject && !ProcessIntegrity.IsAboveCurrent(target) ? PasteKeyGuard.TryGetShared() : null;
                bool guarding = guard is not null && await guard.EngageAsync().ConfigureAwait(false);
                try
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
                }
                finally
                {
                    // The injector released Ctrl before it returned: the held keys follow the paste (or, when nothing was
                    // pasted, go to whatever window has the keyboard).
                    if (guarding)
                    {
                        timeline.KeysHeldBack += (await guard!.ReleaseAsync().ConfigureAwait(false)).Held;
                    }
                }
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

        // The injector says nothing when the window the panel came from is gone (closed while the panel showed).
        if (outcome == PasteOutcome.NoTarget)
        {
            AppLog.Info("Not pasted: the window the panel was opened from is gone. The item is on the clipboard.");
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
    /// Logs why a choice that was meant to paste only copies: "Paste after choosing an item" is off (once per session, since
    /// then every choice copies by design), or no window was captured to paste into (the panel was opened from the tray, or
    /// nothing had the foreground when the shortcut came). Content-free.
    /// </summary>
    /// <param name="pasteOnSelect">The "Paste after choosing an item" setting at the time of the choice.</param>
    /// <remarks>
    /// Without these lines a paste that delivered nothing left no trace: a report like "Enter did nothing" could not be told
    /// apart from a key that never arrived (the paste QA of 0.2.12, 2026-11-18, CLAUDE.md §6). Info, not a warning: both are
    /// everyday outcomes, and warnings stay a health signal.
    /// </remarks>
    private void LogWhyNotPasted(bool pasteOnSelect)
    {
        if (!pasteOnSelect)
        {
            if (!pasteOffLogged)
            {
                pasteOffLogged = true;
                AppLog.Info("Copied only: \"Paste after choosing an item\" is off in Settings (said once per session).");
            }

            return;
        }

        AppLog.Info("Copied only: no window to paste into was captured when the panel opened (it was opened from the tray, or no window had the foreground at the shortcut).");
    }

    /// <summary>
    /// Logs how long a paste took from the key to the Ctrl+V, stage by stage, and how long Ctrl stayed down after it — the
    /// session's first one always, later ones only when slow (<see cref="PasteTimeline.SlowPaste"/>), when Ctrl was released
    /// before the window answered that it took the keys (CLAUDE.md §2.54: the one case left in which a paste may type "v"), or
    /// when keys the user typed meanwhile were held back — so a report about pasting can be checked against the log.
    /// Content-free: stage names, times and counts.
    /// </summary>
    /// <param name="timeline">The finished paste's timeline.</param>
    private void LogPaste(PasteTimeline timeline)
    {
        var total = timeline.Total;
        bool releasedEarly = timeline.ControlHeld is not null && !timeline.ControlHeldUntilTaken;
        if (!PasteTimeline.ShouldLog(total, firstOfSession: !pasteLogged) && !releasedEarly && timeline.KeysHeldBack == 0)
        {
            return;
        }

        // Part of a log line, so English whatever the interface language.
        string kind = !pasteLogged ? " (first paste)" : total >= PasteTimeline.SlowPaste ? " (slow)" : string.Empty;
        AppLog.Info($"Pasted: Ctrl+V sent {PasteTimeline.Milliseconds(total)} ms after the key ({timeline.DescribeStages()}){timeline.DescribeHold()}{kind}.");
        pasteLogged = true;
    }
}
