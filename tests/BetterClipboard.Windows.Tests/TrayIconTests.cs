using BetterClipboard.Windows.Shell;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for how the tray icon gets into the notification area (<see cref="TrayIcon"/>), against a stand-in for Explorer's
/// <c>Shell_NotifyIcon</c>: no icon ever reaches the real notification area.
/// </summary>
/// <remarks>
/// The case these guard (CLAUDE.md §6, 2026-11-13): a busy Explorer answers <c>NIM_ADD</c> with FALSE although it adds the
/// icon. Before the fix the icon was never set up for version-4 callbacks, so clicks and the menu did nothing.
/// </remarks>
public sealed class TrayIconTests
{
    /// <summary>The <c>NIM_*</c> commands, as the tray sends them.</summary>
    private const uint Add = 0, Modify = 1, Delete = 2, SetVersion = 4;

    /// <summary>An icon file that does not exist: the icon then has no picture, which the stand-in does not mind.</summary>
    private static readonly string NoIcon = Path.Combine(Path.GetTempPath(), "BetterClipboard.Tests", "no-icon.ico");

    /// <summary>Explorer accepts the icon at once: added, then set up for version-4 callbacks, nothing else.</summary>
    [Fact]
    public void Add_Accepted_IsSetUpAtOnce()
    {
        var shell = new FakeShell((_, _) => true);
        using (new TrayIcon(NoIcon, "BC-TEST", startHidden: false, shell.Notify, retryAddInterval: TimeSpan.FromMilliseconds(30)))
        {
            Assert.Equal(new[] { Add, SetVersion }, shell.Calls());
        }

        Assert.Equal(Delete, shell.Calls()[^1]);
    }

    /// <summary>
    /// Explorer answers the add with FALSE but has the icon (it answered late): a modify finds it, the icon is set up for
    /// version-4 callbacks, and later calls that need an icon in place (a tooltip, the removal) are made.
    /// </summary>
    [Fact]
    public void Add_RefusedButThere_IsSetUp()
    {
        var shell = new FakeShell((message, _) => message != Add);
        using (var tray = new TrayIcon(NoIcon, "BC-TEST", startHidden: false, shell.Notify, retryAddInterval: TimeSpan.FromMilliseconds(30)))
        {
            Assert.Equal(new[] { Add, Modify, SetVersion }, shell.Calls());
            tray.SetTooltip("BC-TEST tooltip");
            Assert.True(shell.WaitFor(calls => calls.Count(m => m == Modify) == 2));
        }

        Assert.Equal(Delete, shell.Calls()[^1]);
    }

    /// <summary>
    /// Explorer refuses the icon and does not have it (not running yet, or hung): the tray tries again on its timer until
    /// Explorer takes it, then sets it up; nothing that needs the icon is sent while it is not there.
    /// </summary>
    [Fact]
    public void Add_Refused_IsTriedAgainUntilAccepted()
    {
        // The first two adds are refused and the icon is not there; the third add is accepted.
        var shell = new FakeShell((message, nth) => message switch
        {
            Add => nth >= 3,
            Modify => false,
            _ => true,
        });
        using (new TrayIcon(NoIcon, "BC-TEST", startHidden: false, shell.Notify, retryAddInterval: TimeSpan.FromMilliseconds(30)))
        {
            Assert.True(shell.WaitFor(calls => calls.Contains(SetVersion)), string.Join(",", shell.Calls()));
            var calls = shell.Calls();
            Assert.Equal(3, calls.Count(m => m == Add));
            Assert.Equal(SetVersion, calls[^1]);

            // The timer stopped with the icon in place: no more adds come.
            Thread.Sleep(200);
            Assert.Equal(3, shell.Calls().Count(m => m == Add));
        }
    }

    /// <summary>A stand-in for <c>Shell_NotifyIcon</c> that records each command and answers as the test says.</summary>
    /// <param name="answer">The answer to a command, given the command and how many times it came so far (this one included).</param>
    private sealed class FakeShell(Func<uint, int, bool> answer)
    {
        private readonly List<uint> calls = [];

        /// <summary>The <c>Shell_NotifyIcon</c> stand-in; runs on the tray thread.</summary>
        /// <param name="message">The <c>NIM_*</c> command.</param>
        /// <param name="data">The icon data (not looked at).</param>
        /// <returns>The test's answer.</returns>
        public bool Notify(uint message, NOTIFYICONDATAW data)
        {
            lock (calls)
            {
                calls.Add(message);
                return answer(message, calls.Count(m => m == message));
            }
        }

        /// <summary>The commands so far, in order.</summary>
        /// <returns>A copy.</returns>
        public uint[] Calls()
        {
            lock (calls)
            {
                return [.. calls];
            }
        }

        /// <summary>Waits (at most five seconds) until the commands so far satisfy a condition.</summary>
        /// <param name="condition">The condition.</param>
        /// <returns><see langword="true"/> when it held in time.</returns>
        public bool WaitFor(Func<uint[], bool> condition) => SpinWait.SpinUntil(() => condition(Calls()), TimeSpan.FromSeconds(5));
    }
}
