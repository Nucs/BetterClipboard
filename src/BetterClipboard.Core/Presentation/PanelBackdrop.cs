namespace BetterClipboard.Core.Presentation;

/// <summary>
/// One look of Windows' desktop acrylic (<c>DesktopAcrylicController</c>): what the panel's backdrop is told to draw for a
/// level of Settings › Panel › Background transparency (<see cref="PanelBackdrop"/>).
/// </summary>
/// <remarks>
/// The acrylic is Windows' own frosted glass: the screen behind the window, blurred, then a luminosity layer that evens out
/// its brightness, then a tint, then a fine noise. The blur itself is fixed (no API sets its radius); the two opacities
/// decide how much of the blurred background shows through the frost. Colors are ARGB (<c>0xAARRGGBB</c>), so Core needs no
/// UI type.
/// </remarks>
/// <param name="TintColor">The tint's color (ARGB): the theme's own, at every level.</param>
/// <param name="TintOpacity">How much of the tint covers the blurred background, 0 (none) to 1 (solid tint).</param>
/// <param name="LuminosityOpacity">
/// How much of the luminosity layer evens out the blurred background's brightness, 0 (the background's own light and dark
/// shapes show) to 1 (one even brightness, only the background's colors show).
/// </param>
/// <param name="FallbackColor">
/// The solid color (ARGB) Windows draws instead of the glass when it cannot draw it: transparency effects off, battery saver,
/// a remote session, no graphics acceleration. The theme's own, at every level.
/// </param>
public readonly record struct AcrylicLook(uint TintColor, float TintOpacity, float LuminosityOpacity, uint FallbackColor);

/// <summary>
/// The rules of the panel's "Background transparency" (Settings › Panel, CLAUDE.md §2.58): a level from 0 to 100, and the
/// acrylic look each level gives in the light and the dark theme.
/// </summary>
/// <remarks>
/// <para>
/// <b>The request</b> (2026-11-20): "Add to settings 'Background Transparency' slider, right now it is slightly blur
/// transparent. so it always stays blur but theres either more blurred or less blurred." Windows' acrylic has no blur radius
/// to set, so the slider changes what makes the glass look more or less blurred: how strongly its frost (the luminosity
/// layer and the tint) covers the blurred background. The blur stays at every level: 0 is the most frosted glass, never a
/// solid panel, and 100 the clearest, never a plain window.
/// </para>
/// <para>
/// <b>The default, <see cref="DefaultLevel"/>, is Windows' own look</b>, measured with <c>tools/probes/backdrop_lab</c> on
/// Windows 11 25H2 (26200): the "Default" acrylic, <see cref="WindowsDark"/> and <see cref="WindowsLight"/>. At that level
/// the backdrop asks Windows for its own values (<c>ResetProperties</c>) instead of setting them, so the panel looks exactly
/// as it did before the slider existed, also on a later Windows whose acrylic differs.
/// </para>
/// <para>
/// <b>Every other level sets all four values</b>, the theme's colors included: the lab found that once a single value is set,
/// the controller stops following the theme for all of them, and a dark panel got the light theme's tint color.
/// </para>
/// <para>
/// <b>What the level changes: the luminosity layer.</b> The lab measured, over a striped pattern and over other
/// windows, in both themes, how much of what lies behind shows through the glass: about 68 % with no luminosity layer,
/// falling in a straight line with its opacity (51 % at 0.25, 34 % at 0.5, 17 % at 0.75, 4 % at Windows' dark 0.96) to
/// next to nothing at 1 (0.0 to 0.2 %), where the glass is a solid color to the eye. The tint opacity changed nothing
/// measurable at any luminosity tried. So the level drives the luminosity opacity, in a straight line on each side of
/// the default (equal steps show equally more or less), from 0.99 at <see cref="MinLevel"/> (the most frosted, still
/// not solid) to 0.1 at <see cref="MaxLevel"/> (the clearest: about 60 % of the background shows, and a little frost
/// keeps the panel's text readable). The tint keeps Windows' value on the frosted side and fades to none on the clear
/// side, so the clearest glass shows the background's own colors.
/// </para>
/// </remarks>
public static class PanelBackdrop
{
    /// <summary>The most frosted level: the glass covers the most of the background, and still shows it, blurred.</summary>
    public const int MinLevel = 0;

    /// <summary>The clearest level: the most of the background shows, still blurred.</summary>
    public const int MaxLevel = 100;

    /// <summary>The default level: Windows' own acrylic, the panel's look before the slider existed.</summary>
    public const int DefaultLevel = 50;

    /// <summary>How far one press of an arrow key moves the slider, and the steps a drag snaps to.</summary>
    public const int Step = 5;

    /// <summary>
    /// Windows' own "Default" acrylic in the dark theme, as the controller reports it (measured 2026-11-20): a #2C2C2C tint at
    /// 0.15, luminosity 0.96, fallback #2C2C2C.
    /// </summary>
    public static AcrylicLook WindowsDark { get; } = new(0xFF2C2C2C, 0.15f, 0.96f, 0xFF2C2C2C);

    /// <summary>
    /// Windows' own "Default" acrylic in the light theme, as the controller reports it (measured 2026-11-20): a #FCFCFC tint at
    /// 0, luminosity 0.85, fallback #F9F9F9.
    /// </summary>
    public static AcrylicLook WindowsLight { get; } = new(0xFFFCFCFC, 0f, 0.85f, 0xFFF9F9F9);

    /// <summary>
    /// The luminosity opacity of the most frosted level, in both themes: the glass hides nearly all of the background and is
    /// still glass. At 1 the lab measured a solid color (nothing showed through), the one thing the slider must never give.
    /// </summary>
    private const float FrostedLuminosity = 0.99f;

    /// <summary>
    /// The luminosity opacity of the clearest level, in both themes: about 60 % of the background shows through, and the bit
    /// of frost left keeps the panel's text readable over a busy background (0 would leave the bare blurred background).
    /// </summary>
    private const float ClearestLuminosity = 0.1f;

    /// <summary>
    /// The dark theme's stops, from the most frosted level to the clearest, the default level's being Windows' own values.
    /// Levels in between run in straight lines between their neighbors, so each half of the slider changes what shows
    /// through in equal steps (it falls in a straight line with the luminosity opacity).
    /// </summary>
    private static readonly (int Level, float Tint, float Luminosity)[] DarkStops =
    [
        (MinLevel, WindowsDark.TintOpacity, FrostedLuminosity),
        (DefaultLevel, WindowsDark.TintOpacity, WindowsDark.LuminosityOpacity),
        (MaxLevel, 0f, ClearestLuminosity),
    ];

    /// <summary>The light theme's stops, as <see cref="DarkStops"/>; Windows' light tint is none already.</summary>
    private static readonly (int Level, float Tint, float Luminosity)[] LightStops =
    [
        (MinLevel, WindowsLight.TintOpacity, FrostedLuminosity),
        (DefaultLevel, WindowsLight.TintOpacity, WindowsLight.LuminosityOpacity),
        (MaxLevel, 0f, ClearestLuminosity),
    ];

    /// <summary>
    /// A stored level within <see cref="MinLevel"/>..<see cref="MaxLevel"/>: a hand-edited or out-of-range value still gives a
    /// panel with glass.
    /// </summary>
    /// <param name="level">The stored level.</param>
    /// <returns>The level, clamped.</returns>
    public static int Normalize(int level) => Math.Clamp(level, MinLevel, MaxLevel);

    /// <summary>
    /// Whether a level is Windows' own look: the backdrop then lets Windows choose every value for the theme
    /// (<c>ResetProperties</c>) instead of setting them.
    /// </summary>
    /// <param name="level">The level (clamped first).</param>
    /// <returns><see langword="true"/> for <see cref="DefaultLevel"/>.</returns>
    public static bool IsWindowsLook(int level) => Normalize(level) == DefaultLevel;

    /// <summary>
    /// The acrylic look of a level in a theme: the theme's colors, and the two opacities on the straight line between the
    /// level's neighboring stops. At <see cref="DefaultLevel"/> it equals Windows' own values (<see cref="WindowsDark"/>,
    /// <see cref="WindowsLight"/>).
    /// </summary>
    /// <param name="level">The level; clamped to <see cref="MinLevel"/>..<see cref="MaxLevel"/> first.</param>
    /// <param name="light">Whether the panel is in the light theme (else the dark one).</param>
    /// <returns>The look; its opacities always within 0..1.</returns>
    public static AcrylicLook LookFor(int level, bool light)
    {
        var stops = light ? LightStops : DarkStops;
        var windows = light ? WindowsLight : WindowsDark;
        int clamped = Normalize(level);

        // The last stop at or below the level; the stops start at MinLevel, so there always is one.
        int i = 0;
        while (i < stops.Length - 1 && stops[i + 1].Level <= clamped)
        {
            i++;
        }

        if (i == stops.Length - 1 || stops[i].Level == clamped)
        {
            return windows with { TintOpacity = stops[i].Tint, LuminosityOpacity = stops[i].Luminosity };
        }

        var (from, to) = (stops[i], stops[i + 1]);
        float t = (float)(clamped - from.Level) / (to.Level - from.Level);
        return windows with
        {
            TintOpacity = Math.Clamp(from.Tint + ((to.Tint - from.Tint) * t), 0f, 1f),
            LuminosityOpacity = Math.Clamp(from.Luminosity + ((to.Luminosity - from.Luminosity) * t), 0f, 1f),
        };
    }
}
