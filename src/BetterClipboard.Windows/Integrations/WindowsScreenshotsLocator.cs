using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Windows.Interop;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BetterClipboard.Windows.Integrations;

/// <summary>Where Windows' screenshot tools save, and what is known about Snipping Tool (one snapshot of the facts).</summary>
/// <param name="Folder">The Screenshots folder to watch, or <see langword="null"/> when the shell cannot resolve it.</param>
/// <param name="DetectedBy">How <paramref name="Folder"/> was found ("the Screenshots known folder", "BETTERCLIPBOARD_SCREENSHOTS_DIR").</param>
/// <param name="SnippingToolPath">The installed <c>SnippingTool.exe</c> (recorded as the source of its files), or <see langword="null"/>.</param>
/// <param name="SnippingToolVersion">Its package version ("11.2607.23.0"), or <see langword="null"/>.</param>
/// <param name="SnippingTool">Its saving settings, or <see langword="null"/> when not installed or not readable right now.</param>
public sealed record WindowsScreenshotsLocation(
    string? Folder,
    string DetectedBy,
    string? SnippingToolPath,
    string? SnippingToolVersion,
    SnippingToolSettings? SnippingTool)
{
    /// <summary>Nothing located yet.</summary>
    public static readonly WindowsScreenshotsLocation None = new(null, "not located yet", null, null, null);

    /// <summary>Whether Snipping Tool is installed for this user.</summary>
    public bool IsSnippingToolInstalled => SnippingToolPath is not null;

    /// <summary>Whether both describe the same folder to watch (the case Windows ignores, too).</summary>
    /// <param name="other">Another snapshot.</param>
    /// <returns>Whether the watch can stay as it is.</returns>
    public bool WatchesSameAs(WindowsScreenshotsLocation other) =>
        string.Equals(Folder?.TrimEnd('\\'), other?.Folder?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds the Screenshots folder and Snipping Tool (CLAUDE.md §2.18). Read-only: nothing of Windows' or Snipping Tool's is
/// written — Snipping Tool's settings are read from a private copy of its hive.
/// </summary>
public static class WindowsScreenshotsLocator
{
    /// <summary>
    /// Replaces the Screenshots folder (tests, isolated instances), so the user's real folder is never watched by them.
    /// </summary>
    public const string FolderOverrideVariable = "BETTERCLIPBOARD_SCREENSHOTS_DIR";

    /// <summary><c>FOLDERID_Screenshots</c>: Pictures + "Screenshots" unless redirected.</summary>
    public static readonly Guid ScreenshotsFolderId = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    /// <summary>The per-user AppModel repository, which lists installed packages without the admin rights listing WindowsApps needs.</summary>
    private const string PackagesKeyPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Snipping Tool's package family name (stable across versions).</summary>
    private const string SnippingToolFamilyName = "Microsoft.ScreenSketch_8wekyb3d8bbwe";

    /// <summary>Guards <see cref="cachedSettings"/>.</summary>
    private static readonly Lock SettingsGate = new();

    /// <summary>
    /// The settings read last, with the hive's size and write time then: an unchanged hive is not copied again, so a
    /// refresh every few minutes costs one file-info call.
    /// </summary>
    private static (long Length, DateTime WrittenUtc, SnippingToolSettings Settings)? cachedSettings;

    /// <summary>Takes a fresh snapshot (registry, a known-folder lookup, at most one small file copy). Never throws.</summary>
    /// <returns>The facts.</returns>
    public static WindowsScreenshotsLocation Locate()
    {
        var folder = ResolveFolder(out var detectedBy);
        var (path, version) = FindSnippingTool();
        return new WindowsScreenshotsLocation(folder, detectedBy, path, version, path is null ? null : ReadSnippingToolSettings());
    }

    /// <summary>The folder to watch: the override when set, else the Screenshots known folder (not created, not verified).</summary>
    /// <param name="detectedBy">How it was found.</param>
    /// <returns>The folder, or <see langword="null"/> when the shell cannot resolve it.</returns>
    public static string? ResolveFolder(out string detectedBy)
    {
        var overridden = Environment.GetEnvironmentVariable(FolderOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            detectedBy = FolderOverrideVariable;
            return Path.GetFullPath(overridden.Trim());
        }

        detectedBy = "the Screenshots known folder";
        int hr = NativeMethods.SHGetKnownFolderPath(in ScreenshotsFolderId, NativeMethods.KF_FLAG_DONT_VERIFY, 0, out var pointer);
        try
        {
            return hr == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        finally
        {
            // Allocated by the shell even on some failures; freeing NULL is a no-op.
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    /// <summary>Finds the installed Snipping Tool package and its executable.</summary>
    /// <returns>The executable and the package version, or (<see langword="null"/>, <see langword="null"/>) when not installed.</returns>
    public static (string? Path, string? Version) FindSnippingTool()
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackagesKeyPath);

            // Full names look like Microsoft.ScreenSketch_11.2607.23.0_x64__8wekyb3d8bbwe; with several (an update in
            // flight) the newest version wins.
            var newest = (packages?.GetSubKeyNames() ?? [])
                .Where(name => name.StartsWith("Microsoft.ScreenSketch_", StringComparison.OrdinalIgnoreCase) && name.EndsWith("__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase))
                .Select(name => (Name: name, Version: Version.TryParse(name.Split('_')[1], out var parsed) ? parsed : null))
                .Where(candidate => candidate.Version is not null)
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault();
            if (newest.Name is not { } fullName || packages is null)
            {
                return (null, null);
            }

            using var package = packages.OpenSubKey(fullName);
            if (package?.GetValue("PackageRootFolder") is not string root)
            {
                return (null, null);
            }

            var exe = System.IO.Path.Combine(root, "SnippingTool", WindowsScreenshots.SnippingToolExecutableName);
            return File.Exists(exe) ? (exe, newest.Version!.ToString()) : (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Reads Snipping Tool's saving settings from a private copy of its settings hive (cached until the hive changes).
    /// </summary>
    /// <remarks>
    /// <c>RegLoadAppKey</c> on the live file could replay its transaction logs into it — a write to another app's data —
    /// so the hive and its logs are copied to a temp folder first, loaded there, and deleted. While Snipping Tool runs,
    /// its hive may be held open: the copy then fails and the last known settings (or none) are returned.
    /// </remarks>
    /// <returns>The settings; <see cref="SnippingToolSettings.Defaults"/> when it never saved any; <see langword="null"/> when unreadable now.</returns>
    public static SnippingToolSettings? ReadSnippingToolSettings()
    {
        var hive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", SnippingToolFamilyName, "Settings", "settings.dat");
        FileInfo info;
        try
        {
            info = new FileInfo(hive);
            if (!info.Exists)
            {
                return SnippingToolSettings.Defaults;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (SettingsGate)
        {
            if (cachedSettings is { } cached && cached.Length == info.Length && cached.WrittenUtc == info.LastWriteTimeUtc)
            {
                return cached.Settings;
            }
        }

        var read = ReadHiveCopy(info);
        if (read is not null)
        {
            lock (SettingsGate)
            {
                cachedSettings = (info.Length, info.LastWriteTimeUtc, read);
            }
        }
        else
        {
            lock (SettingsGate)
            {
                // In use right now: the last good answer is better than none.
                return cachedSettings?.Settings;
            }
        }

        return read;
    }

    /// <summary>Copies the hive and its logs, loads the copy privately and reads <c>LocalState</c>'s two saving values.</summary>
    /// <param name="hive">The live hive file.</param>
    /// <returns>The settings, or <see langword="null"/> when the copy or the load failed.</returns>
    private static SnippingToolSettings? ReadHiveCopy(FileInfo hive)
    {
        DirectoryInfo? scratch = null;
        try
        {
            scratch = Directory.CreateTempSubdirectory("BetterClipboard-snipping-");
            foreach (var file in hive.Directory!.GetFiles("settings.dat*"))
            {
                File.Copy(file.FullName, Path.Combine(scratch.FullName, file.Name));
            }

            int rc = NativeMethods.RegLoadAppKey(Path.Combine(scratch.FullName, "settings.dat"), out var root, NativeMethods.KEY_READ, 0, 0);
            using (root)
            {
                if (rc != 0)
                {
                    AppLog.Info($"Snipping Tool's settings could not be read ({rc}).");
                    return null;
                }

                using var rootKey = RegistryKey.FromHandle(root);
                using var localState = rootKey.OpenSubKey("LocalState");
                if (localState is null)
                {
                    return SnippingToolSettings.Defaults;
                }

                var values = new Dictionary<string, (uint Type, byte[] Data)>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "AutoSaveScreenshots", "AutoSaveScreenshotsLocation" })
                {
                    if (ReadRaw(localState.Handle, name) is { } value)
                    {
                        values[name] = value;
                    }
                }

                return SnippingToolSettings.Parse(values);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open while Snipping Tool runs; the caller falls back to the last good answer.
            return null;
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// <summary>Reads a value's raw type and bytes (ApplicationData types are not readable through <see cref="RegistryKey"/>).</summary>
    /// <param name="key">The open key.</param>
    /// <param name="name">Value name.</param>
    /// <returns>The type and data, or <see langword="null"/> when absent or larger than 64 KB (no saving setting is).</returns>
    private static unsafe (uint Type, byte[] Data)? ReadRaw(SafeRegistryHandle key, string name)
    {
        var buffer = new byte[64 * 1024];
        uint size = (uint)buffer.Length;
        int rc;
        uint type;
        fixed (byte* data = buffer)
        {
            rc = NativeMethods.RegQueryValueEx(key, name, 0, out type, data, ref size);
        }

        return rc == 0 ? (type, buffer[..(int)size]) : null;
    }

    /// <summary>
    /// Deletes the scratch copy. Closing the hive's last handle unloads it, but the file handle can linger a moment, so
    /// this retries briefly; a folder left behind is only a few KB in the temp folder.
    /// </summary>
    /// <param name="scratch">The folder, or <see langword="null"/>.</param>
    private static void DeleteScratch(DirectoryInfo? scratch)
    {
        for (int attempt = 0; scratch is not null && attempt < 10; attempt++)
        {
            try
            {
                scratch.Delete(recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
