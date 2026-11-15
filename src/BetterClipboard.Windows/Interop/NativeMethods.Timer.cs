using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for a wait that ends on time to the millisecond (CLAUDE.md §2.52): a high-resolution waitable timer, which
/// Windows 10 1803 and later wake within about half a millisecond, where <c>Thread.Sleep</c> and <c>Task.Delay</c> round up to
/// the next 15.6 ms tick of the system timer.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary><c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>: a timer that does not round to the system tick (Windows 10 1803+).</summary>
    internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    /// <summary><c>TIMER_MODIFY_STATE</c> | <c>SYNCHRONIZE</c>: set the timer and wait on it, nothing else.</summary>
    internal const uint TIMER_SET_AND_WAIT = 0x0002 | 0x00100000;

    /// <summary>Creates a waitable timer.</summary>
    /// <param name="lpTimerAttributes">Security attributes; 0 for the defaults.</param>
    /// <param name="lpTimerName"><see langword="null"/>: an unnamed timer only this process uses.</param>
    /// <param name="dwFlags"><see cref="CREATE_WAITABLE_TIMER_HIGH_RESOLUTION"/>.</param>
    /// <param name="dwDesiredAccess"><see cref="TIMER_SET_AND_WAIT"/>.</param>
    /// <returns>The timer (close it with <see cref="CloseHandle"/>), or 0 when Windows refuses (older than 1803: the flag is unknown).</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWaitableTimerEx(nint lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    /// <summary>Arms a waitable timer.</summary>
    /// <param name="hTimer">The timer.</param>
    /// <param name="lpDueTime">When it fires: negative = relative, in 100-ns units.</param>
    /// <param name="lPeriod">0: it fires once.</param>
    /// <param name="pfnCompletionRoutine">0: no completion routine (the caller waits on the handle).</param>
    /// <param name="lpArgToCompletionRoutine">0.</param>
    /// <param name="WakeContext">0: no wake from sleep.</param>
    /// <param name="TolerableDelay">0: no coalescing with other timers.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimerEx(nint hTimer, in long lpDueTime, int lPeriod, nint pfnCompletionRoutine, nint lpArgToCompletionRoutine, nint WakeContext, uint TolerableDelay);
}
