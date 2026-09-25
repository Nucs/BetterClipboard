using System.Text.Json.Serialization;

namespace BetterClipboard.Core.Settings;

/// <summary>Where the flyout appears when summoned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlyoutPlacement>))]
public enum FlyoutPlacement
{
    /// <summary>Next to the text caret like Win+V; falls back to the mouse cursor when the app exposes no caret.</summary>
    NearCaret = 0,

    /// <summary>Next to the mouse cursor.</summary>
    NearCursor = 1,

    /// <summary>Centered on the monitor that holds the foreground window.</summary>
    CenterScreen = 2,
}

/// <summary>Light/dark choice for BetterClipboard's own windows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    /// <summary>Follow the Windows app theme.</summary>
    System = 0,

    /// <summary>Always light.</summary>
    Light = 1,

    /// <summary>Always dark.</summary>
    Dark = 2,
}

/// <summary>
/// User settings, persisted as JSON by <see cref="SettingsStore"/>. Immutable: change settings with
/// <c>with</c> expressions through <see cref="SettingsStore.Update"/> so every reader sees a consistent snapshot.
/// </summary>
/// <remarks>
/// Adding a property is backward compatible (missing JSON members keep their defaults); renaming or
/// removing one silently resets that setting for existing users — avoid it, or migrate in
/// <see cref="Normalize"/> using <see cref="SchemaVersion"/>.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Settings file format version, for future migrations.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Global shortcut that opens the flyout, e.g. <c>Win+V</c> or <c>Ctrl+Shift+V</c>.</summary>
    public string OpenHotkey { get; init; } = "Win+V";

    /// <summary>
    /// When the shortcut is already owned by Windows (Win+V is, by Explorer), intercept it with a
    /// low-level keyboard hook instead of giving up. The hook only swallows the exact combination.
    /// </summary>
    public bool UseKeyboardHookFallback { get; init; } = true;

    /// <summary>Maximum unpinned entries kept (most recently used win); 0 = unlimited.</summary>
    public int MaxItems { get; init; } = 10_000;

    /// <summary>Drop unpinned entries not used for this many days; 0 = keep forever.</summary>
    public int RetentionDays { get; init; }

    /// <summary>Largest single copy recorded, in MB (Win+V caps at 4 MB).</summary>
    public int MaxItemSizeMB { get; init; } = 64;

    /// <summary>Total payload budget for unpinned history, in MB; 0 = unlimited.</summary>
    public int MaxTotalSizeMB { get; init; } = 4096;

    /// <summary>Record image-only copies (screenshots etc.).</summary>
    public bool CaptureImages { get; init; } = true;

    /// <summary>Record file lists copied in Explorer (paths only, not file contents).</summary>
    public bool CaptureFiles { get; init; } = true;

    /// <summary>
    /// Also keep app-private formats (Office, IDEs, design tools) for highest paste fidelity. Costs a lot
    /// more disk space (Excel copies carry many formats), hence off by default.
    /// </summary>
    public bool PreserveAllFormats { get; init; }

    /// <summary>After choosing an item, paste it into the previously focused app (otherwise only copy it).</summary>
    public bool PasteOnSelect { get; init; } = true;

    /// <summary>Move an item to the top of history when it is pasted from the flyout.</summary>
    public bool MoveToTopOnPaste { get; init; } = true;

    /// <summary>Show pinned items above everything else.</summary>
    public bool PinnedOnTop { get; init; } = true;

    /// <summary>Import Windows' current Win+V history (including its pinned items) at every startup.</summary>
    public bool ImportWindowsHistoryOnStartup { get; init; } = true;

    /// <summary>Temporarily stop recording live copies.</summary>
    public bool IsCapturePaused { get; init; }

    /// <summary>Process names (e.g. <c>KeePass</c>) whose copies are never recorded.</summary>
    /// <remarks>
    /// Starts out holding <see cref="KnownPasswordManagers.All"/>: <see cref="Normalize"/> merges every
    /// catalogued name not yet in <see cref="SeededIgnoredApps"/>, for new and existing users alike.
    /// Entries are free text (a name, <c>name.exe</c> or a full path) and matched after
    /// <see cref="Services.CaptureRules.NormalizeProcessName"/>.
    /// </remarks>
    public IReadOnlyList<string> IgnoredApps { get; init; } = [];

    /// <summary>
    /// Catalogued password-manager process names (<see cref="KnownPasswordManagers"/>) already offered to
    /// <see cref="IgnoredApps"/> — whether or not the user kept them.
    /// </summary>
    /// <remarks>
    /// This is what makes a deletion stick: a name listed here is never merged in again, so removing
    /// <c>KeePass</c> from the list survives restarts and updates, while names a later release adds to the
    /// catalog still arrive exactly once. Missing (settings from before the catalog) = nothing offered yet.
    /// Footgun: an older version saving the file drops this member, and the next newer version then offers
    /// the whole catalog again.
    /// </remarks>
    public IReadOnlyList<string> SeededIgnoredApps { get; init; } = [];

    /// <summary>Where the flyout appears.</summary>
    public FlyoutPlacement Placement { get; init; } = FlyoutPlacement.NearCaret;

    /// <summary>Theme of BetterClipboard's windows.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>
    /// Serve the <c>bclip</c> command line (scripts, AI assistants): list, search, read, copy and add
    /// history items over a named pipe restricted to this Windows account.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, on purpose:</b> while on, <i>any</i> program running as the user can read the
    /// whole (otherwise encrypted) history through the pipe. The pipe only exists while this is on, so
    /// turning it off is a real barrier, not just a flag the client checks.
    /// </remarks>
    public bool EnableCommandLine { get; init; }

    /// <summary>
    /// Clamps every value into its supported range so a hand-edited or corrupted file cannot put the app
    /// into a broken state (e.g. a negative item cap), and merges catalogued password managers the
    /// settings have not seen yet into <see cref="IgnoredApps"/> (see <see cref="SeededIgnoredApps"/>).
    /// </summary>
    /// <remarks>
    /// Runs on every load and save, so everything here is idempotent. The catalog merge happens in memory
    /// on load and reaches the file with the next save — until then it simply repeats on each start.
    /// </remarks>
    /// <returns>A normalized copy.</returns>
    public AppSettings Normalize()
    {
        var ignored = (IgnoredApps ?? [])
            .Select(a => a?.Trim() ?? string.Empty)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Stored normalized, so "KeePass.exe" typed into a hand-edited file still counts as seen.
        var seeded = (SeededIgnoredApps ?? [])
            .Select(Services.CaptureRules.NormalizeProcessName)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var (withCatalog, seen) = KnownPasswordManagers.Seed(ignored, seeded);

        var normalized = this with
        {
            SchemaVersion = 1,
            OpenHotkey = string.IsNullOrWhiteSpace(OpenHotkey) ? "Win+V" : OpenHotkey.Trim(),
            MaxItems = Math.Clamp(MaxItems, 0, 1_000_000),
            RetentionDays = Math.Clamp(RetentionDays, 0, 36_500),
            MaxItemSizeMB = Math.Clamp(MaxItemSizeMB, 1, 1024),
            MaxTotalSizeMB = Math.Clamp(MaxTotalSizeMB, 0, 1_048_576),
            IgnoredApps = withCatalog,
            SeededIgnoredApps = seen,
            Placement = Enum.IsDefined(Placement) ? Placement : FlyoutPlacement.NearCaret,
            Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
        };
        return normalized;
    }
}
