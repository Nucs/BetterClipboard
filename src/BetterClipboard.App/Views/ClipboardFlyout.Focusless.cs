using System.Diagnostics;
using BetterClipboard.App.Interop;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BetterClipboard.App.Views;

/// <summary>
/// The panel that opens without taking the keyboard from the app the user types in, like Windows' own Win+V (Settings ›
/// Shortcut › <i>Leave the keyboard with your app</i>, on by default; CLAUDE.md §2.59).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The owner's report (2026-11-20): "Some controls end their editing session as soon as they lose focus. When I
/// open BetterClipboard in these controls, I lose the place where I wanted to paste" — a rename with F2 in File Explorer,
/// Explorer's search box and address bar, a note's name in Obsidian's file tree. The panel took the keyboard when it opened
/// (<see cref="PresentAsync"/>), so the rename ended before anything could be pasted into it.
/// </para>
/// <para>
/// <b>How.</b> The panel's window gets <c>WS_EX_NOACTIVATE</c> (a click on it does not activate it) and is uncloaked without
/// being activated. The app keeps the foreground and the keyboard focus, so its editing goes on, and a paste goes straight
/// into it (the paste's activation of its target finds it in front already). The keys the panel acts on are taken on their
/// way to the app by a low-level hook (<see cref="FocuslessPanelInput"/>, <see cref="FocuslessKeyRules"/>) and handled here
/// (<see cref="HandleKeyWithoutFocus"/>) the way <see cref="Root_PreviewKeyDown"/> handles them with the keyboard. Typing
/// adds to the end of the search (<see cref="SearchTyping"/>): there is no caret, so Left and Right always switch tabs.
/// </para>
/// <para>
/// <b>The closing rules are the panel's usual ones.</b> The hook reports what a deactivation would have told a panel with the
/// keyboard: a click outside the panel and its own windows, another window coming to the front, another desktop taking the
/// input (<see cref="HandleLeave"/>). An unpinned panel goes away then; a pinned one stays and leaves the keys to the app
/// until the shortcut brings it forward again. Esc, the shortcut pressed again and every use of an item close it as before.
/// </para>
/// <para>
/// <b>The panel takes the keyboard the usual way</b> (<see cref="TakeKeyboard"/>) where typing into a control of its own is
/// needed, which ends the app's editing as before: a click into the search box (a caret, and an input method's
/// composition), Ctrl+F, a viewer, a name box, a menu opened from the keyboard. And it opens the usual way when the setting is
/// off (screen readers follow the keyboard focus), when there is no window to type into (the tray), over BetterClipboard's own
/// windows, and over a window that runs as administrator: a hook of a process that is not elevated sees no keys typed there.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>The hooks that give the panel its keys while it shows without the keyboard; created at the first such summon.</summary>
    private FocuslessPanelInput? focuslessInput;

    /// <summary>
    /// Whether the panel shows without the keyboard (this summon left it with the app). Cleared when the panel takes the
    /// keyboard (<see cref="TakeKeyboard"/>) or goes away (<see cref="EndFocusless"/>).
    /// </summary>
    private bool focusless;

    /// <summary>
    /// Counts engagements of the hooks (UI thread). A key or a leave queued under an earlier engagement is dropped: it belongs
    /// to a use of the panel that is over.
    /// </summary>
    private int focuslessGeneration;

    /// <summary>Whether this session logged the mode once (content-free).</summary>
    private bool focuslessLogged;

    /// <summary>
    /// Whether the panel shows without the keyboard and takes the keys now: it is "in front" for the shortcut (pressed again,
    /// it closes the panel) and for the header (an unpinned panel that takes the keys is not left behind another window).
    /// </summary>
    public bool TakesKeysWithoutFocus => focusless && presented && focuslessInput is { IsTaking: true };

    /// <summary>
    /// Whether the user deals with the panel now: its window, or a window it owns, is in front, or it takes the keys without
    /// the focus (<see cref="TakesKeysWithoutFocus"/>).
    /// </summary>
    private bool IsInFront => ForegroundHelper.IsForeground(hwnd) || TakesKeysWithoutFocus;

    /// <summary>
    /// Wires what the mode needs in the panel's tree (constructor): a click into the search box gives the panel the keyboard.
    /// </summary>
    private void InitializeFocusless() =>
        SearchBox.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SearchBox_PointerPressedWithoutFocus), handledEventsToo: true);

    /// <summary>
    /// Whether a summon leaves the keyboard with the app the user types in (see the class remarks for when it does not).
    /// </summary>
    /// <param name="context">Where the user was when pressing the shortcut.</param>
    /// <returns><see langword="true"/> to show the panel without taking the keyboard.</returns>
    private bool ShouldLeaveFocus(ForegroundContext context)
    {
        if (!controller.Settings.Current.LeaveFocusInApp || context.TargetWindow == 0)
        {
            return false;
        }

        // BetterClipboard's own windows (Settings) have no editing to keep, and their keys would go through the hook to a
        // panel on the same thread: the usual way.
        if (context.TargetProcessId == (uint)Environment.ProcessId)
        {
            return false;
        }

        // Windows hides the keys typed into a window that runs as administrator from the hooks of a process that does not:
        // the panel would see none of them, and typing would land in that window.
        return !ProcessIntegrity.IsAboveCurrent(context.TargetWindow);
    }

    /// <summary>
    /// Sets the summon's mode before the panel shows (<see cref="ShowAt"/>): without the keyboard, the window does not activate
    /// on a click and the hooks start taking the keys; with it, the window activates as it always did.
    /// </summary>
    /// <param name="context">Where the user was when pressing the shortcut.</param>
    private void BeginSummonMode(ForegroundContext context)
    {
        bool leave = ShouldLeaveFocus(context);

        // A summon over a panel that still shows without the keyboard (the tray's "Open clipboard" while it is up) ends that
        // use's engagement first; a new one follows below when this summon leaves the keyboard too.
        if (focusless)
        {
            LeaveKeysToApp();
        }

        focusless = leave;
        WindowInterop.SetNoActivate(hwnd, leave);
        if (!leave)
        {
            return;
        }

        // x:Bind of a Window starts at its first activation (the generated code subscribes to Activated), which never comes
        // while the panel leaves the keyboard with the app: without this the panel showed its unbound defaults — the
        // "Paused" chip, no cards, "Start Everything" (live check, 2026-11-20). Idempotent once the bindings run.
        Bindings?.Initialize();

        if (!focuslessLogged)
        {
            focuslessLogged = true;
            AppLog.Info("The panel opens without taking the keyboard: the app keeps it, and the panel takes the keys it acts on (Settings › Shortcut).");
        }

        EngageFocuslessHooks(context.TargetWindow);
    }

    /// <summary>
    /// Starts taking the panel's keys over <paramref name="target"/>, without waiting: the engagement takes a millisecond on
    /// the hook's thread, and the panel shows meanwhile. When the hook cannot be installed, the panel takes the keyboard the
    /// usual way instead (<see cref="TakeKeyboard"/>).
    /// </summary>
    /// <param name="target">The window the user types in.</param>
    /// <remarks><c>async void</c>: started and awaited by nobody; it catches everything itself.</remarks>
    private async void EngageFocuslessHooks(nint target)
    {
        try
        {
            focuslessInput ??= new FocuslessPanelInput(OnFocuslessKey, OnFocuslessLeave);
            int generation = ++focuslessGeneration;
            bool engaged = await focuslessInput.EngageAsync(hwnd, target);
            if (!engaged && generation == focuslessGeneration && focusless && presented)
            {
                TakeKeyboard("the key hook could not be installed");
            }
        }
        catch (Exception ex)
        {
            // The window is up, and its keys would go to the app: the usual way is the safe one.
            AppLog.Warn($"Taking the panel's keys without the focus failed: {ex.Message}");
            if (focusless && presented)
            {
                TakeKeyboard("taking its keys failed");
            }
        }
    }

    /// <summary>
    /// The keys go back to the app at once, and the hooks go once the key-ups of the keys the panel took have come: a pinned
    /// panel stays on screen without the keyboard (the shortcut brings it forward again), or the panel goes away.
    /// </summary>
    private void LeaveKeysToApp()
    {
        if (focuslessInput is not { } input)
        {
            return;
        }

        focuslessGeneration++;
        input.StopTaking();
        _ = input.DisengageAsync();
    }

    /// <summary>The panel goes away (<see cref="PutAway"/>): the mode ends with it; the next summon decides anew.</summary>
    private void EndFocusless()
    {
        if (!focusless)
        {
            return;
        }

        focusless = false;
        LeaveKeysToApp();
    }

    /// <summary>
    /// The panel takes the keyboard the usual way while it shows without it: the hooks stop, the window may be activated by a
    /// click again, and it is activated now. The app's editing ends here, as it did before the mode existed. A no-op unless
    /// the panel shows without the keyboard.
    /// </summary>
    /// <param name="reason">Why, for the log line (content-free: "a click into the search box", "Ctrl+F").</param>
    /// <remarks>
    /// Allowed: the call comes from a click on the panel or from a key this process took, and
    /// <see cref="ForegroundHelper.Activate"/> brings the window forward the way every summon of the usual way does.
    /// </remarks>
    private void TakeKeyboard(string reason)
    {
        if (!focusless || !presented)
        {
            return;
        }

        EndModeForKeyboard(reason);
        Activate();
        ForegroundHelper.Activate(hwnd);
    }

    /// <summary>
    /// The panel has the keyboard now, or is about to get it: the hooks stop, and the window may be activated by a click
    /// again, as a panel with the keyboard always could be.
    /// </summary>
    /// <param name="reason">Why, for the log line (content-free).</param>
    private void EndModeForKeyboard(string reason)
    {
        focusless = false;
        LeaveKeysToApp();
        WindowInterop.SetNoActivate(hwnd, false);
        AppLog.Info($"The panel took the keyboard: {reason}.");
    }

    /// <summary>
    /// An activation event while the panel shows without the keyboard (<see cref="OnActivated"/>): Windows may report its
    /// window as activated within this process without putting it in front (a background activation, e.g. when WinUI moves
    /// its own focus). Only an activation that put the window in front counts: the panel has the keyboard then, the usual way.
    /// </summary>
    /// <param name="state">The event's state.</param>
    /// <returns><see langword="true"/> when the panel now has the keyboard and the usual handling should run.</returns>
    private bool KeyboardTakenByActivation(WindowActivationState state)
    {
        if (state == WindowActivationState.Deactivated || !presented || !ForegroundHelper.IsForeground(hwnd))
        {
            return false;
        }

        EndModeForKeyboard("its window was activated");
        return true;
    }

    /// <summary>
    /// A press on the search box while the panel shows without the keyboard: the user wants a caret (or an input method's
    /// composition), which only the keyboard focus gives. The search box's own buttons (its toggles, its clear button) act
    /// without the keyboard and are left alone.
    /// </summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">Pointer data.</param>
    private void SearchBox_PointerPressedWithoutFocus(object sender, PointerRoutedEventArgs e)
    {
        if (!focusless || !presented)
        {
            return;
        }

        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, SearchBox);
            element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
            {
                return;
            }
        }

        TakeKeyboard("a click into the search box");

        // The text box's own press handling ran already and placed the caret; with the window active now it shows.
        SearchBox.Focus(FocusState.Pointer);
    }

    /// <summary>The hook took a key (hook thread): queued for the panel at high priority, never handled here.</summary>
    /// <param name="key">The key.</param>
    private void OnFocuslessKey(PanelKey key)
    {
        int generation = Volatile.Read(ref focuslessGeneration);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => HandleFocuslessKey(key, generation)))
        {
            // The queue is shutting down (app exit): the key is dropped, and the hook is told so it does not wait for it.
            focuslessInput?.Acknowledge(key.Sequence);
        }
    }

    /// <summary>The hook saw the user leave (hook thread): queued for the panel, never handled here.</summary>
    /// <param name="leave">How the user left.</param>
    /// <param name="window">The window concerned (the one clicked or now in front), or 0.</param>
    private void OnFocuslessLeave(FocuslessLeave leave, nint window)
    {
        int generation = Volatile.Read(ref focuslessGeneration);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => HandleLeave(leave, generation));
    }

    /// <summary>
    /// A key the hook took (UI thread): handled when it still belongs to this use of the panel, dropped otherwise. The hook
    /// is told either way, so it sees the panel answer.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="generation">The engagement it was taken under.</param>
    private void HandleFocuslessKey(PanelKey key, int generation)
    {
        try
        {
            if (generation == focuslessGeneration && focusless && presented && !closingForExit)
            {
                HandleKeyWithoutFocus(key);
            }
        }
        catch (Exception ex)
        {
            // A dispatcher callback: an exception here would end the app. The key is lost, nothing else.
            AppLog.Warn($"Handling a key without the focus failed: {ex.Message}");
        }
        finally
        {
            focuslessInput?.Acknowledge(key.Sequence);
        }
    }

    /// <summary>
    /// The keyboard model of a panel that shows without the keyboard: the same keys and actions as
    /// <see cref="Root_PreviewKeyDown"/>, with the modifiers the hook saw, and typing added to the end of the search.
    /// </summary>
    /// <param name="key">The key the hook took.</param>
    /// <remarks>
    /// <para>
    /// Differences from the panel with the keyboard: Left and Right always switch tabs (there is no caret to move); Backspace
    /// erases the last character and Ctrl+Backspace the last word; Delete acts on the card only (with Shift, or with an empty
    /// search); other shortcuts of a text box (Ctrl+A, Ctrl+C, Ctrl+V, Ctrl+Z), Tab, Home, End and the function keys do
    /// nothing — they are taken, so they never reach the control the user wants to paste into.
    /// </para>
    /// <para>
    /// While a menu, a flyout or the update dialog opened with the mouse shows, it has no keyboard either: Esc closes it, and
    /// every other key is dropped.
    /// </para>
    /// </remarks>
    private void HandleKeyWithoutFocus(PanelKey key)
    {
        var virtualKey = (VirtualKey)key.VirtualKey;
        bool ctrl = (key.Modifiers & HotkeyModifiers.Control) != 0;
        bool shift = (key.Modifiers & HotkeyModifiers.Shift) != 0;
        bool alt = (key.Modifiers & HotkeyModifiers.Alt) != 0;

        // Alt alone: AltGr arrives as Ctrl+Alt (as in Root_PreviewKeyDown).
        bool altOnly = alt && !ctrl;
        bool repeat = key.IsRepeat;

        // A stray hover peek never swallows a keystroke (as in Root_PreviewKeyDown): closed first, which also frees its count.
        HidePeek();

        if (openPopups > 0)
        {
            if (virtualKey == VirtualKey.Escape)
            {
                CloseOpenPopups();
            }

            return;
        }

        switch (virtualKey)
        {
            case VirtualKey.Down:
                MoveVertical(1, repeat);
                break;
            case VirtualKey.Up:
                MoveVertical(-1, repeat);
                break;
            case VirtualKey.PageDown:
                MoveVertical(PanelNavigation.PageStep, repeat);
                break;
            case VirtualKey.PageUp:
                MoveVertical(-PanelNavigation.PageStep, repeat);
                break;
            case VirtualKey.Left:
            case VirtualKey.Right:
                // Not "typed into text": there is no caret, so the arrows are the tabs' (or the groups column's). With a
                // modifier they would edit text, and nothing happens.
                MoveHorizontal(virtualKey == VirtualKey.Right, repeat, hasModifier: ctrl || shift || alt, typedIntoText: false);
                break;
            case VirtualKey.Enter:
                if (!PickGroupByKey(enter: true, ctrl, shift, alt, repeat))
                {
                    _ = PasteBestMatchAsync(ctrl, shift, Stopwatch.GetTimestamp());
                }

                break;
            case VirtualKey.Space:
                // The groups' pick while the arrow keys walk the column, a typed space everywhere else.
                if (!PickGroupByKey(enter: false, ctrl, shift, alt, repeat))
                {
                    TypeIntoSearch(key);
                }

                break;
            case VirtualKey.Escape when drag is { IsMoving: true } moving:
                AppWindow.Move(new global::Windows.Graphics.PointInt32(moving.WindowStart.X, moving.WindowStart.Y));
                EndDrag();
                break;
            case VirtualKey.Escape when KeepOpenTip.IsOpen:
                DismissKeepOpenTip("Esc");
                break;
            case VirtualKey.Escape:
                if (!string.IsNullOrEmpty(ViewModel.SearchText))
                {
                    ViewModel.SearchText = string.Empty;
                }
                else
                {
                    // The user closes the panel: the app has the keyboard already, so nothing is handed back.
                    CloseByUser(restoreFocus: true);
                }

                break;
            case VirtualKey.Back:
                string erased = ctrl ? SearchTyping.EraseLastWord(ViewModel.SearchText) : SearchTyping.EraseLast(ViewModel.SearchText);
                if (!string.Equals(erased, ViewModel.SearchText, StringComparison.Ordinal))
                {
                    ViewModel.SearchText = erased;
                }

                break;
            case VirtualKey.Delete when shift || string.IsNullOrEmpty(ViewModel.SearchText):
                if (Selected is { } toDelete)
                {
                    DeleteItem(toDelete);
                }

                break;
            case VirtualKey.P when ctrl:
                if (Selected is { } toPin)
                {
                    _ = TogglePinAsync(toPin);
                }

                break;
            case VirtualKey.F when ctrl:
                // The search box with a caret: the keyboard the usual way.
                TakeKeyboard("Ctrl+F");
                SearchBox.Focus(FocusState.Keyboard);
                break;
            case VirtualKey.G when ctrl:
                SetGroupsPane(!groupsPaneOpen);
                break;
            case VirtualKey.F3:
                // The viewers take the keyboard themselves (they scroll, zoom and select with keys).
                if (Selected is { } toView)
                {
                    ViewCard(toView);
                }

                break;
            case VirtualKey.C when altOnly:
                ViewModel.MatchCase = !ViewModel.MatchCase;
                break;
            case VirtualKey.W when altOnly:
                ViewModel.WholeWord = !ViewModel.WholeWord;
                break;
            case VirtualKey.E when altOnly:
                ViewModel.UseRegex = !ViewModel.UseRegex;
                break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number9 when ctrl:
                int index = virtualKey - VirtualKey.Number1;
                if (index < ViewModel.Items.Count)
                {
                    _ = PasteItemAsync(ViewModel.Items[index], plainText: shift);
                }

                break;
            case VirtualKey.Application:
            case VirtualKey.F10 when shift:
                // A menu opened from the keyboard is walked with the keyboard: the usual way.
                if (Selected is { } forMenu && ItemsList.ContainerFromItem(forMenu) is FrameworkElement container)
                {
                    TakeKeyboard("the menu key");
                    ShowItemMenu(forMenu, container, new global::Windows.Foundation.Point(24, 24));
                }

                break;
            default:
                TypeIntoSearch(key);
                break;
        }
    }

    /// <summary>
    /// Adds what a key typed to the end of the search (<see cref="SearchTyping.Append"/>); a key that typed nothing printable
    /// (or a shortcut, which the hook gives no text) changes nothing.
    /// </summary>
    /// <param name="key">The key.</param>
    private void TypeIntoSearch(PanelKey key)
    {
        if (key.Text is not { Length: > 0 } typed)
        {
            return;
        }

        string next = SearchTyping.Append(ViewModel.SearchText, typed);
        if (!string.Equals(next, ViewModel.SearchText, StringComparison.Ordinal))
        {
            ViewModel.SearchText = next;
        }
    }

    /// <summary>
    /// Esc while a menu, a flyout or the update dialog shows without the keyboard: closes them, as Esc would with the
    /// keyboard. Each closing raises its own <c>Closed</c>, which keeps the count right.
    /// </summary>
    private void CloseOpenPopups()
    {
        CloseImageOverlays();
        CloseUpdateDialog();
        if (Root.XamlRoot is not { } root)
        {
            return;
        }

        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            // A flyout's popup closing closes the flyout (its light dismiss works the same way); a tooltip goes too.
            popup.IsOpen = false;
        }
    }

    /// <summary>
    /// The user left the panel that shows without the keyboard (UI thread): what a deactivation does to a panel with the
    /// keyboard. An unpinned panel goes away with its viewers, peeks and dialog; a pinned one stays, closes those, and leaves
    /// the keys to the app until the shortcut brings it forward again.
    /// </summary>
    /// <param name="leave">How the user left.</param>
    /// <param name="generation">The engagement the hook reported it under; an earlier one is over already.</param>
    private void HandleLeave(FocuslessLeave leave, int generation)
    {
        try
        {
            if (generation != focuslessGeneration || !focusless || !presented || closingForExit)
            {
                return;
            }

            if (StaysOpen)
            {
                // Pinned: the panel stays up while the user is elsewhere, as after a deactivation, and the keys are the app's.
                LeaveKeysToApp();
                CloseOverlaysOfPinnedPanel(DescribeOpenPopups());
                return;
            }

            // As after a deactivation that left a popup (ClipboardFlyout.Leaving.cs), the popup's kind is named; a plain leave
            // is not logged, as a plain deactivation is not.
            if (openPopups > 0)
            {
                AppLog.Info($"The panel went away: the user went to another window while {DescribeOpenPopups()}.");
            }

            CloseImageOverlays();
            CloseUpdateDialog();
            Conceal();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Putting the panel away after the user left it failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The shortcut, the tray or a second launch asks for a pinned panel that waits on screen without the keyboard: it takes
    /// the keys again over the window the user types in now, where it stands. Over a window the mode does not serve (the
    /// tray, an elevated window), it takes the keyboard the usual way instead.
    /// </summary>
    /// <param name="context">Where the user was when asking.</param>
    /// <returns><see langword="true"/> when the keys are taken without the focus; <see langword="false"/> when the caller brings the panel forward the usual way.</returns>
    private bool BringForwardWithoutFocus(ForegroundContext? context)
    {
        if (!focusless)
        {
            return false;
        }

        if (context is null || !ShouldLeaveFocus(context))
        {
            focusless = false;
            LeaveKeysToApp();
            WindowInterop.SetNoActivate(hwnd, false);
            return false;
        }

        EngageFocuslessHooks(context.TargetWindow);
        return true;
    }

    /// <summary>Removes the hooks for good (the app exits).</summary>
    private void DisposeFocusless()
    {
        focusless = false;
        focuslessInput?.Dispose();
        focuslessInput = null;
    }
}
