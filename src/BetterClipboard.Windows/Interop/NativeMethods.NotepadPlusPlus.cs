using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations the Notepad++ integration needs (CLAUDE.md §2.41): finding Notepad++'s windows, its plugin messages, a
/// buffer in its process to read paths through, its title-change events, and its process's start time and command line.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>Notepad++'s plugin messages start here (<c>NPPMSG = WM_USER + 1000</c>, <c>Notepad_plus_msgs.h</c>).</summary>
    internal const uint NPPMSG = WM_USER + 1000;

    /// <summary><c>int NPPM_GETNBOPENFILES(0, view)</c>: open documents; view 0 = all, 1 = main, 2 = second.</summary>
    internal const uint NPPM_GETNBOPENFILES = NPPMSG + 7;

    /// <summary><c>NPPM_GETNPPVERSION(1, 0)</c>: major in the high word, minor (e.g. 910 for 8.9.1) in the low word.</summary>
    internal const uint NPPM_GETNPPVERSION = NPPMSG + 50;

    /// <summary>
    /// <c>int NPPM_GETFULLPATHFROMBUFFERID(bufferId, wchar_t* path)</c>: with a null pointer, the path's length (-1 for an
    /// unknown id); with a pointer — in Notepad++'s own address space — the path is copied there with <c>lstrcpy</c>.
    /// </summary>
    internal const uint NPPM_GETFULLPATHFROMBUFFERID = NPPMSG + 58;

    /// <summary><c>bufferId NPPM_GETBUFFERIDFROMPOS(index, view)</c>: view 0 = main, 1 = sub; 0 for an invalid position.</summary>
    internal const uint NPPM_GETBUFFERIDFROMPOS = NPPMSG + 59;

    /// <summary><c>bufferId NPPM_GETCURRENTBUFFERID(0, 0)</c>: the document in front.</summary>
    internal const uint NPPM_GETCURRENTBUFFERID = NPPMSG + 60;

    /// <summary><c>PROCESS_VM_OPERATION</c>: allocate and free memory in the process.</summary>
    internal const uint PROCESS_VM_OPERATION = 0x0008;

    /// <summary><c>PROCESS_VM_READ</c>: read the process's memory.</summary>
    internal const uint PROCESS_VM_READ = 0x0010;

    /// <summary><c>MEM_COMMIT | MEM_RESERVE</c>.</summary>
    internal const uint MEM_COMMIT_RESERVE = 0x3000;

    /// <summary><c>MEM_RELEASE</c>.</summary>
    internal const uint MEM_RELEASE = 0x8000;

    /// <summary><c>PAGE_READWRITE</c>: data only, never executable.</summary>
    internal const uint PAGE_READWRITE = 0x04;

    /// <summary><c>EVENT_OBJECT_NAMECHANGE</c>: an object's name changed (a window's title).</summary>
    internal const uint EVENT_OBJECT_NAMECHANGE = 0x800C;

    /// <summary><c>WINEVENT_OUTOFCONTEXT</c>: the callback runs in this process, on the hooking thread's message loop.</summary>
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary><c>WINEVENT_SKIPOWNPROCESS</c>: no events of this process.</summary>
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    /// <summary><c>ProcessCommandLineInformation</c> for <see cref="NtQueryInformationProcess"/> (Windows 8.1+).</summary>
    internal const int ProcessCommandLineInformation = 60;

    /// <summary>A WinEvent callback.</summary>
    /// <param name="hook">The hook.</param>
    /// <param name="eventType">The event.</param>
    /// <param name="hwnd">The window.</param>
    /// <param name="idObject">The object (0 = the window itself).</param>
    /// <param name="idChild">The child (0 = the object itself).</param>
    /// <param name="eventThread">The thread that raised it.</param>
    /// <param name="eventTime">When.</param>
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    /// <summary>Finds a top-level window by class after another (enumerates every window of a class).</summary>
    /// <param name="parent">0 for top-level windows.</param>
    /// <param name="childAfter">The previous match, or 0.</param>
    /// <param name="className">The class.</param>
    /// <param name="windowName">The title, or <see langword="null"/> for any.</param>
    /// <returns>The window, or 0.</returns>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);

    /// <summary>A window's title length (another process's window: from its stored caption, no message sent).</summary>
    /// <param name="hWnd">The window.</param>
    /// <returns>The length in characters.</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(nint hWnd);

    /// <summary>A window's title (another process's window: from its stored caption, no message sent).</summary>
    /// <param name="hWnd">The window.</param>
    /// <param name="text">The buffer.</param>
    /// <param name="maxCount">Its size in characters.</param>
    /// <returns>Characters copied.</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    internal static unsafe partial int GetWindowText(nint hWnd, char* text, int maxCount);

    /// <summary>Hooks WinEvents of one process (out of context: the callback runs on this thread's message loop).</summary>
    /// <param name="eventMin">First event.</param>
    /// <param name="eventMax">Last event.</param>
    /// <param name="module">0 for an out-of-context hook.</param>
    /// <param name="callback">
    /// The callback as a function pointer (<see cref="Marshal.GetFunctionPointerForDelegate{TDelegate}(TDelegate)"/> of a
    /// <see cref="WinEventProc"/> the caller keeps alive while hooked: source-generated P/Invokes do not marshal delegates).
    /// </param>
    /// <param name="processId">The process.</param>
    /// <param name="threadId">0 for every thread.</param>
    /// <param name="flags"><see cref="WINEVENT_OUTOFCONTEXT"/> and friends.</param>
    /// <returns>The hook, or 0.</returns>
    [LibraryImport("user32.dll")]
    internal static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, nint callback, uint processId, uint threadId, uint flags);

    /// <summary>Removes a WinEvent hook.</summary>
    /// <param name="hook">The hook.</param>
    /// <returns>Whether it was removed.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    /// <summary>Allocates memory in another process.</summary>
    /// <param name="process">A handle with <see cref="PROCESS_VM_OPERATION"/>.</param>
    /// <param name="address">0 to let Windows choose.</param>
    /// <param name="size">Bytes.</param>
    /// <param name="allocationType"><see cref="MEM_COMMIT_RESERVE"/>.</param>
    /// <param name="protect"><see cref="PAGE_READWRITE"/>.</param>
    /// <returns>The address in that process, or 0.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);

    /// <summary>Frees memory allocated in another process.</summary>
    /// <param name="process">A handle with <see cref="PROCESS_VM_OPERATION"/>.</param>
    /// <param name="address">The address <see cref="VirtualAllocEx"/> returned.</param>
    /// <param name="size">0 with <see cref="MEM_RELEASE"/>.</param>
    /// <param name="freeType"><see cref="MEM_RELEASE"/>.</param>
    /// <returns>Whether it was freed.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    /// <summary>Reads another process's memory.</summary>
    /// <param name="process">A handle with <see cref="PROCESS_VM_READ"/>.</param>
    /// <param name="address">Where.</param>
    /// <param name="buffer">Into.</param>
    /// <param name="size">Bytes.</param>
    /// <param name="read">Bytes read.</param>
    /// <returns>Whether it was read.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

    /// <summary>A process's times (its start time is <paramref name="creation"/>).</summary>
    /// <param name="process">A handle with <see cref="PROCESS_QUERY_LIMITED_INFORMATION"/>.</param>
    /// <param name="creation">Start, as a FILETIME.</param>
    /// <param name="exit">Exit.</param>
    /// <param name="kernel">Kernel time.</param>
    /// <param name="user">User time.</param>
    /// <returns>Whether they were read.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>
    /// <c>NtQueryInformationProcess</c>; with <see cref="ProcessCommandLineInformation"/> (and
    /// <see cref="PROCESS_QUERY_LIMITED_INFORMATION"/>) it writes a <c>UNICODE_STRING</c> header followed by the command line.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <param name="informationClass">The class.</param>
    /// <param name="information">The buffer.</param>
    /// <param name="length">Its size.</param>
    /// <param name="returnLength">Bytes needed or written.</param>
    /// <returns>An NTSTATUS (0 = success).</returns>
    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtQueryInformationProcess(nint process, int informationClass, void* information, int length, out int returnLength);

    /// <summary>The system's ANSI code page (what Notepad++ writes an "ANSI" document's backup in).</summary>
    /// <returns>The code page.</returns>
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetACP();
}
