using System.ComponentModel;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// The Windows pieces of the silent restart (CLAUDE.md §2.46): the quiet start of the new process, the user-activity
/// reads its timing rests on, and the shortcut hold's argument rules. The hold itself installs a keyboard hook, which a
/// test must not (it would see the whole session's keystrokes), so it is verified live (CLAUDE.md §5).
/// </summary>
public sealed class SilentRestartTests
{
    /// <summary>
    /// A program started quietly runs, gets its own id, and its end is seen through the handle: a console program with
    /// <c>noConsoleWindow</c>, so no window flashes on the test machine either.
    /// </summary>
    [Fact]
    public void QuietProcess_StartsAndEnds()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var process = QuietProcess.Start(cmd, "/d /c exit 7", Path.GetTempPath(), noConsoleWindow: true);

        Assert.True(process.Id > 0);
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)), "cmd.exe did not end within 10 s");
        Assert.True(process.HasExited);

        // Disposing closes the handle once; a second dispose and a read afterwards are harmless.
        process.Dispose();
        process.Dispose();
        Assert.True(process.HasExited);
    }

    /// <summary>A program that does not exist is refused with Windows' own error, and empty paths before any call.</summary>
    [Fact]
    public void QuietProcess_RefusesWhatCannotStart()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"bc-test-missing-{Guid.NewGuid():N}.exe");
        Assert.Throws<Win32Exception>(() => QuietProcess.Start(missing, string.Empty, Path.GetTempPath()));
        Assert.ThrowsAny<ArgumentException>(() => QuietProcess.Start(string.Empty, string.Empty, Path.GetTempPath()));
        Assert.ThrowsAny<ArgumentException>(() => QuietProcess.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), string.Empty, string.Empty));
    }

    /// <summary>
    /// The time since the last input is never negative and never more than the time Windows has been running (the tick
    /// count it is measured against); the held-key read runs anywhere, also without an input desktop.
    /// </summary>
    [Fact]
    public void UserActivity_ReadsSensibly()
    {
        var since = UserActivity.SinceLastInput();
        Assert.True(since >= TimeSpan.Zero);
        Assert.True(since <= TimeSpan.FromMilliseconds(Environment.TickCount64) + TimeSpan.FromSeconds(1), $"since last input {since}");
        _ = UserActivity.AnyModifierOrButtonHeld();
    }

    /// <summary>Holding nothing is refused before any hook is installed, like applying nothing.</summary>
    /// <returns>A task completing when done.</returns>
    [Fact]
    public async Task HoldWithHook_RefusesAnEmptyList()
    {
        using var service = new HotkeyService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.HoldWithHookAsync(Array.Empty<HotkeyGesture>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.HoldWithHookAsync(null!));
        Assert.Empty(service.Registrations);
    }
}
