using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// What BetterClipboard found out about ShareX on this machine.
/// </summary>
/// <param name="IsInstalled">Whether ShareX is installed (or at least has a personal folder) — decides whether the panel shows a ShareX tab.</param>
/// <param name="ExecutablePath"><c>ShareX.exe</c> when known (source app of imported screenshots), else <see langword="null"/>.</param>
/// <param name="PersonalFolder">ShareX's personal folder (settings, history, default screenshots), empty when not installed.</param>
/// <param name="Folders">Where ShareX saves screenshots: roots plus subfolder patterns (see <see cref="ShareXFolderRule"/>).</param>
/// <param name="ThumbnailSuffix">File-name suffix of the thumbnails ShareX can save next to screenshots (skipped).</param>
/// <param name="DetectedBy">How ShareX was found, for the Settings status line and the log.</param>
public sealed record ShareXInstallation(
    bool IsInstalled,
    string? ExecutablePath,
    string PersonalFolder,
    IReadOnlyList<ShareXFolderRule> Folders,
    string ThumbnailSuffix,
    string DetectedBy)
{
    /// <summary>ShareX's default thumbnail suffix (<c>TaskSettings.ImageSettings.ThumbnailName</c>).</summary>
    public const string DefaultThumbnailSuffix = "-thumbnail";

    /// <summary>Still-image formats ShareX can save (plus WebP, which WIC reads). GIF may be an animation — checked when read.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>ShareX is not on this machine.</summary>
    public static ShareXInstallation NotFound { get; } = new(false, null, string.Empty, [], DefaultThumbnailSuffix, "not found");

    /// <summary>
    /// Folders to watch recursively: the distinct <see cref="Folders"/> roots, none inside another. Computed on
    /// each call (a handful of rules).
    /// </summary>
    public IReadOnlyList<string> WatchFolders => ShareXFolderRule.MinimalRoots(Folders);

    /// <summary>
    /// Whether a file is a screenshot ShareX could have saved: an image extension, not a thumbnail, in a
    /// folder one of the <see cref="Folders"/> rules can produce.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <returns><see langword="true"/> for screenshot candidates (the content is checked when read).</returns>
    public bool IsScreenshotFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !ImageExtensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }

        if (ThumbnailSuffix.Length > 0 && Path.GetFileNameWithoutExtension(path).EndsWith(ThumbnailSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Folders.Any(rule => rule.Contains(path));
    }

    /// <summary>Whether two installations watch exactly the same places (the part a re-location must act on).</summary>
    /// <param name="other">Other installation.</param>
    /// <returns><see langword="true"/> when rules and thumbnail suffix are equal.</returns>
    public bool WatchesSameAs(ShareXInstallation? other) =>
        other is not null
        && Folders.SequenceEqual(other.Folders)
        && string.Equals(ThumbnailSuffix, other.ThumbnailSuffix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The inputs <see cref="ShareXLocator.Resolve"/> needs from the machine, separated so the folder logic
/// runs against fake folders in tests.
/// </summary>
/// <param name="InstallDirectory">ShareX's program folder from the uninstall registration, if any.</param>
/// <param name="RegistryPersonalPath">The <c>SOFTWARE\ShareX\PersonalPath</c> registry value, if any.</param>
/// <param name="DocumentsFolder">The user's Documents folder (default personal folder parent).</param>
/// <param name="LocalAppDataFolder"><c>%LOCALAPPDATA%</c> (where old ShareX versions kept <c>PersonalPath.cfg</c>).</param>
/// <param name="DetectedBy">How the install was found.</param>
public sealed record ShareXLocatorInputs(
    string? InstallDirectory,
    string? RegistryPersonalPath,
    string DocumentsFolder,
    string LocalAppDataFolder,
    string DetectedBy);

/// <summary>
/// Finds ShareX and the folders its screenshots are saved to, by applying ShareX's own rules (studied in
/// its source for interoperability — none of its GPL code is used):
/// <list type="number">
/// <item><b>Personal folder:</b> <c>&lt;exe dir&gt;\ShareX</c> in portable mode (a <c>Portable</c> file
/// next to the exe), else the registry value <c>SOFTWARE\ShareX\PersonalPath</c> (HKLM, then HKCU), else
/// <c>PersonalPath.cfg</c> (next to the exe, in <c>Documents\ShareX</c>, or the legacy
/// <c>%LOCALAPPDATA%\ShareX</c>), else <c>Documents\ShareX</c>.</item>
/// <item><b>Screenshots:</b> <c>ApplicationConfig.json</c> → <c>CustomScreenshotsPath</c> when
/// <c>UseCustomScreenshotsPath</c> (with the <c>CustomScreenshotsPath2</c> fallback when the primary is
/// missing), else <c>&lt;personal&gt;\Screenshots</c>. Below it comes the subfolder pattern
/// <c>SaveImageSubFolderPattern</c> (default <c>%y-%mo</c>), or <c>SaveImageSubFolderPatternWindow</c>
/// when set and the capture has a window title.</item>
/// <item><b>Per-hotkey folders:</b> task settings (<c>DefaultTaskSettings</c> in
/// <c>ApplicationConfig.json</c>, each hotkey in <c>HotkeysConfig.json</c>) can override the whole folder with
/// a pattern such as <c>D:\Shots\%y-%mo</c>. The fixed part before the first name token is the root, and the
/// rest is the subfolder pattern.</item>
/// </list>
/// Relative paths are resolved against ShareX's program folder, as ShareX does.
/// </summary>
/// <remarks>
/// Only the few settings above are read, with a sharing mode that never blocks ShareX's own writes;
/// <c>UploadersConfig.json</c> (which holds upload credentials) is never opened. Any unreadable or
/// unexpected file degrades to ShareX's defaults instead of failing.
/// </remarks>
public static class ShareXLocator
{
    /// <summary>The Inno Setup <c>AppId</c> of ShareX's installer (uninstall key <c>{AppId}_is1</c>).</summary>
    public const string InstallerAppId = "82E6AC09-0FEF-4390-AD9F-0DD3F5561EFC";

    /// <summary>
    /// Environment variable that points the integration at a different ShareX personal folder — for tests
    /// and isolated dev runs, which must never pick up the user's real screenshots.
    /// </summary>
    public const string PersonalFolderOverrideVariable = "BETTERCLIPBOARD_SHAREX_DIR";

    /// <summary>ShareX's default <c>SaveImageSubFolderPattern</c> (year-month folders).</summary>
    public const string DefaultSubFolderPattern = "%y-%mo";

    private const string AppName = "ShareX";
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Locates ShareX on this machine: registry (installer, Steam), the override variable, and the
    /// default personal folder.
    /// </summary>
    /// <returns>The installation, or <see cref="ShareXInstallation.NotFound"/>.</returns>
    public static ShareXInstallation Locate()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (Environment.GetEnvironmentVariable(PersonalFolderOverrideVariable) is { Length: > 0 } overridden)
        {
            // The override replaces the registry lookup entirely: an isolated run must not see the real install.
            return Resolve(new ShareXLocatorInputs(null, overridden, documents, localAppData, $"{PersonalFolderOverrideVariable} override"));
        }

        var (installDirectory, detectedBy) = FindInstallDirectory();
        return Resolve(new ShareXLocatorInputs(installDirectory, ReadRegistryPersonalPath(), documents, localAppData, detectedBy));
    }

    /// <summary>
    /// Applies ShareX's folder rules to explicit inputs (see the class summary).
    /// </summary>
    /// <param name="inputs">Machine facts.</param>
    /// <returns>The installation; <see cref="ShareXInstallation.NotFound"/> when there is neither an install nor a personal folder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputs"/> is <see langword="null"/>.</exception>
    public static ShareXInstallation Resolve(ShareXLocatorInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        string? exe = inputs.InstallDirectory is { } dir && File.Exists(Path.Combine(dir, "ShareX.exe")) ? Path.Combine(dir, "ShareX.exe") : null;
        string defaultPersonal = Path.Combine(inputs.DocumentsFolder, AppName);
        string personal = ResolvePersonalFolder(inputs, defaultPersonal);

        bool hasPersonalFolder = Directory.Exists(personal);
        if (inputs.InstallDirectory is null && !hasPersonalFolder)
        {
            return ShareXInstallation.NotFound;
        }

        using var config = ReadJson(Path.Combine(personal, "ApplicationConfig.json"));
        var root = config?.RootElement;

        var rules = new List<ShareXFolderRule>();
        if (Absolute(ScreenshotsParentFolder(root, personal), inputs.InstallDirectory) is { } parent)
        {
            // A missing member means ShareX's default; an explicitly empty one means "no subfolder".
            string subFolders = GetString(root, "SaveImageSubFolderPattern") ?? DefaultSubFolderPattern;
            AddRule(rules, parent, subFolders);
            if (GetString(root, "SaveImageSubFolderPatternWindow") is { Length: > 0 } windowSubFolders)
            {
                AddRule(rules, parent, windowSubFolders);
            }
        }

        if (root?.TryGetProperty("DefaultTaskSettings", out var defaults) == true)
        {
            AddOverrideFolder(defaults, inputs.InstallDirectory, rules);
        }

        string hotkeysPath = GetString(root, "CustomHotkeysConfigPath") is { Length: > 0 } custom && File.Exists(ExpandFolderVariables(custom))
            ? ExpandFolderVariables(custom)
            : Path.Combine(personal, "HotkeysConfig.json");
        using (var hotkeys = ReadJson(hotkeysPath))
        {
            if (hotkeys?.RootElement.TryGetProperty("Hotkeys", out var list) == true && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var hotkey in list.EnumerateArray())
                {
                    if (hotkey.ValueKind == JsonValueKind.Object && hotkey.TryGetProperty("TaskSettings", out var taskSettings))
                    {
                        AddOverrideFolder(taskSettings, inputs.InstallDirectory, rules);
                    }
                }
            }
        }

        string thumbnailSuffix = root?.TryGetProperty("DefaultTaskSettings", out var taskDefaults) == true
            && taskDefaults.TryGetProperty("ImageSettings", out var image)
            && GetString(image, "ThumbnailName") is { Length: > 0 } suffix
                ? suffix
                : ShareXInstallation.DefaultThumbnailSuffix;

        return new ShareXInstallation(true, exe, personal, rules.Distinct().ToArray(), thumbnailSuffix, inputs.DetectedBy);
    }

    /// <summary>
    /// Expands folder variables the way ShareX does for its paths: <c>%&lt;SpecialFolder&gt;%</c> names (e.g.
    /// <c>%MyPictures%</c>, case-insensitive) first, then environment variables. Name-parser tokens such as
    /// <c>%y</c> are left alone.
    /// </summary>
    /// <param name="path">A configured path.</param>
    /// <returns>The expanded path (the input when blank).</returns>
    public static string ExpandFolderVariables(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        foreach (var folder in Enum.GetValues<Environment.SpecialFolder>())
        {
            var token = $"%{folder}%";
            if (path.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace(token, Environment.GetFolderPath(folder), StringComparison.OrdinalIgnoreCase);
            }
        }

        return Environment.ExpandEnvironmentVariables(path);
    }

    /// <summary>
    /// The fixed folder in front of a ShareX folder pattern: <c>D:\Shots\%y-%mo\%pn</c> → <c>D:\Shots</c>.
    /// Everything ShareX can create below it is covered by a recursive watch.
    /// </summary>
    /// <param name="pattern">An expanded folder pattern.</param>
    /// <returns>The fixed prefix, or <see langword="null"/> when nothing fixed and absolute remains.</returns>
    public static string? FixedPrefix(string pattern) => SplitPattern(pattern)?.Root;

    /// <summary>
    /// Splits an expanded ShareX folder pattern into its fixed root and the subfolder pattern below it:
    /// <c>D:\Shots\Game-%pn\%y</c> → (<c>D:\Shots</c>, <c>Game-%pn\%y</c>). A pattern without tokens is all root.
    /// </summary>
    /// <param name="pattern">An expanded, absolute folder pattern.</param>
    /// <returns>The parts, or <see langword="null"/> when nothing fixed and absolute remains (e.g. <c>%y\Shots</c>).</returns>
    public static (string Root, string Relative)? SplitPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        pattern = pattern.Trim();
        int token = ShareXFolderRule.FirstTokenIndex(pattern);
        string prefix = token < 0 ? pattern : pattern[..token];
        if (token >= 0)
        {
            // The token may start mid-name ("Shots-%y"): cut back to the last complete folder.
            int separator = prefix.LastIndexOfAny(['\\', '/']);
            prefix = separator < 0 ? string.Empty : prefix[..(separator + 1)];
        }

        string relative = pattern[prefix.Length..].Trim('\\', '/');
        string root = prefix.TrimEnd('\\', '/');

        // "C:" alone is drive-relative; the separator makes the check about the folder itself.
        return root.Length > 0 && Path.IsPathFullyQualified(root + Path.DirectorySeparatorChar)
            ? (Path.IsPathFullyQualified(root) ? root : root + Path.DirectorySeparatorChar, relative)
            : null;
    }

    /// <summary>Resolves the personal folder by ShareX's precedence (see the class summary).</summary>
    /// <param name="inputs">Machine facts.</param>
    /// <param name="defaultPersonal"><c>Documents\ShareX</c>.</param>
    /// <returns>The personal folder.</returns>
    private static string ResolvePersonalFolder(ShareXLocatorInputs inputs, string defaultPersonal)
    {
        if (inputs.InstallDirectory is { } installDirectory && File.Exists(Path.Combine(installDirectory, "Portable")))
        {
            return Path.Combine(installDirectory, AppName);
        }

        if (!string.IsNullOrWhiteSpace(inputs.RegistryPersonalPath))
        {
            return Path.GetFullPath(ExpandFolderVariables(inputs.RegistryPersonalPath.Trim()));
        }

        // PersonalPath.cfg: next to the exe wins, then the current location, then the pre-migration one.
        var candidates = new List<string>();
        if (inputs.InstallDirectory is { } exeDir)
        {
            candidates.Add(Path.Combine(exeDir, "PersonalPath.cfg"));
        }

        candidates.Add(Path.Combine(defaultPersonal, "PersonalPath.cfg"));
        candidates.Add(Path.Combine(inputs.LocalAppDataFolder, AppName, "PersonalPath.cfg"));
        foreach (var candidate in candidates)
        {
            if (ReadText(candidate) is { Length: > 0 } configured)
            {
                // Relative entries are relative to ShareX's program folder (its "absolute path" base).
                var baseDirectory = inputs.InstallDirectory ?? Path.GetDirectoryName(candidate)!;
                return Path.GetFullPath(ExpandFolderVariables(configured), baseDirectory);
            }
        }

        return defaultPersonal;
    }

    /// <summary>ShareX's <c>ScreenshotsParentFolder</c>: custom path, its fallback, or <c>&lt;personal&gt;\Screenshots</c>.</summary>
    /// <param name="config">Root of <c>ApplicationConfig.json</c>, if readable.</param>
    /// <param name="personal">Personal folder.</param>
    /// <returns>The folder.</returns>
    private static string ScreenshotsParentFolder(JsonElement? config, string personal)
    {
        if (GetBool(config, "UseCustomScreenshotsPath"))
        {
            string primary = GetString(config, "CustomScreenshotsPath") ?? string.Empty;
            string fallback = GetString(config, "CustomScreenshotsPath2") ?? string.Empty;
            if (primary.Length > 0)
            {
                primary = ExpandFolderVariables(primary);
                if (fallback.Length == 0 || Directory.Exists(primary))
                {
                    return primary;
                }
            }

            if (fallback.Length > 0 && Directory.Exists(ExpandFolderVariables(fallback)))
            {
                return ExpandFolderVariables(fallback);
            }
        }

        return Path.Combine(personal, "Screenshots");
    }

    /// <summary>Adds a task's override folder as a rule when the task overrides the screenshots folder.</summary>
    /// <param name="taskSettings">A <c>TaskSettings</c> object.</param>
    /// <param name="installDirectory">ShareX's program folder (base of relative patterns), if known.</param>
    /// <param name="rules">Accumulated rules.</param>
    private static void AddOverrideFolder(JsonElement taskSettings, string? installDirectory, List<ShareXFolderRule> rules)
    {
        if (taskSettings.ValueKind == JsonValueKind.Object
            && GetBool(taskSettings, "OverrideScreenshotsFolder")
            && GetString(taskSettings, "ScreenshotsFolder") is { Length: > 0 } pattern
            && Absolute(ExpandFolderVariables(pattern), installDirectory) is { } absolute
            && SplitPattern(absolute) is { } parts)
        {
            AddRule(rules, parts.Root, parts.Relative);
        }
    }

    /// <summary>Adds a rule, skipping one whose root is not a usable path (a hand-edited or corrupt setting).</summary>
    /// <param name="rules">Accumulated rules.</param>
    /// <param name="root">Absolute root.</param>
    /// <param name="relativePattern">Subfolder pattern.</param>
    private static void AddRule(List<ShareXFolderRule> rules, string root, string relativePattern)
    {
        try
        {
            rules.Add(new ShareXFolderRule(root, relativePattern));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // One unusable folder must not hide ShareX (tab) or the other folders.
        }
    }

    /// <summary>
    /// Makes a configured path absolute the way ShareX does (relative = relative to its program folder).
    /// </summary>
    /// <param name="path">An expanded path or pattern.</param>
    /// <param name="installDirectory">ShareX's program folder, if known.</param>
    /// <returns>The absolute path, or <see langword="null"/> when it is relative and the program folder is unknown.</returns>
    private static string? Absolute(string path, string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim();
        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        // Path.Combine (not GetFullPath) so name tokens such as "%y" survive untouched.
        return installDirectory is not null && !Path.IsPathRooted(path) ? Path.Combine(installDirectory, path) : null;
    }

    /// <summary>Finds ShareX's program folder: its installer's uninstall key, else any uninstall entry named ShareX (Steam), else the running process.</summary>
    /// <returns>The folder (or <see langword="null"/>) and how it was found.</returns>
    private static (string? Directory, string DetectedBy) FindInstallDirectory()
    {
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall is null)
                {
                    continue;
                }

                using (var installer = uninstall.OpenSubKey(InstallerAppId + "_is1"))
                {
                    if (installer?.GetValue("InstallLocation") is string location && Directory.Exists(location))
                    {
                        return (location, $"installer registration ({hive})");
                    }
                }

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is string display && string.Equals(display, AppName, StringComparison.OrdinalIgnoreCase)
                        && entry.GetValue("InstallLocation") is string location && Directory.Exists(location))
                    {
                        return (location, $"uninstall entry '{name}' ({hive})");
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A locked-down hive only hides one source; the others still count.
            }
        }

        try
        {
            foreach (var process in Process.GetProcessesByName(AppName))
            {
                using (process)
                {
                    if (process.MainModule?.FileName is { } exe)
                    {
                        return (Path.GetDirectoryName(exe), "running process");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Not allowed to inspect it (elevated ShareX): fall back to the personal-folder check.
        }

        return (null, "personal folder");
    }

    /// <summary>Reads <c>SOFTWARE\ShareX\PersonalPath</c> (HKLM first, like ShareX).</summary>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static string? ReadRegistryPersonalPath()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\ShareX");
                if (key?.GetValue("PersonalPath") is string value && value.Trim().Length > 0)
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable: same as absent.
            }
        }

        return null;
    }

    /// <summary>Reads a small text file without blocking ShareX's writes.</summary>
    /// <param name="path">File.</param>
    /// <returns>The trimmed text, or <see langword="null"/> when missing or unreadable.</returns>
    private static string? ReadText(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses a ShareX JSON settings file without blocking ShareX's writes.</summary>
    /// <param name="path">File.</param>
    /// <returns>The document (caller disposes), or <see langword="null"/> when missing, unreadable or invalid.</returns>
    private static JsonDocument? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonDocument.Parse(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A string property, or <see langword="null"/>.</summary>
    /// <param name="element">Object (may be absent).</param>
    /// <param name="name">Property name.</param>
    /// <returns>The value.</returns>
    private static string? GetString(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>A boolean property, <see langword="false"/> when absent or not a boolean.</summary>
    /// <param name="element">Object (may be absent).</param>
    /// <param name="name">Property name.</param>
    /// <returns>The value.</returns>
    private static bool GetBool(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
