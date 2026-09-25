using System.Text.Json;
using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON and publishes changes.
/// </summary>
/// <remarks>
/// <para>
/// Loading is forgiving: a missing file yields defaults; an unreadable/corrupt file is renamed to
/// <c>settings.json.corrupt-&lt;timestamp&gt;</c> (so the user can recover it) and defaults are used —
/// the app must always start.
/// </para>
/// <para>
/// Saving is atomic (write a temp file, then replace) so a crash mid-write never leaves a truncated file.
/// <see cref="Current"/> is swapped with a reference assignment and is therefore safe to read from any thread.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Lock gate = new();
    private AppSettings current = new();

    /// <summary>
    /// Creates a store for the settings file at <paramref name="path"/>. Call <see cref="Load"/> to read it.
    /// </summary>
    /// <param name="path">Full path of <c>settings.json</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
    }

    /// <summary>Raised after settings changed through <see cref="Update"/>, on the calling thread, with the new snapshot.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Path of the settings file.</summary>
    public string FilePath { get; }

    /// <summary>The current (normalized) settings snapshot.</summary>
    public AppSettings Current => Volatile.Read(ref current);

    /// <summary>
    /// Reads the settings file (see class remarks for failure behavior).
    /// </summary>
    /// <returns>The loaded, normalized settings (also available as <see cref="Current"/>).</returns>
    public AppSettings Load()
    {
        AppSettings loaded;
        try
        {
            loaded = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Warn($"Settings file unreadable ({ex.Message}); using defaults.");
            TryQuarantineCorruptFile();
            loaded = new AppSettings();
        }

        Volatile.Write(ref current, loaded.Normalize());
        return Current;
    }

    /// <summary>
    /// Reads a settings file <b>without any side effects</b> — for other processes (the <c>bclip</c>
    /// command line) that must never quarantine, rewrite or log about the app's own file.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Load"/>, a missing, unreadable or corrupt file is not renamed aside: a reader in
    /// another process can race the app's atomic save (the rename window) and must not mistake that for
    /// corruption. Callers treat <see langword="null"/> as "unknown" and fall back to defaults.
    /// </remarks>
    /// <param name="path">Settings file path.</param>
    /// <returns>The normalized settings, the defaults when the file does not exist, or <see langword="null"/> when it could not be read.</returns>
    public static AppSettings? TryReadSnapshot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings().Normalize();
            }

            // FileShare.ReadWrite | Delete: never block the app's save (temp file + replace).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return (JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies a change, persists it and raises <see cref="Changed"/>.
    /// </summary>
    /// <param name="mutate">Returns the new settings from the old ones, typically <c>s =&gt; s with { ... }</c>.</param>
    /// <returns>The new normalized snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutate"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file could not be written (the in-memory change is kept).</exception>
    /// <exception cref="UnauthorizedAccessException">The file or directory is not writable (the in-memory change is kept).</exception>
    public AppSettings Update(Func<AppSettings, AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings next;
        lock (gate)
        {
            next = mutate(Current).Normalize();
            Volatile.Write(ref current, next);
            Save(next);
        }

        Changed?.Invoke(this, next);
        return next;
    }

    /// <summary>Atomically writes <paramref name="settings"/> to <see cref="FilePath"/>.</summary>
    /// <param name="settings">The snapshot to persist.</param>
    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Renames a corrupt settings file aside so defaults can be written without losing it.</summary>
    private void TryQuarantineCorruptFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Move(FilePath, $"{FilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not move the corrupt settings file aside: {ex.Message}");
        }
    }
}
