using System.Diagnostics;

namespace BetterClipboard.Core.Diagnostics;

/// <summary>
/// The stages of one paste, timed from the key or click that asked for it to the moment Ctrl+V went to the window the user
/// came from — so a "pasting takes a moment" report can be checked against the log, the way "Panel on screen N ms after the
/// shortcut" answers one about the summon (CLAUDE.md §2.52). Content-free: stage names and milliseconds only.
/// </summary>
/// <remarks>
/// The stages are marked along one async flow that hops between the UI thread, the thread pool and the clipboard thread;
/// <see cref="StageTimeline"/> keeps them in order and adds them up. This type adds what only a paste needs: when it is slow
/// enough to log, and a start for a paste that no key asked for.
/// </remarks>
public sealed class PasteTimeline : StageTimeline
{
    /// <summary>
    /// A paste slower than this from the key to Ctrl+V is logged (the session's first one always is): about where a delay
    /// stops feeling instant, and well above what a warm paste needs (the settle wait after activating the target included).
    /// </summary>
    public static readonly TimeSpan SlowPaste = TimeSpan.FromMilliseconds(120);

    /// <summary>Starts a timeline at <paramref name="startedAt"/>.</summary>
    /// <param name="startedAt">A <see cref="Stopwatch.GetTimestamp"/> value: when the user asked for the paste.</param>
    public PasteTimeline(long startedAt)
        : base(startedAt)
    {
    }

    /// <summary>Starts a timeline now, for a paste asked for by a click or a menu (no key timestamp to start from).</summary>
    /// <returns>The timeline.</returns>
    public static PasteTimeline StartNow() => new(Stopwatch.GetTimestamp());

    /// <summary>
    /// How long Ctrl stayed down after the V went in, waiting for the window to take it (CLAUDE.md §2.54); <see langword="null"/>
    /// until the paste released it, and for a paste that sent no keys. Not one of the stages: the paste happened when the V
    /// went in, so the log's total stops there.
    /// </summary>
    /// <remarks>Set once by the paste's injection, on a thread-pool thread, and read after the paste was awaited.</remarks>
    public TimeSpan? ControlHeld { get; set; }

    /// <summary>
    /// Whether the window answered that it took the keys before Ctrl was released; <see langword="false"/> when the hold's cap
    /// released it (a window that stayed busy, or went away) — the one case in which the paste may still have typed "v".
    /// </summary>
    public bool ControlHeldUntilTaken { get; set; }

    /// <summary>
    /// How many key events the user typed while the paste held Ctrl down, which BetterClipboard held back and typed after it
    /// (CLAUDE.md §2.54); 0 for none. A count only: which keys never reaches the log.
    /// </summary>
    /// <remarks>Set once by the paste's injection, on a thread-pool thread, and read after the paste was awaited.</remarks>
    public int KeysHeldBack { get; set; }

    /// <summary>
    /// The hold for the log line, after the stages: "; Ctrl held 27 ms", or "; Ctrl held 1000 ms (released before the window
    /// took the keys)" when the cap released it, then "; 2 key events typed meanwhile went in after it" when the user typed
    /// during the hold. Empty when no hold was recorded. Invariant culture.
    /// </summary>
    /// <returns>The text to append to the log line, starting with "; ", or empty.</returns>
    public string DescribeHold()
    {
        if (ControlHeld is not { } held)
        {
            return string.Empty;
        }

        string text = $"; Ctrl held {Milliseconds(held)} ms{(ControlHeldUntilTaken ? string.Empty : " (released before the window took the keys)")}";
        return KeysHeldBack switch
        {
            0 => text,
            1 => text + "; 1 key event typed meanwhile went in after it",
            _ => text + $"; {KeysHeldBack.ToString(System.Globalization.CultureInfo.InvariantCulture)} key events typed meanwhile went in after it",
        };
    }

    /// <summary>Whether a finished paste belongs in the log: the session's first, or one at least <see cref="SlowPaste"/> long.</summary>
    /// <param name="total">The paste's <see cref="StageTimeline.Total"/>.</param>
    /// <param name="firstOfSession">Whether it is the first paste since the app started.</param>
    /// <returns><see langword="true"/> to log it.</returns>
    public static bool ShouldLog(TimeSpan total, bool firstOfSession) => firstOfSession || total >= SlowPaste;
}
