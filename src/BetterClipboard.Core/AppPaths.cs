namespace BetterClipboard.Core;

/// <summary>
/// Resolves where BetterClipboard keeps its data: <c>%LOCALAPPDATA%\BetterClipboard</c> for every kind of install (the
/// Microsoft Store edition too, once it proved that folder safe on the PC), the Store package's own folder as that
/// edition's fallback, or the directory in the <c>BETTERCLIPBOARD_DATA_DIR</c> environment variable (used by tests and
/// dev runs so they never touch the real history).
/// </summary>
/// <remarks>
/// <para>
/// LocalAppData (not Roaming) on purpose: history can be gigabytes and must not be synced by roaming
/// profiles, and it is per-machine by nature (file paths, local apps).
/// </para>
/// <para>
/// <b>The Microsoft Store edition</b> (CLAUDE.md §2.47) shares <see cref="DefaultDataDirectory"/> with every other kind
/// of install since 2026-11-14 (the owner's "Share it"), so a user can switch between them and keep one history. Left to
/// itself, Windows would decide where such a package's writes land: into a private copy, deleted with the app, when the
/// package creates the folder, but for real when the folder already exists (measured). So the app makes the folder
/// with a program outside the package and proves its writes real before it uses it
/// (<see cref="Editions.StoreDataFolder"/>); <see cref="PackagedDataDirectory"/>, the package's own folder, is the
/// fallback and where version 1.2.11 kept its data.
/// </para>
/// </remarks>
public sealed class AppPaths
{
    /// <summary>Environment variable that overrides the data directory.</summary>
    public const string DataDirectoryVariable = "BETTERCLIPBOARD_DATA_DIR";

    /// <summary>Instance name of the installed app (default data directory): single-instance lock, commands, bclip pipe.</summary>
    public const string DefaultInstanceName = "BetterClipboard";

    /// <summary>
    /// The data directory of every kind of install when <see cref="DataDirectoryVariable"/> is not set:
    /// <c>%LOCALAPPDATA%\BetterClipboard</c>. The Microsoft Store edition uses it too once it is proven safe on the PC
    /// (<see cref="Editions.StoreDataFolder"/>); while that edition runs from its own folder instead, it reads this one
    /// only to move another edition's history over (<c>Editions.EditionDataMigrator</c>).
    /// </summary>
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterClipboard");

    /// <summary>
    /// Creates paths rooted at <paramref name="dataDirectory"/>, for a copy whose home is
    /// <see cref="DefaultDataDirectory"/> (every copy but the Microsoft Store edition).
    /// </summary>
    /// <param name="dataDirectory">Root data directory; created lazily by <see cref="EnsureCreated"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="dataDirectory"/> is null or blank.</exception>
    public AppPaths(string dataDirectory)
        : this(dataDirectory, DefaultDataDirectory)
    {
    }

    /// <summary>
    /// Creates paths rooted at <paramref name="dataDirectory"/> for a copy whose own folder is
    /// <paramref name="homeDirectory"/>.
    /// </summary>
    /// <param name="dataDirectory">Root data directory; created lazily by <see cref="EnsureCreated"/>.</param>
    /// <param name="homeDirectory">
    /// The folder this copy uses when nothing overrides it: <see cref="DefaultDataDirectory"/>, or the Store package's
    /// folder. A data directory equal to it makes this the regular instance (<see cref="InstanceScope"/> is
    /// <see langword="null"/>); any other one makes it an isolated instance.
    /// </param>
    /// <exception cref="ArgumentException">An argument is null or blank.</exception>
    public AppPaths(string dataDirectory, string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        HomeDirectory = Path.GetFullPath(homeDirectory);
    }

    /// <summary>
    /// The paths for this process: the environment override when set, otherwise <c>%LOCALAPPDATA%\BetterClipboard</c>.
    /// </summary>
    /// <returns>The resolved paths.</returns>
    public static AppPaths ResolveDefault() => ResolveDefault(packageFamilyName: null);

    /// <summary>
    /// The paths for this process: the environment override when set; otherwise, for a process that runs from the Store
    /// package, the folder that edition's app recorded (<see cref="Editions.StoreDataFolder.ResolveReadOnly"/>: the shared
    /// folder once proven, else the package's own); otherwise <c>%LOCALAPPDATA%\BetterClipboard</c>. Changes nothing on
    /// disk: <c>bclip</c> resolves its folder this way.
    /// </summary>
    /// <param name="packageFamilyName">
    /// The package family name of the running process (<c>Windows.Packaging.PackageIdentity.FamilyName</c>), or
    /// <see langword="null"/> or blank for a process without package identity. Core has no way to ask Windows itself,
    /// so the program's entry point passes it.
    /// </param>
    /// <returns>The resolved paths.</returns>
    public static AppPaths ResolveDefault(string? packageFamilyName) => ResolveDefault(packageFamilyName, storeDataDirectory: null);

    /// <summary>
    /// The paths for this process, with the folder the Store edition's app decided on at this start
    /// (<see cref="Editions.StoreDataFolder.Establish"/>): the environment override when set; otherwise
    /// <paramref name="storeDataDirectory"/> for a process of the package, or what <see cref="ResolveDefault(string?)"/>
    /// resolves.
    /// </summary>
    /// <param name="packageFamilyName">The running process's package family name, or <see langword="null"/> or blank without one.</param>
    /// <param name="storeDataDirectory">
    /// The Store edition's established data folder for this process, or <see langword="null"/> when nothing was established
    /// (every other kind of install, an isolated instance, a process that only reads).
    /// </param>
    /// <returns>The resolved paths.</returns>
    public static AppPaths ResolveDefault(string? packageFamilyName, string? storeDataDirectory)
    {
        string home = string.IsNullOrWhiteSpace(packageFamilyName)
            ? DefaultDataDirectory
            : !string.IsNullOrWhiteSpace(storeDataDirectory)
                ? storeDataDirectory
                : Editions.StoreDataFolder.ResolveReadOnly(Editions.StoreDataPlaces.For(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), packageFamilyName));
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return new AppPaths(!string.IsNullOrWhiteSpace(overridden) ? overridden : home, home);
    }

    /// <summary>
    /// The Microsoft Store package's own data folder: its <c>LocalState</c> folder, by its real path
    /// (<c>%LOCALAPPDATA%\Packages\{family}\LocalState\BetterClipboard</c>), which Windows deletes when the app is
    /// uninstalled. The edition's fallback when the shared folder cannot be used, and where version 1.2.11 kept its data
    /// (<see cref="Editions.StoreDataFolder"/>).
    /// </summary>
    /// <param name="packageFamilyName">The package family name, e.g. <c>Publisher.BetterClipboard_1a2b3c4d5e6f7</c>.</param>
    /// <returns>The folder.</returns>
    /// <exception cref="ArgumentException"><paramref name="packageFamilyName"/> is null or blank.</exception>
    public static string PackagedDataDirectory(string packageFamilyName) =>
        Editions.StoreDataPlaces.For(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), packageFamilyName).PackageDirectory;

    /// <summary>
    /// Whether this copy keeps its data in the folder every kind of install uses (<see cref="DefaultDataDirectory"/>):
    /// every copy but an isolated instance, and the Microsoft Store edition once it proved that folder on the PC. When
    /// true, there is no "other editions' data" to bring over or remove: it is this copy's own.
    /// </summary>
    public bool UsesSharedDataDirectory =>
        string.Equals(Path.TrimEndingDirectorySeparator(DataDirectory), Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultDataDirectory)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Root data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// The folder this copy uses when nothing overrides it (see the two-argument constructor).
    /// </summary>
    public string HomeDirectory { get; }

    /// <summary>
    /// <see langword="null"/> for the installed app's own data directory (<see cref="HomeDirectory"/>); otherwise a
    /// short, stable id of the directory (e.g. <c>dir-3fa2c1d09b7e</c>) that separates this instance from the installed
    /// one.
    /// </summary>
    /// <remarks>
    /// A dev or test run with <see cref="DataDirectoryVariable"/> set used to share the installed app's
    /// single-instance lock, command events and bclip pipe: it could not start next to it, and its
    /// <c>--exit</c> closed the <i>user's</i> app. Scoping every per-session name by data directory makes an
    /// isolated run truly isolated. The default location keeps the unscoped names, so installed versions
    /// keep finding each other across updates — and across editions: the Microsoft Store edition in its own folder
    /// uses the same unscoped names as a copy in <see cref="DefaultDataDirectory"/>, because only one BetterClipboard
    /// may own the clipboard listener and Win+V in a session, whichever edition it is.
    /// </remarks>
    public string? InstanceScope =>
        string.Equals(Path.TrimEndingDirectorySeparator(DataDirectory), Path.TrimEndingDirectorySeparator(HomeDirectory), StringComparison.OrdinalIgnoreCase)
            ? null
            : "dir-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(DataDirectory).ToUpperInvariant())).AsSpan(0, 6));

    /// <summary>
    /// Name for this instance's per-session objects: <see cref="DefaultInstanceName"/>, or that name plus
    /// <see cref="InstanceScope"/> for an overridden data directory.
    /// </summary>
    public string InstanceName => InstanceScope is { } scope ? $"{DefaultInstanceName}.{scope}" : DefaultInstanceName;

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
    /// The updater's working folder: the cached release list, a downloaded package until it is installed, the installer
    /// script taken from it, and the installer's result. Everything in it can be deleted at any time; the app empties it
    /// of old downloads when it starts.
    /// </summary>
    public string UpdatesDirectory => Path.Combine(DataDirectory, "updates");

    /// <summary>
    /// The dev-builds folder: development builds published on this PC by <c>tools/release/publish-dev.ps1</c>, which a
    /// copy the installer installed offers through its update button before GitHub's releases
    /// (<see cref="Updates.LocalBuildFeed"/>). Nothing creates it but the publish script, so it exists only on a developer's
    /// PC; an isolated instance (<see cref="DataDirectoryVariable"/>) has its own, inside its own data directory.
    /// </summary>
    public string DevBuildsDirectory => Path.Combine(DataDirectory, Updates.LocalBuildFeed.DirectoryName);

    /// <summary>
    /// <c>installer.json</c>, which <c>install.ps1</c> writes on every install: what was installed where. The updater
    /// reads its <c>installDir</c> to tell a copy the installer owns from one extracted by hand
    /// (<see cref="Updates.InstalledCopy"/>).
    /// </summary>
    public string InstallerStatePath => Path.Combine(DataDirectory, "installer.json");

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

    /// <summary>
    /// Whether this data folder ever held a history: anything in <see cref="StoresDirectory"/> (a store of any machine
    /// or user, a quarantined one included) or a v0.1 plaintext database. False only before the app's very first start
    /// here: installers may write <see cref="SettingsPath"/> before that start, but none of them creates a store.
    /// </summary>
    /// <remarks>
    /// Must be read before the store is opened, which creates it. Until 2026-11-09 it decided whether the language picker
    /// asked; now the picker asks whenever no language was chosen (<c>Localization.LanguageChoice.ShouldAsk</c>), and
    /// this only words its log line ("chosen at the first start" or "chosen at a start with a history already kept").
    /// A folder that cannot be read counts as holding a history.
    /// </remarks>
    /// <returns>Whether a history was kept here before.</returns>
    public bool HasHistory()
    {
        try
        {
            return File.Exists(LegacyDatabasePath)
                || (Directory.Exists(StoresDirectory) && Directory.EnumerateFileSystemEntries(StoresDirectory).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
