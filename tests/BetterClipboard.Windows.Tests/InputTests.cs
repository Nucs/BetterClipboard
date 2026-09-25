using BetterClipboard.Core.Settings;
using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for shortcut parsing, the Win+V interception state machine and flyout placement.
/// </summary>
public sealed class InputTests
{
    /// <summary>Friendly spellings parse and format canonically.</summary>
    /// <param name="text">User input.</param>
    /// <param name="canonical">Expected canonical form.</param>
    [Theory]
    [InlineData("Win+V", "Win+V")]
    [InlineData("  ctrl + shift + v ", "Ctrl+Shift+V")]
    [InlineData("Windows+Alt+V", "Alt+Win+V")]
    [InlineData("Ctrl+`", "Ctrl+`")]
    [InlineData("Alt+Insert", "Alt+Insert")]
    [InlineData("F9", "F9")]
    [InlineData("Ctrl+Alt+F12", "Ctrl+Alt+F12")]
    [InlineData("control+1", "Ctrl+1")]
    public void Hotkey_ParsesAndFormats(string text, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(canonical, gesture.ToString());
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    /// <summary>Unsafe or malformed shortcuts are rejected (bare letters would become untypable system-wide).</summary>
    /// <param name="text">User input.</param>
    [Theory]
    [InlineData("V")]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+V")]
    [InlineData("Hyper+V")]
    [InlineData("Ctrl+NoSuchKey")]
    [InlineData("F25")]
    public void Hotkey_RejectsInvalid(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    /// <summary>Virtual keys and modifier bits match Win32 (they are passed straight to RegisterHotKey).</summary>
    [Fact]
    public void Hotkey_MapsToWin32Values()
    {
        HotkeyGesture.TryParse("Win+V", out var winV);
        Assert.Equal(0x56u, winV.VirtualKey);
        Assert.Equal(0x8u, (uint)winV.Modifiers);
        Assert.True(winV.UsesWinKey);
        HotkeyGesture.TryParse("Ctrl+`", out var backtick);
        Assert.Equal(0xC0u, backtick.VirtualKey);
    }

    /// <summary>Win+V fires once, its auto-repeats and key-up are swallowed, then it re-arms.</summary>
    [Fact]
    public void Interceptor_SwallowsWholeKeystroke()
    {
        var interceptor = new HotkeyInterceptor(new HotkeyGesture(HotkeyModifiers.Win, 0x56));
        Assert.Equal(InterceptDecision.SwallowAndFire, interceptor.OnKey(0x56, true, false, HotkeyModifiers.Win));
        Assert.Equal(InterceptDecision.Swallow, interceptor.OnKey(0x56, true, false, HotkeyModifiers.Win));
        Assert.Equal(InterceptDecision.Swallow, interceptor.OnKey(0x56, false, false, HotkeyModifiers.Win));
        Assert.Equal(InterceptDecision.SwallowAndFire, interceptor.OnKey(0x56, true, false, HotkeyModifiers.Win));
    }

    /// <summary>Only the exact chord fires: Win+Shift+V (PowerToys) and plain V pass through untouched.</summary>
    [Fact]
    public void Interceptor_RequiresExactModifiers()
    {
        var interceptor = new HotkeyInterceptor(new HotkeyGesture(HotkeyModifiers.Win, 0x56));
        Assert.Equal(InterceptDecision.PassThrough, interceptor.OnKey(0x56, true, false, HotkeyModifiers.Win | HotkeyModifiers.Shift));
        Assert.Equal(InterceptDecision.PassThrough, interceptor.OnKey(0x56, false, false, HotkeyModifiers.Win | HotkeyModifiers.Shift));
        Assert.Equal(InterceptDecision.PassThrough, interceptor.OnKey(0x56, true, false, HotkeyModifiers.None));
        Assert.Equal(InterceptDecision.PassThrough, interceptor.OnKey(0x43, true, false, HotkeyModifiers.Win));
    }

    /// <summary>Our own synthetic keys pass; keys injected by other tools (remappers) are intercepted.</summary>
    [Fact]
    public void Interceptor_IgnoresOnlyOwnInjection()
    {
        var interceptor = new HotkeyInterceptor(new HotkeyGesture(HotkeyModifiers.Win, 0x56));
        Assert.Equal(InterceptDecision.PassThrough, interceptor.OnKey(0x56, true, isOwnInjection: true, HotkeyModifiers.Win));
        Assert.Equal(InterceptDecision.SwallowAndFire, interceptor.OnKey(0x56, true, isOwnInjection: false, HotkeyModifiers.Win));
    }

    /// <summary>Default placement: just below the caret, caret slightly inside the left edge.</summary>
    [Fact]
    public void Positioner_BelowCaret()
    {
        var context = Context(caret: new ScreenRect(500, 300, 502, 320));
        var rect = FlyoutPositioner.Compute(FlyoutPlacement.NearCaret, context, Work, 400, 560, 8);
        Assert.Equal(new ScreenRect(484, 328, 884, 888), rect);
    }

    /// <summary>Near the bottom it opens upwards so the caret line stays visible.</summary>
    [Fact]
    public void Positioner_FlipsAboveNearBottom()
    {
        var context = Context(caret: new ScreenRect(500, 900, 502, 920));
        var rect = FlyoutPositioner.Compute(FlyoutPlacement.NearCaret, context, Work, 400, 560, 8);
        Assert.Equal(900 - 8, rect.Bottom);
    }

    /// <summary>It never leaves the work area (right edge / taskbar).</summary>
    [Fact]
    public void Positioner_ClampsToWorkArea()
    {
        var context = Context(caret: new ScreenRect(1900, 1000, 1902, 1020));
        var rect = FlyoutPositioner.Compute(FlyoutPlacement.NearCaret, context, Work, 400, 560, 8);
        Assert.True(rect.Right <= Work.Right && rect.Bottom <= Work.Bottom && rect.Left >= Work.Left && rect.Top >= Work.Top);
    }

    /// <summary>Without a caret it anchors at the mouse cursor; center mode centers in the work area.</summary>
    [Fact]
    public void Positioner_CursorFallbackAndCenter()
    {
        var context = Context(caret: null, cursorX: 100, cursorY: 100);
        Assert.Equal(new ScreenRect(84, 109, 484, 669), FlyoutPositioner.Compute(FlyoutPlacement.NearCaret, context, Work, 400, 560, 8));
        var centered = FlyoutPositioner.Compute(FlyoutPlacement.CenterScreen, context, Work, 400, 560, 8);
        Assert.Equal(new ScreenRect(760, 236, 1160, 796), centered);
    }

    /// <summary>On a monitor smaller than the flyout it shrinks to fit instead of spilling off-screen.</summary>
    [Fact]
    public void Positioner_ShrinksOnTinyScreens()
    {
        var tiny = new ScreenRect(0, 0, 300, 400);
        var rect = FlyoutPositioner.Compute(FlyoutPlacement.NearCaret, Context(null, 10, 10), tiny, 400, 560, 8);
        Assert.Equal(new ScreenRect(0, 0, 300, 400), rect);
    }

    /// <summary>1920×1032 work area (1080p minus taskbar).</summary>
    private static readonly ScreenRect Work = new(0, 0, 1920, 1032);

    /// <summary>Builds a context.</summary>
    /// <param name="caret">Caret rect, if any.</param>
    /// <param name="cursorX">Cursor X.</param>
    /// <param name="cursorY">Cursor Y.</param>
    /// <returns>The context.</returns>
    private static ForegroundContext Context(ScreenRect? caret, int cursorX = 0, int cursorY = 0) =>
        new(0x1234, 42, caret, new ScreenPoint(cursorX, cursorY));
}
