using BetterClipboard.Windows.Diagnostics;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Runs alone: a reclaim blocks every thread of the test process for two full collections and trims its working set, which
/// would slow the timing-sensitive tests running beside it. (The pipe server's hang-up test that failed on 2026-11-12 was
/// not slowed by it, as first thought: it failed by the test order of one folder, CLAUDE.md §2.45.)
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessMemoryCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Process memory (collects and trims the test process)";
}

/// <summary>
/// Tests for reading and giving back this process's memory (CLAUDE.md §2.45): the counters are the ones Windows keeps, and
/// a reclaim gives back what the collector can, also with the working-set trim.
/// </summary>
[Collection(ProcessMemoryCollection.Name)]
public sealed class ProcessMemoryTests
{
    /// <summary>
    /// The counters read sensibly for a running test process: private memory and working set are there, the private working
    /// set (reported on every Windows the CI and this machine run) is part of the working set, and the .NET heap is counted.
    /// </summary>
    /// <remarks>
    /// The working set may be tiny: the other test here trims it, and a trimmed process holds only what it touched since. The
    /// private memory (committed) is not changed by a trim.
    /// </remarks>
    [Fact]
    public void Read_ReportsThisProcess()
    {
        var usage = ProcessMemory.Read();
        Assert.True(usage.PrivateBytes > 1024 * 1024, $"private {usage.PrivateBytes}");
        Assert.True(usage.WorkingSetBytes > 0, $"working set {usage.WorkingSetBytes}");
        Assert.True(usage.ManagedHeapBytes > 0);
        if (usage.PrivateWorkingSetBytes is { } privateWorkingSet)
        {
            Assert.InRange(privateWorkingSet, 1, usage.WorkingSetBytes);
        }

        Assert.StartsWith("private ", usage.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A reclaim collects what is no longer referenced — a large array dropped before it is gone from the .NET heap after —
    /// and the trimmed variant runs too; neither throws, and the counters read afterwards.
    /// </summary>
    [Fact]
    public void Reclaim_GivesBackGarbage_AndTrims()
    {
        var weak = AllocateAndDrop();
        ProcessMemory.Reclaim(trimWorkingSet: false);
        Assert.False(weak.TryGetTarget(out _), "the dropped array survived a reclaim");

        ProcessMemory.Reclaim(trimWorkingSet: true);
        Assert.True(ProcessMemory.Read().PrivateBytes > 0);
    }

    /// <summary>
    /// Allocates 32 MB on the large-object heap and keeps only a weak reference to it. Not inlined: inlined, the array could
    /// stay referenced from the caller's frame until the caller returns.
    /// </summary>
    /// <returns>The weak reference.</returns>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<byte[]> AllocateAndDrop()
    {
        var array = new byte[32 * 1024 * 1024];
        array[^1] = 1;
        return new WeakReference<byte[]>(array);
    }
}
