namespace BetterClipboard.Core;

/// <summary>
/// Resolves where BetterClipboard keeps its data: <c>%LOCALAPPDATA%\BetterClipboard</c> by default, or the
/// directory in the <c>BETTERCLIPBOARD_DATA_DIR</c> environment variable (used by tests and dev runs so they
/// never touch the real history).
/// </summary>
/// <remarks>
/// LocalAppData (not Roaming) on purpose: history can be gigabytes and must not be synced by roaming
/// profiles, and it is per-machine by nature (file paths, local apps).
/// </remarks>
public sealed class AppPaths
{
    /// <summary>Environment variable that overrides the data directory.</summary>
    public const string DataDirectoryVariable = "BETTERCLIPBOARD_DATA_DIR";

    /// <summary>
    /// Creates paths rooted at <paramref name="dataDirectory"/>.
    /// </summary>
    /// <param name="dataDirectory">Root data directory; created lazily by <see cref="EnsureCreated"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="dataDirectory"/> is null or blank.</exception>
    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// <summary>
    /// The paths for this process: the environment override when set, otherwise <c>%LOCALAPPDATA%\BetterClipboard</c>.
    /// </summary>
    /// <returns>The resolved paths.</returns>
    public static AppPaths ResolveDefault()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return new AppPaths(!string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterClipboard"));
    }

    /// <summary>Root data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Where v0.1 pre-releases kept the <b>plaintext</b> history database. Only read once, to adopt it into
    /// the machine-bound store (see <see cref="Storage.MachineBoundHistory"/>); nothing writes here any more.
    /// </summary>
    public string LegacyDatabasePath => Path.Combine(DataDirectory, "history.db");

    /// <summary>Parent of the per-machine/per-user encrypted history stores.</summary>
    public string StoresDirectory => Path.Combine(DataDirectory, "stores");

    /// <summary>
    /// Folder of one encrypted store, named by its id (a UUIDv5 computed from the machine binding, see
    /// <see cref="Security.MachineBinding.StoreId"/>), so each machine + Windows user pair gets its own
    /// folder and a copied profile can never be mistaken for this machine's history.
    /// </summary>
    /// <param name="storeId">The store id.</param>
    /// <returns><c>{DataDirectory}\stores\{storeId}</c>.</returns>
    public string StoreDirectory(Guid storeId) => Path.Combine(StoresDirectory, storeId.ToString("D"));

    /// <summary>User settings JSON.</summary>
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Daily rolling log files.</summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Creates the data and log directories if missing.
    /// </summary>
    /// <exception cref="IOException">The directories could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The location is not writable.</exception>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
