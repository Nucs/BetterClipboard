// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Backdrop lab: what Windows' desktop acrylic does with its tint and luminosity, for the panel's "Background transparency"
// slider (CLAUDE.md §2.58).
//
//   BackdropLab.exe <log> [--show]
//
// The values (always; never on screen): a window set up like BetterClipboard's panel, cloaked before it is shown and never
// activated, gets a DesktopAcrylicController. The lab logs the controller's own TintColor, TintOpacity, LuminosityOpacity,
// FallbackColor and Kind before and after its configuration and its target, for each theme and kind, after explicit values
// and after ResetProperties: the numbers the slider's default must reproduce, so the panel looks as it did.
//
// The look (--show; on screen for about fifteen seconds): two windows of the lab's own at the bottom-left corner of the main
// monitor's work area, never activated — a striped pattern, and over it a window with the acrylic. For each setting the lab
// copies the acrylic window's inner rectangle from the screen and logs the mean luminance and how much of the stripes shows
// through: the standard deviation of the luminance, and its ratio to the bare stripes'. Only the lab's own windows lie in the
// copied rectangles, and no pixel is kept. Run it only while nobody watches that corner; it takes no keyboard.
//
// Build: dotnet build tools/probes/backdrop_lab -c Release (self-contained).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace BackdropLab;

/// <summary>Entry point: WinUI's start-up with a custom Main, like BetterClipboard.App's Program.</summary>
public static class Program
{
    /// <summary>Starts the lab.</summary>
    /// <param name="args">The log file, then options.</param>
    /// <returns>0 (the lab reports in its log).</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new LabApp(args);
        });
        return 0;
    }
}

/// <summary>
/// A system backdrop of the lab's own: one <see cref="DesktopAcrylicController"/> per window, configured with the
/// configuration the base class keeps in step with the window's theme and activation — what BetterClipboard's backdrop does.
/// </summary>
/// <remarks>One target only: a controller set up for one window cannot serve a second.</remarks>
public sealed partial class ProbeBackdrop : SystemBackdrop
{
    /// <summary>Completed when the window connected its target (the first frame of its content).</summary>
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The controller, while a target is connected; <see langword="null"/> before and after.</summary>
    public DesktopAcrylicController? Controller { get; private set; }

    /// <summary>The configuration the base class tracks for the connected window (theme, activation).</summary>
    public SystemBackdropConfiguration? DefaultConfiguration { get; private set; }

    /// <summary>Waits until the window connected its target.</summary>
    /// <param name="timeout">How long to wait at most.</param>
    /// <returns><see langword="true"/> when it connected in time.</returns>
    public async Task<bool> WaitConnectedAsync(TimeSpan timeout) =>
        await Task.WhenAny(connected.Task, Task.Delay(timeout)) == connected.Task;

    /// <summary>Creates the controller for the window that connected, with the tracked configuration.</summary>
    /// <param name="connectedTarget">The window's backdrop target.</param>
    /// <param name="xamlRoot">The window's XAML root.</param>
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        DefaultConfiguration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        Controller = new DesktopAcrylicController();
        Controller.SetSystemBackdropConfiguration(DefaultConfiguration);
        Controller.AddSystemBackdropTarget(connectedTarget);
        connected.TrySetResult();
    }

    /// <summary>Lets the controller go with the window.</summary>
    /// <param name="disconnectedTarget">The window's backdrop target.</param>
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        Controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        Controller?.Dispose();
        Controller = null;
    }
}

/// <summary>The lab application: a cloaked window for the values, two shown ones for the look, a log file.</summary>
public sealed partial class LabApp : Application
{
    /// <summary>How long a change of the acrylic is given to reach the screen before its pixels are read.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(450);

    /// <summary>The command line.</summary>
    private readonly string[] args;

    /// <summary>The log, written to the log file when the experiment ends.</summary>
    private readonly StringBuilder log = new();

    /// <summary>
    /// Creates the app (code only: plain panels need no control styles). It ends only when the experiment says so: by
    /// default WinUI shuts its dispatcher down when the last window closes, which ended the first run between the values'
    /// window and the look's windows, before anything was written.
    /// </summary>
    /// <param name="args">The command line.</param>
    public LabApp(string[] args)
    {
        this.args = args;
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    /// <summary>Runs the experiments once WinUI is up, writes the log, and exits.</summary>
    /// <param name="e">Launch data (unused).</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        try
        {
            Log($"foreground before: 0x{GetForegroundWindow():X}");
            await ValuesAsync();
            if (args.Contains("--steps"))
            {
                await StepsAsync();
            }

            if (args.Contains("--show"))
            {
                await LookAsync();
            }

            Log($"foreground after: 0x{GetForegroundWindow():X}");
        }
        catch (Exception ex)
        {
            Log($"failed: {ex}");
        }
        finally
        {
            File.WriteAllText(LogPath, log.ToString());
            Exit();
        }
    }

    /// <summary>
    /// The controller's own values, from a cloaked window that is never on screen: unconfigured, with the window's tracked
    /// configuration, per kind and theme, after explicit values and after <see cref="DesktopAcrylicController.ResetProperties"/>.
    /// </summary>
    /// <returns>A task that completes when the values are logged.</returns>
    private async Task ValuesAsync()
    {
        Log($"DesktopAcrylicController.IsSupported: {DesktopAcrylicController.IsSupported()}");
        using (var fresh = new DesktopAcrylicController())
        {
            Log("fresh, unconfigured: " + Describe(fresh));
        }

        var (window, hwnd) = CreatePanelLikeWindow(new Grid());
        if (!SetCloaked(hwnd, true))
        {
            Log("DWM refused to cloak the window: the values are skipped (the window would be visible).");
            window.Close();
            return;
        }

        var backdrop = new ProbeBackdrop();
        window.SystemBackdrop = backdrop;
        window.AppWindow.MoveAndResize(new RectInt32(100, 100, 400, 240));
        window.AppWindow.Show(false);
        if (!await backdrop.WaitConnectedAsync(TimeSpan.FromSeconds(5)) || backdrop.Controller is not { } controller)
        {
            Log("The backdrop was never connected.");
            window.Close();
            return;
        }

        Log("connected with the tracked configuration: " + Describe(controller) + " | " + Describe(backdrop.DefaultConfiguration!));

        // A configuration of the lab's own, so the theme can be switched; active, as the panel is while it shows.
        var config = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        controller.SetSystemBackdropConfiguration(config);
        foreach (var kind in new[] { DesktopAcrylicKind.Default, DesktopAcrylicKind.Base, DesktopAcrylicKind.Thin })
        {
            controller.Kind = kind;
            foreach (var theme in new[] { SystemBackdropTheme.Dark, SystemBackdropTheme.Light })
            {
                config.Theme = theme;
                await Task.Delay(80);
                Log($"kind {kind}, theme {theme}: {Describe(controller)}");
            }
        }

        controller.Kind = DesktopAcrylicKind.Default;
        config.Theme = SystemBackdropTheme.Dark;
        controller.TintOpacity = 0.3f;
        controller.LuminosityOpacity = 0.4f;
        await Task.Delay(80);
        Log("explicit tint 0.3, luminosity 0.4, dark: " + Describe(controller));
        config.Theme = SystemBackdropTheme.Light;
        await Task.Delay(80);
        Log("the same, the theme switched to light: " + Describe(controller));
        controller.ResetProperties();
        await Task.Delay(80);
        Log("after ResetProperties, light: " + Describe(controller));
        config.Theme = SystemBackdropTheme.Dark;
        await Task.Delay(80);
        Log("after ResetProperties, dark: " + Describe(controller));
        window.Close();
    }

    /// <summary>
    /// The steps a window's backdrop goes through, each logged before and after, in a cloaked window that is never on screen
    /// (--steps): the content's theme switched to light and back with the lab's backdrop connected, then that backdrop
    /// replaced by the stock one and back, then the window closed. A step that ends the process shows as the last line.
    /// </summary>
    /// <returns>A task that completes when every step ran.</returns>
    private async Task StepsAsync()
    {
        var root = new Grid();
        var (window, hwnd) = CreatePanelLikeWindow(root);
        if (!SetCloaked(hwnd, true))
        {
            Log("DWM refused to cloak the window: the steps are skipped.");
            window.Close();
            return;
        }

        var probe = new ProbeBackdrop();
        window.SystemBackdrop = probe;
        window.AppWindow.MoveAndResize(new RectInt32(100, 100, 400, 240));
        window.AppWindow.Show(false);
        Log($"steps: connected {await probe.WaitConnectedAsync(TimeSpan.FromSeconds(5))}");
        var config = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        probe.Controller!.SetSystemBackdropConfiguration(config);
        probe.Controller.TintColor = Colors.DimGray;
        probe.Controller.TintOpacity = 0.3f;
        probe.Controller.LuminosityOpacity = 0.4f;
        await Task.Delay(Settle);
        Log("steps: custom values set");

        Log("steps: ResetProperties, kind Thin, kind Default …");
        probe.Controller.ResetProperties();
        probe.Controller.Kind = DesktopAcrylicKind.Thin;
        await Task.Delay(Settle);
        probe.Controller.Kind = DesktopAcrylicKind.Default;
        Log("steps: … done");

        Log("steps: content theme to Light …");
        root.RequestedTheme = ElementTheme.Light;
        await Task.Delay(Settle);
        Log($"steps: … done; tracked {Describe(probe.DefaultConfiguration!)}");

        Log("steps: content theme to Dark …");
        root.RequestedTheme = ElementTheme.Dark;
        await Task.Delay(Settle);
        Log("steps: … done");

        Log("steps: backdrop replaced by the stock one …");
        window.SystemBackdrop = new DesktopAcrylicBackdrop();
        await Task.Delay(Settle);
        Log("steps: … done");

        Log("steps: content theme to Light with the stock one …");
        root.RequestedTheme = ElementTheme.Light;
        await Task.Delay(Settle);
        Log("steps: … done");

        Log("steps: the lab's backdrop again …");
        var second = new ProbeBackdrop();
        window.SystemBackdrop = second;
        Log($"steps: … connected {await second.WaitConnectedAsync(TimeSpan.FromSeconds(5))}");
        await Task.Delay(Settle);

        // The look's light pass did this, with no pause between, and WinUI ended the process (0xC000027B, a stowed
        // E_INVALIDARG): the theme's change, still pending, then met a backdrop that was gone.
        Log("steps: content theme to Dark and, at once, the stock backdrop …");
        root.RequestedTheme = ElementTheme.Dark;
        window.SystemBackdrop = new DesktopAcrylicBackdrop();
        await Task.Delay(Settle);
        Log("steps: … done");

        Log("steps: window closed …");
        window.Close();
        await Task.Delay(Settle);
        Log("steps: … done");
    }

    /// <summary>
    /// The look: the stock backdrop and the lab's at several values over a striped window of the lab's own, measured from
    /// the screen. The windows stand at the bottom-left corner of the main monitor's work area and are never activated. Each
    /// setting gets a window of its own: replacing a shown window's backdrop right after its theme changed ended the first
    /// such run inside WinUI (0xC000027B, a stowed E_INVALIDARG), which BetterClipboard never does (it sets its backdrop once).
    /// </summary>
    /// <returns>A task that completes when every setting is measured and every window is closed.</returns>
    private async Task LookAsync()
    {
        var work = MainWorkArea();
        const int PatternWidth = 600, PatternHeight = 400;
        var patternRect = new RectInt32(work.Left + 24, work.Bottom - PatternHeight - 24, PatternWidth, PatternHeight);
        var acrylicRect = new RectInt32(patternRect.X + 100, patternRect.Y + 110, 400, 240);
        var reference = new RECT { Left = patternRect.X + 10, Top = patternRect.Y + 10, Right = patternRect.X + PatternWidth - 10, Bottom = patternRect.Y + 90 };
        var inner = new RECT { Left = acrylicRect.X + 24, Top = acrylicRect.Y + 24, Right = acrylicRect.X + acrylicRect.Width - 24, Bottom = acrylicRect.Y + acrylicRect.Height - 24 };

        var (pattern, patternHwnd) = CreatePanelLikeWindow(Stripes());
        pattern.AppWindow.MoveAndResize(patternRect);
        try
        {
            pattern.AppWindow.Show(false);

            // Always on top, or a window the user keeps there (a terminal) can cover the stripes, and the acrylic windows,
            // which go on top of it, would blur that window instead (the second run measured a terminal that way).
            SetWindowPos(patternHwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST, no move, no size, no activate
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            Log($"stripes alone: {Measure(reference)}");
            Log($"stripes window: {DescribeShown(patternHwnd)}");
            double bare = MeasureValues(reference).Sd;

            foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
            {
                bool light = theme == ElementTheme.Light;

                // The theme's own colors, set with every value as BetterClipboard does (setting one value alone makes the
                // controller drop the theme for every value).
                var tint = light ? Color.FromArgb(255, 0xFC, 0xFC, 0xFC) : Color.FromArgb(255, 0x2C, 0x2C, 0x2C);
                var fallback = light ? Color.FromArgb(255, 0xF9, 0xF9, 0xF9) : tint;

                var (stock, stockHwnd) = CreatePanelLikeWindow(new Grid { RequestedTheme = theme });
                stock.SystemBackdrop = new DesktopAcrylicBackdrop();
                await ShowAboveAsync(stock, stockHwnd, acrylicRect);
                Log($"[{theme}] stock DesktopAcrylicBackdrop, window not active: {Measure(inner, bare)}");
                stock.Close();
                await Task.Delay(300);

                var (lab, labHwnd) = CreatePanelLikeWindow(new Grid { RequestedTheme = theme });
                var probe = new ProbeBackdrop();
                lab.SystemBackdrop = probe;
                await ShowAboveAsync(lab, labHwnd, acrylicRect);
                if (!await probe.WaitConnectedAsync(TimeSpan.FromSeconds(5)) || probe.Controller is not { } c)
                {
                    Log($"[{theme}] the lab's backdrop was never connected.");
                    lab.Close();
                    continue;
                }

                Log($"[{theme}] lab backdrop, tracked configuration (window not active): {Measure(inner, bare)} | {Describe(c)}");
                c.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
                {
                    IsInputActive = true,
                    Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark,
                });
                await Task.Delay(Settle);
                Log($"[{theme}] lab backdrop, active, Windows' own values: {Measure(inner, bare)} | {Describe(c)}");

                foreach (var (tintOpacity, luminosity) in new (float, float)[]
                {
                    (0f, 0f), (0f, 0.15f), (0f, 0.25f), (0f, 0.5f), (0f, 0.75f), (0f, 0.85f), (0f, 0.93f),
                    (0f, 0.96f), (0.15f, 0.96f), (0.25f, 0.975f), (0.4f, 0.99f), (0.4f, 1f), (0.95f, 1f),
                })
                {
                    c.TintColor = tint;
                    c.FallbackColor = fallback;
                    c.TintOpacity = tintOpacity;
                    c.LuminosityOpacity = luminosity;
                    await Task.Delay(Settle);
                    Log($"[{theme}] tint {tintOpacity:0.00}, luminosity {luminosity:0.00}: {Measure(inner, bare)}");
                }

                c.ResetProperties();
                c.Kind = DesktopAcrylicKind.Thin;
                await Task.Delay(Settle);
                Log($"[{theme}] kind Thin, Windows' own values: {Measure(inner, bare)} | {Describe(c)}");
                lab.Close();
                await Task.Delay(300);
            }
        }
        finally
        {
            pattern.Close();
        }
    }

    /// <summary>
    /// Shows a lab window over the stripes without activating it, always on top of them, and waits until its acrylic is drawn.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="hwnd">Its handle.</param>
    /// <param name="rect">Where it goes, in screen pixels.</param>
    /// <returns>A task that completes when the window has had time to draw.</returns>
    private static async Task ShowAboveAsync(Window window, nint hwnd, RectInt32 rect)
    {
        window.AppWindow.MoveAndResize(rect);
        window.AppWindow.Show(false);
        SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST, no move, no size, no activate
        await Task.Delay(Settle + TimeSpan.FromMilliseconds(300));
    }

    /// <summary>
    /// A window set up like the panel's: an overlapped window without a title bar, always on top, kept out of Alt+Tab and the
    /// taskbar, holding <paramref name="content"/>.
    /// </summary>
    /// <param name="content">The window's content.</param>
    /// <returns>The window and its handle.</returns>
    private static (Window Window, nint Hwnd) CreatePanelLikeWindow(UIElement content)
    {
        var window = new Window { Content = content };
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        return (window, hwnd);
    }

    /// <summary>Eight vertical stripes of saturated and neutral colors: what the acrylic blurs and tints.</summary>
    /// <returns>The pattern's root.</returns>
    private static Grid Stripes()
    {
        var grid = new Grid();
        var colors = new[]
        {
            Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow, Colors.White, Colors.Black, Colors.Cyan, Colors.Magenta,
        };
        for (int i = 0; i < colors.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var stripe = new Border { Background = new SolidColorBrush(colors[i]) };
            Grid.SetColumn(stripe, i);
            grid.Children.Add(stripe);
        }

        return grid;
    }

    /// <summary>A controller's values as text.</summary>
    /// <param name="c">The controller.</param>
    /// <returns>"kind …, tint #AARRGGBB @ opacity, luminosity …, fallback #AARRGGBB".</returns>
    private static string Describe(DesktopAcrylicController c) => string.Create(
        CultureInfo.InvariantCulture,
        $"kind {c.Kind}, tint {Hex(c.TintColor)} @ {c.TintOpacity:0.###}, luminosity {c.LuminosityOpacity:0.###}, fallback {Hex(c.FallbackColor)}, state {c.State}");

    /// <summary>A configuration's values as text.</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>"theme …, input active …, high contrast …".</returns>
    private static string Describe(SystemBackdropConfiguration config) =>
        $"configuration: theme {config.Theme}, input active {config.IsInputActive}, high contrast {config.IsHighContrast}";

    /// <summary>A color as #AARRGGBB.</summary>
    /// <param name="color">The color.</param>
    /// <returns>The text.</returns>
    private static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Measures a screen rectangle and describes it, relative to the bare stripes' spread when given.</summary>
    /// <param name="rect">The rectangle, in screen pixels.</param>
    /// <param name="bare">The bare stripes' standard deviation, or 0 for none.</param>
    /// <returns>"mean Y …, sd …, shows …% of the stripes, mean RGB …".</returns>
    private static string Measure(RECT rect, double bare = 0)
    {
        var m = MeasureValues(rect);
        string through = bare > 0 ? string.Create(CultureInfo.InvariantCulture, $", shows {100 * m.Sd / bare:0.0}% of the stripes") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"mean Y {m.Mean:0.0}, sd {m.Sd:0.0}{through}, mean RGB ({m.R:0},{m.G:0},{m.B:0})");
    }

    /// <summary>
    /// Copies a screen rectangle (DWM's composed picture, as GDI sees it) and returns the luminance's mean and standard
    /// deviation and the mean color. The pixels are dropped at once.
    /// </summary>
    /// <param name="rect">The rectangle, in screen pixels.</param>
    /// <returns>The statistics.</returns>
    private static (double Mean, double Sd, double R, double G, double B) MeasureValues(RECT rect)
    {
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        nint bitmap = CreateDIBSection(memory, ref info, 0, out nint bits, 0, 0);
        nint old = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            double sum = 0, sumSq = 0, r = 0, g = 0, b = 0;
            int n = width * height;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double y = (0.2126 * pixels[i + 2]) + (0.7152 * pixels[i + 1]) + (0.0722 * pixels[i]);
                sum += y;
                sumSq += y * y;
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }

            double mean = sum / n;
            return (mean, Math.Sqrt(Math.Max(0, (sumSq / n) - (mean * mean))), r / n, g / n, b / n);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>The main monitor's work area.</summary>
    /// <returns>The rectangle, in screen pixels.</returns>
    private static RECT MainWorkArea()
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromPoint(new POINT(), 1), ref info); // MONITOR_DEFAULTTOPRIMARY: (0, 0) is the main monitor's
        return info.rcWork;
    }

    /// <summary>
    /// A shown lab window's own state, for when the screen does not show what the lab drew: its rectangle, whether Windows
    /// has it visible and DWM cloaked, whether it stands above the window in front, and the mean of its own pixels as
    /// <c>PrintWindow</c> renders them (only this window, never the screen around it).
    /// </summary>
    /// <param name="hwnd">The lab's window.</param>
    /// <returns>One line of text.</returns>
    private static string DescribeShown(nint hwnd)
    {
        GetWindowRect(hwnd, out var r);
        int cloaked = 0;
        DwmGetWindowAttribute(hwnd, 14, out cloaked, sizeof(int)); // DWMWA_CLOAKED

        // Above the foreground window when walking up from this window never meets it. The visible windows met on the way
        // that overlap this one are named (process, always-on-top flag), to see what covers it.
        nint foreground = GetForegroundWindow();
        bool above = true;
        var over = new List<string>();
        for (nint w = GetWindow(hwnd, 3); w != 0; w = GetWindow(w, 3)) // GW_HWNDPREV
        {
            if (w == foreground)
            {
                above = false;
            }

            if (IsWindowVisible(w) && GetWindowRect(w, out var o) && o.Left < r.Right && o.Right > r.Left && o.Top < r.Bottom && o.Bottom > r.Top)
            {
                DwmGetWindowAttribute(w, 14, out int overCloaked, sizeof(int));
                if (overCloaked == 0)
                {
                    over.Add($"{ProcessNameOf(w)}{((GetWindowLong(w, -20) & 0x8) != 0 ? " (topmost)" : string.Empty)}");
                }
            }
        }

        bool topmost = (GetWindowLong(hwnd, -20) & 0x8) != 0;

        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        string own = "not captured";
        if (width > 0 && height > 0)
        {
            nint screen = GetDC(0);
            nint memory = CreateCompatibleDC(screen);
            var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            nint bitmap = CreateDIBSection(memory, ref info, 0, out nint bits, 0, 0);
            nint old = SelectObject(memory, bitmap);
            try
            {
                if (PrintWindow(hwnd, memory, 2)) // PW_RENDERFULLCONTENT
                {
                    var pixels = new byte[width * height * 4];
                    Marshal.Copy(bits, pixels, 0, pixels.Length);
                    double sum = 0;
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        sum += (0.2126 * pixels[i + 2]) + (0.7152 * pixels[i + 1]) + (0.0722 * pixels[i]);
                    }

                    own = string.Create(CultureInfo.InvariantCulture, $"own pixels mean Y {sum / (width * height):0.0}");
                }
                else
                {
                    own = "PrintWindow failed";
                }
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(0, screen);
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"rect ({r.Left},{r.Top})-({r.Right},{r.Bottom}), visible {IsWindowVisible(hwnd)}, cloaked {cloaked}, topmost {topmost}, above the foreground window {above}, {own}; windows over it: {(over.Count == 0 ? "none" : string.Join(", ", over))}");
    }

    /// <summary>The image name of a window's process, or "?" when it cannot be read.</summary>
    /// <param name="hwnd">Window.</param>
    /// <returns>The file name, such as "explorer.exe".</returns>
    private static string ProcessNameOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            return "?";
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    /// <summary>Cloaks or uncloaks a window (DWMWA_CLOAK).</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="cloaked">Whether to cloak it.</param>
    /// <returns><see langword="true"/> when DWM accepted.</returns>
    private static bool SetCloaked(nint hwnd, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, 13, ref value, sizeof(int)) == 0;
    }

    /// <summary>
    /// Adds a line to the log, and appends it to the log file at once: a run that ends abruptly (a crash of the composition
    /// layer leaves no event in the Application log) still shows how far it got.
    /// </summary>
    /// <param name="line">The line.</param>
    private void Log(string line)
    {
        log.AppendLine(line);
        File.AppendAllText(LogPath + ".partial", line + Environment.NewLine);
    }

    /// <summary>The log file the command line names, or <c>backdrop_lab.log</c> in the working folder.</summary>
    private string LogPath => args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "backdrop_lab.log";

    /// <summary>A point (<c>POINT</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        /// <summary>X.</summary>
        public int X;
        /// <summary>Y.</summary>
        public int Y;
    }

    /// <summary>A rectangle (<c>RECT</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        /// <summary>Left.</summary>
        public int Left;
        /// <summary>Top.</summary>
        public int Top;
        /// <summary>Right.</summary>
        public int Right;
        /// <summary>Bottom.</summary>
        public int Bottom;
    }

    /// <summary>Monitor information (<c>MONITORINFO</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        /// <summary>Size of the structure.</summary>
        public uint cbSize;
        /// <summary>Monitor rectangle.</summary>
        public RECT rcMonitor;
        /// <summary>Work area.</summary>
        public RECT rcWork;
        /// <summary>Flags (1 = main monitor).</summary>
        public uint dwFlags;
    }

    /// <summary>A 32-bit top-down DIB's header (<c>BITMAPINFOHEADER</c>; no color table follows for 32 bits).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        /// <summary>Size of the structure.</summary>
        public int biSize;
        /// <summary>Width.</summary>
        public int biWidth;
        /// <summary>Height; negative for top-down.</summary>
        public int biHeight;
        /// <summary>1.</summary>
        public short biPlanes;
        /// <summary>32.</summary>
        public short biBitCount;
        /// <summary>BI_RGB (0).</summary>
        public int biCompression;
        /// <summary>0 for BI_RGB.</summary>
        public int biSizeImage;
        /// <summary>Unused.</summary>
        public int biXPelsPerMeter;
        /// <summary>Unused.</summary>
        public int biYPelsPerMeter;
        /// <summary>Unused.</summary>
        public int biClrUsed;
        /// <summary>Unused.</summary>
        public int biClrImportant;
    }

    /// <summary>The foreground window, logged to show the lab never took it.</summary>
    /// <returns>The window.</returns>
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    /// <summary>A window's rectangle on the screen.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="rect">Receives the rectangle.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    /// <summary>A window's extended style (index -20) or other long value.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="index">-20 = GWL_EXSTYLE.</param>
    /// <returns>The value.</returns>
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    /// <summary>The process that created a window.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="processId">Receives the process id.</param>
    /// <returns>The thread id.</returns>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>Whether a window has the visible style.</summary>
    /// <param name="hwnd">Window.</param>
    /// <returns>Visible.</returns>
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    /// <summary>The next window in the Z order in a direction.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="command">3 = GW_HWNDPREV (the window above).</param>
    /// <returns>The window, or 0.</returns>
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    /// <summary>Draws one window's own content into a DC.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="hdc">Target DC.</param>
    /// <param name="flags">2 = PW_RENDERFULLCONTENT.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    /// <summary>Reads a DWM window attribute (14 = cloaked).</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="attribute">Attribute.</param>
    /// <param name="value">Receives the value.</param>
    /// <param name="size">Size of the value.</param>
    /// <returns>HRESULT.</returns>
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    /// <summary>Moves a window in the Z order.</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="after">-1 = HWND_TOPMOST.</param>
    /// <param name="x">Unused (SWP_NOMOVE).</param>
    /// <param name="y">Unused (SWP_NOMOVE).</param>
    /// <param name="cx">Unused (SWP_NOSIZE).</param>
    /// <param name="cy">Unused (SWP_NOSIZE).</param>
    /// <param name="flags">SWP flags.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    /// <summary>The monitor at a point.</summary>
    /// <param name="point">The point.</param>
    /// <param name="flags">1 = MONITOR_DEFAULTTOPRIMARY.</param>
    /// <returns>The monitor.</returns>
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, uint flags);

    /// <summary>Reads a monitor's rectangles.</summary>
    /// <param name="monitor">Monitor.</param>
    /// <param name="info">Receives the information.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    /// <summary>Sets a DWM window attribute (13 = cloak).</summary>
    /// <param name="hwnd">Window.</param>
    /// <param name="attribute">Attribute.</param>
    /// <param name="value">Value.</param>
    /// <param name="size">Size of the value.</param>
    /// <returns>HRESULT.</returns>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>The screen's device context (hwnd 0).</summary>
    /// <param name="hwnd">0.</param>
    /// <returns>The DC.</returns>
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    /// <summary>Releases a DC from <see cref="GetDC"/>.</summary>
    /// <param name="hwnd">0.</param>
    /// <param name="hdc">The DC.</param>
    /// <returns>1 when released.</returns>
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    /// <summary>A memory DC.</summary>
    /// <param name="hdc">The DC it is compatible with.</param>
    /// <returns>The memory DC.</returns>
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    /// <summary>A DIB section the pixels are copied into.</summary>
    /// <param name="hdc">A DC.</param>
    /// <param name="info">The DIB's header.</param>
    /// <param name="usage">0 = DIB_RGB_COLORS.</param>
    /// <param name="bits">Receives the pixels' address.</param>
    /// <param name="section">0.</param>
    /// <param name="offset">0.</param>
    /// <returns>The bitmap.</returns>
    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);

    /// <summary>Selects an object into a DC.</summary>
    /// <param name="hdc">The DC.</param>
    /// <param name="obj">The object.</param>
    /// <returns>The object it replaced.</returns>
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    /// <summary>Copies pixels between DCs.</summary>
    /// <param name="dest">Destination DC.</param>
    /// <param name="x">Destination x.</param>
    /// <param name="y">Destination y.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="source">Source DC.</param>
    /// <param name="sx">Source x.</param>
    /// <param name="sy">Source y.</param>
    /// <param name="rop">Raster operation.</param>
    /// <returns>Success.</returns>
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);

    /// <summary>Deletes a GDI object.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>Success.</returns>
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    /// <summary>Deletes a memory DC.</summary>
    /// <param name="hdc">The DC.</param>
    /// <returns>Success.</returns>
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);
}
