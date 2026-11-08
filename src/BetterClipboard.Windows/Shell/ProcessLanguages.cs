using System.Runtime.InteropServices;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Shell;

/// <summary>
/// Tells Windows which languages this process prefers for the text it loads from system and framework resources, so
/// the parts of the window BetterClipboard does not word itself speak the interface language too.
/// </summary>
/// <remarks>
/// <para>
/// WinUI's own controls take their text from <c>Microsoft.ui.xaml.dll.mui</c> in a folder per region next to the
/// executable (<c>zh-CN\</c>, <c>en-us\</c>): a text box's right-click menu (Cut, Copy, Paste, Select all, Undo), the
/// search box's clear button. Windows picks that folder by the process's preferred UI languages, which follow Windows'
/// display language unless the process says otherwise. Without this, an English Windows shows "Cut / Copy / Paste" in
/// the Chinese interface, and a Chinese Windows shows "剪切 / 复制 / 粘贴" in the English one.
/// </para>
/// <para>
/// Call it once, early in the process, before any window has loaded its control text: a resource already loaded keeps
/// its language. Like the interface language itself, a change shows fully after a restart.
/// </para>
/// </remarks>
public static class ProcessLanguages
{
    /// <summary>Makes the given languages the process's preferred UI languages, most preferred first.</summary>
    /// <param name="languageNames">Language names Windows knows (<c>zh-CN</c>, <c>zh-Hans</c>); an empty list leaves Windows' choice alone.</param>
    /// <param name="win32Error">Windows' error code when it refused the list; 0 otherwise.</param>
    /// <returns>
    /// <see langword="true"/> when Windows accepted at least one name; <see langword="false"/> otherwise, and the process
    /// keeps Windows' choice (nothing breaks: only WinUI's own control text stays in Windows' language).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="languageNames"/> is <see langword="null"/>.</exception>
    public static bool TryPrefer(IReadOnlyList<string> languageNames, out int win32Error)
    {
        ArgumentNullException.ThrowIfNull(languageNames);
        win32Error = 0;

        // A NUL inside a name would end the list early and hand Windows a different list than the one asked for.
        var names = languageNames.Where(name => !string.IsNullOrWhiteSpace(name) && !name.Contains('\0', StringComparison.Ordinal)).ToArray();
        if (names.Length == 0)
        {
            return false;
        }

        // A multi-string: every name ends with a NUL, and the marshaller's terminating NUL closes the list.
        string buffer = string.Concat(names.Select(name => name + "\0"));
        if (!SetProcessPreferredUILanguages(MUI_LANGUAGE_NAME, buffer, out uint accepted))
        {
            // Read before any other call can overwrite it.
            win32Error = Marshal.GetLastPInvokeError();
            return false;
        }

        return accepted > 0;
    }
}
