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

    /// <summary>
    /// A notification asked for while the icon is hidden (a silent restart's new process, before the hand-over) waits, and
    /// goes out once the icon shows; it is never lost and never shown twice.
    /// </summary>
    [Fact]
    public void Notification_WhileHidden_WaitsForTheIcon()
    {
        var shell = new FakeShell((_, _) => true);
        using var tray = new TrayIcon(NoIcon, "BC-TEST", startHidden: true, shell.Notify, retryAddInterval: TimeSpan.FromMilliseconds(30));
        tray.ShowNotification("BC-TEST title", "BC-TEST text", "bc-test");
        Sync(tray, shell);
        Assert.Equal(0, shell.Notifications());

        tray.Reveal();
        Assert.True(shell.WaitFor(_ => shell.Notifications() == 1));
        Sync(tray, shell);
        Assert.Equal(1, shell.Notifications());
    }

    /// <summary>
    /// A click on a notification shown with a tag raises that tag once (the budget warning opens Settings with it); a
    /// second click event for the same notification, and a click on one shown without a tag, raise nothing.
    /// </summary>
    [Fact]
    public void NotificationClick_RaisesItsTagOnce()
    {
        var shell = new FakeShell((_, _) => true);
        using var tray = new TrayIcon(NoIcon, "BC-TEST", startHidden: false, shell.Notify, retryAddInterval: TimeSpan.FromMilliseconds(30));
        var clicks = new List<string>();
        tray.NotificationClicked += (_, tag) =>
        {
            lock (clicks)
            {
                clicks.Add(tag);
            }
        };

        int selects = 0;
        tray.Invoked += (_, _) => Interlocked.Increment(ref selects);

        // The notification is shown before the clicks are sent: Windows reports clicks only on a notification it shows.
        tray.ShowNotification("BC-TEST title", "BC-TEST text", "history-budget");
        Assert.True(shell.WaitFor(_ => shell.Notifications() == 1));
        Click(tray);
        Click(tray);
        Barrier(tray, () => Volatile.Read(ref selects));
        lock (clicks)
        {
            Assert.Equal(["history-budget"], clicks);
        }

        tray.ShowNotification("BC-TEST plain", "BC-TEST no tag");
        Assert.True(shell.WaitFor(_ => shell.Notifications() == 2));
        Click(tray);
        Barrier(tray, () => Volatile.Read(ref selects));
        lock (clicks)
        {
            Assert.Single(clicks);
        }
    }

    /// <summary>Sends the tray window the click event Windows sends for a notification (version-4 callback).</summary>
    /// <param name="tray">The tray icon.</param>
    private static void Click(TrayIcon tray) =>
        Assert.True(PostMessage(tray.HostWindow, TrayIcon.CallbackMessage, 0, (nint)TrayIcon.NotificationClickEvent));

    /// <summary>
    /// Waits until the tray window has handled every message posted to it before: a posted select event (a left click on
    /// the icon, <c>NIN_SELECT</c> = <c>WM_USER</c>) comes after them in the window's queue, and its <c>Invoked</c> shows
    /// it was handled. (Work queued with <c>Post</c> is no barrier for posted messages: one wake-up runs all of it.)
    /// </summary>
    /// <param name="tray">The tray icon.</param>
    /// <param name="selects">How many select events were handled so far.</param>
    private static void Barrier(TrayIcon tray, Func<int> selects)
    {
        int before = selects();
        Assert.True(PostMessage(tray.HostWindow, TrayIcon.CallbackMessage, 0, 0x400));
        Assert.True(SpinWait.SpinUntil(() => selects() > before, TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// Waits until the tray thread has run everything queued with <c>Post</c> before: a tooltip change is queued the same
    /// way, so once Explorer's stand-in sees it, every earlier action ran.
    /// </summary>
    /// <param name="tray">The tray icon.</param>
    /// <param name="shell">The stand-in.</param>
    private static void Sync(TrayIcon tray, FakeShell shell)
    {
        int before = shell.Calls().Length;
        tray.SetTooltip("BC-TEST sync " + before);
        Assert.True(shell.WaitFor(calls => calls.Length > before));
    }

    /// <summary>A stand-in for <c>Shell_NotifyIcon</c> that records each command and answers as the test says.</summary>
    /// <param name="answer">The answer to a command, given the command and how many times it came so far (this one included).</param>
    private sealed class FakeShell(Func<uint, int, bool> answer)
    {
        /// <summary><c>NIF_INFO</c>: the call shows a notification.</summary>
        private const uint InfoFlag = 0x10;

        private readonly List<uint> calls = [];
        private int notifications;

        /// <summary>The <c>Shell_NotifyIcon</c> stand-in; runs on the tray thread.</summary>
        /// <param name="message">The <c>NIM_*</c> command.</param>
        /// <param name="data">The icon data: only its flags are looked at (a notification's).</param>
        /// <returns>The test's answer.</returns>
        public bool Notify(uint message, NOTIFYICONDATAW data)
        {
            lock (calls)
            {
                calls.Add(message);
                if (message == Modify && (data.uFlags & InfoFlag) != 0)
                {
                    notifications++;
                }

                return answer(message, calls.Count(m => m == message));
            }
        }

        /// <summary>How many notifications were shown so far.</summary>
        /// <returns>The count.</returns>
        public int Notifications()
        {
            lock (calls)
            {
                return notifications;
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
