using System.ComponentModel;
using BetterClipboard.App.Interop;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;

namespace BetterClipboard.App.Views;

/// <summary>
/// The panel's resizable size: Windows' own border drag resizes it (the window is resizable, ConfigureChrome), and the size
/// the user leaves it at is remembered (<see cref="AppSettings.FlyoutWidth"/> / <see cref="AppSettings.FlyoutHeight"/>) for
/// every later summon — Win+V's 25-item panel never let anyone make it bigger.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is saved:</b> only a resize the user made with a border (or Alt+Space › Size), once it ended
/// (<see cref="WindowSizeHook.UserResized"/>), in DIPs without the groups column (<see cref="FlyoutSizing.ToDips"/>). Size
/// changes the panel makes itself — opening at the remembered size, the groups column growing it on the left, a move to a
/// monitor with another scale, the work area shrinking it — are never saved, so a small monitor does not shrink the size
/// kept for a large one.
/// </para>
/// <para>
/// <b>Minimum:</b> <see cref="AppSettings.MinFlyoutWidth"/> × <see cref="AppSettings.MinFlyoutHeight"/> DIPs (plus the
/// groups column while it is open), answered per monitor scale through <c>WM_GETMINMAXINFO</c>
/// (<see cref="FlyoutSizing.MinimumTrackSize"/>), so the header never gets cut off.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>
    /// Watches the user's resizes and supplies the minimum size; <see langword="null"/> when subclassing failed (the panel
    /// is then still resizable, but without a minimum, and its size is not remembered — logged at creation).
    /// </summary>
    private readonly WindowSizeHook? sizeHook;

    /// <summary>
    /// Subclasses the panel's window for its resizes (from the constructor, once the window exists).
    /// </summary>
    /// <returns>The hook, or <see langword="null"/> when it could not be installed (logged; the panel works on without it).</returns>
    private WindowSizeHook? CreateSizeHook()
    {
        try
        {
            // The minimum grows with the groups column, read when Windows asks, so it follows the column opening.
            var hook = new WindowSizeHook(hwnd, scale => FlyoutSizing.MinimumTrackSize(scale, groupsPaneOpen ? GroupsPaneDip : 0));
            hook.UserResized += OnUserResized;
            return hook;
        }
        catch (Win32Exception ex)
        {
            AppLog.Error("Watching the panel's resizes failed; its size will not be remembered.", ex);
            return null;
        }
    }

    /// <summary>
    /// The user finished resizing the panel: remember its size (in DIPs, without the groups column) for the next summons.
    /// </summary>
    /// <param name="sender">The size hook.</param>
    /// <param name="e">Unused.</param>
    /// <remarks>
    /// Runs inside the window procedure (on the UI thread): the size is read now, while it is exact, and the settings file
    /// is written from the dispatcher queue right after, outside the message.
    /// </remarks>
    private void OnUserResized(object? sender, EventArgs e)
    {
        var size = AppWindow.Size;
        var (width, height) = FlyoutSizing.ToDips(size.Width, size.Height, WindowInterop.GetScale(hwnd), groupsPaneOpen ? GroupsPaneDip : 0);
        var current = controller.Settings.Current;
        if (width == current.FlyoutWidth && height == current.FlyoutHeight)
        {
            return; // a resize that ended where it began, or within a DIP of it
        }

        AppLog.Info($"Panel resized to {width} × {height} DIPs (remembered for the next summons).");
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                controller.Settings.Update(s => s with { FlyoutWidth = width, FlyoutHeight = height });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The new size stays in effect for this session; it is written with the next settings change that saves.
                AppLog.Warn($"Saving the panel's size failed: {ex.Message}");
            }
        });
    }
}
