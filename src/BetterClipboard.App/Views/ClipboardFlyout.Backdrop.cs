using BetterClipboard.App.Controls;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.App.Views;

/// <summary>
/// The panel's frosted glass and how see-through it is: Settings › App › Background transparency (CLAUDE.md §2.58).
/// </summary>
/// <remarks>
/// <para>
/// <b>The request</b> (2026-11-20): "Add to settings 'Background Transparency' slider, right now it is slightly blur
/// transparent. so it always stays blur but theres either more blurred or less blurred." Windows' acrylic has a fixed blur,
/// so the level sets how strongly its frost covers the blurred screen behind the panel (<see cref="PanelBackdrop.LookFor"/>);
/// the blur itself stays at every level, and 50 is Windows' own look.
/// </para>
/// <para>
/// <b>Live.</b> A settings change applies the level at once (<see cref="ApplyHeaderButtons"/> calls
/// <see cref="ApplyBackdropLevel"/>), and while the slider is dragged Settings previews each step here before the file is
/// written (<see cref="PreviewBackdropLevel"/>): a pinned panel on screen beside Settings changes as the thumb moves, and a
/// hidden panel shows the new look at the next summon. The glass also stays while a pinned panel waits behind another app
/// (<see cref="AdjustableAcrylicBackdrop"/>'s remarks).
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>The panel's backdrop, set once in <c>ConfigureChrome</c>; serves only this window.</summary>
    private AdjustableAcrylicBackdrop? backdrop;

    /// <summary>
    /// Shows a level while Settings' slider is being dragged, before it is saved (the saved level arrives through
    /// <see cref="ApplyBackdropLevel"/> once the drag settles). Cheap and idempotent; UI thread only.
    /// </summary>
    /// <param name="level">The level, <see cref="PanelBackdrop.MinLevel"/>..<see cref="PanelBackdrop.MaxLevel"/> (clamped).</param>
    public void PreviewBackdropLevel(int level) => ApplyBackdropLevel(level);

    /// <summary>
    /// Gives the panel's glass a level (<see cref="Core.Settings.AppSettings.BackgroundTransparency"/>): applied to Windows'
    /// acrylic at once when the window has drawn its first frame, from then on otherwise. Unchanged levels change nothing.
    /// </summary>
    /// <param name="level">The level, <see cref="PanelBackdrop.MinLevel"/>..<see cref="PanelBackdrop.MaxLevel"/> (clamped).</param>
    private void ApplyBackdropLevel(int level)
    {
        if (backdrop is not null)
        {
            backdrop.Level = level;
        }
    }
}
