using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterClipboard.Core.Content;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Collection for tests that switch the process into a private window station: they must run alone,
/// because the clipboard belongs to the <i>process</i> window station and every clipboard call made by any
/// thread of the test process during that time lands in the private one.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IsolatedClipboardCollection
{
    /// <summary>Collection name.</summary>
    public const string Name = "Isolated clipboard";
}

/// <summary>
/// A private, anonymous window station with its own desktop — and therefore its <b>own clipboard</b> —
/// so capture tests exercise the real Win32 clipboard without replacing the user's clipboard content or
/// flooding Windows' 25-item Win+V history (which cannot be restored).
/// </summary>
/// <remarks>
/// Named window stations need elevation; an anonymous one (null name) is allowed for standard users. The
/// process window station is switched for the fixture's lifetime (clipboard calls resolve through it) and
/// restored on dispose. Threads join the private desktop through <see cref="CreateWindowThread"/>, which
/// must be MTA (see <see cref="MessageWindowThread"/>).
/// </remarks>
internal sealed class IsolatedClipboard : IDisposable
{
    private const uint WINSTA_ALL_ACCESS = 0x37F;
    private const uint GENERIC_ALL = 0x10000000;

    private readonly nint originalStation;
    private readonly nint station;

    /// <summary>
    /// Creates the window station and desktop and switches the process to it.
    /// </summary>
    /// <exception cref="Win32Exception">The objects could not be created or selected.</exception>
    public IsolatedClipboard()
    {
        originalStation = GetProcessWindowStation();
        station = CreateWindowStationW(null, 0, WINSTA_ALL_ACCESS, 0);
        if (station == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowStation failed.");
        }

        if (!SetProcessWindowStation(station))
        {
            int error = Marshal.GetLastPInvokeError();
            CloseWindowStation(station);
            throw new Win32Exception(error, "SetProcessWindowStation failed.");
        }

        // CreateDesktop creates the desktop inside the process's current window station, hence the order.
        Desktop = CreateDesktopW("BetterClipboardTests", 0, 0, 0, GENERIC_ALL, 0);
        if (Desktop == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            SetProcessWindowStation(originalStation);
            CloseWindowStation(station);
            throw new Win32Exception(error, "CreateDesktop failed.");
        }
    }

    /// <summary>The private desktop (pass to <c>ClipboardMonitor.IsolatedDesktop</c>).</summary>
    public nint Desktop { get; }

    /// <summary>Creates a message-window thread living on the private desktop.</summary>
    /// <param name="name">Thread/window name.</param>
    /// <returns>The thread (caller disposes).</returns>
    public MessageWindowThread CreateWindowThread(string name) => new(name, messageOnly: true, sta: false, desktop: Desktop);

    /// <summary>Restores the process window station and closes the private objects (best effort).</summary>
    public void Dispose()
    {
        SetProcessWindowStation(originalStation);
        CloseDesktop(Desktop);
        CloseWindowStation(station);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowStationW(string? name, uint flags, uint desiredAccess, nint securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWindowStation(nint windowStation);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateDesktopW(string name, nint device, nint devMode, uint flags, uint desiredAccess, nint securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseWindowStation(nint windowStation);
}

/// <summary>
/// "Another application" copying text inside an <see cref="IsolatedClipboard"/>: owns its own window and
/// writes raw <c>CF_UNICODETEXT</c> sessions, optionally delay-rendered.
/// </summary>
internal sealed class ClipboardProducer : IDisposable
{
    private const uint CF_UNICODETEXT = 13;
    private const uint WM_RENDERFORMAT = 0x0305;

    private readonly MessageWindowThread thread;
    private string? promisedText;

    /// <summary>
    /// Starts the producer's window thread on the isolated desktop.
    /// </summary>
    /// <param name="isolation">The private clipboard.</param>
    public ClipboardProducer(IsolatedClipboard isolation)
    {
        thread = isolation.CreateWindowThread("Producer");
        thread.MessageHandler = (msg, _, _) =>
        {
            // Delayed rendering: the consumer's GetClipboardData sends this to us, the owner.
            if (msg == WM_RENDERFORMAT && promisedText is not null)
            {
                SetClipboardData(CF_UNICODETEXT, Allocate(promisedText));
                return 0;
            }

            return null;
        };
    }

    /// <summary>Number of delayed renders served.</summary>
    public int RendersServed { get; private set; }

    /// <summary>
    /// Writes each text as its own clipboard session, back to back on the producer thread (no awaits in
    /// between, so a zero gap is as fast as Win32 allows).
    /// </summary>
    /// <remarks>
    /// Gaps are busy-waited on a <see cref="Stopwatch"/>: <see cref="Thread.Sleep(int)"/> rounds up to the
    /// system timer tick (15.6 ms by default), which would make every short gap silently ~15 ms long.
    /// </remarks>
    /// <param name="texts">Texts in order.</param>
    /// <param name="gap">Time between the starts of consecutive copies (zero = none).</param>
    /// <returns>A task completing after the last session closed.</returns>
    public Task WriteBurstAsync(IReadOnlyList<string> texts, TimeSpan gap = default) => thread.InvokeAsync(() =>
    {
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            WriteSession(() => SetClipboardData(CF_UNICODETEXT, Allocate(text)));
            var next = gap * (i + 1);
            while (gap > TimeSpan.Zero && clock.Elapsed < next)
            {
                Thread.SpinWait(64);
            }
        }
    });

    /// <summary>
    /// Promises <c>CF_UNICODETEXT</c> without data (delayed rendering); it is rendered when a consumer asks.
    /// </summary>
    /// <param name="text">Text to render on demand.</param>
    /// <returns>A task completing once the promise is on the clipboard.</returns>
    public Task PromiseAsync(string text) => thread.InvokeAsync(() =>
    {
        promisedText = text;
        WriteSession(() => SetClipboardData(CF_UNICODETEXT, 0));
    });

    /// <summary>
    /// Reads the isolated clipboard's current text (to verify what another component wrote).
    /// </summary>
    /// <returns>The text, or <see langword="null"/> when there is none.</returns>
    public Task<string?> ReadTextAsync() => thread.InvokeAsync(() =>
    {
        var deadline = Environment.TickCount64 + 2000;
        while (!OpenClipboard(thread.Handle))
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new IOException("The isolated clipboard stayed locked.");
            }

            Thread.Sleep(1);
        }

        try
        {
            nint handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == 0)
            {
                return null;
            }

            nint pointer = GlobalLock(handle);
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    });

    /// <summary>Stops the producer thread.</summary>
    public void Dispose() => thread.Dispose();

    /// <summary>One Open/Empty/…/Close session, retrying briefly while the consumer is reading.</summary>
    /// <param name="fill">Places the data.</param>
    /// <exception cref="IOException">The clipboard stayed locked for 2 s.</exception>
    private void WriteSession(Action fill)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (!OpenClipboard(thread.Handle))
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new IOException("The isolated clipboard stayed locked.");
            }

            Thread.Sleep(1);
        }

        try
        {
            EmptyClipboard();
            fill();
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Copies text into a movable global block (ownership passes to the clipboard).</summary>
    /// <param name="text">Text.</param>
    /// <returns>The block.</returns>
    private nint Allocate(string text)
    {
        if (ReferenceEquals(text, promisedText))
        {
            RendersServed++;
        }

        var bytes = UnicodeTextCodec.Encode(text);
        nint memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        nint pointer = GlobalLock(memory);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(memory);
        return memory;
    }
}
