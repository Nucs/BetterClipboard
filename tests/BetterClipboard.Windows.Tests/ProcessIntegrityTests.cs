using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for reading integrity levels, which decide whether a paste into a window can work (UIPI drops keys sent to a
/// window of a higher level, and <c>SendInput</c> does not say so).
/// </summary>
public sealed class ProcessIntegrityTests
{
    /// <summary>
    /// This process has a known level — Medium on a developer's normal prompt, High on an elevated one or a CI runner — and
    /// reading it by process id or through one of its windows gives the same; its own window is never "above" it.
    /// </summary>
    [Fact]
    public void OwnProcess_ReadsTheSameLevelEveryWay()
    {
        var current = ProcessIntegrity.Current;
        Assert.NotNull(current);
        Assert.Contains(current.Value, new[] { ProcessIntegrity.Medium, ProcessIntegrity.High, 0x4000 });
        Assert.Equal(current, ProcessIntegrity.OfProcess((uint)Environment.ProcessId));

        using var window = new MessageWindowThread("IntegrityTest", messageOnly: false);
        Assert.Equal(current, ProcessIntegrity.OfWindow(window.Handle));
        Assert.False(ProcessIntegrity.IsAboveCurrent(window.Handle));
    }

    /// <summary>No window, or a process that does not exist, has no level — and is never treated as elevated.</summary>
    [Fact]
    public void Unknown_IsNullAndNeverAbove()
    {
        Assert.Null(ProcessIntegrity.OfWindow(0));
        Assert.False(ProcessIntegrity.IsAboveCurrent(0));

        // Process ids are multiples of 4; an odd number is never a live process.
        Assert.Null(ProcessIntegrity.OfProcess(0x7FFF_FFFD));
    }
}
