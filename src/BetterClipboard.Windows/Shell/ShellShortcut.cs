using System.Runtime.InteropServices;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// Reads where a Windows shortcut (<c>.lnk</c>) points, through Windows' own Shell Link object: the same reader Explorer
/// uses, so every shortcut Windows can open is read, whatever its format version.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> Explorer offers "Run as administrator" for a shortcut exactly when its target has an administrator mode
/// (the shortcut's own context menu takes the target's verbs). <see cref="RunCommandLauncher.CanRunAsAdministrator(string)"/>
/// needs the target to answer the same way for a Win+R command that names a shortcut.
/// </para>
/// <para>
/// <b>COM:</b> the Shell Link object takes both apartments. Each call initializes COM on its thread (multithreaded) and
/// balances it; on a thread that is a single-threaded apartment already (the run's own thread) the call keeps that
/// apartment. The three methods used are called through the vtable, like the accessibility caret's
/// (<see cref="Input.AccessibleCaret"/>): the slots are in <c>NativeMethods.Elevation.cs</c>.
/// </para>
/// <para>
/// <b>Footgun:</b> loading a shortcut reads its file: a shortcut on a network share that does not answer blocks the call.
/// Never call it on the UI thread.
/// </para>
/// </remarks>
internal static class ShellShortcut
{
    /// <summary>
    /// The longest target read: Windows' longest path (32,767 characters) and its terminator.
    /// </summary>
    private const int MaxTargetChars = 32_768;

    /// <summary>
    /// The file or folder a shortcut points at.
    /// </summary>
    /// <param name="path">The shortcut's full path.</param>
    /// <returns>
    /// The target's full path, environment variables expanded; or <see langword="null"/> when the file is not a shortcut
    /// Windows can load, or points at no file-system location (Control Panel, an app from the Store, a shell folder).
    /// Never throws for such a file.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    internal static unsafe string? ReadTarget(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // This thread's own apartment, multithreaded (the Shell Link object takes both). A negative result means the thread
        // is a single-threaded apartment already, which works as well and needs no CoUninitialize.
        int init = CoInitializeEx(0, COINIT_MULTITHREADED);
        try
        {
            if (CoCreateInstance(in CLSID_ShellLink, 0, CLSCTX_INPROC_SERVER, in IID_IShellLinkW, out nint link) < 0 || link == 0)
            {
                return null;
            }

            nint* linkVtable = *(nint**)link;
            try
            {
                if (!Load(link, linkVtable, path))
                {
                    return null;
                }

                // GetPath without flags gives the target with its environment variables expanded. S_FALSE (1) means the
                // shortcut points at something that is not a file-system path; its buffer is then empty.
                var buffer = new char[MaxTargetChars];
                fixed (char* target = buffer)
                {
                    var getPath = (delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)linkVtable[IShellLinkGetPathSlot];
                    int hr = getPath(link, target, MaxTargetChars, 0, 0);
                    if (hr != 0 || target[0] == '\0')
                    {
                        return null;
                    }

                    return new string(target);
                }
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)linkVtable[IUnknownReleaseSlot])(link);
            }
        }
        finally
        {
            if (init >= 0)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>
    /// Writes a shortcut to <paramref name="target"/>. Tests only: the app never writes shortcuts (the tests make the ones
    /// <see cref="ReadTarget"/> and <see cref="RunCommandLauncher.CanRunAsAdministrator(string)"/> are checked against).
    /// </summary>
    /// <param name="path">Where the <c>.lnk</c> file goes (replaced when it exists).</param>
    /// <param name="target">The file or folder it points at.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> or <paramref name="target"/> is null or blank.</exception>
    /// <exception cref="COMException">Windows refused to create, fill or save the shortcut (its HRESULT).</exception>
    internal static unsafe void Create(string path, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        int init = CoInitializeEx(0, COINIT_MULTITHREADED);
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(in CLSID_ShellLink, 0, CLSCTX_INPROC_SERVER, in IID_IShellLinkW, out nint link));
            nint* linkVtable = *(nint**)link;
            try
            {
                fixed (char* targetChars = target)
                {
                    var setPath = (delegate* unmanaged[Stdcall]<nint, char*, int>)linkVtable[IShellLinkSetPathSlot];
                    Marshal.ThrowExceptionForHR(setPath(link, targetChars));
                }

                nint persist = QueryPersistFile(link, linkVtable);
                if (persist == 0)
                {
                    // E_NOINTERFACE: every Shell Link object has one, so this is Windows refusing, not a missing feature.
                    throw new COMException("The Shell Link object gave no IPersistFile to save the shortcut with.", unchecked((int)0x80004002));
                }

                nint* persistVtable = *(nint**)persist;
                try
                {
                    fixed (char* file = path)
                    {
                        // fRemember TRUE: the object now names this file (irrelevant: it is released right after).
                        var save = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)persistVtable[IPersistFileSaveSlot];
                        Marshal.ThrowExceptionForHR(save(persist, file, 1));
                    }
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<nint, uint>)persistVtable[IUnknownReleaseSlot])(persist);
                }
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)linkVtable[IUnknownReleaseSlot])(link);
            }
        }
        finally
        {
            if (init >= 0)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>
    /// Loads a shortcut file into a Shell Link object (through its <c>IPersistFile</c>), read-only.
    /// </summary>
    /// <param name="link">The object's <c>IShellLinkW</c> pointer.</param>
    /// <param name="linkVtable">Its vtable.</param>
    /// <param name="path">The shortcut's path.</param>
    /// <returns><see langword="true"/> when Windows loaded it; <see langword="false"/> for a file that is not a shortcut.</returns>
    private static unsafe bool Load(nint link, nint* linkVtable, string path)
    {
        nint persist = QueryPersistFile(link, linkVtable);
        if (persist == 0)
        {
            return false;
        }

        nint* persistVtable = *(nint**)persist;
        try
        {
            fixed (char* file = path)
            {
                var load = (delegate* unmanaged[Stdcall]<nint, char*, uint, int>)persistVtable[IPersistFileLoadSlot];
                return load(persist, file, STGM_READ) >= 0;
            }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)persistVtable[IUnknownReleaseSlot])(persist);
        }
    }

    /// <summary>Asks a Shell Link object for its <c>IPersistFile</c> (the half that reads and writes the file).</summary>
    /// <param name="link">The object's <c>IShellLinkW</c> pointer.</param>
    /// <param name="linkVtable">Its vtable.</param>
    /// <returns>The <c>IPersistFile</c> pointer (release it), or 0 when the object has none.</returns>
    private static unsafe nint QueryPersistFile(nint link, nint* linkVtable)
    {
        Guid iid = IID_IPersistFile;
        nint persist = 0;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)linkVtable[IUnknownQueryInterfaceSlot];
        return queryInterface(link, &iid, &persist) >= 0 ? persist : 0;
    }
}
