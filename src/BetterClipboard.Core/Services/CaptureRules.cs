using BetterClipboard.Core.Model;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Services;

/// <summary>
/// An immutable snapshot of everything that decides whether and how a capture is stored. Built from
/// <see cref="AppSettings"/> and re-read for every capture, so settings changes apply to the next copy
/// without restarting the pipeline.
/// </summary>
public sealed record CaptureRules
{
    /// <summary>When set, live captures are dropped (imports and user actions still work).</summary>
    public bool IsPaused { get; init; }

    /// <summary>Whether image-only clips are stored.</summary>
    public bool CaptureImages { get; init; } = true;

    /// <summary>Whether file-list clips are stored.</summary>
    public bool CaptureFiles { get; init; } = true;

    /// <summary>Captures whose total payload exceeds this many bytes are dropped.</summary>
    public long MaxItemBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Process names (no extension, case-insensitive) whose copies are never recorded.</summary>
    public IReadOnlySet<string> IgnoredProcessNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Retention applied after every new entry.</summary>
    public RetentionPolicy Retention { get; init; } = RetentionPolicy.Unlimited;

    /// <summary>
    /// Builds rules from user settings.
    /// </summary>
    /// <param name="settings">The current settings snapshot.</param>
    /// <returns>The equivalent rules.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public static CaptureRules FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CaptureRules
        {
            IsPaused = settings.IsCapturePaused,
            CaptureImages = settings.CaptureImages,
            CaptureFiles = settings.CaptureFiles,
            MaxItemBytes = settings.MaxItemSizeMB * 1024L * 1024L,
            IgnoredProcessNames = new HashSet<string>(
                settings.IgnoredApps.Select(NormalizeProcessName).Where(n => n.Length > 0),
                StringComparer.OrdinalIgnoreCase),
            Retention = new RetentionPolicy(
                settings.MaxItems,
                settings.RetentionDays > 0 ? TimeSpan.FromDays(settings.RetentionDays) : null,
                settings.MaxTotalSizeMB * 1024L * 1024L),
        };
    }

    /// <summary>
    /// Returns whether the capture's source app is on the ignore list.
    /// </summary>
    /// <param name="source">The capture's source, if known; unknown sources are never ignored.</param>
    /// <returns><see langword="true"/> when the capture must be dropped.</returns>
    public bool IsIgnored(SourceAppInfo? source) =>
        source is not null && IgnoredProcessNames.Contains(NormalizeProcessName(source.ProcessName));

    /// <summary>
    /// Normalizes user input like <c>"KeePass.exe "</c> or a full path to the bare process name <c>KeePass</c>.
    /// </summary>
    /// <param name="value">Raw user input or process name.</param>
    /// <returns>The normalized name; empty for blank input.</returns>
    public static string NormalizeProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(value.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
