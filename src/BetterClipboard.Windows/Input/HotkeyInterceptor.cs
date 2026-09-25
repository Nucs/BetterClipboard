namespace BetterClipboard.Windows.Input;

/// <summary>What the low-level hook should do with one keyboard event.</summary>
public enum InterceptDecision
{
    /// <summary>Let the event through untouched.</summary>
    PassThrough,

    /// <summary>Swallow the event (key-up or auto-repeat of an intercepted gesture).</summary>
    Swallow,

    /// <summary>Swallow the event and trigger the hotkey action.</summary>
    SwallowAndFire,
}

/// <summary>
/// Pure decision logic for intercepting one gesture (e.g. <c>Win+V</c>) from a low-level keyboard hook,
/// kept free of Win32 calls so it is unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// Only the <i>main key</i> of the gesture is swallowed — never the modifiers — so the rest of the system
/// keeps a consistent modifier state. Once a key-down was swallowed, its auto-repeats and its key-up are
/// swallowed too (an orphan key-up would reach the focused app as a stray "V released").
/// </para>
/// <para>
/// Modifiers must match <b>exactly</b>: intercepting <c>Win+V</c> must not steal <c>Win+Shift+V</c>
/// (PowerToys Advanced Paste) or <c>Win+Ctrl+V</c> (Windows' sound output flyout).
/// Events BetterClipboard injected itself (mask key, Ctrl+V paste) always pass through; keys injected
/// by <i>other</i> tools (remappers, macro tools) are treated like physical keys on purpose.
/// </para>
/// </remarks>
public sealed class HotkeyInterceptor
{
    private bool swallowing;

    /// <summary>
    /// Creates an interceptor for <paramref name="gesture"/>.
    /// </summary>
    /// <param name="gesture">The gesture to intercept.</param>
    public HotkeyInterceptor(HotkeyGesture gesture) => Gesture = gesture;

    /// <summary>The intercepted gesture.</summary>
    public HotkeyGesture Gesture { get; }

    /// <summary>
    /// Decides the fate of one keyboard event.
    /// </summary>
    /// <param name="virtualKey">Virtual-key code of the event.</param>
    /// <param name="isKeyDown">Key-down (including auto-repeat) vs key-up.</param>
    /// <param name="isOwnInjection">Event was synthesized by BetterClipboard itself (<see cref="KeyboardInput.OwnInjectionMarker"/>).</param>
    /// <param name="heldModifiers">Modifiers physically/logically down at the time of the event.</param>
    /// <returns>The decision.</returns>
    public InterceptDecision OnKey(uint virtualKey, bool isKeyDown, bool isOwnInjection, HotkeyModifiers heldModifiers)
    {
        if (isOwnInjection || virtualKey != Gesture.VirtualKey)
        {
            return InterceptDecision.PassThrough;
        }

        if (!isKeyDown)
        {
            if (swallowing)
            {
                swallowing = false;
                return InterceptDecision.Swallow;
            }

            return InterceptDecision.PassThrough;
        }

        if (swallowing)
        {
            return InterceptDecision.Swallow; // auto-repeat while held
        }

        if (heldModifiers != Gesture.Modifiers)
        {
            return InterceptDecision.PassThrough;
        }

        swallowing = true;
        return InterceptDecision.SwallowAndFire;
    }
}
