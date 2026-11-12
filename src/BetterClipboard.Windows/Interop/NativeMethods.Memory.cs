using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for reading and giving back the process's memory (CLAUDE.md §2.45): its memory counters, the process heap's
/// compaction, and the working-set trim.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// <c>PROCESS_MEMORY_COUNTERS_EX2</c> (psapi.h): the process's memory counters, with the private working set Task Manager
    /// shows as "Memory". Windows versions that predate the structure fill only the first part and leave
    /// <see cref="PrivateWorkingSetSize"/> zero, or refuse the size (see <see cref="K32GetProcessMemoryInfo"/>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS_EX2
    {
        /// <summary>Size of this structure in bytes, set by the caller.</summary>
        public uint cb;

        /// <summary>Page faults so far.</summary>
        public uint PageFaultCount;

        /// <summary>Largest working set so far, in bytes.</summary>
        public nuint PeakWorkingSetSize;

        /// <summary>Working set now, in bytes (shared pages included).</summary>
        public nuint WorkingSetSize;

        /// <summary>Peak paged pool quota.</summary>
        public nuint QuotaPeakPagedPoolUsage;

        /// <summary>Paged pool quota now.</summary>
        public nuint QuotaPagedPoolUsage;

        /// <summary>Peak non-paged pool quota.</summary>
        public nuint QuotaPeakNonPagedPoolUsage;

        /// <summary>Non-paged pool quota now.</summary>
        public nuint QuotaNonPagedPoolUsage;

        /// <summary>Commit charge (the same as <see cref="PrivateUsage"/> on current Windows).</summary>
        public nuint PagefileUsage;

        /// <summary>Peak commit charge.</summary>
        public nuint PeakPagefileUsage;

        /// <summary>Committed private memory in bytes ("Commit size" in Task Manager).</summary>
        public nuint PrivateUsage;

        /// <summary>Private pages in the working set, in bytes ("Memory" in Task Manager); zero where Windows predates it.</summary>
        public nuint PrivateWorkingSetSize;

        /// <summary>Shared commit charge, in bytes.</summary>
        public ulong SharedCommitUsage;
    }

    /// <summary>
    /// Reads a process's memory counters (kernel32's export of psapi's <c>GetProcessMemoryInfo</c>). Fails when
    /// <paramref name="cb"/> names a structure this Windows does not know.
    /// </summary>
    /// <param name="process">Process handle (<see cref="GetCurrentProcess"/> for this one).</param>
    /// <param name="counters">Receives the counters; its <c>cb</c> must be set.</param>
    /// <param name="cb">Size of <paramref name="counters"/> in bytes.</param>
    /// <returns><see langword="true"/> on success; otherwise call <see cref="Marshal.GetLastPInvokeError"/>.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool K32GetProcessMemoryInfo(nint process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint cb);

    /// <summary>The process's default heap (where WinUI, WinRT and SQLite allocate native memory). Never closed.</summary>
    /// <returns>The heap handle, or 0 on failure.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GetProcessHeap();

    /// <summary>
    /// Coalesces a heap's free blocks and gives back what it can to Windows. Cheap on an idle heap; it locks the heap while
    /// it runs, so other threads' allocations wait for it.
    /// </summary>
    /// <param name="heap">The heap (<see cref="GetProcessHeap"/>).</param>
    /// <param name="flags">0, or <c>HEAP_NO_SERIALIZE</c> (never for the process heap, which every thread shares).</param>
    /// <returns>The largest free block left, in bytes; 0 when there is none or on failure.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint HeapCompact(nint heap, uint flags);

    /// <summary>
    /// Sets a process's working-set limits; both <see cref="nuint.MaxValue"/> (<c>(SIZE_T)-1</c>) takes every page out of
    /// the working set that Windows can take (they stay in memory on the standby list until something else needs it, and
    /// fault back in, cheaply, when touched again).
    /// </summary>
    /// <param name="process">Process handle (<see cref="GetCurrentProcess"/> for this one).</param>
    /// <param name="minimum">Minimum working set, in bytes, or <c>(SIZE_T)-1</c>.</param>
    /// <param name="maximum">Maximum working set, in bytes, or <c>(SIZE_T)-1</c>.</param>
    /// <param name="flags">0: no hard limits.</param>
    /// <returns><see langword="true"/> on success; otherwise call <see cref="Marshal.GetLastPInvokeError"/>.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessWorkingSetSizeEx(nint process, nuint minimum, nuint maximum, uint flags);
}
