using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Diagnostics;

/// <summary>
/// Reads how much memory BetterClipboard holds and gives back what it no longer uses (CLAUDE.md §2.45).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reclaiming</b> is a full, compacting garbage collection that asks the runtime to decommit what it can, then the
/// finalizers (which release the native objects — XAML elements, decoded images — whose .NET wrappers were collected),
/// then a second collection for what those finalizers freed, then a compaction of the process heap. It pauses every
/// managed thread for some milliseconds, so it is run when nobody waits for the app (<see cref="MemoryReclaimPlanner"/>),
/// and never on the UI thread: a finalizer may need the UI thread to release a WinUI object, and a UI thread waiting for
/// the finalizers would wait for itself.
/// </para>
/// <para>
/// <b>Trimming</b> the working set (<c>trimWorkingSet</c>) also hands every page Windows can take to its standby list: the
/// "Memory" column of Task Manager drops to a few megabytes, and the pages come back, from RAM, the next time they are
/// used — unless Windows gave that RAM to another program meanwhile, when they come back from the page file.
/// </para>
/// </remarks>
public static class ProcessMemory
{
    /// <summary>
    /// Reads this process's memory counters. Never throws: a counter Windows does not report reads as 0, or as
    /// <see langword="null"/> for the private working set.
    /// </summary>
    /// <returns>The numbers.</returns>
    public static MemoryUsage Read()
    {
        // What the collector committed as of its last collection (a reclaim just ran two); before the first collection of
        // the process that reads 0, and the heap's size is the best there is.
        long committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
        long managed = committed > 0 ? committed : GC.GetTotalMemory(forceFullCollection: false);
        var counters = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS_EX2>() };
        if (NativeMethods.K32GetProcessMemoryInfo(NativeMethods.GetCurrentProcess(), ref counters, counters.cb))
        {
            // Zero means this Windows predates the field (a process always has private pages in its working set).
            long? privateWorkingSet = counters.PrivateWorkingSetSize == 0 ? null : (long)counters.PrivateWorkingSetSize;
            return new MemoryUsage((long)counters.PrivateUsage, (long)counters.WorkingSetSize, privateWorkingSet, managed);
        }

        // Windows before 10 21H2 refuses the larger structure: the plain counters still say private and working set.
        var basic = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX2 { cb = 80 };
        return NativeMethods.K32GetProcessMemoryInfo(NativeMethods.GetCurrentProcess(), ref basic, basic.cb)
            ? new MemoryUsage((long)basic.PrivateUsage, (long)basic.WorkingSetSize, null, managed)
            : new MemoryUsage(0, Environment.WorkingSet, null, managed);
    }

    /// <summary>
    /// Gives back what this process no longer uses: collects (twice, around the finalizers), compacts the process heap, and
    /// optionally trims the working set (see the class remarks). Call it from a thread-pool thread, never the UI thread.
    /// </summary>
    /// <param name="trimWorkingSet">Also take the process's pages out of its working set (Task Manager's "Memory").</param>
    /// <remarks>
    /// Failures of the heap compaction and the trim are ignored: both only give memory back sooner, and Windows does the
    /// same by itself when it needs the memory.
    /// </remarks>
    public static void Reclaim(bool trimWorkingSet)
    {
        // Aggressive: also decommit the free space the collector would otherwise keep for future allocations. It requires
        // a blocking, compacting collection (the overload throws otherwise).
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // The collected wrappers of WinUI objects release their native objects in their finalizers; the native memory of a
        // closed window's XAML tree comes back only then.
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        nint heap = NativeMethods.GetProcessHeap();
        if (heap != 0)
        {
            _ = NativeMethods.HeapCompact(heap, 0);
        }

        if (trimWorkingSet)
        {
            _ = NativeMethods.SetProcessWorkingSetSizeEx(NativeMethods.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue, 0);
        }
    }
}
