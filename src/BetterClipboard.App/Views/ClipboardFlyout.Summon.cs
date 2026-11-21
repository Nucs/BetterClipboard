using System.Diagnostics;
using BetterClipboard.App.Interop;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BetterClipboard.App.Views;

/// <summary>
/// Instant summons: the panel's window is never hidden between uses, only cloaked (DWM keeps it off the screen), and its
/// list is kept ready while nobody looks, so Win+V only moves an already drawn window to the caret and uncloaks it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Measured 2026-10-09 with <c>tools/probes/probe_summon.cs</c> on a claude-desktops Windows 11 VM (no GPU,
/// "Animation effects" on): the panel was in front 30 ms after Win+V and its first pixels showed after 78 ms, but the
/// picture settled only after 630 ms (median), changing about 17 times on the way. Every summon showed a hidden window,
/// which WinUI renders from nothing; faded the content in from transparent (150 ms) and slid it 10 px (220 ms); then read
/// the list from the store again and rebuilt every card, whose add animations played after that.
/// </para>
/// <para>
/// <b>Concealing</b> (<see cref="PutAway"/>, every way the panel goes away): cloak the window and mark it away, hand the
/// foreground to the window the user came from when there is one, then hide the window and show it again without activation
/// (<see cref="FinishPuttingAway"/>). Hiding makes Windows hand the foreground to the next window if the panel still had it —
/// a cloaked foreground window would keep the keyboard, invisibly. Showing it again keeps XAML drawing into it: a cloaked
/// window is still shown as far as Windows and WinUI are concerned. PowerToys' Command Palette hides its window this way.
/// The rest is queued, so none of it runs inside the hand-off of the foreground; a paste does only the cloak before its
/// Ctrl+V and the rest after it (<see cref="PutAwayForPaste"/>, CLAUDE.md §2.52).
/// </para>
/// <para>
/// <b>Preparing</b> (<see cref="PrepareForNextSummon"/>, right after concealing): reset the view a summon shows (search
/// empty, the tab the user last chose, no group, the strip showing that tab, the newest copy selected with the list at its
/// top), and reload the list if it is not current.
/// Whenever the history changes while the panel is concealed, the list is refreshed again
/// (<see cref="ScheduleConcealedRefresh"/>, at low priority, so a burst of changes costs one refresh). Reloads patch the
/// list instead of rebuilding it (<c>FlyoutViewModel.ApplyEntries</c>), so a refresh after a copy only adds that copy's card.
/// </para>
/// <para>
/// <b>A remembered tab of another app</b> (Everything, Pwsh, Cmd, Claude, Codex) is prepared too, but its list is never
/// "current": it also shows what that app keeps, which no history event announces. Such a tab is loaded once after
/// concealing — not again after every copy, which would ask Everything or start Command Prompt helpers each time — and
/// once more right after a summon showed it. A prompt tab is also refreshed when its agent's archive changes, so a prompt
/// just sent is already there when the panel appears.
/// </para>
/// <para>
/// <b>Presenting</b> (<see cref="PresentAsync"/>): move the window where the placement setting puts it (the caret by
/// default, ClipboardFlyout.Placement.cs) — at exactly the bounds asked for, also on a monitor with another scale — activate
/// it, uncloak it. A new size (another monitor scale, the groups column) waits for one rendered frame first, so DWM never
/// shows the old picture stretched.
/// </para>
/// <para>
/// <b>Fallbacks.</b> If DWM refuses to cloak the window (<see cref="cloaking"/> false), the panel hides and shows like any
/// window, as it did before. A window the shell keeps cloaked after we uncloak it belongs to another virtual desktop: it is
/// hidden and shown once, which moves it to the current one.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>A summon slower than this, from the key to the panel on screen, is logged (the session's first one always is).</summary>
    private static readonly TimeSpan SlowSummon = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Whether the user can see the panel: set when a summon puts it on screen, cleared by <see cref="Conceal"/>. This is
    /// <see cref="IsOpen"/>; the window itself stays shown (cloaked) between uses, so <c>AppWindow.IsVisible</c> says
    /// nothing about it.
    /// </summary>
    private bool presented;

    /// <summary>
    /// Whether DWM cloaks the window (decided once, in <see cref="ConfigureChrome"/>). When it does not, the panel hides and
    /// shows like any window: slower, but correct.
    /// </summary>
    private bool cloaking;

    /// <summary>Set while a refresh of the concealed panel's list is queued (<see cref="ScheduleConcealedRefresh"/>).</summary>
    private bool concealedRefreshQueued;

    /// <summary>Whether a summon was logged this session (the first one always is, see <see cref="LogSummon"/>).</summary>
    private bool summonLogged;

    /// <summary>
    /// Set while the panel is off the screen but not yet put away completely: from <see cref="PutAway"/> until
    /// <see cref="FinishPuttingAway"/> — the next turn of the dispatcher for most ways the panel goes away, after the Ctrl+V
    /// for a paste. A summon in between clears it (<see cref="PresentAsync"/>): the panel is back, and the late rest of the
    /// put-away must not run.
    /// </summary>
    private bool finishPending;

    /// <summary>
    /// Takes the panel off the screen without hiding its window (see the class remarks), then prepares it for the next
    /// summon: the deactivation path (a click elsewhere, the update dialog closing over another window).
    /// </summary>
    private void Conceal() => PutAway(handTo: 0, finishSoon: true);

    /// <summary>
    /// Takes the panel off the screen for a paste and does nothing else: its overlays closed, its window cloaked (DWM drops it
    /// at its next frame), and marked away, so its deactivation — the paste activates its target next — finds nothing more to
    /// do; a pinned panel only loses its overlays and stays. The caller hands the foreground to the target itself, sends
    /// Ctrl+V, and then calls <see cref="FinishPuttingAway"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the split: everything else that putting the panel away does — hiding and showing the window, resetting its view for
    /// the next summon with a layout pass of the list — took 40 to 75 ms on the UI thread in a warm process and up to 1.3 s
    /// once the app's memory had been trimmed and paged out, and it all came before the Ctrl+V (CLAUDE.md §2.52).
    /// </para>
    /// <para>
    /// A panel that stays open after use (pinned, CLAUDE.md §2.55) only closes its overlays — a viewer would cover the window
    /// the paste goes into — and stays on screen: the target's activation deactivates it, which leaves it up, and the rest of
    /// the paste is the same. <see cref="FinishPuttingAway"/> then finds nothing pending.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the panel went off the screen; <see langword="false"/> when it was not open or stays open.</returns>
    public bool PutAwayForPaste()
    {
        if (!IsOpen)
        {
            return false;
        }

        CloseImageOverlays();
        CloseUpdateDialog();
        if (StaysOpen)
        {
            // It must stay above the target the paste activates next (ClipboardFlyout.KeepOpen.cs).
            KeepOnTop();

            // Without the keyboard (ClipboardFlyout.Focusless.cs), the keys go back to the app after a pinned paste, as the
            // keyboard does for a pinned panel that had it: the user types on there, and the shortcut brings the panel forward.
            if (focusless)
            {
                LeaveKeysToApp();
            }

            return false;
        }

        PutAway(handTo: 0, finishSoon: false);
        return true;
    }

    /// <summary>
    /// Takes the panel off the screen: every way the panel goes away passes here (Esc, a paste, the shortcut again, a click
    /// elsewhere). Cloaks and marks it away, hands the foreground to <paramref name="handTo"/>, and queues the rest of the
    /// put-away or leaves it to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters. The window is cloaked and <see cref="presented"/> cleared first: activating another window deactivates
    /// the panel — synchronously, inside <c>SetForegroundWindow</c>, when called from this thread — and
    /// <see cref="OnActivated"/> must find it away already; it used to put the whole panel away right there, before the
    /// target even got its activation. Then the foreground goes to <paramref name="handTo"/> while the panel still owns it
    /// (cloaking does not give it away), so <c>SetForegroundWindow</c> is always allowed. Hiding comes last
    /// (<see cref="FinishPuttingAway"/>): it hands the foreground to whatever window Windows picks next, after which the
    /// panel could no longer give it to the target.
    /// </para>
    /// <para>
    /// The rest is queued, never run here: this runs inside the panel's deactivation (a click elsewhere: the window being
    /// activated waits for that handler to return) or right before the window the user came from takes its activation,
    /// which can wait on this thread too (CLAUDE.md §2.52). The view reset with its layout pass of the list must not hold
    /// either back; the window's keyboard is gone already.
    /// </para>
    /// </remarks>
    /// <param name="handTo">The window to give the foreground to (the one the user came from), or 0 for none.</param>
    /// <param name="finishSoon">
    /// Queue <see cref="FinishPuttingAway"/> now; <see langword="false"/> leaves it to the caller (a paste runs it after its
    /// Ctrl+V).
    /// </param>
    private void PutAway(nint handTo, bool finishSoon)
    {
        if (!presented)
        {
            return;
        }

        // The weekly update reminder is a tooltip, a window of its own: it must not stay on screen without its panel
        // (ClipboardFlyout.UpdateReminder.cs). Every way the panel goes away passes here, so this is the one place.
        CancelUpdateReminder();

        // Nor the pin's tip, for the same reason; taken off the screen without spending it, so a pinned panel shows it
        // again at its next summon until the user closes it (ClipboardFlyout.KeepOpenTip.cs).
        CloseKeepOpenTip();

        // Without the keyboard, the keys go back to the app at once (a key typed now must not be taken by a panel that is
        // gone), and the hooks go once the keys the panel took are up (ClipboardFlyout.Focusless.cs).
        EndFocusless();

        presented = false;
        finishPending = true;

        // A pinned panel stops following the foreground window: the next summon captures its target from the shortcut.
        UpdateForegroundTracking();

        // Off the screen at DWM's next frame, without any animation. Where DWM refuses to cloak, the window is hidden by
        // FinishPuttingAway instead, after the foreground went on.
        if (cloaking)
        {
            WindowInterop.SetCloaked(hwnd, true);
        }

        if (handTo != 0)
        {
            ForegroundHelper.Activate(handTo);
        }

        if (finishSoon && !DispatcherQueue.TryEnqueue(FinishPuttingAway))
        {
            // The queue is shutting down (app exit): nothing would run it later.
            FinishPuttingAway();
        }
    }

    /// <summary>
    /// The rest of putting the panel away, once it is off the screen (<see cref="PutAway"/>): the window hidden — so Windows
    /// activates the next window if the panel still had the foreground — and shown again without activation; the view reset
    /// for the next summon; the remembered tab saved; what this use read or decoded let go. A no-op when nothing is pending,
    /// when a summon brought the panel back meanwhile, or once the app closes the window for its exit.
    /// </summary>
    /// <remarks>
    /// It runs a moment after the put-away (queued, or after a paste's Ctrl+V), so the exit can have closed the window by
    /// then: hiding a closed window throws, and the next start prepares a fresh panel anyway.
    /// </remarks>
    public void FinishPuttingAway()
    {
        if (!finishPending || presented || closingForExit)
        {
            return;
        }

        finishPending = false;
        if (cloaking)
        {
            // Hidden, so Windows activates the next window if the panel still had the foreground (a paste or Esc gave it to
            // the target already; from the tray there is none), and shown again without activation, so XAML keeps drawing
            // into it and the next summon finds a finished picture.
            AppWindow.Hide();
            AppWindow.Show(false);

            // Always on top for the next summon, which may come while Start is open and Windows ignores the request
            // (ClipboardFlyout.ShellSurface.cs).
            KeepTopmostWhileConcealed();
        }
        else
        {
            AppWindow.Hide();
        }

        // The tab chosen during this use reaches the settings file now (once per use, and only when it changed): the write is
        // queued, so it never sits between a paste's Enter and its Ctrl+V.
        SaveRememberedTab();
        PrepareForNextSummon();

        // What this use left behind — the full-size picture of the image peek, the formats read ahead for a paste, the cards
        // of a search — is given back once no summon follows for a while (AppController.Memory.cs): most of it is native
        // memory WinUI frees only when the .NET objects holding it are collected.
        ReleaseImageCache();
        controller.ForgetPrefetchedFormats();
        controller.NotePanelPutAway();
    }

    /// <summary>
    /// Sets the concealed panel to what the next summon shows — the regular view with an empty search, the tab the user
    /// last chose (<see cref="ResetViewForShow"/>), the tab strip showing that tab, the groups column as remembered, the
    /// newest copy selected at the top of the list, the pins above it (<see cref="ResetListPosition"/>) — and reloads the
    /// list if it is not current. <see cref="ShowAt"/> repeats the resets, which are then no-ops, so nothing changes on
    /// screen.
    /// </summary>
    private void PrepareForNextSummon()
    {
        try
        {
            controller.ApplyTheme(Root);
            ResetViewForShow();
            groupsPaneOpen = controller.Settings.Current.ShowGroupsPane;
            ApplyGroupsPaneLayout();
            if (!ViewModel.GroupsLoaded)
            {
                _ = ViewModel.LoadGroupsAsync();
            }

            if (ViewModel.IsListCurrent)
            {
                ResetListPosition();
            }
            else
            {
                ScheduleConcealedRefresh();
            }
        }
        catch (Exception ex)
        {
            // Called from Conceal: a failure must not keep the panel from going away. The next summon then resets and
            // reloads by itself, as it always did.
            AppLog.Warn($"Preparing the panel for the next summon failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Queues one refresh of the concealed panel's list at low priority: after the events still waiting (an import's burst
    /// of changes then costs one refresh) and after any work the user is waiting for. No-op while one is queued.
    /// </summary>
    private void ScheduleConcealedRefresh()
    {
        if (concealedRefreshQueued)
        {
            return;
        }

        concealedRefreshQueued = true;
        bool queued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            concealedRefreshQueued = false;
            _ = RefreshConcealedListAsync();
        });

        if (!queued)
        {
            // The queue is shutting down (app exit): nothing will run it, and the next summon reloads anyway.
            concealedRefreshQueued = false;
        }
    }

    /// <summary>
    /// Reloads the list while the panel is concealed, unless it is current already or a summon came first (which reloads by
    /// itself), then selects the newest copy at the top of the list, the pins above it (<see cref="ResetListPosition"/>).
    /// </summary>
    /// <returns>A task completing when the list is current; it never throws (<c>ReloadAsync</c> logs its own failures).</returns>
    private async Task RefreshConcealedListAsync()
    {
        if (presented || ViewModel.IsListCurrent)
        {
            return;
        }

        await ViewModel.ReloadAsync();
        if (!presented)
        {
            ResetListPosition();
        }

        // The cards this refresh replaced hold native memory until a collection; copies come in bursts, so this asks for
        // one after the burst (AppController.Memory.cs).
        controller.NoteConcealedListRefreshed();
    }

    /// <summary>
    /// Selects the armed card — the newest copy, below the pinned cards while they are listed first — and scrolls the list
    /// to where a summon starts, without animation: the concealed list, for the next summon, and the summon itself once its
    /// list is in.
    /// </summary>
    /// <remarks>
    /// Below pinned cards, <see cref="ArmCard"/> scrolls by itself: a summon shows the plain history (no search text, no
    /// group), so the armed card goes to the top and the pins above it out of view (<see cref="ScrollPinsOutOfView"/>). A
    /// plain scroll to the top afterwards would undo that, so it follows only when the armed card is the top card (or the
    /// list is empty).
    /// </remarks>
    private void ResetListPosition()
    {
        if (!ArmCard())
        {
            scrollViewer?.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    /// <summary>
    /// Puts the panel on screen at <paramref name="rect"/> and gives it the keyboard: moves the window (cloaked, so nobody
    /// sees it move), activates it, uncloaks it. Without the keyboard (ClipboardFlyout.Focusless.cs, decided by
    /// <see cref="BeginSummonMode"/>) the window is never activated: shown and uncloaked only, the app keeps the focus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Activation comes before uncloaking, so the first frame on screen already has the active look (focused search box,
    /// selection colors) and keys typed from then on reach the panel. If Windows refuses to activate a cloaked window, the
    /// activation is repeated once it is uncloaked.
    /// </para>
    /// <para>
    /// A window that has nothing drawn at this size yet waits before it is uncloaked, already active (keys typed meanwhile
    /// reach it): one whose size changes (a monitor with another scale, the groups column opened last time) for two rendered
    /// frames, at most 100 ms — DWM would otherwise show the old picture stretched — and one shown for the first time (a
    /// summon before the warm-up) also until its XAML tree has loaded, at most 500 ms.
    /// </para>
    /// </remarks>
    /// <param name="rect">Where the panel goes, in physical screen pixels.</param>
    /// <param name="timeline">The summon's stages for its log line ("moved", "activated", "drawn"); null when not timed.</param>
    /// <returns>A task completing once the panel is on screen, or once a dismissal during the wait made that moot.</returns>
    private async Task PresentAsync(RectInt32 rect, StageTimeline? timeline)
    {
        var sizeBefore = AppWindow.Size;
        bool wasShown = AppWindow.IsVisible;

        // Exactly the bounds asked for: a move onto a monitor with another scale makes WinUI rescale the window a second
        // time (ClipboardFlyout.Placement.cs has the measurements), and the panel would open 25 % too large or 20 % too small.
        MoveAndResizeExactly(rect);
        if (!wasShown)
        {
            // Never shown yet (a summon before the warm-up), or hidden by the fallback path: show it — still cloaked when
            // cloaking — and re-apply the bounds, since a monitor with another DPI may have rescaled it on showing.
            AppWindow.Show(false);
            MoveAndResizeExactly(rect);
        }

        timeline?.Mark("moved");

        // Back on screen: a put-away still owed (FinishPuttingAway: queued by the last dismissal, or due after a paste's
        // Ctrl+V) would now hide the panel, and a paste's Enter guard would swallow this use's first Enter. Everything from
        // ShowAt's start to here runs in one go, so a queued rest cannot slip in between.
        finishPending = false;
        enterPending = false;
        enterSerial++;
        presented = true;

        // The summon's mode, read once: a panel that takes the keyboard on the way (a hook that failed) activates itself.
        bool takeKeyboard = !focusless;
        if (!takeKeyboard)
        {
            // Activation brings a window to the front; without it the panel must be put there: on top of the topmost band,
            // above the app the user types in. The always-on-top flag set at the window's start was found missing at the
            // first summon in the live check (2026-11-20): the panel opened behind File Explorer. With the keyboard the
            // activation hid that.
            WindowInterop.MakeTopmost(hwnd);

            // A window that lost the flag can refuse it for good; the presenter sets it again then
            // (ClipboardFlyout.ShellSurface.cs). Beside Start or Search nothing can be raised, so the flag must hold.
            if (!WindowInterop.IsTopmost(hwnd))
            {
                EnsureTopmost();
            }
        }

        if (!cloaking)
        {
            AppWindow.Show(takeKeyboard);
            if (takeKeyboard)
            {
                Activate();
                ForegroundHelper.Activate(hwnd);
                timeline?.Mark("activated");
            }

            return;
        }

        // Without the keyboard there is nothing to activate, and nothing to repeat below.
        bool active = true;
        if (takeKeyboard)
        {
            Activate();
            active = ForegroundHelper.Activate(hwnd);
            timeline?.Mark("activated");
        }
        bool sizeChanged = sizeBefore.Width != rect.Width || sizeBefore.Height != rect.Height;
        if (!wasShown || sizeChanged)
        {
            if (!wasShown)
            {
                await LoadedAsync(TimeSpan.FromMilliseconds(500));
            }

            await RenderedFramesAsync(2, TimeSpan.FromMilliseconds(100));
            if (!presented)
            {
                return; // dismissed while it waited (the shortcut pressed again): Conceal already ran
            }

            timeline?.Mark("drawn");
        }

        WindowInterop.SetCloaked(hwnd, false);
        if (WindowInterop.IsCloaked(hwnd))
        {
            // Still cloaked by someone else: the shell cloaks a window that belongs to another virtual desktop. Hiding and
            // showing it once moves it to the current desktop (the way every summon worked before cloaking).
            AppWindow.Hide();
            AppWindow.Show(takeKeyboard);
            active = !takeKeyboard;
        }

        if (!active)
        {
            Activate();
            ForegroundHelper.Activate(hwnd);
        }
    }

    /// <summary>
    /// Waits until the panel's XAML tree has loaded (its root's <c>Loaded</c>), or <paramref name="timeout"/> passed — the
    /// first show of the window builds the tree, and uncloaking before it exists would show an empty panel.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>A task completing once loaded or after the timeout.</returns>
    private async Task LoadedAsync(TimeSpan timeout)
    {
        if (Root.IsLoaded)
        {
            return;
        }

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onLoaded = (_, _) => loaded.TrySetResult();
        Root.Loaded += onLoaded;
        try
        {
            await Task.WhenAny(loaded.Task, Task.Delay(timeout));
        }
        finally
        {
            Root.Loaded -= onLoaded;
        }
    }

    /// <summary>
    /// Waits until XAML has started rendering <paramref name="frames"/> frames (<c>CompositionTarget.Rendering</c>), or
    /// <paramref name="timeout"/> passed, so a summon never hangs on a window that renders nothing.
    /// </summary>
    /// <remarks>
    /// Footgun: while any <c>Rendering</c> handler is attached, XAML renders every frame whether or not anything changed
    /// (seen in the 2026-10-09 trace: a frame every 16 ms for as long as a handler stayed attached). The handler is therefore
    /// removed the moment the wait ends; a forgotten one keeps the panel drawing 60 frames a second, cloaked or not.
    /// </remarks>
    /// <param name="frames">Frames to wait for (two: the first one with the new layout has then been handed to DWM).</param>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>A task completing after the frames or the timeout.</returns>
    private static async Task RenderedFramesAsync(int frames, TimeSpan timeout)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int seen = 0;
        EventHandler<object> onRendering = (_, _) =>
        {
            if (++seen >= frames)
            {
                done.TrySetResult();
            }
        };

        CompositionTarget.Rendering += onRendering;
        try
        {
            await Task.WhenAny(done.Task, Task.Delay(timeout));
        }
        finally
        {
            CompositionTarget.Rendering -= onRendering;
        }
    }

    /// <summary>
    /// Logs how long a summon took — the session's first one always, later ones only when slow (<see cref="SlowSummon"/>) —
    /// so a "Win+V feels slow" report can be checked against the log. Content-free: times and a count only.
    /// </summary>
    /// <remarks>
    /// The line names each stage's own time and the page faults the process took from the key to the panel on screen
    /// (CLAUDE.md §2.53): "Panel on screen 154 ms after the shortcut (queued 3 ms, prepared 61 ms, moved 2 ms, activated
    /// 70 ms, shown 18 ms; 6,412 page faults), its list ready (slow)." Thousands of faults mean the panel's memory had to
    /// come back (from RAM or from disk); a few hundred mean it waited for something else (the window being activated, the
    /// GPU). The prefix up to "after the shortcut" is what it was before the stages were added, for scripts that read it.
    /// </remarks>
    /// <param name="context">The summon's context (its <see cref="ForegroundContext.CapturedAt"/> is when the key arrived).</param>
    /// <param name="onScreen">Stopwatch timestamp of the moment the panel was uncloaked (or shown) and activated.</param>
    /// <param name="faultsOnScreen">This process's page-fault count at <paramref name="onScreen"/>.</param>
    /// <param name="timeline">The summon's stages up to <paramref name="onScreen"/>; null when not timed.</param>
    /// <param name="reloaded">Whether the list had to be reloaded after the panel appeared (it was not current).</param>
    private void LogSummon(ForegroundContext context, long onScreen, uint faultsOnScreen, StageTimeline? timeline, bool reloaded)
    {
        if (context.CapturedAt == 0)
        {
            return; // a context made without a timestamp: nothing to measure from
        }

        var toScreen = Stopwatch.GetElapsedTime(context.CapturedAt, onScreen);
        var toList = Stopwatch.GetElapsedTime(context.CapturedAt);
        if (summonLogged && toScreen < SlowSummon)
        {
            return;
        }

        // Part of a log line, so English whatever the interface language, and invariant numbers.
        string stages = timeline?.DescribeStages() ?? string.Empty;
        if (context.PageFaultsAtCapture != 0)
        {
            // The count wraps around after 2³²: the difference is right across a wrap with unchecked arithmetic.
            uint faults = unchecked(faultsOnScreen - context.PageFaultsAtCapture);
            stages += (stages.Length > 0 ? "; " : string.Empty)
                + faults.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                + (faults == 1 ? " page fault" : " page faults");
        }

        string details = stages.Length > 0 ? $" ({stages})" : string.Empty;
        string list = reloaded ? $", its list reloaded after {StageTimeline.Milliseconds(toList)} ms" : ", its list ready";
        AppLog.Info($"Panel on screen {StageTimeline.Milliseconds(toScreen)} ms after the shortcut{details}{list}{(summonLogged ? " (slow)" : " (first summon)")}.");
        summonLogged = true;
    }
}
