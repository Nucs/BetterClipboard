using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BetterClipboard.Core.Editions;

/// <summary>Where the Microsoft Store edition keeps its data on this PC (<see cref="StoreDataFolder"/>).</summary>
public enum StoreDataMode
{
    /// <summary>
    /// <c>%LOCALAPPDATA%\BetterClipboard</c>, the folder every other kind of install uses: one history for all of them,
    /// kept when the app is uninstalled (the owner's choice, 2026-11-14).
    /// </summary>
    Shared = 0,

    /// <summary>
    /// The package's own <c>LocalState\BetterClipboard</c>, which Windows deletes with the app: the fallback when the
    /// shared folder could not be made, its writes were not proven real, or it holds another history while this edition
    /// still has its own from before.
    /// </summary>
    Package = 1,
}

/// <summary>
/// The decision of one start: the mode, the folder, and what happened on the way (lines for the log, content-free).
/// </summary>
/// <param name="Mode">Shared or the package's own folder.</param>
/// <param name="Directory">The data folder to use.</param>
/// <param name="Notes">What was found or done, in the log's English; empty for an ordinary start.</param>
public sealed record StoreDataDecision(StoreDataMode Mode, string Directory, IReadOnlyList<string> Notes);

/// <summary>
/// The folders and files one Store package uses for its data, all by their real paths. Never through Windows'
/// redirection: these are the paths a program outside the package sees.
/// </summary>
/// <param name="SharedDirectory"><c>%LOCALAPPDATA%\BetterClipboard</c>: every edition's data folder.</param>
/// <param name="PackageDirectory"><c>%LOCALAPPDATA%\Packages\{family}\LocalState\BetterClipboard</c>: the package's own folder.</param>
/// <param name="RedirectDirectory">
/// <c>%LOCALAPPDATA%\Packages\{family}\LocalCache\Local\BetterClipboard</c>: where Windows puts a folder of that name when
/// the package itself creates it directly under Local AppData. Such a private copy shadows the real folder for this
/// package only (measured, docs/microsoft-store.md §2).
/// </param>
/// <param name="MarkerPath">
/// <c>%LOCALAPPDATA%\Packages\{family}\LocalState\data-folder.json</c>: the last decision and the Windows build it was
/// proven on (<see cref="StoreDataMarker"/>). In the package's own folder, so Windows removes it with the app.
/// </param>
public sealed record StoreDataPlaces(string SharedDirectory, string PackageDirectory, string RedirectDirectory, string MarkerPath)
{
    /// <summary>The marker's file name, in the package's <c>LocalState</c> (next to its <c>BetterClipboard</c> folder).</summary>
    public const string MarkerFileName = "data-folder.json";

    /// <summary>
    /// The places of one package on this PC.
    /// </summary>
    /// <param name="localAppData">The user's Local AppData folder (<c>%LOCALAPPDATA%</c>); tests pass a scratch folder.</param>
    /// <param name="packageFamilyName">The package family name, e.g. <c>Nucs.BetterClipboard_mgez7y7xa0my4</c>.</param>
    /// <returns>The places.</returns>
    /// <exception cref="ArgumentException">An argument is null or blank.</exception>
    public static StoreDataPlaces For(string localAppData, string packageFamilyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        string root = Path.GetFullPath(localAppData);
        string package = Path.Combine(root, "Packages", packageFamilyName.Trim());
        return new StoreDataPlaces(
            Path.Combine(root, "BetterClipboard"),
            Path.Combine(package, "LocalState", "BetterClipboard"),
            Path.Combine(package, "LocalCache", "Local", "BetterClipboard"),
            Path.Combine(package, "LocalState", MarkerFileName));
    }
}

/// <summary>
/// File operations done by a program outside the Store package (cmd.exe in the app), which Windows does not redirect:
/// the only way for the package to make a folder directly under Local AppData for real, and the only view of the real
/// file system while a private copy shadows a folder (measured, docs/microsoft-store.md §2).
/// </summary>
/// <remarks>
/// Every method answers whether it worked instead of throwing: the caller checks the outcome on the file system anyway,
/// and a program that could not be started is one more way of "not done".
/// </remarks>
public interface IOutsideFileSystem
{
    /// <summary>Whether a file or folder exists in the real file system.</summary>
    /// <param name="path">The full path.</param>
    /// <returns><see langword="true"/> when it exists; <see langword="false"/> when not, or when the program could not tell.</returns>
    bool Exists(string path);

    /// <summary>Creates a folder, and its missing parents, in the real file system.</summary>
    /// <param name="path">The full path.</param>
    /// <returns>Whether the program reported success.</returns>
    bool CreateDirectory(string path);

    /// <summary>Moves (renames) a file or folder within one volume. The target must not exist.</summary>
    /// <param name="source">The full path of what is moved.</param>
    /// <param name="target">Its new full path.</param>
    /// <returns>Whether the program reported success.</returns>
    bool Move(string source, string target);
}

/// <summary>
/// The Store edition's last decision about its data folder, kept in the package's <c>LocalState</c>: which folder, and
/// the Windows build it was proven on. Read at every start (cheap); the proof runs again only on another build.
/// </summary>
/// <param name="Mode">The folder in use.</param>
/// <param name="WindowsBuild">The build of Windows the decision was made on, e.g. <c>10.0.26200.8875</c>.</param>
/// <param name="CheckedUtc">When.</param>
/// <param name="Reason">For <see cref="StoreDataMode.Package"/>: why the shared folder is not used (English, content-free).</param>
public sealed record StoreDataMarker(StoreDataMode Mode, string WindowsBuild, DateTimeOffset CheckedUtc, string? Reason)
{
    /// <summary>The marker's spelling of <see cref="StoreDataMode.Shared"/>.</summary>
    private const string SharedName = "shared";

    /// <summary>The marker's spelling of <see cref="StoreDataMode.Package"/>.</summary>
    private const string PackageName = "package";

    /// <summary>
    /// Reads a marker. A missing, unreadable or malformed file is no marker: the next start decides again, which costs a
    /// proof, never a wrong folder.
    /// </summary>
    /// <param name="path">The marker file.</param>
    /// <returns>The marker, or <see langword="null"/>.</returns>
    public static StoreDataMarker? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("folder", out var folder) || folder.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("windowsBuild", out var build) || build.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("checkedUtc", out var checkedUtc) || checkedUtc.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(checkedUtc.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                return null;
            }

            StoreDataMode? mode = folder.GetString() switch
            {
                SharedName => StoreDataMode.Shared,
                PackageName => StoreDataMode.Package,
                _ => null,
            };
            if (mode is null)
            {
                return null;
            }

            string? reason = root.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
            return new StoreDataMarker(mode.Value, build.GetString() ?? string.Empty, when.ToUniversalTime(), reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the marker: a new file next to it, then moved over the old one, so a reader never sees half a file.
    /// </summary>
    /// <param name="path">The marker file; its folder is created when missing (the package's <c>LocalState</c> always exists).</param>
    /// <returns>Whether it was written. A marker that could not be written costs a proof at the next start, nothing else.</returns>
    public bool TryWrite(string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("folder", Mode == StoreDataMode.Shared ? SharedName : PackageName);
                writer.WriteString("windowsBuild", WindowsBuild);
                writer.WriteString("checkedUtc", CheckedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                if (Reason is not null)
                {
                    writer.WriteString("reason", Reason);
                }

                writer.WriteEndObject();
            }

            string temp = path + ".tmp";
            File.WriteAllBytes(temp, buffer.ToArray());
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Decides where the Microsoft Store edition keeps its data, and makes that folder real: <c>%LOCALAPPDATA%\BetterClipboard</c>,
/// the folder every other kind of install uses, whenever that is proven safe on this PC; the package's own folder
/// otherwise (CLAUDE.md §2.47, the owner's "Share it", 2026-11-14).
/// </summary>
/// <remarks>
/// <para>
/// <b>What Windows does to a Store app's files</b> (documented, and measured on Windows 11 25H2 with
/// <c>tools/probes/msix_lab</c>): a folder the package creates directly under Local AppData lands in a private copy
/// (<see cref="StoreDataPlaces.RedirectDirectory"/>), which Windows deletes with the app. Inside a folder that exists for
/// real, everything the package writes is real: new files and subfolders, deletes, renames, memory-mapped files, a file
/// moved over another. So the shared folder must exist for real before the package writes anything into it, and a
/// program outside the package (cmd's <c>mkdir</c>) makes it.
/// </para>
/// <para>
/// <b>The danger it guards against: a private copy.</b> When a private copy of the folder exists next to the real one,
/// the package reads the private files first and writes its new files into the private copy, while every other program
/// sees the real folder: two histories under one name. <see cref="Establish"/> therefore repairs a private copy before
/// anything opens the history, proves once per Windows build that a file it writes lands in the real folder, and falls
/// back to the package's own folder when either fails. <see cref="ResolveReadOnly"/> (bclip) uses the shared folder only
/// when the app recorded a proof and no private copy exists.
/// </para>
/// <para>
/// <b>A history from before.</b> Version 1.2.11 of the Store edition kept its data in the package's own folder.
/// <see cref="Establish"/> moves that history and its settings into the shared folder when the shared folder has no
/// history of its own (one rename each, by the outside program: nothing is copied). When both hold a history (the user
/// went on with another kind of install after 1.2.11), this edition keeps using its own folder: two histories are never
/// merged, and neither is set aside or deleted by a start.
/// </para>
/// </remarks>
public static class StoreDataFolder
{
    /// <summary>The probe's file name prefix (followed by a GUID and <c>.tmp</c>); a leftover is harmless.</summary>
    public const string ProbePrefix = ".store-edition-check-";

    /// <summary>The prefix of a private copy that was set aside inside the shared folder (followed by a time stamp).</summary>
    public const string RecoveredPrefix = "recovered-private-copy-";

    /// <summary>
    /// The folder a process that does not set it up itself should use (bclip): the shared folder when the app recorded
    /// that it uses it and no private copy shadows it, the package's own folder otherwise. Reads two things; changes nothing.
    /// </summary>
    /// <param name="places">The package's places.</param>
    /// <returns>The data folder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="places"/> is <see langword="null"/>.</exception>
    public static string ResolveReadOnly(StoreDataPlaces places)
    {
        ArgumentNullException.ThrowIfNull(places);
        return StoreDataMarker.TryRead(places.MarkerPath) is { Mode: StoreDataMode.Shared } && !Directory.Exists(places.RedirectDirectory)
            ? places.SharedDirectory
            : places.PackageDirectory;
    }

    /// <summary>
    /// Decides this start's data folder and makes it ready, in this order: a private copy is repaired, the shared folder
    /// is made when missing, its writes are proven real (once per Windows build), and a history this edition kept in its
    /// own folder before is moved in when the shared folder has none (see the class remarks).
    /// </summary>
    /// <param name="places">The package's places.</param>
    /// <param name="outside">The program outside the package that makes, moves and looks.</param>
    /// <param name="windowsBuild">This PC's Windows build, e.g. <c>10.0.26200.8875</c>: a new build is proven again.</param>
    /// <param name="now">The time, for the marker and the name of a private copy set aside.</param>
    /// <returns>The decision with its log lines.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="windowsBuild"/> is blank.</exception>
    /// <exception cref="IOException">Listing or reading this edition's own folder failed; the caller then uses that folder as before.</exception>
    /// <exception cref="UnauthorizedAccessException">Listing or reading was refused; see above.</exception>
    /// <remarks>
    /// Never call it while this edition's history is open in this or another process of the package: the caller holds a
    /// lock of its own around it and runs it before the store opens. Not thread-safe; one call per process is enough.
    /// </remarks>
    public static StoreDataDecision Establish(StoreDataPlaces places, IOutsideFileSystem outside, string windowsBuild, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(outside);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsBuild);
        var notes = new List<string>();
        var marker = StoreDataMarker.TryRead(places.MarkerPath);

        // A proof that failed on this very build fails the same way at every start: not tried again until Windows changes.
        if (marker is { Mode: StoreDataMode.Package } && marker.WindowsBuild == windowsBuild && marker.Reason is not null)
        {
            return new StoreDataDecision(StoreDataMode.Package, places.PackageDirectory, notes);
        }

        // 1. A private copy first: while it exists, this process's view of the shared folder is not the real one.
        if (Directory.Exists(places.RedirectDirectory) && !RepairPrivateCopy(places, outside, now, notes))
        {
            notes.Add("A private copy of the shared data folder could not be moved out of the way; this edition uses its own folder for now.");
            return new StoreDataDecision(StoreDataMode.Package, places.PackageDirectory, notes);
        }

        // 2. The shared folder itself, made by a program outside the package. With no private copy left, this process's
        // view of the folder is the real one, so Directory.Exists answers for the real file system here.
        bool madeNow = false;
        if (!Directory.Exists(places.SharedDirectory))
        {
            outside.CreateDirectory(places.SharedDirectory);
            if (!Directory.Exists(places.SharedDirectory))
            {
                return Fallback(places, windowsBuild, now, notes, "The shared data folder could not be made.");
            }

            madeNow = true;
            notes.Add("Made the shared data folder (%LOCALAPPDATA%\\BetterClipboard).");
        }

        // 3. The proof: once per Windows build, a file this process writes must reach the real folder.
        if (marker is not { Mode: StoreDataMode.Shared } || marker.WindowsBuild != windowsBuild)
        {
            if (Prove(places, outside) is { } failure)
            {
                return Fallback(places, windowsBuild, now, notes, failure);
            }

            new StoreDataMarker(StoreDataMode.Shared, windowsBuild, now, null).TryWrite(places.MarkerPath);
            notes.Add($"The shared data folder is written for real on this Windows build ({windowsBuild}).");
        }

        // 4. A history this edition kept in its own folder before (1.2.11).
        var (stays, lasting) = SettleOwnFolder(places, outside, madeNow, now, notes);
        if (stays is null)
        {
            return new StoreDataDecision(StoreDataMode.Shared, places.SharedDirectory, notes);
        }

        // Two histories stay apart until Windows changes (recorded, so bclip reads the same folder); a file in use is
        // tried again at the next start (not recorded).
        if (lasting)
        {
            return Fallback(places, windowsBuild, now, notes, stays);
        }

        notes.Add(stays);
        return new StoreDataDecision(StoreDataMode.Package, places.PackageDirectory, notes);
    }

    /// <summary>
    /// Whether a folder holds a history: anything in its <c>stores</c> folder, or a v0.1 plaintext database. A folder that
    /// cannot be read counts as holding one (never treated as empty, which could set it aside or overwrite it).
    /// </summary>
    /// <param name="dataDirectory">The data folder.</param>
    /// <returns>Whether a history is there.</returns>
    public static bool HasHistory(string dataDirectory)
    {
        try
        {
            string stores = Path.Combine(dataDirectory, EditionDataMigrator.StoresFolder);
            return File.Exists(Path.Combine(dataDirectory, "history.db"))
                || (Directory.Exists(stores) && Directory.EnumerateFileSystemEntries(stores).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Moves a private copy of the shared folder out of the way with the outside program: to the real path when the
    /// real folder does not exist (its files then become the shared folder's), else into the shared folder under a
    /// dated name, where nothing reads it and nothing deletes it.
    /// </summary>
    /// <param name="places">The package's places.</param>
    /// <param name="outside">The outside program.</param>
    /// <param name="now">For the dated name.</param>
    /// <param name="notes">Receives what was done.</param>
    /// <returns>Whether the private copy is gone.</returns>
    private static bool RepairPrivateCopy(StoreDataPlaces places, IOutsideFileSystem outside, DateTimeOffset now, List<string> notes)
    {
        if (!outside.Exists(places.SharedDirectory))
        {
            if (outside.Move(places.RedirectDirectory, places.SharedDirectory))
            {
                notes.Add("A private copy of the shared data folder became the real one (the real folder did not exist).");
            }
        }
        else
        {
            string aside = Path.Combine(places.SharedDirectory, RecoveredPrefix + Stamp(now));
            if (outside.Move(places.RedirectDirectory, aside))
            {
                notes.Add($"A private copy of the shared data folder was set aside inside it ({Path.GetFileName(aside)}); nothing in it was deleted.");
            }
        }

        return !Directory.Exists(places.RedirectDirectory);
    }

    /// <summary>
    /// Writes a probe file into the shared folder from this process and asks the outside program whether it exists: the
    /// one test that tells a real write from a redirected one on this PC. The probe is deleted again either way; a
    /// private copy that the probe itself made (nothing else is in it) is removed too.
    /// </summary>
    /// <param name="places">The package's places.</param>
    /// <param name="outside">The outside program.</param>
    /// <returns><see langword="null"/> when the write was real; otherwise why not, for the marker and the log.</returns>
    private static string? Prove(StoreDataPlaces places, IOutsideFileSystem outside)
    {
        string probe = Path.Combine(places.SharedDirectory, ProbePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(probe, "BetterClipboard checks that this folder is written for real. This file can be deleted.", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The shared data folder could not be written ({ex.GetType().Name}).";
        }

        bool real = outside.Exists(probe) && !Directory.Exists(places.RedirectDirectory);
        try
        {
            File.Delete(probe);
            if (!real && Directory.Exists(places.RedirectDirectory) && !Directory.EnumerateFileSystemEntries(places.RedirectDirectory).Any())
            {
                // The probe's own private copy (empty now): this process made it, and this process removes it.
                Directory.Delete(places.RedirectDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover probe is harmless; a leftover empty private copy is repaired at the next start.
        }

        return real ? null : "A file written into the shared data folder did not reach the real folder.";
    }

    /// <summary>
    /// Moves a history and settings this edition kept in its own folder before into the shared folder, when the shared
    /// folder has no history of its own. Unfinished moves of version 1.2.11 are settled first: undone when their original
    /// (the shared folder) still exists, kept when the shared folder was only made now.
    /// </summary>
    /// <param name="places">The package's places.</param>
    /// <param name="outside">The outside program (one rename per entry).</param>
    /// <param name="sharedMadeNow">Whether the shared folder did not exist before this start.</param>
    /// <param name="now">For the dated name of settings set aside.</param>
    /// <param name="notes">Receives what was done.</param>
    /// <returns>
    /// <c>Reason</c> <see langword="null"/> to use the shared folder; otherwise the log line for staying in this edition's own
    /// folder, and <c>Lasting</c>: whether that holds until something on the PC changes (two histories) rather than only
    /// for this start (a file in use).
    /// </returns>
    /// <exception cref="IOException">
    /// Settling an unfinished move or listing a folder failed: always before anything was moved, so the caller's fallback
    /// to this edition's own folder finds everything where it was.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">Settling or listing was refused; see above.</exception>
    private static (string? Reason, bool Lasting) SettleOwnFolder(StoreDataPlaces places, IOutsideFileSystem outside, bool sharedMadeNow, DateTimeOffset now, List<string> notes)
    {
        string own = places.PackageDirectory;
        if (!Directory.Exists(own))
        {
            return (null, false);
        }

        if (EditionDataMigrator.HasPendingMove(own))
        {
            if (sharedMadeNow)
            {
                // The move's original is gone (the shared folder did not exist): the copy is all there is, so it is kept.
                EditionDataMigrator.Commit(own);
                notes.Add("An unfinished move of an earlier version was kept: its original no longer exists.");
            }
            else
            {
                // Its original is the shared folder itself, still there: the half copy goes.
                EditionDataMigrator.Rollback(own);
                notes.Add("An unfinished move of an earlier version was undone; its original is the shared data folder.");
            }
        }

        string ownStores = Path.Combine(own, EditionDataMigrator.StoresFolder);
        string ownSettings = Path.Combine(own, EditionDataMigrator.SettingsFileName);
        bool ownHistory = HasHistory(own);
        if (!ownHistory && !File.Exists(ownSettings))
        {
            return (null, false);
        }

        if (ownHistory && HasHistory(places.SharedDirectory))
        {
            return ("This edition's own folder and the shared data folder each hold a history; two histories are never merged, so this edition keeps using its own.", true);
        }

        string sharedStores = Path.Combine(places.SharedDirectory, EditionDataMigrator.StoresFolder);
        string sharedSettings = Path.Combine(places.SharedDirectory, EditionDataMigrator.SettingsFileName);
        if (ownHistory)
        {
            // The history first, the settings after it: a history without its settings still opens (the defaults apply), and
            // a failure here leaves everything where it was.
            if (Directory.Exists(sharedStores) && !Directory.EnumerateFileSystemEntries(sharedStores).Any())
            {
                Directory.Delete(sharedStores);
            }

            if (!outside.Move(ownStores, sharedStores) || Directory.Exists(ownStores))
            {
                return ("This edition's history could not be moved into the shared data folder (a file may be in use); this edition uses its own folder for now.", false);
            }

            notes.Add("Moved this edition's history from its own folder into the shared data folder.");
        }

        if (!File.Exists(ownSettings))
        {
            return (null, false);
        }

        if (File.Exists(sharedSettings) && !ownHistory)
        {
            // Nothing else came from this edition's folder: the shared folder's settings stay, these stay where they are.
            return (null, false);
        }

        // From here on the history may already be in the shared folder, so nothing may turn this start back to this
        // edition's own folder: a failure only costs the settings (the defaults apply; the file stays where it is).
        try
        {
            if (File.Exists(sharedSettings))
            {
                // The settings belong to the history that just moved. The shared folder's own were written without a
                // history (an installer's options before a first start): kept under a dated name, never deleted. A rename
                // inside a real folder, which this process does for real (measured).
                string aside = Path.Combine(places.SharedDirectory, $"settings-set-aside-{Stamp(now)}.json");
                File.Move(sharedSettings, aside);
                notes.Add($"The shared data folder's settings, written without a history, were set aside ({Path.GetFileName(aside)}).");
            }

            if (outside.Move(ownSettings, sharedSettings) && !File.Exists(ownSettings))
            {
                notes.Add("Moved this edition's settings into the shared data folder.");
            }
            else
            {
                // Copying is the fallback, written by this process into a real folder (real, measured); the original goes
                // only once the copy is there.
                File.Copy(ownSettings, sharedSettings, overwrite: false);
                File.Delete(ownSettings);
                notes.Add("Copied this edition's settings into the shared data folder.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"This edition's settings could not be moved into the shared data folder ({ex.GetType().Name}); they stay in its own folder, and the defaults apply.");
        }

        return (null, false);
    }

    /// <summary>Records and returns the decision to use the package's own folder, with its reason.</summary>
    /// <param name="places">The package's places.</param>
    /// <param name="windowsBuild">The build the decision is for.</param>
    /// <param name="now">When.</param>
    /// <param name="notes">The notes so far.</param>
    /// <param name="reason">Why (English, content-free).</param>
    /// <returns>The decision.</returns>
    private static StoreDataDecision Fallback(StoreDataPlaces places, string windowsBuild, DateTimeOffset now, List<string> notes, string reason)
    {
        new StoreDataMarker(StoreDataMode.Package, windowsBuild, now, reason).TryWrite(places.MarkerPath);
        notes.Add($"{reason} This edition uses its own folder on this Windows build ({windowsBuild}).");
        return new StoreDataDecision(StoreDataMode.Package, places.PackageDirectory, notes);
    }

    /// <summary>A time stamp for a name: <c>yyyyMMdd-HHmmss</c> in UTC.</summary>
    /// <param name="now">The time.</param>
    /// <returns>The stamp.</returns>
    private static string Stamp(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
