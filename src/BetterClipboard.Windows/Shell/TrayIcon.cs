using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Windows.Interop;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// One entry of the tray context menu.
/// </summary>
/// <param name="Text">Label (ignored for separators). Use <c>&amp;</c> for an access key.</param>
/// <param name="Invoke">Action run on the tray thread when chosen — marshal to the UI thread inside it.</param>
/// <param name="IsChecked">Show a check mark.</param>
/// <param name="IsEnabled">Allow choosing it.</param>
/// <param name="IsSeparator">Render a separator line instead of an item.</param>
public sealed record TrayMenuItem(string Text, Action? Invoke, bool IsChecked = false, bool IsEnabled = true, bool IsSeparator = false)
{
    /// <summary>A separator line.</summary>
    public static TrayMenuItem Separator { get; } = new(string.Empty, null, IsSeparator: true);
}

/// <summary>
/// A notification-area (tray) icon implemented directly on <c>Shell_NotifyIcon</c>, with a native popup
/// menu — no UI-framework dependency, so it keeps working even when no XAML window exists.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>NOTIFYICON_VERSION_4</c> callbacks: a left click arrives as <c>NIN_SELECT</c>, keyboard
/// activation as <c>NIN_KEYSELECT</c>, and the context menu as <c>WM_CONTEXTMENU</c> with the anchor point
/// packed in wParam.
/// </para>
/// <para>
/// The host window is a hidden <b>top-level</b> window (not message-only) because only top-level windows
/// receive the <c>TaskbarCreated</c> broadcast sent when Explorer restarts — without re-adding on that
/// message the icon would silently vanish after an Explorer crash or after "free Win+V" restarts it.
/// </para>
/// <para>
/// <b>Handing the icon to another process</b> (the silent restart, CLAUDE.md §2.46). An icon belongs to its window, and
/// two processes cannot share one, so the new process adds its own icon <b>hidden</b> (<c>NIS_HIDDEN</c>: registered, not
/// shown anywhere), and the old process, at the moment it lets go, reveals that icon and removes its own in two calls a
/// fraction of a millisecond apart (<see cref="HandOverTo"/>). Explorer handles both before it draws again, so the icon
/// stays in its place [verified on Windows 11 25H2 with the icon pinned to the taskbar: no frame changed]. Adding the
/// new icon visibly first showed two icons for about 60 ms; removing the old one first would leave a gap. Explorer
/// accepts the reveal from the old process although the icon's window belongs to the new one.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 0x40;
    private const uint IconId = 1;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIS_HIDDEN = 0x1;

    /// <summary>
    /// The title of the icon's host window: <see cref="MessageWindowThread"/> titles its window "BetterClipboard {name}",
    /// and the name here is "Tray". Another process finds a BetterClipboard's icon window by it (its class name is made up
    /// per process); keep the two in step.
    /// </summary>
    private const string HostWindowTitle = "BetterClipboard Tray";
    private const uint NIIF_INFO = 0x1, NIIF_NOSOUND = 0x10;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;

    private readonly MessageWindowThread window;
    private readonly uint taskbarCreatedMessage;
    private readonly string iconPath;
    private nint icon;
    private string tooltip;
    private bool added;

    /// <summary>The icon is registered but not shown (<c>NIS_HIDDEN</c>) until <see cref="Reveal"/>. Tray thread only.</summary>
    private bool hidden;

    /// <summary>
    /// Creates the tray icon: visible immediately, or registered but hidden (<paramref name="startHidden"/>) for a silent
    /// restart's new process, whose icon the old process reveals when it lets go of its own (<see cref="HandOverTo"/>).
    /// </summary>
    /// <param name="iconPath">Path of a multi-size <c>.ico</c> file.</param>
    /// <param name="tooltip">Hover text (max 127 chars).</param>
    /// <param name="startHidden">
    /// Register the icon hidden. Footgun: it stays invisible until someone reveals it (<see cref="Reveal"/>, or another
    /// process's <see cref="HandOverTo"/>); an Explorer restart meanwhile re-adds it hidden too.
    /// </param>
    /// <exception cref="System.ComponentModel.Win32Exception">The host window could not be created.</exception>
    public TrayIcon(string iconPath, string tooltip, bool startHidden = false)
    {
        this.iconPath = iconPath;
        this.tooltip = tooltip;
        hidden = startHidden;
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        window = new MessageWindowThread("Tray", messageOnly: false);
        window.MessageHandler = OnMessage;
        window.InvokeAsync(() =>
        {
            icon = LoadIcon();
            Add();
        }).GetAwaiter().GetResult();
    }

    /// <summary>Raised on the tray thread on left click / keyboard activation.</summary>
    public event EventHandler? Invoked;

    /// <summary>
    /// Builds the context menu each time it opens (so check marks reflect current state). Called on the tray thread.
    /// </summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    /// <summary>
    /// Updates the hover text.
    /// </summary>
    /// <param name="text">New tooltip (truncated to 127 chars).</param>
    public void SetTooltip(string text) => window.Post(() =>
    {
        tooltip = text;
        if (added)
        {
            var data = CreateData(NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIcon(NIM_MODIFY, data);
        }
    });

    /// <summary>
    /// Shows a short notification from the tray icon (Windows 10 and 11 show it as a toast, without a sound). For a message
    /// the user must see although no BetterClipboard window is open, such as "copied, but not pasted".
    /// </summary>
    /// <remarks>
    /// Nothing appears while the icon is not in the notification area yet (early sign-in), or when the user turned off
    /// notifications for the app; callers must not depend on it being seen.
    /// </remarks>
    /// <param name="title">The title (truncated to 63 characters).</param>
    /// <param name="text">The message (truncated to 255 characters).</param>
    public void ShowNotification(string title, string text) => window.Post(() =>
    {
        if (!added)
        {
            return;
        }

        var data = CreateData(NIF_INFO);
        data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
        SetInfoTexts(ref data, title, text);
        Shell_NotifyIcon(NIM_MODIFY, data);
    });

    /// <summary>
    /// Shows an icon created hidden; nothing happens for a visible one. Also right after another process revealed it
    /// (<see cref="HandOverTo"/>): Explorer shows it already, and this brings this object in line, so an Explorer restart
    /// later re-adds it visible.
    /// </summary>
    public void Reveal() => window.Post(() =>
    {
        if (!hidden)
        {
            return;
        }

        hidden = false;
        if (added)
        {
            var data = CreateData(NIF_STATE);
            data.dwState = 0;
            data.dwStateMask = NIS_HIDDEN;
            Shell_NotifyIcon(NIM_MODIFY, data);
        }
    });

    /// <summary>
    /// Hands the notification-area icon to another BetterClipboard process that created its own hidden: reveals that icon
    /// and removes this one, back to back on the tray thread, so the notification area shows neither two icons nor none
    /// (see the class remarks). Afterwards this object has no icon; <see cref="Dispose"/> still ends its thread.
    /// </summary>
    /// <param name="processId">The other process (it must have created its icon with <c>startHidden</c>).</param>
    /// <returns>
    /// <see langword="true"/> when the other icon was revealed and this one removed. <see langword="false"/> when the other
    /// process has no icon window, or Explorer refused the reveal (no icon of that window yet, Explorer not running): this
    /// icon is then left as it was, and the caller removes it with <see cref="Dispose"/>, leaving a moment without an icon
    /// until the other process reveals its own.
    /// </returns>
    /// <exception cref="ObjectDisposedException">This icon was disposed.</exception>
    public bool HandOverTo(int processId)
    {
        nint other = FindHostWindow(processId);
        if (other == 0)
        {
            return false;
        }

        return window.InvokeAsync(() =>
        {
            // The other icon has the same id on its own window; only its state changes. Both calls go to Explorer within a
            // fraction of a millisecond, before its next layout pass.
            var reveal = CreateData(NIF_STATE);
            reveal.hWnd = other;
            reveal.dwState = 0;
            reveal.dwStateMask = NIS_HIDDEN;
            if (!Shell_NotifyIcon(NIM_MODIFY, reveal))
            {
                return false;
            }

            if (added)
            {
                Shell_NotifyIcon(NIM_DELETE, CreateData(0));
                added = false;
            }

            return true;
        }).GetAwaiter().GetResult();
    }

    /// <summary>Removes the icon and stops the tray thread.</summary>
    public void Dispose()
    {
        try
        {
            window.InvokeAsync(() =>
            {
                if (added)
                {
                    Shell_NotifyIcon(NIM_DELETE, CreateData(0));
                    added = false;
                }

                if (icon != 0)
                {
                    DestroyIcon(icon);
                    icon = 0;
                }
            }).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            AppLog.Warn($"Removing the tray icon failed: {ex.InnerException?.Message}");
        }

        window.Dispose();
    }

    /// <summary>Tray-thread message handler.</summary>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">Parameter.</param>
    /// <param name="lParam">Parameter.</param>
    /// <returns>0 when handled, otherwise <see langword="null"/>.</returns>
    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == taskbarCreatedMessage && taskbarCreatedMessage != 0)
        {
            // Explorer restarted: the old icon is gone, add it again.
            added = false;
            Add();
            return 0;
        }

        if (msg != CallbackMessage)
        {
            return null;
        }

        uint trayEvent = (uint)(lParam & 0xFFFF);
        switch (trayEvent)
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
                Invoked?.Invoke(this, EventArgs.Empty);
                return 0;
            case WM_CONTEXTMENU:
                // Version 4 packs the anchor point into wParam as signed 16-bit coordinates.
                int x = (short)(wParam & 0xFFFF);
                int y = (short)((wParam >> 16) & 0xFFFF);
                ShowMenu(x, y);
                return 0;
        }

        return 0;
    }

    /// <summary>Shows the context menu and runs the chosen item.</summary>
    /// <param name="x">Screen X.</param>
    /// <param name="y">Screen Y.</param>
    private void ShowMenu(int x, int y)
    {
        var items = MenuProvider?.Invoke();
        if (items is null || items.Count == 0)
        {
            return;
        }

        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                uint flags = item.IsSeparator ? MF_SEPARATOR : MF_STRING | (item.IsChecked ? MF_CHECKED : 0) | (item.IsEnabled ? 0 : MF_GRAYED);
                AppendMenu(menu, flags, (nuint)(i + 1), item.IsSeparator ? null : item.Text);
            }

            // The owner must be foreground or the menu will not close when the user clicks elsewhere (KB135788).
            SetForegroundWindow(window.Handle);
            int chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, window.Handle, 0);
            PostMessage(window.Handle, WM_NULL, 0, 0);
            if (chosen > 0 && chosen <= items.Count)
            {
                items[chosen - 1].Invoke?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Finds the icon window of another BetterClipboard process: a top-level window titled <see cref="HostWindowTitle"/>
    /// that belongs to <paramref name="processId"/>.
    /// </summary>
    /// <param name="processId">The process.</param>
    /// <returns>The window, or 0 when that process has none.</returns>
    private static nint FindHostWindow(int processId)
    {
        // Every BetterClipboard instance has one (an isolated one too): walk them all; a bound keeps a changing window list
        // from looping.
        nint candidate = 0;
        for (int i = 0; i < 64; i++)
        {
            candidate = FindWindowByTitle(0, candidate, 0, HostWindowTitle);
            if (candidate == 0)
            {
                return 0;
            }

            GetWindowThreadProcessId(candidate, out uint owner);
            if (owner == (uint)processId)
            {
                return candidate;
            }
        }

        return 0;
    }

    /// <summary>Adds the icon (hidden while <see cref="hidden"/>) and opts into version-4 callbacks.</summary>
    private void Add()
    {
        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP | (hidden ? NIF_STATE : 0));
        if (hidden)
        {
            data.dwState = NIS_HIDDEN;
            data.dwStateMask = NIS_HIDDEN;
        }

        added = Shell_NotifyIcon(NIM_ADD, data);
        if (!added)
        {
            // Happens when Explorer is not running yet (early logon); TaskbarCreated will retry.
            AppLog.Warn("Shell_NotifyIcon(NIM_ADD) failed; waiting for TaskbarCreated.");
            return;
        }

        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, data);
    }

    /// <summary>Builds the notify-icon structure.</summary>
    /// <param name="flags">Valid-member flags.</param>
    /// <returns>The structure.</returns>
    private unsafe NOTIFYICONDATAW CreateData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = icon,
        };
        var text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        for (int i = 0; i < text.Length; i++)
        {
            data.szTip[i] = text[i];
        }

        data.szTip[text.Length] = '\0';
        return data;
    }

    /// <summary>Copies a notification's title and text into the structure's fixed buffers, NUL-terminated and truncated.</summary>
    /// <param name="data">The structure to fill.</param>
    /// <param name="title">The title (at most 63 characters are kept).</param>
    /// <param name="text">The text (at most 255 characters are kept).</param>
    private static unsafe void SetInfoTexts(ref NOTIFYICONDATAW data, string title, string text)
    {
        var shortTitle = title.Length > 63 ? title[..63] : title;
        for (int i = 0; i < shortTitle.Length; i++)
        {
            data.szInfoTitle[i] = shortTitle[i];
        }

        data.szInfoTitle[shortTitle.Length] = '\0';
        var shortText = text.Length > 255 ? text[..255] : text;
        for (int i = 0; i < shortText.Length; i++)
        {
            data.szInfo[i] = shortText[i];
        }

        data.szInfo[shortText.Length] = '\0';
    }

    /// <summary>Loads the small icon at the system DPI.</summary>
    /// <returns>The icon handle, or 0 when the file is missing/invalid.</returns>
    private nint LoadIcon()
    {
        uint dpi = GetDpiForSystem();
        int width = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        int height = GetSystemMetricsForDpi(SM_CYSMICON, dpi);
        nint handle = LoadImage(0, iconPath, IMAGE_ICON, width, height, LR_LOADFROMFILE);
        if (handle == 0)
        {
            AppLog.Warn($"Tray icon '{iconPath}' could not be loaded (error {Marshal.GetLastPInvokeError()}).");
        }

        return handle;
    }
}
