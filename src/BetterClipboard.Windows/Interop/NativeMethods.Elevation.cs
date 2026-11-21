using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Interop;

/// <summary>
/// Declarations for deciding whether Windows can run a Win+R command as administrator
/// (<see cref="Shell.RunCommandLauncher.CanRunAsAdministrator(string)"/>, CLAUDE.md §2.17): a file type's own verbs through
/// <c>AssocQueryString</c>, and a shortcut's target through the Shell Link object, whose three methods the reader calls
/// through function pointers.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// <c>ASSOCSTR_COMMAND</c>: the command line of a verb (<c>shell\&lt;verb&gt;\command</c>). A verb registered this way
    /// is one <c>ShellExecuteEx</c> can run.
    /// </summary>
    internal const int ASSOCSTR_COMMAND = 1;

    /// <summary>
    /// <c>ASSOCSTR_DELEGATEEXECUTE</c>: the CLSID of the COM object a verb runs through instead of a command line (packaged
    /// apps register their verbs this way).
    /// </summary>
    internal const int ASSOCSTR_DELEGATEEXECUTE = 18;

    /// <summary>
    /// Reads one string of a file type's association, for example a verb's command line.
    /// </summary>
    /// <remarks>
    /// Given an extension (<c>.png</c>), it follows the user's own default app (Settings › Default apps) to its ProgID,
    /// exactly as opening the file does. Flags 0 leave out the catch-all keys (<c>*</c>, <c>AllFilesystemObjects</c>):
    /// only the file type's own verbs answer. Reads the registry only: no shell extension is loaded.
    /// </remarks>
    /// <param name="flags">0 (no <c>ASSOCF_*</c> flags).</param>
    /// <param name="str">Which string (<see cref="ASSOCSTR_COMMAND"/>, <see cref="ASSOCSTR_DELEGATEEXECUTE"/>).</param>
    /// <param name="pszAssoc">An extension with its dot, a ProgID, or a protocol name.</param>
    /// <param name="pszExtra">The verb (<c>runas</c>), or <see langword="null"/> for the default one.</param>
    /// <param name="pszOut">A buffer for the string, or <see langword="null"/> to ask only whether there is one.</param>
    /// <param name="pcchOut">In: the buffer's size in characters; out: the string's length with its terminator.</param>
    /// <returns>
    /// S_OK, or S_FALSE when <paramref name="pszOut"/> is null and the string exists; a negative HRESULT when it does not
    /// (<c>HRESULT_FROM_WIN32(ERROR_NO_ASSOCIATION)</c>, 0x80070483, for a verb the type lacks).
    /// </returns>
    [LibraryImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial int AssocQueryString(uint flags, int str, string pszAssoc, string? pszExtra, char* pszOut, ref uint pcchOut);

    /// <summary><c>CLSID_ShellLink</c> {00021401-0000-0000-C000-000000000046}: the object that reads and writes <c>.lnk</c> files.</summary>
    internal static readonly Guid CLSID_ShellLink = new(0x00021401, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    /// <summary><c>IID_IShellLinkW</c> {000214F9-0000-0000-C000-000000000046}.</summary>
    internal static readonly Guid IID_IShellLinkW = new(0x000214F9, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    /// <summary><c>IID_IPersistFile</c> {0000010B-0000-0000-C000-000000000046}.</summary>
    internal static readonly Guid IID_IPersistFile = new(0x0000010B, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    /// <summary><c>CLSCTX_INPROC_SERVER</c>: the object runs in this process (the Shell Link object is shell32's own).</summary>
    internal const uint CLSCTX_INPROC_SERVER = 0x1;

    /// <summary><c>STGM_READ</c>: <c>IPersistFile::Load</c> opens the file for reading only.</summary>
    internal const uint STGM_READ = 0x0;

    /// <summary>Vtable slot of <c>IUnknown::QueryInterface</c>.</summary>
    internal const int IUnknownQueryInterfaceSlot = 0;

    /// <summary>
    /// Vtable slot of <c>IShellLinkW::GetPath</c>: right after IUnknown's three methods (shobjidl_core.idl). A wrong slot
    /// would call another method with these arguments; the tests read real shortcuts through it (RunHistoryTests).
    /// </summary>
    internal const int IShellLinkGetPathSlot = 3;

    /// <summary>
    /// Vtable slot of <c>IShellLinkW::SetPath</c>, the interface's last method (slot 20). Only the tests write shortcuts.
    /// </summary>
    internal const int IShellLinkSetPathSlot = 20;

    /// <summary>
    /// Vtable slot of <c>IPersistFile::Load</c>: IUnknown's three methods, <c>IPersist::GetClassID</c> (3),
    /// <c>IsDirty</c> (4), then <c>Load</c> (5).
    /// </summary>
    internal const int IPersistFileLoadSlot = 5;

    /// <summary>Vtable slot of <c>IPersistFile::Save</c> (6), after <c>Load</c>. Only the tests write shortcuts.</summary>
    internal const int IPersistFileSaveSlot = 6;

    /// <summary>
    /// Creates a COM object. Needs COM on the calling thread (any apartment: the Shell Link object takes both).
    /// </summary>
    /// <param name="rclsid">The object's class.</param>
    /// <param name="pUnkOuter">0 (no aggregation).</param>
    /// <param name="dwClsContext"><see cref="CLSCTX_INPROC_SERVER"/>.</param>
    /// <param name="riid">The interface asked for.</param>
    /// <param name="ppv">Receives the interface pointer (release it through its vtable), or 0.</param>
    /// <returns>An HRESULT; negative on failure.</returns>
    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);
}
