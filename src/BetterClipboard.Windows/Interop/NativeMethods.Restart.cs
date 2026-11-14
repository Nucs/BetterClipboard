using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for the silent restart (CLAUDE.md §2.46): starting the new process without Windows' launch feedback,
/// waiting on it, and telling whether the user is busy right now.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// <c>STARTF_FORCEOFFFEEDBACK</c>: no "app starting" pointer while the new process starts. Without it Windows shows
    /// the busy pointer until the process asks for its first message or a few seconds pass, which the user would see.
    /// </summary>
    internal const uint STARTF_FORCEOFFFEEDBACK = 0x00000080;

    /// <summary><c>CREATE_NO_WINDOW</c>: a console program gets no console window (tests and helpers only).</summary>
    internal const uint CREATE_NO_WINDOW = 0x08000000;

    /// <summary>
    /// <c>CREATE_UNICODE_ENVIRONMENT</c>: the environment block passed to <see cref="CreateProcess"/> is UTF-16. Required
    /// whenever a block is passed from this (Unicode) process.
    /// </summary>
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    /// <summary><c>STILL_ACTIVE</c>: what <see cref="GetExitCodeProcess"/> reports for a process that has not ended.</summary>
    internal const uint STILL_ACTIVE = 259;

    /// <summary><c>WAIT_OBJECT_0</c>: the waited-on object is signaled.</summary>
    internal const uint WAIT_OBJECT_0 = 0;

    /// <summary><c>WAIT_TIMEOUT</c>: the wait ended before the object was signaled.</summary>
    internal const uint WAIT_TIMEOUT = 0x102;

    /// <summary>
    /// <c>STARTUPINFOW</c> (processthreadsapi.h): how the new process's first window and its launch feedback look. Only
    /// <see cref="cb"/> and <see cref="dwFlags"/> are used here; the string fields stay zero.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        /// <summary>Size of this structure in bytes, set by the caller.</summary>
        public uint cb;

        /// <summary>Reserved (zero).</summary>
        public nint lpReserved;

        /// <summary>Desktop name (zero: the caller's).</summary>
        public nint lpDesktop;

        /// <summary>Console title (zero).</summary>
        public nint lpTitle;

        /// <summary>Console x (unused).</summary>
        public uint dwX;

        /// <summary>Console y (unused).</summary>
        public uint dwY;

        /// <summary>Console width (unused).</summary>
        public uint dwXSize;

        /// <summary>Console height (unused).</summary>
        public uint dwYSize;

        /// <summary>Console buffer columns (unused).</summary>
        public uint dwXCountChars;

        /// <summary>Console buffer rows (unused).</summary>
        public uint dwYCountChars;

        /// <summary>Console colors (unused).</summary>
        public uint dwFillAttribute;

        /// <summary><c>STARTF_*</c> flags.</summary>
        public uint dwFlags;

        /// <summary>Show command (unused without <c>STARTF_USESHOWWINDOW</c>).</summary>
        public ushort wShowWindow;

        /// <summary>Reserved (zero).</summary>
        public ushort cbReserved2;

        /// <summary>Reserved (zero).</summary>
        public nint lpReserved2;

        /// <summary>Standard input (unused without <c>STARTF_USESTDHANDLES</c>).</summary>
        public nint hStdInput;

        /// <summary>Standard output (unused).</summary>
        public nint hStdOutput;

        /// <summary>Standard error (unused).</summary>
        public nint hStdError;
    }

    /// <summary><c>PROCESS_INFORMATION</c>: the new process's and its first thread's handles and ids.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        /// <summary>Handle to the new process (close with <see cref="CloseHandle"/>).</summary>
        public nint hProcess;

        /// <summary>Handle to its first thread (close with <see cref="CloseHandle"/>).</summary>
        public nint hThread;

        /// <summary>The new process's id.</summary>
        public uint dwProcessId;

        /// <summary>Its first thread's id.</summary>
        public uint dwThreadId;
    }

    /// <summary><c>LASTINPUTINFO</c>: when the session last received keyboard or mouse input.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        /// <summary>Size of this structure in bytes, set by the caller.</summary>
        public uint cbSize;

        /// <summary>The tick count (<c>GetTickCount</c>) of the last input.</summary>
        public uint dwTime;
    }

    /// <summary>
    /// Starts a process. Used instead of <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
    /// because only <see cref="STARTUPINFOW.dwFlags"/> can turn the launch feedback off.
    /// </summary>
    /// <param name="lpApplicationName">The program's full path.</param>
    /// <param name="lpCommandLine">
    /// The whole command line, program first, NUL-terminated, in a writable buffer (CreateProcessW may change it); a pointer,
    /// because a <c>char[]</c> parameter would need runtime marshalling (CLAUDE.md §4).
    /// </param>
    /// <param name="lpProcessAttributes">Process security (zero: default, not inheritable).</param>
    /// <param name="lpThreadAttributes">Thread security (zero: default).</param>
    /// <param name="bInheritHandles">Whether the new process inherits this one's inheritable handles.</param>
    /// <param name="dwCreationFlags"><c>CREATE_*</c> flags.</param>
    /// <param name="lpEnvironment">Environment block (zero: this process's).</param>
    /// <param name="lpCurrentDirectory">The new process's working folder.</param>
    /// <param name="lpStartupInfo">Window and feedback settings.</param>
    /// <param name="lpProcessInformation">Receives the handles and ids.</param>
    /// <returns><see langword="true"/> on success; see the last error otherwise.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateProcess(
        string lpApplicationName,
        char* lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    /// <summary>Reads a process's exit code.</summary>
    /// <param name="hProcess">The process (needs query access; a handle from <see cref="CreateProcess"/> has it).</param>
    /// <param name="lpExitCode">Receives the code, or <see cref="STILL_ACTIVE"/> while the process runs.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    /// <summary>Waits for a kernel object (here: a process ending).</summary>
    /// <param name="hHandle">The object.</param>
    /// <param name="dwMilliseconds">How long to wait; 0 only tests the state.</param>
    /// <returns><see cref="WAIT_OBJECT_0"/>, <see cref="WAIT_TIMEOUT"/>, or a failure code.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    /// <summary>Reads when the session last received keyboard or mouse input.</summary>
    /// <param name="plii">Receives the tick count; <see cref="LASTINPUTINFO.cbSize"/> must be set.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>
    /// <c>FindWindowExW</c> by title alone: the next top-level window after <paramref name="childAfter"/> titled
    /// <paramref name="windowName"/>, whatever its class (a tray icon's host window has a class name of its own in every
    /// process). Walk all of them by passing the last one found.
    /// </summary>
    /// <param name="parent">0: top-level windows (message-only windows are never found this way).</param>
    /// <param name="childAfter">0 to start, else the window found last.</param>
    /// <param name="className">0: any class.</param>
    /// <param name="windowName">The exact title.</param>
    /// <returns>The window, or 0 when there is no further one.</returns>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowByTitle(nint parent, nint childAfter, nint className, string windowName);
}
