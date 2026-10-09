using BetterClipboard.Windows.Input;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for "any key" shortcuts: the whole key vocabulary of <see cref="HotkeyGesture"/> (names, aliases, the <c>0xNN</c>
/// form, the kinds), the safety rules with their reasons, and the decisions of Settings' shortcut recorder
/// (<see cref="HotkeyCapture"/>).
/// </summary>
public sealed class HotkeyRecorderTests
{
    private const uint K = 0x4B;
    private const uint V = 0x56;
    private const uint E = 0x45;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_F9 = 0x78;
    private const uint VK_TAB = 0x09;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

    /// <summary>
    /// Every usable virtual-key code, with Win+Shift held, formats to a text that parses back to the same gesture — named or
    /// written <c>0xNN</c> — so any key a person presses can be saved and read back. (Win+Shift because no rule refuses it
    /// for any key; Ctrl+Alt+Delete, for one, is refused.)
    /// </summary>
    [Fact]
    public void EveryKey_RoundTripsThroughItsText()
    {
        int usable = 0;
        for (uint vk = 0x01; vk <= 0xFE; vk++)
        {
            if (HotkeyGesture.KindOf(vk) == HotkeyKeyKind.Unusable)
            {
                continue;
            }

            usable++;
            var modifiers = HotkeyModifiers.Win | HotkeyModifiers.Shift;
            Assert.True(HotkeyGesture.TryCreate(modifiers, vk, out var gesture, out var problem), $"0x{vk:X2}: {problem}");
            var text = gesture.ToString();
            Assert.True(HotkeyGesture.TryParse(text, out var again, out var parseProblem), $"0x{vk:X2} as \"{text}\": {parseProblem}");
            Assert.Equal(gesture, again);
        }

        // 254 codes minus the eleven modifiers, five mouse buttons and four codes that are no key press.
        Assert.Equal(254 - 11 - 5 - 4, usable);
    }

    /// <summary>Names, aliases and spellings of keys the old parser did not know all reach the right code.</summary>
    /// <param name="text">What a person types.</param>
    /// <param name="canonical">The saved form.</param>
    [Theory]
    [InlineData("Ctrl+Alt+Num 5", "Ctrl+Alt+Num5")]
    [InlineData("ctrl+alt+numpad5", "Ctrl+Alt+Num5")]
    [InlineData("Ctrl+Shift+NumPlus", "Ctrl+Shift+NumPlus")]
    [InlineData("Ctrl+Num*", "Ctrl+NumMultiply")]
    [InlineData("Win+Shift+F23", "Shift+Win+F23")]
    [InlineData("Ctrl+Page Up", "Ctrl+PageUp")]
    [InlineData("alt+page-down", "Alt+PageDown")]
    [InlineData("Ctrl+PrtScn", "Ctrl+PrintScreen")]
    [InlineData("PrintScreen", "PrintScreen")]
    [InlineData("Pause", "Pause")]
    [InlineData("Ctrl+Break", "Ctrl+Break")]
    [InlineData("ScrollLock", "ScrollLock")]
    [InlineData("Caps Lock", "CapsLock")]
    [InlineData("Apps", "Apps")]
    [InlineData("ContextMenu", "Apps")]
    [InlineData("VolumeUp", "VolumeUp")]
    [InlineData("Media Play Pause", "MediaPlayPause")]
    [InlineData("Ctrl+Backspace", "Ctrl+Backspace")]
    [InlineData("Alt+Enter", "Alt+Enter")]
    [InlineData("Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    [InlineData("Ctrl+Plus", "Ctrl+=")]
    [InlineData("Ctrl+Minus", "Ctrl+-")]
    [InlineData("Ctrl+Alt+Oem102", "Ctrl+Alt+Oem102")]
    [InlineData("Win+VK_SNAPSHOT", "Win+PrintScreen")]
    [InlineData("Ctrl+VK_F13", "Ctrl+F13")]
    [InlineData("LCtrl+RAlt+K", "Ctrl+Alt+K")]
    [InlineData("Ctrl+Alt+0x97", "Ctrl+Alt+0x97")]
    [InlineData("0x97", "0x97")]
    [InlineData("Ctrl+Shift+0x56", "Ctrl+Shift+V")]
    [InlineData("Win+0x56", "Win+V")]
    public void Names_ParseToTheRightKey(string text, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture, out var problem), problem);
        Assert.Equal(canonical, gesture.ToString());
    }

    /// <summary>
    /// The gestures a global shortcut would break are refused with a reason that names the problem: keys that type or edit
    /// without Ctrl, Alt or Win, the keys Windows keeps, and the clipboard's own keys (BetterClipboard pastes with Ctrl+V).
    /// </summary>
    /// <param name="text">The refused shortcut.</param>
    /// <param name="reasonPart">A part of the reason.</param>
    [Theory]
    [InlineData("V", "alone would stop")]
    [InlineData("Shift+V", "with Shift")]
    [InlineData("Space", "alone would stop")]
    [InlineData("Num5", "alone would stop")]
    [InlineData("Shift+Insert", "with Shift")]
    [InlineData("Tab", "alone would stop")]
    [InlineData("Kana", "alone would stop")]
    [InlineData("Ctrl+V", "pastes")]
    [InlineData("Ctrl+C", "copies")]
    [InlineData("Ctrl+Insert", "copies")]
    [InlineData("Ctrl+X", "cuts")]
    [InlineData("Ctrl+Alt+Delete", "always goes to Windows")]
    [InlineData("Ctrl+Alt+Shift+Del", "always goes to Windows")]
    [InlineData("Win+L", "locks the PC")]
    [InlineData("Alt+Tab", "switches between windows")]
    [InlineData("Ctrl+Alt+Tab", "switches between windows")]
    [InlineData("Alt+Esc", "switches between windows")]
    [InlineData("Ctrl+Esc", "opens Start")]
    [InlineData("Ctrl+Shift+Esc", "Task Manager")]
    [InlineData("Alt+F4", "closes windows")]
    [InlineData("Ctrl+Shift", "is a modifier")]
    [InlineData("Ctrl+Win", "is a modifier")]
    [InlineData("Ctrl+LeftWin", "is a modifier")]
    [InlineData("Ctrl+0xE8", "BetterClipboard sends that code itself")]
    [InlineData("Ctrl+0x01", "mouse button")]
    [InlineData("Ctrl+NoSuchKey", "is not a key name")]
    [InlineData("Hyper+V", "is not a modifier")]
    [InlineData("Ctrl+Ctrl+V", "twice")]
    [InlineData("F25", "is not a key name")]
    [InlineData("  ", "Type a shortcut")]
    [InlineData("+", "Plus")]
    public void UnsafeOrMalformed_IsRefusedWithAReason(string text, string reasonPart)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture, out var problem));
        Assert.Equal(default, gesture);
        Assert.Contains(reasonPart, problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Keys that do nothing in text may stand alone or with Shift only; the same keys with Ctrl, Alt or Win always may.
    /// Win+Tab and Win+Esc stay possible (Explorer's, like Win+V, so they can be taken over).
    /// </summary>
    /// <param name="text">An accepted shortcut.</param>
    [Theory]
    [InlineData("F9")]
    [InlineData("Shift+F5")]
    [InlineData("Pause")]
    [InlineData("Shift+Pause")]
    [InlineData("MediaPlayPause")]
    [InlineData("BrowserHome")]
    [InlineData("LaunchApp2")]
    [InlineData("NumLock")]
    [InlineData("Ctrl+Shift+V")]
    [InlineData("Win+Tab")]
    [InlineData("Win+Esc")]
    [InlineData("Ctrl+Alt+V")]
    [InlineData("Alt+Insert")]
    [InlineData("Ctrl+`")]
    public void HarmlessKeys_AreAccepted(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out _, out var problem), problem);
    }

    /// <summary>The kinds behind the rules: letters type, arrows edit, F keys and media keys stand alone, modifiers are no keys.</summary>
    [Fact]
    public void Kinds_SortTheKeys()
    {
        Assert.Equal(HotkeyKeyKind.Typing, HotkeyGesture.KindOf(V));
        Assert.Equal(HotkeyKeyKind.Typing, HotkeyGesture.KindOf(0x65)); // Num5
        Assert.Equal(HotkeyKeyKind.Editing, HotkeyGesture.KindOf(0x25)); // Left
        Assert.Equal(HotkeyKeyKind.InputMethod, HotkeyGesture.KindOf(0x19)); // Kanji
        Assert.Equal(HotkeyKeyKind.InputMethod, HotkeyGesture.KindOf(0xF3)); // Hankaku/Zenkaku (VK_OEM_AUTO)
        Assert.Equal(HotkeyKeyKind.Function, HotkeyGesture.KindOf(VK_F9));
        Assert.Equal(HotkeyKeyKind.Standalone, HotkeyGesture.KindOf(VK_MEDIA_PLAY_PAUSE));
        Assert.Equal(HotkeyKeyKind.Standalone, HotkeyGesture.KindOf(0x97)); // unassigned: a macro key
        Assert.Equal(HotkeyKeyKind.Unusable, HotkeyGesture.KindOf(VK_LCONTROL));
        Assert.Equal(HotkeyKeyKind.Unusable, HotkeyGesture.KindOf(0xE8));
        Assert.Equal(HotkeyKeyKind.Unusable, HotkeyGesture.KindOf(0));
        Assert.Equal(HotkeyKeyKind.Unusable, HotkeyGesture.KindOf(0x1FF));
        Assert.True(HotkeyGesture.IsModifierKey(VK_LWIN));
        Assert.True(HotkeyGesture.IsModifierKey(0x10));
        Assert.False(HotkeyGesture.IsModifierKey(V));
        Assert.Equal("Ctrl+Alt+Shift+Win+", HotkeyGesture.FormatModifiers(HotkeyModifiers.Win | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control));
        Assert.Equal("0x97", HotkeyGesture.KeyName(0x97));
    }

    /// <summary>
    /// The recorder takes a key pressed with Ctrl, Alt or Win — even Win+E, which Explorer owns — swallows its repeats and
    /// key-up, and never touches the modifiers, which stay with the system.
    /// </summary>
    [Fact]
    public void Capture_TakesAShortcut_AndItsWholeKeystroke()
    {
        var capture = new HotkeyCapture();
        var ctrlShift = HotkeyModifiers.Control | HotkeyModifiers.Shift;

        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(VK_LCONTROL, true, false, HotkeyModifiers.None, true, out var captured));
        Assert.Null(captured);
        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(K, true, false, ctrlShift, true, out captured));
        Assert.Equal(new HotkeyGesture(ctrlShift, K), captured);
        Assert.True(capture.IsHoldingKeys);
        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(K, true, false, ctrlShift, true, out captured));
        Assert.Null(captured);
        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(K, false, false, ctrlShift, true, out _));
        Assert.False(capture.IsHoldingKeys);
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(VK_LCONTROL, false, false, HotkeyModifiers.None, true, out _));

        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(E, true, false, HotkeyModifiers.Win, true, out captured));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Win, E), captured);
    }

    /// <summary>
    /// Keys without Ctrl, Alt or Win type into the box (letters, Shift+letter, Backspace, Enter, Tab), while a key that works
    /// alone (F9, a media key) is recorded on its own.
    /// </summary>
    [Fact]
    public void Capture_LetsTypingThrough_AndTakesStandaloneKeys()
    {
        var capture = new HotkeyCapture();

        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(V, true, false, HotkeyModifiers.None, true, out var captured));
        Assert.Null(captured);
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(V, false, false, HotkeyModifiers.None, true, out _));
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(V, true, false, HotkeyModifiers.Shift, true, out _));
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(0x08, true, false, HotkeyModifiers.None, true, out _)); // Backspace
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(0x0D, true, false, HotkeyModifiers.None, true, out _)); // Enter adds
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(VK_TAB, true, false, HotkeyModifiers.Shift, true, out _)); // leaves the box

        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(VK_F9, true, false, HotkeyModifiers.None, true, out captured));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.None, VK_F9), captured);
        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(VK_F9, false, false, HotkeyModifiers.None, true, out _));

        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(VK_MEDIA_PLAY_PAUSE, true, false, HotkeyModifiers.Shift, true, out captured));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Shift, VK_MEDIA_PLAY_PAUSE), captured);
    }

    /// <summary>
    /// Gestures no shortcut may have keep their meaning in the box: Alt+Tab and Ctrl+Esc reach Windows, Ctrl+C / Ctrl+V copy
    /// and paste the box's text. Keys BetterClipboard injected itself (its mask key) always pass.
    /// </summary>
    [Fact]
    public void Capture_LeavesWindowsAndClipboardKeysAlone()
    {
        var capture = new HotkeyCapture();

        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(VK_TAB, true, false, HotkeyModifiers.Alt, true, out var captured));
        Assert.Null(captured);
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(VK_ESCAPE, true, false, HotkeyModifiers.Control, true, out _));
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(V, true, false, HotkeyModifiers.Control, true, out _));
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(0x43, true, false, HotkeyModifiers.Control, true, out _));
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(0xE8, true, isOwnInjection: true, HotkeyModifiers.Win, true, out _));
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(0xE8, true, isOwnInjection: false, HotkeyModifiers.Win, true, out _));
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(K, true, isOwnInjection: true, HotkeyModifiers.Control, true, out _));
        Assert.False(capture.IsHoldingKeys);
    }

    /// <summary>
    /// While not listening (another app in front, or stopping) nothing new is taken, but the key-up of a key already
    /// swallowed still is — and a key-up whose key-down was never swallowed always passes. After a reset (the hook was
    /// removed), a stale key-up is not swallowed.
    /// </summary>
    [Fact]
    public void Capture_NotListening_OnlyFinishesSwallowedKeys()
    {
        var capture = new HotkeyCapture();

        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(K, true, false, HotkeyModifiers.Control, listening: false, out var captured));
        Assert.Null(captured);
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(K, false, false, HotkeyModifiers.Control, listening: false, out _));

        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(K, true, false, HotkeyModifiers.Control, listening: true, out _));
        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(K, true, false, HotkeyModifiers.Control, listening: false, out _));
        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(K, false, false, HotkeyModifiers.Control, listening: false, out _));
        Assert.False(capture.IsHoldingKeys);

        Assert.Equal(RecorderDecision.Swallow, capture.OnKey(V, true, false, HotkeyModifiers.Win, listening: true, out _));
        capture.Reset();
        Assert.False(capture.IsHoldingKeys);
        Assert.Equal(RecorderDecision.PassThrough, capture.OnKey(V, false, false, HotkeyModifiers.None, listening: true, out _));
    }

    /// <summary>
    /// The recorder refuses a window handle of 0 (it would never know when to take keys), and its hook is installed only
    /// between start and stop — creating and disposing one hooks nothing.
    /// </summary>
    [Fact]
    public void Recorder_NeedsAnOwnerWindow_AndStartsIdle()
    {
        Assert.Throws<ArgumentException>(() => new HotkeyRecorder(0));
        using var recorder = new HotkeyRecorder(0x1234);
        Assert.False(recorder.IsListening);
    }
}
