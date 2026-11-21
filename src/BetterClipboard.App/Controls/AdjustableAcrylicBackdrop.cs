using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Presentation;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BetterClipboard.App.Controls;

/// <summary>
/// The panel's background: Windows' desktop acrylic, as the stock <see cref="DesktopAcrylicBackdrop"/> draws it, with how
/// strongly its frost covers the blurred screen behind set by Settings › App › Background transparency (<see cref="Level"/>,
/// CLAUDE.md §2.58). Set on <c>Window.SystemBackdrop</c>; serves that one window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the stock backdrop:</b> <see cref="DesktopAcrylicBackdrop"/> has no property to change. This one owns the same
/// <see cref="DesktopAcrylicController"/> the stock one creates and sets its tint and luminosity
/// (<see cref="PanelBackdrop.LookFor"/>); at the default level it lets Windows choose them (<c>ResetProperties</c>), so a
/// panel left at 50 looks exactly as before.
/// </para>
/// <para>
/// <b>Its own configuration, input active for good.</b> The configuration the base class tracks
/// (<see cref="SystemBackdrop.GetDefaultSystemBackdropConfiguration"/>) says whether the window has the keyboard, and Windows
/// draws an inactive window's acrylic as a solid color (the backdrop lab saw state <c>Fallback</c>). An unpinned panel is
/// never inactive on screen (it goes away when it loses the keyboard), but a pinned one waits behind the app the user works
/// in (CLAUDE.md §2.55) and was a flat gray there, whatever the slider said. So the theme and the high-contrast colors are
/// mirrored from the tracked configuration (<see cref="OnDefaultSystemBackdropConfigurationChanged"/>) and input stays
/// active: the glass stays blurred, as the owner asked ("it always stays blur"). Where Windows draws no glass at all
/// (transparency effects off, battery saver, a remote session) it still shows the fallback color.
/// </para>
/// <para>
/// <b>Lifetime.</b> The controller is created when the window connects its target (its first frame) and disposed when the
/// target goes (the window closes): a controller holds composition resources, and one set up for a window cannot serve
/// another. Where the system has no desktop acrylic (<see cref="DesktopAcrylicController.IsSupported"/>), nothing is drawn
/// behind the content and the window shows its default background, as with the stock backdrop.
/// </para>
/// <para>
/// <b>A theme switched while the app runs gets a new controller</b> (<see cref="Renew"/>): Windows' own switch, Settings ›
/// App › Theme, and high contrast turned on or off all reach it, since each changes the theme of the panel's content, which
/// the tracked configuration follows. Until 2026-11-21 the mirrored theme and <c>ResetProperties</c> were left to recolor
/// the existing controller, and at the default level they did not: after Windows switched from light to dark, the panel's
/// text turned light while its glass kept the light colors, so the panel was unreadable until BetterClipboard restarted
/// [verified in a test desktop, twice; 0.2.11's stock backdrop, which hands the controller the tracked configuration itself
/// and never resets it, recolored]. Most likely the reset ran before the controller had taken in the configuration's new
/// theme, and so reset to the old theme's values [inferred]. A controller set up for the new theme is what a start does,
/// and a start draws either theme right [verified], so a switch now does the same: verified both ways at the default level
/// and at 80, through Settings, on a pinned panel left on screen, and into and out of High Contrast Black. CLAUDE.md §2.58.
/// </para>
/// </remarks>
public sealed partial class AdjustableAcrylicBackdrop : SystemBackdrop
{
    /// <summary>
    /// The configuration handed to the controller: the theme and high-contrast state mirrored from the tracked one, input
    /// active always (see remarks). Kept for the controller's lifetime, which reads it whenever it changes.
    /// </summary>
    private readonly SystemBackdropConfiguration configuration = new() { IsInputActive = true };

    /// <summary>The window's acrylic while its target is connected; <see langword="null"/> before, after, and where unsupported.</summary>
    private DesktopAcrylicController? controller;

    /// <summary>
    /// The target <see cref="controller"/> draws on, kept so <see cref="Renew"/> can hand it to a new controller;
    /// <see langword="null"/> whenever <see cref="controller"/> is.
    /// </summary>
    private ICompositionSupportsSystemBackdrop? controllerTarget;

    /// <summary>The connected window's XAML root, for its theme when the tracked configuration leaves the theme to the system.</summary>
    private XamlRoot? root;

    /// <summary>The level applied, <see cref="PanelBackdrop.MinLevel"/>..<see cref="PanelBackdrop.MaxLevel"/>.</summary>
    private int level;

    /// <summary>Whether the look applied last was for the light theme; <see langword="null"/> before the first.</summary>
    private bool? appliedLight;

    /// <summary>Whether this session logged that the system has no desktop acrylic: said once.</summary>
    private static bool unsupportedLogged;

    /// <summary>Creates the backdrop at a level (clamped).</summary>
    /// <param name="level">The level to start with (<see cref="Core.Settings.AppSettings.BackgroundTransparency"/>).</param>
    public AdjustableAcrylicBackdrop(int level) => this.level = PanelBackdrop.Normalize(level);

    /// <summary>
    /// How see-through the glass is, <see cref="PanelBackdrop.MinLevel"/> (most frosted) to <see cref="PanelBackdrop.MaxLevel"/>
    /// (clearest); <see cref="PanelBackdrop.DefaultLevel"/> is Windows' own look. A change applies at once when the window is
    /// connected (UI thread only), and from the first frame otherwise.
    /// </summary>
    public int Level
    {
        get => level;
        set
        {
            int next = PanelBackdrop.Normalize(value);
            if (next == level)
            {
                return;
            }

            level = next;
            Apply(force: true);
        }
    }

    /// <summary>
    /// The window connected its target (its content's first frame): creates the acrylic, mirrors the tracked configuration
    /// into the one input stays active in, and applies the level.
    /// </summary>
    /// <param name="connectedTarget">The window's backdrop target.</param>
    /// <param name="xamlRoot">The window's XAML root.</param>
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (controller is not null)
        {
            // A second window: this backdrop's controller already serves the first, and cannot serve two.
            AppLog.Warn("The panel's backdrop was set on a second window; that window gets no acrylic.");
            return;
        }

        if (!DesktopAcrylicController.IsSupported())
        {
            if (!unsupportedLogged)
            {
                unsupportedLogged = true;
                AppLog.Info("This system has no desktop acrylic: the panel shows a plain background, and Background transparency has nothing to change.");
            }

            return;
        }

        root = xamlRoot;
        controllerTarget = connectedTarget;
        Mirror(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
        controller = new DesktopAcrylicController();
        controller.SetSystemBackdropConfiguration(configuration);
        controller.AddSystemBackdropTarget(connectedTarget);
        Apply(force: true);
    }

    /// <summary>The window's target went (the window closes): lets the acrylic go with it.</summary>
    /// <param name="disconnectedTarget">The window's backdrop target.</param>
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (controller is null)
        {
            return;
        }

        controller.RemoveSystemBackdropTarget(disconnectedTarget);
        controller.Dispose();
        controller = null;
        controllerTarget = null;
        root = null;
        appliedLight = null;
    }

    /// <summary>
    /// The tracked configuration changed — the panel's theme, high contrast, or its activation, which is ignored here (see
    /// remarks): mirrors it, and when the theme or high contrast changed, sets the acrylic up afresh for them
    /// (<see cref="Renew"/>). An activation alone changes nothing mirrored and costs nothing.
    /// </summary>
    /// <param name="target">The window's backdrop target.</param>
    /// <param name="xamlRoot">The window's XAML root.</param>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (controller is null)
        {
            return;
        }

        if (Mirror(GetDefaultSystemBackdropConfiguration(target, xamlRoot)))
        {
            Renew();
        }
    }

    /// <summary>
    /// Copies the theme and high-contrast state of the tracked configuration into <see cref="configuration"/>, leaving input
    /// active. A controller reads the configuration object itself, but a switch while it runs is not left to that: the
    /// caller renews the controller when this reports a change (class remarks).
    /// </summary>
    /// <param name="tracked">The configuration the base class keeps in step with the window.</param>
    /// <returns>Whether the theme, high contrast or its background color changed.</returns>
    private bool Mirror(SystemBackdropConfiguration tracked)
    {
        bool changed = false;
        if (configuration.Theme != tracked.Theme)
        {
            configuration.Theme = tracked.Theme;
            changed = true;
        }

        if (configuration.IsHighContrast != tracked.IsHighContrast)
        {
            configuration.IsHighContrast = tracked.IsHighContrast;
            changed = true;
        }

        if (!configuration.HighContrastBackgroundColor.Equals(tracked.HighContrastBackgroundColor))
        {
            configuration.HighContrastBackgroundColor = tracked.HighContrastBackgroundColor;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Replaces the controller with a new one set up for the configuration as it is now, on the same target, and applies the
    /// level to it: what a start does, which draws either theme right, where recoloring the old controller left the old
    /// theme's glass at the default level (class remarks). Logs the switch, content-free.
    /// </summary>
    /// <remarks>
    /// The target holds one controller at a time, so the old one lets go of it before the new one takes it: a panel on
    /// screen at that moment (a pinned one) may show one frame without glass. A theme switch is rare, and the panel is
    /// usually concealed then.
    /// </remarks>
    private void Renew()
    {
        if (controller is null || controllerTarget is null)
        {
            return;
        }

        controller.RemoveSystemBackdropTarget(controllerTarget);
        controller.Dispose();
        controller = new DesktopAcrylicController();
        controller.SetSystemBackdropConfiguration(configuration);
        controller.AddSystemBackdropTarget(controllerTarget);
        Apply(force: true);
        // Windows' own switch and Settings › App › Theme both arrive here (the panel's content changed its actual theme). In
        // high contrast the controller draws the contrast theme's background whatever the theme says (WinUI's configuration
        // read "light" under High Contrast Black in the test desktop), so the line names high contrast alone then.
        string look = configuration.IsHighContrast ? "high contrast" : appliedLight == true ? "the light theme" : "the dark theme";
        AppLog.Info($"The panel's glass was set up again for {look}: the theme changed while BetterClipboard ran.");
    }

    /// <summary>
    /// Gives the controller the level's look in the panel's theme: Windows' own values at the default level, all four
    /// values otherwise (setting one makes the controller stop following the theme for every value, so the colors are set
    /// too; <see cref="PanelBackdrop"/>'s remarks).
    /// </summary>
    /// <param name="force">Apply even when the theme did not change (a new level, a new controller).</param>
    private void Apply(bool force)
    {
        if (controller is null)
        {
            return;
        }

        bool light = IsLight();
        if (!force && appliedLight == light)
        {
            return;
        }

        appliedLight = light;
        if (PanelBackdrop.IsWindowsLook(level))
        {
            controller.ResetProperties();
            return;
        }

        var look = PanelBackdrop.LookFor(level, light);
        controller.TintColor = ToColor(look.TintColor);
        controller.FallbackColor = ToColor(look.FallbackColor);
        controller.TintOpacity = look.TintOpacity;
        controller.LuminosityOpacity = look.LuminosityOpacity;
    }

    /// <summary>
    /// Whether the panel is light now: the mirrored theme when it names one; otherwise (the configuration leaves it to the
    /// system) the window content's actual theme, else Windows' app mode as the application reads it.
    /// </summary>
    /// <returns><see langword="true"/> for the light theme.</returns>
    private bool IsLight() => configuration.Theme switch
    {
        SystemBackdropTheme.Light => true,
        SystemBackdropTheme.Dark => false,
        _ => root?.Content is FrameworkElement content
            ? content.ActualTheme == ElementTheme.Light
            : Application.Current.RequestedTheme == ApplicationTheme.Light,
    };

    /// <summary>An ARGB value (<c>0xAARRGGBB</c>) as a Windows color.</summary>
    /// <param name="argb">The value.</param>
    /// <returns>The color.</returns>
    private static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
