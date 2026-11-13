#:property PublishAot=false
#:property Nullable=enable
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: what a running process's memory is (CLAUDE.md §2.45) — committed, charged (what "Private Bytes" and Task
// Manager's "Commit size" count) and resident (what Task Manager's "Memory" counts) per kind: native heap, .NET GC heap,
// images per DLL, GPU write-combined memory, thread stacks, mapped files, pagefile-backed sections, other private data.
//
// Read-only, and built for a process someone is using: no window (VMMap shows one), no thread injected into the target
// (heap-summary APIs do that), no dump (it would hold clipboard content). It reads the address-space layout, the PEB's
// heap list, each thread's TEB stack base, the first 32 bytes at allocation bases (heap headers) and the working-set
// state of every committed page. It prints addresses and sizes, never memory contents. Reading the headers may fault a
// few hundred pages into the target's working set.
//
// Run: dotnet run tools/probes/probe_memmap.cs -- <pid> [rows per list = 12]
//
// Heap identification (x64): heap roots from the PEB, NT heap segments by their 0xFFEEFFEE signature, NT large blocks
// and segment-heap page segments by a LIST_ENTRY at their base that links into a heap or into each other, and the rest
// of the segment heap's segments by their encoded signature (segment ^ context ^ key ^ constant at +0x10: all segments of
// one context share signature ^ address). The segment heap's large allocations carry no header and stay in
// "Private Data", as do allocations whose owner cannot be seen from outside (the graphics driver's, among others).
//
// Verified 2026-11-12 on this PC (NVIDIA GPU): the installed 0.2.10 after 2.5 days and an isolated 0.2.11-dev.4 idle
// (CLAUDE.md §2.45); a segment-heap check of three 16 MB reservations matched to 2^-64.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
int pid = int.Parse(args[0], CultureInfo.InvariantCulture);
int top = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 12;
IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
if (process == IntPtr.Zero)
{
    throw new Win32Exception();
}

// 1. The address space, region by region; allocation groups are the regions of one VirtualAlloc, view or image.
var regions = new List<Native.MBI>();
ulong address = 0;
while (address < 0x7FFF_FFFF_0000UL)
{
    if (Native.VirtualQueryEx(process, (IntPtr)(long)address, out var mbi, (IntPtr)Marshal.SizeOf<Native.MBI>()) == IntPtr.Zero)
    {
        break;
    }

    regions.Add(mbi);
    ulong next = mbi.BaseAddress + mbi.RegionSize;
    if (next <= address)
    {
        break;
    }

    address = next;
}

var groups = regions.Where(r => r.State != Native.MEM_FREE).GroupBy(r => r.AllocationBase).ToDictionary(g => g.Key, g => g.ToList());
var privateBases = groups.Where(g => g.Value.Any(r => r.Type == Native.MEM_PRIVATE && r.State == Native.MEM_COMMIT)).Select(g => g.Key).ToList();

// 2. Heaps. The PEB (x64) holds the heap count at +0xE8 and the heap array at +0xF0; a heap's signature at +0x10 tells an
//    NT heap (0xFFEEFFEE) from a segment heap (0xDDEEDDEE).
var pbi = new Native.PROCESS_BASIC_INFORMATION();
Native.NtQueryInformationProcess(process, 0, ref pbi, Marshal.SizeOf<Native.PROCESS_BASIC_INFORMATION>(), out _);
uint heapCount = ReadU32(pbi.PebBaseAddress + 0xE8);
ulong heapArray = ReadU64(pbi.PebBaseAddress + 0xF0);
var heaps = new List<(ulong Base, string Kind)>();
for (int i = 0; i < heapCount; i++)
{
    ulong heap = ReadU64(heapArray + (ulong)(8 * i));
    uint signature = ReadU32(heap + 0x10);
    heaps.Add((heap, signature == 0xFFEEFFEE ? "NT heap" : signature == 0xDDEEDDEE ? "segment heap" : $"heap 0x{signature:X8}"));
}

var category = new Dictionary<ulong, string>();
var heapPart = new Dictionary<ulong, string>();
foreach (var (heap, kind) in heaps)
{
    category[heap] = "Heap";
    heapPart[heap] = "heap root (" + kind + ")";
}

foreach (var b in privateBases.Where(b => !category.ContainsKey(b) && ReadU32(b + 0x10) == 0xFFEEFFEE))
{
    category[b] = "Heap";
    heapPart[b] = "NT heap segment";
}

// Blocks whose first LIST_ENTRY links into a heap structure or into other such blocks (NT large blocks, segment-heap
// page segments). A fixpoint drops every candidate with a link that leads anywhere else.
var candidates = privateBases.Where(b => !category.ContainsKey(b)).ToDictionary(b => b, b => (Flink: ReadU64(b), Blink: ReadU64(b + 8)));
for (bool changed = true; changed;)
{
    changed = false;
    foreach (var (b, links) in candidates.ToList())
    {
        if (links.Flink == 0 || !LinksIntoHeaps(links.Flink) || !LinksIntoHeaps(links.Blink))
        {
            candidates.Remove(b);
            changed = true;
        }
    }
}

foreach (var b in candidates.Keys)
{
    category[b] = "Heap";
    heapPart[b] = "linked heap block";
}

// The segment heap's other page segments: allocations that share (signature ^ address) with each other or with a linked
// segment. A value below 2^47 is a pointer or a self-pointer, never an encoded signature.
if (heaps.Any(hp => hp.Kind == "segment heap"))
{
    var byKey = new Dictionary<ulong, List<ulong>>();
    foreach (var b in privateBases.Where(b => !category.ContainsKey(b)))
    {
        ulong key = ReadU64(b + 0x10) ^ b;
        if (key < 0x8000_0000_0000UL)
        {
            continue;
        }

        if (!byKey.TryGetValue(key, out var members))
        {
            byKey[key] = members = new List<ulong>();
        }

        members.Add(b);
    }

    var anchors = heapPart.Where(kv => kv.Value == "linked heap block").Select(kv => ReadU64(kv.Key + 0x10) ^ kv.Key).ToHashSet();
    foreach (var (_, members) in byKey.Where(kv => kv.Value.Count >= 2 || anchors.Contains(kv.Key)))
    {
        foreach (var b in members)
        {
            category[b] = "Heap";
            heapPart[b] = "segment-heap segment";
        }
    }
}

// 3. Thread stacks: each thread's TEB (+0x08) names its stack base; the allocation holding it is the stack.
int threads = 0;
IntPtr snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPTHREAD, 0);
var entry = new Native.THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<Native.THREADENTRY32>() };
if (Native.Thread32First(snapshot, ref entry))
{
    do
    {
        if (entry.th32OwnerProcessID != pid)
        {
            continue;
        }

        IntPtr thread = Native.OpenThread(Native.THREAD_QUERY_INFORMATION | Native.THREAD_QUERY_LIMITED_INFORMATION, false, entry.th32ThreadID);
        if (thread == IntPtr.Zero)
        {
            continue;
        }

        var tbi = new Native.THREAD_BASIC_INFORMATION();
        if (Native.NtQueryInformationThread(thread, 0, ref tbi, Marshal.SizeOf<Native.THREAD_BASIC_INFORMATION>(), out _) == 0)
        {
            ulong stackBase = ReadU64(tbi.TebBaseAddress + 0x08);
            var holder = regions.FirstOrDefault(r => stackBase - 1 >= r.BaseAddress && stackBase - 1 < r.BaseAddress + r.RegionSize);
            if (holder.RegionSize != 0)
            {
                category[holder.AllocationBase] = "Stack";
            }

            threads++;
        }

        Native.CloseHandle(thread);
    }
    while (Native.Thread32Next(snapshot, ref entry));
}

Native.CloseHandle(snapshot);

// 4. Everything else: images, views, the .NET GC's huge reservation, executable code, write-combined memory, the rest.
foreach (var (b, list) in groups)
{
    if (category.ContainsKey(b))
    {
        continue;
    }

    ulong reserved = list.Aggregate(0UL, (s, r) => s + r.RegionSize);
    category[b] = list[0].Type switch
    {
        Native.MEM_IMAGE => "Image",
        Native.MEM_MAPPED => MappedName(b) is null ? "Shareable" : "Mapped File",
        _ when reserved >= 4UL << 30 => "Managed Heap (GC reservation)",
        _ when list.Any(r => r.State == Native.MEM_COMMIT && (r.Protect & 0xF0) != 0) => "Private executable (JIT, stubs)",
        _ when list.Any(r => r.State == Native.MEM_COMMIT && (r.Protect & Native.PAGE_WRITECOMBINE) != 0) => "Write-combined (GPU)",
        _ => "Private Data",
    };
}

// 5. The working-set state of every committed page, in batches.
var pages = new List<ulong>();
foreach (var r in regions.Where(r => r.State == Native.MEM_COMMIT))
{
    for (ulong p = r.BaseAddress; p < r.BaseAddress + r.RegionSize; p += 4096)
    {
        pages.Add(p);
    }
}

var resident = new Dictionary<ulong, (bool Valid, bool Shared)>(pages.Count);
for (int start = 0; start < pages.Count; start += 65536)
{
    int n = Math.Min(65536, pages.Count - start);
    var info = new Native.WSEX[n];
    for (int i = 0; i < n; i++)
    {
        info[i].VirtualAddress = pages[start + i];
    }

    if (!Native.K32QueryWorkingSetEx(process, info, (uint)(n * Marshal.SizeOf<Native.WSEX>())))
    {
        throw new Win32Exception();
    }

    for (int i = 0; i < n; i++)
    {
        bool valid = (info[i].Attributes & 1) != 0;
        resident[info[i].VirtualAddress] = (valid, valid && (info[i].Attributes & (1UL << 15)) != 0);
    }
}

// 6. Sums per kind and per allocation: committed, charged, working set, private working set.
var totals = new Dictionary<string, long[]>();
var perGroup = new Dictionary<ulong, long[]>();
foreach (var r in regions.Where(r => r.State == Native.MEM_COMMIT))
{
    string kind = category.TryGetValue(r.AllocationBase, out var c) ? c : "?";
    uint protect = r.Protect & 0xFF;

    // Commit charge: all private memory, writable or copy-on-write image pages, and copy-on-write views.
    bool writable = protect is 0x04 or 0x08 or 0x40 or 0x80;
    bool copyOnWrite = protect is 0x08 or 0x80;
    bool charged = r.Type == Native.MEM_PRIVATE || (r.Type == Native.MEM_IMAGE && writable) || copyOnWrite;
    long inSet = 0, inSetPrivate = 0;
    for (ulong p = r.BaseAddress; p < r.BaseAddress + r.RegionSize; p += 4096)
    {
        var state = resident[p];
        if (state.Valid)
        {
            inSet += 4096;
            if (!state.Shared)
            {
                inSetPrivate += 4096;
            }
        }
    }

    if (!totals.TryGetValue(kind, out var byKind))
    {
        totals[kind] = byKind = new long[4];
    }

    if (!perGroup.TryGetValue(r.AllocationBase, out var byGroup))
    {
        perGroup[r.AllocationBase] = byGroup = new long[4];
    }

    foreach (var t in new[] { byKind, byGroup })
    {
        t[0] += (long)r.RegionSize;
        t[1] += charged ? (long)r.RegionSize : 0;
        t[2] += inSet;
        t[3] += inSetPrivate;
    }
}

var counters = new Native.PMC2 { cb = (uint)Marshal.SizeOf<Native.PMC2>() };
Native.K32GetProcessMemoryInfo(process, ref counters, counters.cb);
Console.WriteLine($"pid {pid}: Private Bytes {Mb(counters.PrivateUsage)}, working set {Mb(counters.WorkingSetSize)}, private working set (Task Manager 'Memory') {Mb(counters.PrivateWorkingSetSize)}; {threads} threads; heaps: {string.Join(", ", heaps.Select(hp => hp.Kind))}");
Console.WriteLine();
Console.WriteLine($"{"Kind",-34}{"Committed",12}{"Charged",12}{"WS",12}{"Private WS",12}");
foreach (var (kind, t) in totals.OrderByDescending(kv => kv.Value[1]).ThenByDescending(kv => kv.Value[0]))
{
    Console.WriteLine($"{kind,-34}{Mb(t[0]),12}{Mb(t[1]),12}{Mb(t[2]),12}{Mb(t[3]),12}");
}

var sum = totals.Values.Aggregate(new long[4], (a, t) =>
{
    for (int i = 0; i < 4; i++)
    {
        a[i] += t[i];
    }

    return a;
});
Console.WriteLine($"{"Total",-34}{Mb(sum[0]),12}{Mb(sum[1]),12}{Mb(sum[2]),12}{Mb(sum[3]),12}");

PrintLargest("Image", "Largest images", b => Path.GetFileName(MappedName(b) ?? "?"));
PrintLargest("Mapped File", "Largest mapped files", b => Path.GetFileName(MappedName(b) ?? "?"));
PrintLargest("Private Data", "Largest other private allocations", b => $"0x{b:X12} reserved {Mb((long)Reserved(b))} protection {string.Join("/", groups[b].Where(r => r.State == Native.MEM_COMMIT).Select(r => $"{r.Protect:X}").Distinct())}");
PrintLargest("Heap", "Largest heap parts", b => $"0x{b:X12} {heapPart.GetValueOrDefault(b, "?")} reserved {Mb((long)Reserved(b))}");

// <summary>
// Whether a LIST_ENTRY pointer could belong to a heap's own lists: it points into the first 8 KB of a heap (where both
// heap kinds keep their list heads) or at the base of another candidate block. Anything else disqualifies the block.
// </summary>
// <param name="pointer">A Flink or Blink read at a candidate's base.</param>
// <returns><see langword="true"/> when the link stays among heaps and their blocks.</returns>
bool LinksIntoHeaps(ulong pointer) => heaps.Any(hp => pointer >= hp.Base && pointer < hp.Base + 0x2000) || candidates.ContainsKey(pointer);

// <summary>Everything one allocation reserved: its committed and its reserved-only regions.</summary>
// <param name="allocationBase">The allocation's base address.</param>
// <returns>The size in bytes.</returns>
ulong Reserved(ulong allocationBase) => groups[allocationBase].Aggregate(0UL, (s, r) => s + r.RegionSize);

// <summary>
// Prints the largest allocations of one kind, by what they charge or keep resident, whichever is larger (a DLL's code
// is resident but not charged; a driver's reserve is charged but not resident).
// </summary>
// <param name="kind">The kind to list.</param>
// <param name="title">The list's heading.</param>
// <param name="label">Names one allocation from its base address.</param>
void PrintLargest(string kind, string title, Func<ulong, string> label)
{
    Console.WriteLine();
    Console.WriteLine($"{title} (committed / charged / WS / private WS):");
    foreach (var (b, t) in perGroup.Where(kv => category.TryGetValue(kv.Key, out var c) && c == kind).OrderByDescending(kv => Math.Max(kv.Value[1], kv.Value[2])).Take(top))
    {
        Console.WriteLine($"  {label(b),-64}{Mb(t[0]),10}{Mb(t[1]),10}{Mb(t[2]),10}{Mb(t[3]),10}");
    }
}

// <summary>
// The file behind an image or a mapped view, as an NT device path; <see langword="null"/> for a pagefile-backed section
// (which this probe counts as "Shareable", like VMMap).
// </summary>
// <param name="allocationBase">An address inside the view.</param>
// <returns>The path, or <see langword="null"/>.</returns>
string? MappedName(ulong allocationBase)
{
    var name = new StringBuilder(1024);
    return Native.K32GetMappedFileNameW(process, (IntPtr)(long)allocationBase, name, (uint)name.Capacity) > 0 ? name.ToString() : null;
}

// <summary>Reads 4 bytes of the target; 0 when the address cannot be read (a decommitted or guarded page).</summary>
// <param name="at">The address in the target.</param>
// <returns>The value, little-endian.</returns>
uint ReadU32(ulong at)
{
    var buffer = new byte[4];
    return Native.ReadProcessMemory(process, (IntPtr)(long)at, buffer, (IntPtr)4, out _) ? BitConverter.ToUInt32(buffer) : 0;
}

// <summary>Reads 8 bytes of the target; 0 when the address cannot be read.</summary>
// <param name="at">The address in the target.</param>
// <returns>The value, little-endian.</returns>
ulong ReadU64(ulong at)
{
    var buffer = new byte[8];
    return Native.ReadProcessMemory(process, (IntPtr)(long)at, buffer, (IntPtr)8, out _) ? BitConverter.ToUInt64(buffer) : 0;
}

// <summary>Megabytes with one decimal, for the table.</summary>
// <param name="bytes">A byte count.</param>
// <returns>"12.3 MB".</returns>
static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

/// <summary>The Win32 and native calls the probe reads the target with; every one of them is read-only.</summary>
static class Native
{
    /// <summary>Access to query a process's information (its PEB, memory counters, working set).</summary>
    public const uint PROCESS_QUERY_INFORMATION = 0x400;

    /// <summary>Access to read a process's memory (heap headers, the PEB's heap list, TEBs).</summary>
    public const uint PROCESS_VM_READ = 0x10;

    /// <summary>Access to query a thread's basic information (its TEB address).</summary>
    public const uint THREAD_QUERY_INFORMATION = 0x40;

    /// <summary>The limited form of the same access, enough on current Windows.</summary>
    public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x800;

    /// <summary>Toolhelp flag: include every thread of the system in the snapshot.</summary>
    public const uint TH32CS_SNAPTHREAD = 0x4;

    /// <summary>Region state: committed (backed by RAM or the page file).</summary>
    public const uint MEM_COMMIT = 0x1000;

    /// <summary>Region state: free address space.</summary>
    public const uint MEM_FREE = 0x10000;

    /// <summary>Region type: private memory (VirtualAlloc, heaps, stacks).</summary>
    public const uint MEM_PRIVATE = 0x20000;

    /// <summary>Region type: a view of a section (a mapped file or a pagefile-backed section).</summary>
    public const uint MEM_MAPPED = 0x40000;

    /// <summary>Region type: an executable image (a DLL or the EXE).</summary>
    public const uint MEM_IMAGE = 0x1000000;

    /// <summary>Protection modifier: write-combined, which graphics drivers use for memory the GPU reads.</summary>
    public const uint PAGE_WRITECOMBINE = 0x400;

    /// <summary>MEMORY_BASIC_INFORMATION (x64): one region of uniform state, protection and type.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MBI
    {
        /// <summary>The region's first address.</summary>
        public ulong BaseAddress;

        /// <summary>The base of the allocation the region belongs to.</summary>
        public ulong AllocationBase;

        /// <summary>The protection the allocation was made with.</summary>
        public uint AllocationProtect;

        /// <summary>The memory partition (unused here).</summary>
        public ushort PartitionId;

        /// <summary>Alignment padding.</summary>
        public ushort Pad0;

        /// <summary>The region's size in bytes.</summary>
        public ulong RegionSize;

        /// <summary>MEM_COMMIT, MEM_RESERVE or MEM_FREE.</summary>
        public uint State;

        /// <summary>The region's current protection, with its modifiers.</summary>
        public uint Protect;

        /// <summary>MEM_PRIVATE, MEM_MAPPED or MEM_IMAGE.</summary>
        public uint Type;

        /// <summary>Alignment padding.</summary>
        public uint Pad1;
    }

    /// <summary>PSAPI_WORKING_SET_EX_INFORMATION: one page's working-set state.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WSEX
    {
        /// <summary>The page asked about.</summary>
        public ulong VirtualAddress;

        /// <summary>Bit 0: resident (valid); bit 15: shareable (counted outside the private working set).</summary>
        public ulong Attributes;
    }

    /// <summary>PROCESS_BASIC_INFORMATION (x64): the PEB's address, among others.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        /// <summary>The exit status (with padding).</summary>
        public IntPtr ExitStatus;

        /// <summary>The PEB's address in the target.</summary>
        public ulong PebBaseAddress;

        /// <summary>The affinity mask.</summary>
        public IntPtr AffinityMask;

        /// <summary>The base priority (with padding).</summary>
        public IntPtr BasePriority;

        /// <summary>The process id.</summary>
        public IntPtr UniqueProcessId;

        /// <summary>The parent's process id.</summary>
        public IntPtr InheritedFromUniqueProcessId;
    }

    /// <summary>THREAD_BASIC_INFORMATION (x64): the TEB's address, among others.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct THREAD_BASIC_INFORMATION
    {
        /// <summary>The exit status.</summary>
        public int ExitStatus;

        /// <summary>The TEB's address in the target.</summary>
        public ulong TebBaseAddress;

        /// <summary>The client id's process half.</summary>
        public IntPtr ClientIdProcess;

        /// <summary>The client id's thread half.</summary>
        public IntPtr ClientIdThread;

        /// <summary>The affinity mask.</summary>
        public IntPtr AffinityMask;

        /// <summary>The priority.</summary>
        public int Priority;

        /// <summary>The base priority.</summary>
        public int BasePriority;
    }

    /// <summary>THREADENTRY32: one thread of a Toolhelp snapshot.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct THREADENTRY32
    {
        /// <summary>The structure's size, set before the first call.</summary>
        public uint dwSize;

        /// <summary>Unused.</summary>
        public uint cntUsage;

        /// <summary>The thread's id.</summary>
        public uint th32ThreadID;

        /// <summary>The id of the process the thread belongs to.</summary>
        public int th32OwnerProcessID;

        /// <summary>The base priority.</summary>
        public int tpBasePri;

        /// <summary>Unused.</summary>
        public int tpDeltaPri;

        /// <summary>Unused.</summary>
        public uint dwFlags;
    }

    /// <summary>PROCESS_MEMORY_COUNTERS_EX2: the counters Task Manager shows (Windows 10 21H2 and later).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PMC2
    {
        /// <summary>The structure's size, set before the call.</summary>
        public uint cb;

        /// <summary>Page faults so far.</summary>
        public uint PageFaultCount;

        /// <summary>The largest working set so far.</summary>
        public long PeakWorkingSetSize;

        /// <summary>The working set now.</summary>
        public long WorkingSetSize;

        /// <summary>Paged pool peak.</summary>
        public long QuotaPeakPagedPoolUsage;

        /// <summary>Paged pool now.</summary>
        public long QuotaPagedPoolUsage;

        /// <summary>Non-paged pool peak.</summary>
        public long QuotaPeakNonPagedPoolUsage;

        /// <summary>Non-paged pool now.</summary>
        public long QuotaNonPagedPoolUsage;

        /// <summary>The commit charge (same as <see cref="PrivateUsage"/>).</summary>
        public long PagefileUsage;

        /// <summary>The largest commit charge so far.</summary>
        public long PeakPagefileUsage;

        /// <summary>The commit charge: "Private Bytes", Task Manager's "Commit size".</summary>
        public long PrivateUsage;

        /// <summary>The private working set: Task Manager's "Memory" column.</summary>
        public long PrivateWorkingSetSize;

        /// <summary>Shared commit charged to the process.</summary>
        public ulong SharedCommitUsage;
    }

    /// <summary>Opens a process for reading.</summary>
    /// <param name="access">The access asked for.</param>
    /// <param name="inherit">Whether child processes inherit the handle.</param>
    /// <param name="pid">The process id.</param>
    /// <returns>The handle, or zero (see the last error).</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    /// <summary>Opens a thread for querying.</summary>
    /// <param name="access">The access asked for.</param>
    /// <param name="inherit">Whether child processes inherit the handle.</param>
    /// <param name="tid">The thread id.</param>
    /// <returns>The handle, or zero.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

    /// <summary>Closes a handle.</summary>
    /// <param name="h">The handle.</param>
    /// <returns>Whether it was closed.</returns>
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    /// <summary>Describes the region of the target that holds an address.</summary>
    /// <param name="h">The process.</param>
    /// <param name="address">Any address in the region.</param>
    /// <param name="mbi">The region.</param>
    /// <param name="length">The size of <paramref name="mbi"/>.</param>
    /// <returns>The bytes written, zero past the end of the address space.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr address, out MBI mbi, IntPtr length);

    /// <summary>Copies bytes out of the target (never into it).</summary>
    /// <param name="h">The process.</param>
    /// <param name="address">The first byte.</param>
    /// <param name="buffer">Receives the bytes.</param>
    /// <param name="size">How many.</param>
    /// <param name="read">How many were copied.</param>
    /// <returns>Whether all were copied.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr h, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

    /// <summary>The file behind a mapped view or image.</summary>
    /// <param name="h">The process.</param>
    /// <param name="address">An address in the view.</param>
    /// <param name="name">Receives the NT path.</param>
    /// <param name="size">The capacity of <paramref name="name"/>.</param>
    /// <returns>The path's length, zero when the view has no file.</returns>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint K32GetMappedFileNameW(IntPtr h, IntPtr address, StringBuilder name, uint size);

    /// <summary>The working-set state of each page asked about.</summary>
    /// <param name="h">The process.</param>
    /// <param name="info">The pages in, their states out.</param>
    /// <param name="size">The size of <paramref name="info"/> in bytes.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32QueryWorkingSetEx(IntPtr h, [In, Out] WSEX[] info, uint size);

    /// <summary>The process's memory counters.</summary>
    /// <param name="h">The process.</param>
    /// <param name="counters">Receives the counters.</param>
    /// <param name="size">The size of <paramref name="counters"/>.</param>
    /// <returns>Whether it succeeded.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32GetProcessMemoryInfo(IntPtr h, ref PMC2 counters, uint size);

    /// <summary>A Toolhelp snapshot (here: of every thread).</summary>
    /// <param name="flags">What to include.</param>
    /// <param name="pid">The process (ignored for threads).</param>
    /// <returns>The snapshot handle.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);

    /// <summary>The snapshot's first thread.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="entry">Receives the thread.</param>
    /// <returns>Whether there was one.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

    /// <summary>The snapshot's next thread.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="entry">Receives the thread.</param>
    /// <returns>Whether there was one.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

    /// <summary>A process's basic information (class 0: the PEB's address).</summary>
    /// <param name="h">The process.</param>
    /// <param name="cls">The information class.</param>
    /// <param name="info">Receives the information.</param>
    /// <param name="size">The size of <paramref name="info"/>.</param>
    /// <param name="returned">The bytes written.</param>
    /// <returns>An NTSTATUS (0 on success).</returns>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    /// <summary>A thread's basic information (class 0: the TEB's address).</summary>
    /// <param name="h">The thread.</param>
    /// <param name="cls">The information class.</param>
    /// <param name="info">Receives the information.</param>
    /// <param name="size">The size of <paramref name="info"/>.</param>
    /// <param name="returned">The bytes written.</param>
    /// <returns>An NTSTATUS (0 on success).</returns>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationThread(IntPtr h, int cls, ref THREAD_BASIC_INFORMATION info, int size, out int returned);
}
