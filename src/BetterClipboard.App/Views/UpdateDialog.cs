using BetterClipboard.App.Interop;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BetterClipboard.App.Views;

/// <summary>
/// The update dialog: an <see cref="UpdateView"/> in a flyout, wired to the controller. Opened from the panel's update
/// button and from Settings › Updates; each owner creates its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>A flyout, not a window.</b> It opens at the button that was clicked and closes on Esc or a click elsewhere, like
/// the panel's other bubbles. It may leave its window's bounds (<see cref="FlyoutBase.ShouldConstrainToRootBounds"/> is
/// off): the panel can be as small as 396 × 320 DIPs, which is no room for release notes.
/// </para>
/// <para>
/// <b>It stays at its button.</b> Before it opens, it measures the room under and over the button on the button's
/// monitor and opens under it when there is room, else over it, never taller than that side allows
/// (<see cref="UpdateDialogLayout"/>); the release notes scroll, so a shorter dialog only shows fewer lines of them.
/// Left to itself, WinUI moves a flyout that does not fit to whichever side takes it whole, away from its button.
/// </para>
/// <para>
/// <b>Opening it approves nothing and requests little.</b> It shows what the last check found, and asks GitHub again
/// only when that is older than five minutes and this copy checks by itself (<see cref="AppController.RefreshUpdatesForDialogAsync"/>).
/// An update is downloaded only by its "Update and restart" button.
/// </para>
/// <para>
/// It listens to the controller's update status only while it is open, so a closed dialog costs nothing during a
/// download's ten progress reports a second, and an owner that goes away (the Settings window) leaves no handler behind.
/// </para>
/// <para>UI thread only.</para>
/// </remarks>
internal sealed class UpdateDialog
{
    private readonly AppController controller;
    private readonly nint ownerWindow;
    private readonly bool alignRight;
    private readonly UpdateView view = new();
    private readonly Flyout flyout;

    /// <summary>Whether <see cref="OnStatusChanged"/> is subscribed (the flyout is open or opening).</summary>
    private bool listening;

    /// <summary>
    /// Creates the dialog; nothing is shown or requested until it opens.
    /// </summary>
    /// <param name="controller">The app controller, which owns the update service.</param>
    /// <param name="ownerWindow">The window the dialog's button is in (its position on screen decides where the bubble opens).</param>
    /// <param name="alignRight">
    /// <see langword="true"/> to line the bubble's right edge up with the button's (a button near its window's right
    /// edge, like the panel's), <see langword="false"/> for the left edges (a button on the left, like the Settings card's).
    /// </param>
    public UpdateDialog(AppController controller, nint ownerWindow, bool alignRight)
    {
        this.controller = controller;
        this.ownerWindow = ownerWindow;
        this.alignRight = alignRight;
        view.ReleasesPage = controller.ReleasesPage;
        flyout = new Flyout
        {
            Content = view,
            Placement = alignRight ? FlyoutPlacementMode.BottomEdgeAlignedRight : FlyoutPlacementMode.BottomEdgeAlignedLeft,

            // Its own window, so it can be larger than the panel it belongs to (class remarks).
            ShouldConstrainToRootBounds = false,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["UpdateFlyoutPresenterStyle"],
        };
        flyout.Opening += OnOpening;
        flyout.Opened += OnOpened;
        flyout.Closed += OnClosed;

        view.InstallRequested += (_, _) => _ = controller.InstallUpdateAsync();
        view.CheckRequested += (_, _) => _ = controller.CheckForUpdatesAsync();
        view.CancelRequested += (_, _) => controller.CancelUpdate();
        view.CopyRequested += (_, command) => _ = controller.CopyTextAsync(command);
        view.LaterRequested += (_, _) => Hide();
        view.SkipRequested += (_, skip) =>
        {
            controller.SkipOfferedUpdate(skip);

            // Skipping is a way of saying "not now": the dialog is done. Un-skipping keeps it open, to update right away.
            if (skip)
            {
                Hide();
            }
        };
        view.LinkRequested += (_, link) =>
        {
            // The browser is about to take the foreground: close first, and let the owner get out of its way.
            Hide();
            LeavingForBrowser?.Invoke();
            controller.OpenWebLink(link);
        };
    }

    /// <summary>
    /// The flyout itself, to attach to a button (<c>Button.Flyout</c>): the button then opens and closes it like any
    /// button flyout, including a second click that closes it.
    /// </summary>
    public FlyoutBase Flyout => flyout;

    /// <summary>Whether the dialog is on screen.</summary>
    public bool IsOpen => flyout.IsOpen;

    /// <summary>
    /// Called just before a link opens in the browser. The panel conceals itself here: it is always on top, and would
    /// otherwise stay in front of the browser the user just asked for.
    /// </summary>
    public Action? LeavingForBrowser { get; set; }

    /// <summary>
    /// Raised before the bubble appears. The panel counts its open popups from here, before the new window can take the
    /// activation away from it.
    /// </summary>
    public event EventHandler? Opening;

    /// <summary>Raised after the bubble closed, however it closed.</summary>
    public event EventHandler? Closed;

    /// <summary>Opens the dialog at an element; does nothing when it is open already.</summary>
    /// <param name="anchor">The element the bubble points at (normally the button it is attached to).</param>
    public void ShowAt(FrameworkElement anchor)
    {
        if (!flyout.IsOpen)
        {
            flyout.ShowAt(anchor);
        }
    }

    /// <summary>Closes the dialog; does nothing when it is closed.</summary>
    public void Hide()
    {
        if (flyout.IsOpen)
        {
            flyout.Hide();
        }
    }

    /// <summary>
    /// The bubble is about to appear: decide where, show the current state, follow its changes, and make the release
    /// list current.
    /// </summary>
    /// <param name="sender">The flyout.</param>
    /// <param name="e">Event data.</param>
    private void OnOpening(object? sender, object e)
    {
        Opening?.Invoke(this, EventArgs.Empty);
        if (flyout.Target is { } target)
        {
            Arrange(target);
        }

        if (!listening)
        {
            listening = true;
            controller.UpdateStatusChanged += OnStatusChanged;
        }

        RenderCurrent();

        // Never throws; its result arrives through UpdateStatusChanged.
        _ = controller.RefreshUpdatesForDialogAsync();
    }

    /// <summary>
    /// The bubble is on screen: its notes start at their beginning. Done here, not while opening: a scroll request to a
    /// scroller that is not on screen yet is dropped, and the notes then kept the position of the last time (measured).
    /// </summary>
    /// <param name="sender">The flyout.</param>
    /// <param name="e">Event data.</param>
    private void OnOpened(object? sender, object e) => view.ScrollNotesToTop();

    /// <summary>The bubble closed: stop following the status.</summary>
    /// <param name="sender">The flyout.</param>
    /// <param name="e">Event data.</param>
    private void OnClosed(object? sender, object e)
    {
        if (listening)
        {
            listening = false;
            controller.UpdateStatusChanged -= OnStatusChanged;
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The update status changed while the dialog is open.</summary>
    /// <param name="sender">The controller.</param>
    /// <param name="e">Event data.</param>
    private void OnStatusChanged(object? sender, EventArgs e) => RenderCurrent();

    /// <summary>Shows the controller's current update status; closes the dialog when the update system is gone (the app is exiting).</summary>
    private void RenderCurrent()
    {
        if (controller.UpdateStatus is { } snapshot)
        {
            view.Render(snapshot);
        }
        else
        {
            Hide();
        }
    }

    /// <summary>
    /// Puts the bubble under or over its button and limits its height to the room there (class remarks). A failure to
    /// measure keeps the last arrangement: the dialog then opens where WinUI finds room for it.
    /// </summary>
    /// <param name="anchor">The button the bubble opens at.</param>
    private void Arrange(FrameworkElement anchor)
    {
        try
        {
            if (anchor.XamlRoot is not { } root)
            {
                return;
            }

            // The button's top and bottom edges in screen pixels: its place in the window's content (DIPs) times the
            // scale, from where the content starts on screen.
            double scale = root.RasterizationScale > 0 ? root.RasterizationScale : 1.0;
            var (_, clientTop) = WindowInterop.GetClientOrigin(ownerWindow);
            double topDip = anchor.TransformToVisual(null).TransformPoint(default).Y;
            double anchorTop = clientTop + (topDip * scale);
            double anchorBottom = anchorTop + (anchor.ActualHeight * scale);
            var (workArea, _) = MonitorLookup.FromWindow(ownerWindow);

            var placement = UpdateDialogLayout.Choose((workArea.Bottom - anchorBottom) / scale, (anchorTop - workArea.Top) / scale);
            flyout.Placement = (placement.Below, alignRight) switch
            {
                (true, true) => FlyoutPlacementMode.BottomEdgeAlignedRight,
                (true, false) => FlyoutPlacementMode.BottomEdgeAlignedLeft,
                (false, true) => FlyoutPlacementMode.TopEdgeAlignedRight,
                (false, false) => FlyoutPlacementMode.TopEdgeAlignedLeft,
            };

            // The view's notes take what is left of this height (UpdateView.xaml), so a lower limit only shows fewer lines.
            view.MaxHeight = placement.MaxHeightDip;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // An element that left the tree between the click and here; nothing to measure against.
            Core.Diagnostics.AppLog.Info($"The update dialog's place was not measured ({ex.GetType().Name}).");
        }
    }
}
