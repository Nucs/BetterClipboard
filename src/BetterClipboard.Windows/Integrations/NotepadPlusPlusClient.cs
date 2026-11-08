using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BetterClipboard.Core.NotepadPlusPlus;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Decides whether the process behind a "Notepad++" window really is Notepad++ before BetterClipboard places a buffer in it
/// or records what it reports (CLAUDE.md §2.41).
/// </summary>
/// <remarks>
/// Any process can create a window of class "Notepad++"; such an impostor could make up paths for the history. So the
/// window's owner must be named <c>notepad++.exe</c> and carry a valid Authenticode signature (no UI, no revocation lookup,
/// local caches only) by organization <c>NOTEPAD++</c> — verified on 8.9.1 (signer <c>CN="NOTEPAD++", O="NOTEPAD++"</c>).
/// Verdicts are cached per file version, like <see cref="EverythingOwnerVerifier"/>'s, whose signature check this shares.
/// Thread-safe.
/// </remarks>
public static class NotepadPlusPlusOwnerVerifier
{
    /// <summary>Signer organizations (subject <c>O=</c>) Notepad++ is signed with, compared case-insensitively.</summary>
    public static readonly IReadOnlySet<string> TrustedOrganizations = new HashSet<string>(["NOTEPAD++"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The reason when the window's process cannot be looked at (an elevated or exited process). Worded like
    /// <see cref="EverythingOwnerVerifier"/>'s, so both share one catalog entry and its translations.
    /// </summary>
    private const string ProcessNotInspected = "its process could not be inspected (it may be elevated or gone)";

    /// <summary>{0}: the file name of the program behind the window, which is not Notepad++'s.</summary>
    private const string NotNotepadPlusPlusProcess = "its process is {0}, not notepad++.exe";

    /// <summary>The reason when the executable cannot be read.</summary>
    private const string ExecutableUnreadable = "its executable could not be read";

    /// <summary>{0}: <c>WinVerifyTrust</c>'s result (an HRESULT, shown in hex).</summary>
    private const string NoValidSignature = "its executable has no valid signature (0x{0:X8})";

    /// <summary>The reason when the signature is valid but its certificate cannot be read.</summary>
    private const string SignerUnreadable = "its signer could not be read";

    /// <summary>{0}: the organization that signed it (the trusted verdict's reason).</summary>
    private const string SignedBy = "signed by {0}";

    /// <summary>{0}: the organization that signed it instead of Notepad++.</summary>
    private const string SignedByOther = "it is signed by {0}, not Notepad++";

    /// <summary>What <see cref="SignedByOther"/> names when the certificate names no organization.</summary>
    private const string UnknownOrganization = "an unknown organization";

    /// <summary>Verdicts by file version (path|size|last write ticks).</summary>
    private static readonly ConcurrentDictionary<string, NotepadPlusPlusTrust> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every reason a verdict gives (<see cref="NotepadPlusPlusTrust.Reason"/>), as composite formats. The log keeps the
    /// English; Settings shows a reason in the interface language through <c>UiText.TranslateFormatted</c> (as part of
    /// <see cref="NotepadPlusPlusClient.ProblemTemplates"/>), so a new reason must be one of these constants and have an
    /// entry in every catalog (<c>TranslationTemplateTests</c> checks the English one).
    /// </summary>
    public static IReadOnlyList<string> ReasonTemplates { get; } =
        [ProcessNotInspected, NotNotepadPlusPlusProcess, ExecutableUnreadable, NoValidSignature, SignerUnreadable, SignedBy, SignedByOther, UnknownOrganization];

    /// <summary>Checks the process behind a window.</summary>
    /// <param name="processId">The window's process.</param>
    /// <returns>The verdict; never throws.</returns>
    public static NotepadPlusPlusTrust VerifyProcess(uint processId)
    {
        var path = Clipboard.SourceAppResolver.TryGetImagePath(processId);
        return path is null
            ? new NotepadPlusPlusTrust(false, null, ProcessNotInspected)
            : VerifyFile(path);
    }

    /// <summary>Checks an executable: name, Authenticode signature, signer organization.</summary>
    /// <param name="path">The executable's full path.</param>
    /// <returns>The verdict; never throws (an unreadable file is not trusted).</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    public static NotepadPlusPlusTrust VerifyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(Path.GetFileName(path), NotepadPlusPlusSources.ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return new NotepadPlusPlusTrust(false, path, string.Format(CultureInfo.InvariantCulture, NotNotepadPlusPlusProcess, Path.GetFileName(path)));
        }

        string key;
        try
        {
            var info = new FileInfo(path);
            key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new NotepadPlusPlusTrust(false, path, ExecutableUnreadable);
        }

        return Cache.GetOrAdd(key, _ => Check(path));
    }

    /// <summary>Runs the signature and signer checks (uncached).</summary>
    /// <param name="path">The executable.</param>
    /// <returns>The verdict.</returns>
    private static NotepadPlusPlusTrust Check(string path)
    {
        int status = EverythingOwnerVerifier.VerifySignature(path);
        if (status != 0)
        {
            return new NotepadPlusPlusTrust(false, path, string.Format(CultureInfo.InvariantCulture, NoValidSignature, status));
        }

        string? organization;
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile reads the embedded signer; the signature itself was verified above.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            organization = EverythingOwnerVerifier.Organization(signer);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new NotepadPlusPlusTrust(false, path, SignerUnreadable);
        }

        return organization is not null && TrustedOrganizations.Contains(organization)
            ? new NotepadPlusPlusTrust(true, path, string.Format(CultureInfo.InvariantCulture, SignedBy, organization))
            : new NotepadPlusPlusTrust(false, path, string.Format(CultureInfo.InvariantCulture, SignedByOther, organization ?? UnknownOrganization));
    }
}

/// <summary>Whether an executable is trusted as Notepad++, and why (for the log and the Settings status).</summary>
/// <param name="IsTrusted">Name, signature and signer all passed.</param>
/// <param name="ExecutablePath">The checked executable, when known.</param>
/// <param name="Reason">Short reason, e.g. "signed by NOTEPAD++".</param>
public sealed record NotepadPlusPlusTrust(bool IsTrusted, string? ExecutablePath, string Reason);

/// <summary>
/// One running Notepad++ as BetterClipboard talks to it: its main window, its plugin messages (answers that are plain numbers
/// cross process boundaries as they are), and a buffer in its process through which it hands over a document's path
/// (CLAUDE.md §2.41). Create with <see cref="TryOpen"/>; dispose to free the buffer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a buffer in Notepad++'s process.</b> <c>NPPM_GETFULLPATHFROMBUFFERID</c> copies the path to a pointer in Notepad++'s
/// own address space. So BetterClipboard allocates <see cref="BufferBytes"/> there once (data pages, never executable),
/// asks Notepad++ to copy each new document's path into it, reads it back with <c>ReadProcessMemory</c>, and frees it when
/// done. The path's length is asked first (a null pointer only returns it), and anything longer than the buffer is skipped —
/// Notepad++ copies without a bound. Measured on this PC: 329 documents listed in 24 ms, all their paths in 0.27 s.
/// </para>
/// <para>
/// <b>Never blocking.</b> Every message goes with <c>SendMessageTimeout</c> (<see cref="MessageTimeoutMs"/>, aborted at once
/// when Notepad++ hangs). Windows refuses the messages and the process access for a Notepad++ running as administrator when
/// BetterClipboard does not (UIPI): <see cref="TryOpen"/> then reports it.
/// </para>
/// <para>Not thread-safe: one caller (the watcher's thread) uses an instance.</para>
/// </remarks>
public sealed class NotepadPlusPlusClient : IDisposable
{
    /// <summary>The window class of Notepad++'s main window (<c>Notepad_plus_Window::_className</c>).</summary>
    public const string WindowClass = "Notepad++";

    /// <summary>Size of the buffer in Notepad++'s process: the longest Windows path (32,767 characters) and its terminator.</summary>
    public const int BufferBytes = 65536;

    /// <summary>How long one message may take; a busy Notepad++ answers in microseconds.</summary>
    public const uint MessageTimeoutMs = 500;

    /// <summary>
    /// The watcher's reason when nothing more specific is known (a Notepad++ that could not be asked for another cause).
    /// </summary>
    internal const string CouldNotBeAsked = "it could not be asked";

    /// <summary>The reason when Windows refuses BetterClipboard the process: Notepad++ runs as administrator (UIPI).</summary>
    private const string RunsAsAdministrator = "it runs as administrator, and Windows lets BetterClipboard ask it nothing";

    /// <summary>{0}: Windows' own message for why the process could not be opened, shown as it came.</summary>
    private const string ProcessNotOpened = "its process could not be opened ({0})";

    /// <summary>Handle to Notepad++'s process (VM operation, VM read, limited query); 0 once disposed.</summary>
    private nint process;

    /// <summary>The buffer this client placed in Notepad++'s process (0 until the first path read, and once freed).</summary>
    private nint buffer;

    /// <summary>Whether <see cref="Dispose"/> ran: no message goes out afterwards.</summary>
    private bool disposed;

    /// <summary>
    /// Every problem the opened-files status can name (<see cref="TryOpen"/>'s, the owner check's and the watcher's), as
    /// composite formats. The log keeps the English; Settings translates it through <c>UiText.TranslateFormatted</c>, so a
    /// new problem must be one of these constants and have an entry in every catalog (<c>TranslationTemplateTests</c>).
    /// </summary>
    public static IReadOnlyList<string> ProblemTemplates { get; } =
        [.. NotepadPlusPlusOwnerVerifier.ReasonTemplates, RunsAsAdministrator, ProcessNotOpened, CouldNotBeAsked];

    /// <summary>Creates the client over an opened process (see <see cref="TryOpen"/>).</summary>
    /// <param name="window">The main window.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="process">A handle with VM operation, VM read and limited query access.</param>
    /// <param name="executablePath">The verified executable.</param>
    private NotepadPlusPlusClient(nint window, uint processId, nint process, string executablePath)
    {
        Window = window;
        ProcessId = processId;
        this.process = process;
        ExecutablePath = executablePath;
    }

    /// <summary>Notepad++'s main window.</summary>
    public nint Window { get; }

    /// <summary>Its process id.</summary>
    public uint ProcessId { get; }

    /// <summary>Its executable (verified as Notepad++).</summary>
    public string ExecutablePath { get; }

    /// <summary>
    /// Every top-level window of class "Notepad++" with its process (several with <c>-multiInst</c>).
    /// </summary>
    /// <param name="windowClass">The class to look for (tests use a class of their own).</param>
    /// <returns>(window, process id) pairs.</returns>
    public static IReadOnlyList<(nint Window, uint ProcessId)> FindWindows(string windowClass = WindowClass)
    {
        var found = new List<(nint, uint)>();
        nint window = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            window = FindWindowEx(0, window, windowClass, null);
            if (window == 0)
            {
                break;
            }

            if (GetWindowThreadProcessId(window, out uint processId) != 0 && processId != 0)
            {
                found.Add((window, processId));
            }
        }

        return found;
    }

    /// <summary>
    /// Opens a Notepad++ for path reads: the owner check, then the process with the access the buffer needs.
    /// </summary>
    /// <param name="window">Its main window.</param>
    /// <param name="processId">Its process.</param>
    /// <param name="verify">The owner check (tests pass their own); defaults to <see cref="NotepadPlusPlusOwnerVerifier.VerifyProcess"/>.</param>
    /// <param name="problem">Why it could not be opened, for the status line; <see langword="null"/> on success.</param>
    /// <returns>The client, or <see langword="null"/>.</returns>
    public static NotepadPlusPlusClient? TryOpen(nint window, uint processId, Func<uint, NotepadPlusPlusTrust>? verify, out string? problem)
    {
        var trust = (verify ?? NotepadPlusPlusOwnerVerifier.VerifyProcess)(processId);
        if (!trust.IsTrusted || trust.ExecutablePath is null)
        {
            problem = trust.Reason;
            return null;
        }

        nint handle = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            problem = error == 5
                ? RunsAsAdministrator
                : string.Format(CultureInfo.InvariantCulture, ProcessNotOpened, new Win32Exception(error).Message);
            return null;
        }

        problem = null;
        return new NotepadPlusPlusClient(window, processId, handle, trust.ExecutablePath);
    }

    /// <summary>Whether the main window is still a window and visible (hidden while Notepad++ loads its session).</summary>
    /// <returns>(exists, visible).</returns>
    public (bool Exists, bool Visible) WindowState() => IsWindow(Window) ? (true, IsWindowVisible(Window)) : (false, false);

    /// <summary>The main window's title (read from its stored caption: no message is sent).</summary>
    /// <returns>The title, or an empty string.</returns>
    public unsafe string Title()
    {
        int length = Math.Clamp(GetWindowTextLength(Window), 0, 32767);
        if (length == 0)
        {
            return string.Empty;
        }

        var chars = new char[length + 1];
        fixed (char* pointer = chars)
        {
            int copied = GetWindowText(Window, pointer, chars.Length);
            return new string(pointer, 0, Math.Max(0, copied));
        }
    }

    /// <summary>How many documents are open (both views), or <see langword="null"/> when Notepad++ did not answer.</summary>
    /// <returns>The count.</returns>
    public int? OpenCount() => Send(NPPM_GETNBOPENFILES, 0, 0) is { } count ? (int)count : null;

    /// <summary>Notepad++'s version as "8.9.1", or <see langword="null"/> when it did not answer.</summary>
    /// <returns>The version.</returns>
    public string? Version()
    {
        if (Send(NPPM_GETNPPVERSION, 1, 0) is not { } word)
        {
            return null;
        }

        long major = (word >> 16) & 0xFFFF;
        long minor = word & 0xFFFF;

        // The low word holds the minor digits padded to three ("910" = .9.1, "93" = .93): show them as Notepad++ does.
        string digits = minor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return major + "." + string.Join('.', digits.TrimEnd('0').DefaultIfEmpty('0'));
    }

    /// <summary>The buffer id of the document in front, or <see langword="null"/> when Notepad++ did not answer.</summary>
    /// <returns>The id.</returns>
    public long? CurrentId() => Send(NPPM_GETCURRENTBUFFERID, 0, 0);

    /// <summary>
    /// The documents open now: every buffer id in tab order (main view, then sub view) and the one in front, or
    /// <see langword="null"/> when Notepad++ did not answer a question.
    /// </summary>
    /// <returns>(ids, current id).</returns>
    public (IReadOnlyList<long> Ids, long Current)? Documents()
    {
        var ids = new List<long>();
        for (int view = 0; view < 2; view++)
        {
            // NPPM_GETNBOPENFILES counts views as 1 (main) and 2 (second); NPPM_GETBUFFERIDFROMPOS numbers them 0 and 1.
            if (Send(NPPM_GETNBOPENFILES, 0, view + 1) is not { } count)
            {
                return null;
            }

            for (int index = 0; index < count && index < 100_000; index++)
            {
                if (Send(NPPM_GETBUFFERIDFROMPOS, index, view) is not { } id)
                {
                    return null;
                }

                if (id != 0)
                {
                    ids.Add(id);
                }
            }
        }

        return Send(NPPM_GETCURRENTBUFFERID, 0, 0) is { } current ? (ids, current) : null;
    }

    /// <summary>
    /// A document's full path (or an untitled tab's name), read through the buffer in Notepad++'s process; the buffer is
    /// allocated at the first call.
    /// </summary>
    /// <param name="bufferId">The document's buffer id.</param>
    /// <returns>The path, or <see langword="null"/> (unknown id, too long, no answer, memory not readable).</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed (its buffer is gone).</exception>
    public unsafe string? PathOf(long bufferId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Send(NPPM_GETFULLPATHFROMBUFFERID, (nint)bufferId, 0) is not { } length || length < 0 || (length + 1) * 2 > BufferBytes)
        {
            // Unknown id (-1), no answer, or a path longer than the buffer: Notepad++ would copy past its end.
            return null;
        }

        if (buffer == 0)
        {
            buffer = VirtualAllocEx(process, 0, BufferBytes, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (buffer == 0)
            {
                return null;
            }
        }

        if (Send(NPPM_GETFULLPATHFROMBUFFERID, (nint)bufferId, buffer) is not { } copied || copied < 0 || copied != length)
        {
            // The document changed between the two questions (renamed, closed): ask again at the next look.
            return null;
        }

        var chars = new char[copied];
        fixed (char* destination = chars)
        {
            if (copied > 0 && !ReadProcessMemory(process, buffer, destination, (nuint)(copied * 2), out _))
            {
                return null;
            }
        }

        return new string(chars);
    }

    /// <summary>When the process started (UTC), or <see langword="null"/>.</summary>
    /// <returns>The start time.</returns>
    public DateTimeOffset? StartTime() =>
        GetProcessTimes(process, out long creation, out _, out _, out _) ? DateTimeOffset.FromFileTime(creation).ToUniversalTime() : null;

    /// <summary>The process's command line (Windows 8.1+), or <see langword="null"/>.</summary>
    /// <returns>The command line.</returns>
    public unsafe string? CommandLine()
    {
        // Two tries: the size Windows asks for, then that size.
        int size = 4096;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var bytes = new byte[size];
            fixed (byte* pointer = bytes)
            {
                int status = NtQueryInformationProcess(process, ProcessCommandLineInformation, pointer, bytes.Length, out int needed);
                if (status == 0)
                {
                    // A UNICODE_STRING header: Length (bytes), MaximumLength, then a pointer to the characters (inside this buffer).
                    ushort lengthBytes = *(ushort*)pointer;
                    nint characters = *(nint*)(pointer + nint.Size);
                    return lengthBytes == 0 || characters == 0 ? string.Empty : new string((char*)characters, 0, lengthBytes / 2);
                }

                if (needed <= size || needed > 1 << 20)
                {
                    return null;
                }

                size = needed;
            }
        }

        return null;
    }

    /// <summary>Frees the buffer in Notepad++'s process (if it still runs) and closes the process handle.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (buffer != 0)
        {
            // Fails harmlessly when Notepad++ exited meanwhile: its memory went with it.
            VirtualFreeEx(process, buffer, 0, MEM_RELEASE);
            buffer = 0;
        }

        if (process != 0)
        {
            CloseHandle(process);
            process = 0;
        }
    }

    /// <summary>Sends one plugin message with the timeout.</summary>
    /// <param name="message">The message.</param>
    /// <param name="wParam">First parameter.</param>
    /// <param name="lParam">Second parameter.</param>
    /// <returns>The answer, or <see langword="null"/> on a timeout, a hung Notepad++, or a refusal (UIPI).</returns>
    private long? Send(uint message, nint wParam, nint lParam) =>
        SendMessageTimeout(Window, message, wParam, lParam, SMTO_ABORTIFHUNG, MessageTimeoutMs, out nint result) != 0 ? result : null;
}
