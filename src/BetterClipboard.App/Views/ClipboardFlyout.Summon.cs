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
/// <b>Concealing</b> (<see cref="Conceal"/>, every way the panel goes away): cloak the window, then hide it and show it
/// again without activation. Hiding makes Windows hand the foreground to the next window if the panel still had it — a
/// cloaked foreground window would keep the keyboard, invisibly. Showing it again keeps XAML drawing into it: a cloaked
/// window is still shown as far as Windows and WinUI are concerned. PowerToys' Command Palette hides its window this way.
/// </para>
/// <para>
/// <b>Preparing</b> (<see cref="PrepareForNextSummon"/>, right after concealing): reset the view a summon shows (search
/// empty, the tab the user last chose, no group, the strip showing that tab), and reload the list if it is not current.
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
/// <b>Presenting</b> (<see cref="PresentAsync"/>): move the window to the caret, activate it, uncloak it. A new size (another
/// monitor scale, the groups column) waits for one rendered frame first, so DWM never shows the old picture stretched.
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
    /// Takes the panel off the screen without hiding its window (see the class remarks), then prepares it for the next
    /// summon. Every way the panel goes away ends here: Esc, a paste, the shortcut again, a click elsewhere.
    /// </summary>
    /// <remarks>
    /// Callers that want the foreground to go to a particular window (the paste target) activate it first, while the panel
    /// still has the right to give the foreground away (<see cref="Dismiss"/>). <see cref="presented"/> is cleared before the
    /// window is hidden, because hiding deactivates it and <see cref="OnActivated"/> must not conceal it a second time.
    /// </remarks>
    private void Conceal()
    {
        if (!presented)
        {
            return;
        }

        presented = false;
        if (cloaking)
        {
            // Off the screen at DWM's next frame, without any animation...
            WindowInterop.SetCloaked(hwnd, true);

            // ...then hidden, so Windows activates the next window if the panel still had the foreground, and shown again
            // without activation, so XAML keeps drawing into it and the next summon finds a finished picture.
            AppWindow.Hide();
            AppWindow.Show(false);
        }
        else
        {
            AppWindow.Hide();
        }

        // The tab chosen during this use reaches the settings file now (once per use, and only when it changed), after
        // the window is gone: the write is queued, so it never sits between a paste's conceal and its Ctrl+V.
        SaveRememberedTab();
        PrepareForNextSummon();
    }

    /// <summary>
    /// Sets the concealed panel to what the next summon shows — the regular view with an empty search, the tab the user
    /// last chose (<see cref="ResetViewForShow"/>), the tab strip showing that tab, the groups column as remembered, the
    /// first card selected at the top of the list — and reloads the list if it is not current. <see cref="ShowAt"/> repeats
    /// the resets, which are then no-ops, so nothing changes on screen.
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
    /// itself), then puts the first card at the top, selected.
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
    }

    /// <summary>Selects the first card and scrolls the list to its top, without animation (the concealed list, for the next summon).</summary>
    private void ResetListPosition()
    {
        SelectIndex(0);
        scrollViewer?.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>
    /// Puts the panel on screen at <paramref name="rect"/> and gives it the keyboard: moves the window (cloaked, so nobody
    /// sees it move), activates it, uncloaks it.
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
    /// <returns>A task completing once the panel is on screen, or once a dismissal during the wait made that moot.</returns>
    private async Task PresentAsync(RectInt32 rect)
    {
        var sizeBefore = AppWindow.Size;
        bool wasShown = AppWindow.IsVisible;
        AppWindow.MoveAndResize(rect);
        if (!wasShown)
        {
            // Never shown yet (a summon before the warm-up), or hidden by the fallback path: show it — still cloaked when
            // cloaking — and re-apply the bounds, since a monitor with another DPI may have rescaled it on showing.
            AppWindow.Show(false);
            AppWindow.MoveAndResize(rect);
        }

        presented = true;
        if (!cloaking)
        {
            AppWindow.Show(true);
            Activate();
            ForegroundHelper.Activate(hwnd);
            return;
        }

        Activate();
        bool active = ForegroundHelper.Activate(hwnd);
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
        }

        WindowInterop.SetCloaked(hwnd, false);
        if (WindowInterop.IsCloaked(hwnd))
        {
            // Still cloaked by someone else: the shell cloaks a window that belongs to another virtual desktop. Hiding and
            // showing it once moves it to the current desktop (the way every summon worked before cloaking).
            AppWindow.Hide();
            AppWindow.Show(true);
            active = false;
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
    /// so a "Win+V feels slow" report can be checked against the log. Content-free: times only.
    /// </summary>
    /// <param name="context">The summon's context (its <see cref="ForegroundContext.CapturedAt"/> is when the key arrived).</param>
    /// <param name="onScreen">Stopwatch timestamp of the moment the panel was uncloaked (or shown) and activated.</param>
    /// <param name="reloaded">Whether the list had to be reloaded after the panel appeared (it was not current).</param>
    private void LogSummon(ForegroundContext context, long onScreen, bool reloaded)
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

        string list = reloaded ? $", its list reloaded after {toList.TotalMilliseconds:0} ms" : ", its list ready";
        AppLog.Info($"Panel on screen {toScreen.TotalMilliseconds:0} ms after the shortcut{list}{(summonLogged ? " (slow)" : " (first summon)")}.");
        summonLogged = true;
    }
}
