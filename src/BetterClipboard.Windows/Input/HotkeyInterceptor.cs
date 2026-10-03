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

/// <summary>
/// The interceptors of every shortcut the keyboard hook takes over, asked in turn — one hook serves them all, however
/// many shortcuts another app or Windows owns.
/// </summary>
/// <remarks>
/// <para>
/// Each gesture keeps its own <see cref="HotkeyInterceptor"/> state, so shortcuts that share a key (Win+V and
/// Alt+Win+V) stay apart: a key-down goes to the first interceptor that does not pass it through, and the key-up of a
/// swallowed press finds the interceptor still swallowing it. Pure and unit-testable, like <see cref="HotkeyInterceptor"/>.
/// </para>
/// <para>
/// The hook calls <see cref="Handles"/> for every keystroke in the session before anything else, so that check is a
/// scan of a handful of keys and allocates nothing.
/// </para>
/// </remarks>
public sealed class HotkeyInterceptorSet
{
    private readonly HotkeyInterceptor[] interceptors;

    /// <summary>
    /// Creates interceptors for <paramref name="gestures"/>.
    /// </summary>
    /// <param name="gestures">The gestures to intercept; repeats are kept once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gestures"/> is <see langword="null"/>.</exception>
    public HotkeyInterceptorSet(IEnumerable<HotkeyGesture> gestures)
    {
        ArgumentNullException.ThrowIfNull(gestures);
        interceptors = gestures.Distinct().Select(g => new HotkeyInterceptor(g)).ToArray();
    }

    /// <summary>The intercepted gestures, in order.</summary>
    public IReadOnlyList<HotkeyGesture> Gestures => interceptors.Select(i => i.Gesture).ToArray();

    /// <summary>
    /// Whether any intercepted gesture uses <paramref name="virtualKey"/> as its main key (every other key passes through
    /// without reading the modifiers).
    /// </summary>
    /// <param name="virtualKey">Virtual-key code of the event.</param>
    /// <returns><see langword="true"/> when the event must go through <see cref="OnKey"/>.</returns>
    public bool Handles(uint virtualKey)
    {
        foreach (var interceptor in interceptors)
        {
            if (interceptor.Gesture.VirtualKey == virtualKey)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides the fate of one keyboard event across all intercepted gestures.
    /// </summary>
    /// <param name="virtualKey">Virtual-key code of the event.</param>
    /// <param name="isKeyDown">Key-down (including auto-repeat) vs key-up.</param>
    /// <param name="isOwnInjection">Event was synthesized by BetterClipboard itself (<see cref="KeyboardInput.OwnInjectionMarker"/>).</param>
    /// <param name="heldModifiers">Modifiers down at the time of the event.</param>
    /// <param name="fired">The gesture that fired when the decision is <see cref="InterceptDecision.SwallowAndFire"/>; otherwise <see langword="default"/>.</param>
    /// <returns>The first decision other than <see cref="InterceptDecision.PassThrough"/>, or pass-through when no gesture claims the event.</returns>
    public InterceptDecision OnKey(uint virtualKey, bool isKeyDown, bool isOwnInjection, HotkeyModifiers heldModifiers, out HotkeyGesture fired)
    {
        fired = default;
        foreach (var interceptor in interceptors)
        {
            var decision = interceptor.OnKey(virtualKey, isKeyDown, isOwnInjection, heldModifiers);
            if (decision == InterceptDecision.PassThrough)
            {
                continue;
            }

            if (decision == InterceptDecision.SwallowAndFire)
            {
                fired = interceptor.Gesture;
            }

            return decision;
        }

        return InterceptDecision.PassThrough;
    }
}
