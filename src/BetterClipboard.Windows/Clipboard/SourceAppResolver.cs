using System.Collections.Concurrent;
using System.Diagnostics;
using BetterClipboard.Core.Model;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Clipboard;

/// <summary>
/// Resolves a window handle (clipboard owner or foreground window) to the application that owns it.
/// </summary>
/// <remarks>
/// Uses <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which works for most elevated processes from a
/// non-elevated caller; protected processes still fail and resolve to a name-only record. File
/// descriptions (the friendly names) are cached per executable path because reading version resources
/// costs milliseconds and the same few apps copy all day. Thread-safe.
/// </remarks>
public sealed class SourceAppResolver
{
    /// <summary>
    /// Version-resource descriptions that name a runtime rather than the app (Electron apps such as
    /// GitHub Desktop ship "Electron"); for these the product name or the executable name is shown instead.
    /// </summary>
    private static readonly HashSet<string> GenericDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Electron", "Chromium", "Node.js", "Python", "OpenJDK Platform binary", "Java(TM) Platform SE binary",
        "Application Frame Host", "Microsoft Edge WebView2",
    };

    private readonly ConcurrentDictionary<string, string> displayNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the application that owns <paramref name="hwnd"/>.
    /// </summary>
    /// <param name="hwnd">Any window handle; 0 yields <see langword="null"/>.</param>
    /// <returns>The source app, or <see langword="null"/> when the window or its process is gone.</returns>
    public SourceAppInfo? Resolve(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == 0 ? null : ResolveProcess(processId);
    }

    /// <summary>
    /// Resolves a process id to its application identity.
    /// </summary>
    /// <param name="processId">Process id.</param>
    /// <returns>The source app, or <see langword="null"/> when the process no longer exists.</returns>
    public SourceAppInfo? ResolveProcess(uint processId)
    {
        var path = TryGetImagePath(processId);
        string processName;
        if (path is not null)
        {
            processName = Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
                return null; // exited meanwhile
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        var display = path is null ? processName : displayNames.GetOrAdd(path, static (p, fallback) => ReadDisplayName(p) ?? fallback, processName);
        return new SourceAppInfo(processName, path, display);
    }

    /// <summary>Queries the full image path of a process.</summary>
    /// <param name="processId">Process id.</param>
    /// <returns>The path, or <see langword="null"/> when access is denied or the process is gone.</returns>
    private static string? TryGetImagePath(uint processId)
    {
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            // 1024 chars covers long-path-aware image paths in practice; longer ones fail and fall back to the name.
            Span<char> buffer = stackalloc char[1024];
            uint size = (uint)buffer.Length;
            bool ok;
            unsafe
            {
                fixed (char* pointer = buffer)
                {
                    ok = QueryFullProcessImageName(process, 0, pointer, ref size);
                }
            }

            return ok ? new string(buffer[..(int)size]) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Reads the friendly name from the executable's version resource.</summary>
    /// <param name="path">Executable path.</param>
    /// <returns>
    /// The file description, else the product name, skipping runtime names such as "Electron";
    /// <see langword="null"/> when nothing meaningful is present (the caller then shows the process name).
    /// </returns>
    private static string? ReadDisplayName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var candidate in new[] { info.FileDescription, info.ProductName })
            {
                var name = candidate?.Trim();
                if (!string.IsNullOrEmpty(name) && !GenericDescriptions.Contains(name))
                {
                    return name;
                }
            }

            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
