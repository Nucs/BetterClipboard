using System.Diagnostics;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Input;

/// <summary>
/// What the window in front when the shortcut was pressed means for where the panel can open: an app window, or one of the
/// shell's surfaces that Windows keeps above every app window (CLAUDE.md §2.61).
/// </summary>
public enum ShellSurfaceKind
{
    /// <summary>An app window, the desktop, or a window whose layer is unknown: the panel opens next to it as always.</summary>
    None = 0,

    /// <summary>
    /// Start or Search: above every app window, with a search box that takes a paste. The panel opens beside it — never
    /// under it — and pastes into its search box, as Windows' own Win+V does.
    /// </summary>
    Search = 1,

    /// <summary>
    /// The notification center, Quick settings or Task View: above every app window, with nothing to paste into. The
    /// surface is closed first, the way Esc closes it, and the panel opens over the window that has the keyboard then.
    /// </summary>
    Other = 2,
}

/// <summary>The shell surface that had the foreground when the shortcut was pressed: what it is and where it is.</summary>
/// <param name="Kind">What it is; <see cref="ShellSurfaceKind.None"/> for an app window (or none at all).</param>
/// <param name="Bounds">Its rectangle in physical screen pixels; empty for <see cref="ShellSurfaceKind.None"/>.</param>
public readonly record struct ShellSurface(ShellSurfaceKind Kind, ScreenRect Bounds)
{
    /// <summary>No shell surface: an app window had the foreground, or nothing did.</summary>
    public static ShellSurface None => default;
}

/// <summary>
/// Tells the shell's surfaces that sit above every app window — Start, Search, Quick settings, the notification center, Task
/// View — from app windows, and closes one the way Esc does (CLAUDE.md §2.61).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed.</b> The owner's report (2026-11-20): "I pressed Win to open start menu and then Win+V and the
/// BetterClipboard menu has shown behind the win start menu." Windows keeps these surfaces in z-order bands above the
/// desktop band that app windows live in, always-on-top ones included, and no window of an ordinary process can be raised
/// into them. Nor can such a process take the keyboard from Start: activation is refused while Start is open (measured in a
/// test desktop: the panel shown the usual way, with the keyboard, ended up behind Start and behind the app beneath it).
/// So a summon over one of them has two ways out: open beside it (Start and Search, where the paste goes into their search
/// box; a window that never activates takes clicks next to Start without closing it), or close it first and open over the
/// app beneath (<see cref="CloseAsync"/>).
/// </para>
/// <para>
/// <b>How a surface is recognized: its band</b> (<c>GetWindowBand</c>, undocumented, in user32 since Windows 8). Measured on
/// Windows 11 26200 (2026-11-20): app windows, the desktop's <c>Progman</c> and the taskbar while Start is closed are in band
/// 1; the wallpaper's <c>WorkerW</c> in band 12, below them; Start (<c>SearchHost</c>'s <c>Windows.UI.Core.CoreWindow</c>) in
/// band 6 and the taskbar with it while Start is open; Search in band 13; Quick settings (<c>ShellHost</c>'s
/// <c>ControlCenterWindow</c>) and the notification center in band 4; Task View in band 5. The ids match the ones public
/// research of user32 names (<c>ZBID_DESKTOP</c> = 1, <c>ZBID_IMMERSIVE_NOTIFICATION</c> = 4,
/// <c>ZBID_IMMERSIVE_APPCHROME</c> = 5, <c>ZBID_IMMERSIVE_MOGO</c> = 6, <c>ZBID_IMMERSIVE_BACKGROUND</c> = 12,
/// <c>ZBID_IMMERSIVE_SEARCH</c> = 13).
/// </para>
/// <para>
/// Footgun: only the bands measured are acted on (<see cref="KindOfBand"/>). Another band above the apps (a UIAccess window,
/// the lock screen's, one a later Windows adds) is treated as an app window, as every version before this did: the panel may
/// open under it. Sending Esc to something not known to close on Esc could cancel a dialog of an app instead.
/// </para>
/// </remarks>
public static class ShellSurfaces
{
    /// <summary><c>ZBID_DEFAULT</c>: no band given (what the call reports for some windows); treated as an app window.</summary>
    internal const uint DefaultBand = 0;

    /// <summary><c>ZBID_DESKTOP</c>: the band of app windows, the desktop and the taskbar while Start is closed.</summary>
    internal const uint DesktopBand = 1;

    /// <summary>
    /// <c>ZBID_UIACCESS</c>: windows of UIAccess processes (the On-Screen Keyboard, Magnifier, accessibility tools). Above the
    /// desktop band, but app windows the user may type in: never a shell surface.
    /// </summary>
    internal const uint UiAccessBand = 2;

    /// <summary><c>ZBID_IMMERSIVE_NOTIFICATION</c>: the notification center and Quick settings (measured).</summary>
    internal const uint NotificationBand = 4;

    /// <summary><c>ZBID_IMMERSIVE_APPCHROME</c>: Task View (measured).</summary>
    internal const uint AppChromeBand = 5;

    /// <summary><c>ZBID_IMMERSIVE_MOGO</c>: Start (measured), and the taskbar while Start is open.</summary>
    internal const uint StartBand = 6;

    /// <summary><c>ZBID_IMMERSIVE_BACKGROUND</c>: the wallpaper's <c>WorkerW</c>, below the app windows (measured).</summary>
    internal const uint BackgroundBand = 12;

    /// <summary><c>ZBID_IMMERSIVE_SEARCH</c>: Search, opened with Win+S or the taskbar's search box (measured).</summary>
    internal const uint SearchBand = 13;

    /// <summary>
    /// What a band means for the panel: Start and Search (<see cref="ShellSurfaceKind.Search"/>), the notification center,
    /// Quick settings and Task View (<see cref="ShellSurfaceKind.Other"/>), anything else an app window.
    /// </summary>
    /// <param name="band">A band id as <c>GetWindowBand</c> reports it.</param>
    /// <returns>The kind; <see cref="ShellSurfaceKind.None"/> for every band not measured to be one of those surfaces.</returns>
    public static ShellSurfaceKind KindOfBand(uint band) => band switch
    {
        StartBand or SearchBand => ShellSurfaceKind.Search,
        NotificationBand or AppChromeBand => ShellSurfaceKind.Other,
        _ => ShellSurfaceKind.None,
    };

    /// <summary>
    /// Whether a band holds only the shell's own surfaces: above the app windows and not the UIAccess band. Broader than
    /// <see cref="KindOfBand"/> on purpose: it only decides what is never a paste target (<see cref="IsShellWindow"/>), where
    /// a band not measured yet is safer left out than pasted into.
    /// </summary>
    /// <param name="band">A band id.</param>
    /// <returns><see langword="true"/> for every band but the default, desktop, UIAccess and wallpaper ones.</returns>
    public static bool IsShellBand(uint band) => band is not (DefaultBand or DesktopBand or UiAccessBand or BackgroundBand);

    /// <summary>A window's band, or <see langword="null"/> when it cannot be read.</summary>
    /// <param name="window">A top-level window; 0 gives <see langword="null"/>.</param>
    /// <returns>
    /// The band id; <see langword="null"/> for 0, a window that closed meanwhile, or a Windows without the (undocumented) call:
    /// callers then treat the window as an app window, as every version before this did.
    /// </returns>
    public static uint? BandOf(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        try
        {
            return GetWindowBand(window, out uint band) ? band : null;
        }
        catch (EntryPointNotFoundException)
        {
            // A Windows without the export: no band is known, which leaves every window an app window.
            return null;
        }
    }

    /// <summary>
    /// Whether a window lives in the UI-access band (<see cref="UiAccessBand"/>), which Windows draws above Start and Search:
    /// true for an always-on-top window of a process Windows started with UI access (<see cref="UiAccess"/>, CLAUDE.md §2.63).
    /// The panel asks it of its own window before it opens over Start instead of beside it.
    /// </summary>
    /// <param name="window">A top-level window.</param>
    /// <returns>
    /// <see langword="true"/> only when the band reads as the UI-access band; <see langword="false"/> also when it cannot be
    /// read (the panel then stands beside Start, as without UI access).
    /// </returns>
    public static bool IsInUiAccessBand(nint window) => BandOf(window) == UiAccessBand;

    /// <summary>
    /// Describes the window that had the foreground: a shell surface above every app window (its kind and bounds), or
    /// <see cref="ShellSurface.None"/>. Cheap (two calls into user32): the shortcut's thread calls it for every summon.
    /// </summary>
    /// <param name="window">The window; 0 gives <see cref="ShellSurface.None"/>.</param>
    /// <returns>The surface, or <see cref="ShellSurface.None"/> for an app window, an unreadable band or unreadable bounds.</returns>
    public static ShellSurface Describe(nint window)
    {
        if (BandOf(window) is not { } band)
        {
            return ShellSurface.None;
        }

        var kind = KindOfBand(band);
        if (kind == ShellSurfaceKind.None)
        {
            return ShellSurface.None;
        }

        // Without its bounds the panel cannot be placed beside it; as an app window it is at least not closed blindly.
        return GetWindowRect(window, out var rect)
            ? new ShellSurface(kind, new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom))
            : ShellSurface.None;
    }

    /// <summary>
    /// Whether a window is one of the shell's own surfaces, never a paste target: one of the window classes the pinned panel's
    /// tracker knows (<see cref="ForegroundTracker.IsShellSurface"/>: the taskbar, the tray's overflow, Start, Alt+Tab, …), or
    /// any window in a band of the shell's own (<see cref="IsShellBand"/>: Quick settings' <c>ControlCenterWindow</c> is in no
    /// class list, and a later Windows may add surfaces).
    /// </summary>
    /// <param name="window">The window; 0 is not one.</param>
    /// <returns><see langword="true"/> for a shell surface.</returns>
    public static bool IsShellWindow(nint window) =>
        window != 0 && (ForegroundTracker.IsShellSurface(MonitorLookup.ClassNameOf(window)) || BandOf(window) is { } band && IsShellBand(band));

    /// <summary>
    /// Closes the shell surface a summon found in front, the way Esc closes it, and waits until another window has the
    /// foreground: the window Windows gives it back to, the one the user was in before opening the surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held modifiers are released first (<see cref="KeyboardInput.ReleaseModifiers"/>, the mask key tapped so releasing
    /// Win opens nothing): the shortcut's Win is usually still down, and Win+Esc is Magnifier's "exit" while Magnifier runs.
    /// Measured in a test desktop (2026-11-20): Esc closed Start in 17 to 45 ms, also with Win still held, and the foreground
    /// went back to Notepad, which had it before Start opened; a Win released by the injection and then by the hand
    /// opened nothing.
    /// </para>
    /// <para>
    /// No Esc is sent when the surface is no longer in front by the time this runs (the user closed it meanwhile): it would
    /// reach the app the user is in, where Esc may cancel a dialog or a rename.
    /// </para>
    /// </remarks>
    /// <param name="context">The summon's context; its window is the surface.</param>
    /// <param name="timeout">The longest wait for the surface to leave the foreground.</param>
    /// <param name="cancellationToken">Ends the wait early.</param>
    /// <returns>
    /// A context captured once the surface left the foreground (or the wait ended), with the original summon's
    /// <see cref="ForegroundContext.CapturedAt"/> and <see cref="ForegroundContext.PageFaultsAtCapture"/>, so the summon's log
    /// line counts the wait. Its <see cref="ForegroundContext.Surface"/> is <see cref="ShellSurface.None"/>: a surface is closed
    /// once, never again in a loop, also when it did not close.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled during the wait.</exception>
    public static async Task<ForegroundContext> CloseAsync(ForegroundContext context, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        nint surface = context.TargetWindow;
        if (surface != 0 && GetForegroundWindow() == surface)
        {
            KeyboardInput.ReleaseModifiers();
            KeyboardInput.SendEscape();

            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < timeout)
            {
                // 0 means no foreground window for a moment (a switch under way): the switch is not over yet.
                nint foreground = GetForegroundWindow();
                if (foreground != 0 && foreground != surface)
                {
                    break;
                }

                // Any thread will do from here on: the foreground and the capture below are read the same from every thread.
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        return ForegroundContext.Capture() with
        {
            CapturedAt = context.CapturedAt,
            PageFaultsAtCapture = context.PageFaultsAtCapture,
            Surface = ShellSurface.None,
        };
    }
}
