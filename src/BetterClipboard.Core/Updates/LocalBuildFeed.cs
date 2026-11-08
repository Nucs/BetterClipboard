using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// The dev-builds folder: development builds published on this PC (<c>tools/release/publish-dev.ps1</c>), read as releases
/// the update button can offer, and the copy that takes one of them into the updater's working folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout</b> (one folder per build, named exactly like its version):
/// </para>
/// <code>
/// &lt;data directory&gt;\dev-builds\
///   0.2.10-dev.12.gabc1234\
///     BetterClipboard-0.2.10-dev.12.gabc1234-win-x64.zip   the package, as tools/release/package.ps1 writes it
///     SHA256SUMS.txt                                        sha256sum format; must list the zip
///     notes.md                                              optional: what the build adds (Markdown)
///     build.json                                            {"version", "publishedUtc", "commit", "title"}; written last
/// </code>
/// <para>
/// <b>Complete builds only.</b> A folder counts when its name equals the version in its <c>build.json</c>, and its zip for
/// this PC's architecture and a <c>SHA256SUMS.txt</c> that lists that zip are both there. The publish script prepares a
/// build in a folder whose name starts with a dot and renames it into place when everything is written, so a build that
/// is still being copied is never offered; a folder of any other shape is ignored, never an error.
/// </para>
/// <para>
/// <b>Trust.</b> The folder belongs to the user who runs the app, like the app's own install folder, which the same user
/// can replace at will: a build from it gives nobody anything they did not have. What is checked is integrity — the
/// copy's SHA-256 must be the one the build's own <c>SHA256SUMS.txt</c> states, so a zip that was cut short or replaced
/// while it was copied is refused — and that a file to be copied really lies inside the folder.
/// </para>
/// <para>
/// <b>Bounds.</b> Every file read is size-capped and every failure is swallowed per build (<see cref="Read"/> never
/// throws), because the folder is written by a script while the app watches it, and a half-written file must cost at most
/// that one build until the next look.
/// </para>
/// </remarks>
public static class LocalBuildFeed
{
    /// <summary>The folder's name in the data directory (<see cref="AppPaths.DevBuildsDirectory"/>).</summary>
    public const string DirectoryName = "dev-builds";

    /// <summary>The file that makes a folder a build; the publish script writes it last.</summary>
    public const string BuildFileName = "build.json";

    /// <summary>The optional release notes of a build (Markdown): the commits it adds.</summary>
    public const string NotesFileName = "notes.md";

    /// <summary>
    /// The most build folders looked at in one read. The publish script keeps five; the cap only bounds a folder that
    /// someone filled by hand.
    /// </summary>
    public const int MaxBuilds = 64;

    /// <summary>The largest <see cref="BuildFileName"/> read, in bytes (the real one is under 1 KB).</summary>
    private const int MaxBuildFileBytes = 64 * 1024;

    /// <summary>The largest <see cref="NotesFileName"/> read, in bytes; longer notes are cut (the dialog shows the start).</summary>
    private const int MaxNotesBytes = 256 * 1024;

    /// <summary>The largest <c>SHA256SUMS.txt</c> read, in bytes (the real one is one line).</summary>
    private const int MaxChecksumListBytes = 64 * 1024;

    /// <summary>The longest title kept from <see cref="BuildFileName"/>.</summary>
    private const int MaxTitleLength = 256;

    /// <summary>
    /// Reads the complete builds in a dev-builds folder that have a package for <paramref name="architecture"/>.
    /// </summary>
    /// <param name="directory">The dev-builds folder; <see langword="null"/>, blank or missing reads as empty.</param>
    /// <param name="architecture">This PC's architecture: <c>x64</c> or <c>arm64</c>.</param>
    /// <returns>
    /// The builds, newest version first, each marked <see cref="ReleaseInfo.IsLocalBuild"/>, with two assets: its package
    /// and its checksum list (<c>file:</c> addresses). Empty when there are none. Never throws for anything the folder
    /// holds or fails to hold.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="architecture"/> is neither <c>x64</c> nor <c>arm64</c>.</exception>
    public static IReadOnlyList<ReleaseInfo> Read(string? directory, string architecture)
    {
        // Checked before the folder: a wrong architecture is the caller's bug, not something the folder holds.
        var arch = UpdateCatalog.NormalizeArchitecture(architecture);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [];
        }

        var builds = new List<ReleaseInfo>();
        try
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            int looked = 0;
            foreach (var folder in Directory.EnumerateDirectories(directory))
            {
                if (looked++ >= MaxBuilds)
                {
                    break;
                }

                if (TryReadBuild(folder, arch) is { } build)
                {
                    builds.Add(build);
                }
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // The folder went away or cannot be listed right now: what was read so far stands, the next look reads again.
        }

        builds.Sort((a, b) => b.Version.CompareTo(a.Version));
        return builds;
    }

    /// <summary>
    /// Reads one build folder.
    /// </summary>
    /// <param name="folder">The build's folder.</param>
    /// <param name="architecture">The normalized architecture (<c>x64</c> or <c>arm64</c>).</param>
    /// <returns>The build, or <see langword="null"/> when the folder is not a complete build for this architecture.</returns>
    private static ReleaseInfo? TryReadBuild(string folder, string architecture)
    {
        try
        {
            var name = Path.GetFileName(folder);

            // The publish script's staging folders start with a dot: a build in the making is never offered.
            if (name.Length == 0 || name[0] == '.')
            {
                return null;
            }

            var buildFile = new FileInfo(Path.Combine(folder, BuildFileName));
            if (!buildFile.Exists || buildFile.Length is 0 or > MaxBuildFileBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(buildFile.FullName), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !SemanticVersion.TryParse(ReadString(root, "version"), out var version))
            {
                return null;
            }

            // The folder's name is the version: a copied or renamed folder must not pass for another build, and the
            // package's name is derived from the same text.
            if (!string.Equals(name, version.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var package = new FileInfo(Path.Combine(folder, UpdateCatalog.PackageFileName(version, architecture)));
            var checksums = new FileInfo(Path.Combine(folder, UpdateCatalog.ChecksumsFileName));
            if (!package.Exists || package.Length == 0 || package.Length > UpdateClient.MaxPackageBytes ||
                !checksums.Exists || checksums.Length is 0 or > MaxChecksumListBytes)
            {
                return null;
            }

            // A list that does not name the package vouches for nothing, so the build would fail at install time: skip it
            // now rather than offer what cannot be verified.
            if (!Sha256Sums.TryFind(File.ReadAllText(checksums.FullName), package.Name, out _))
            {
                return null;
            }

            var title = ReadString(root, "title");
            title = string.IsNullOrWhiteSpace(title) ? $"BetterClipboard {version}" : Truncate(title.Trim(), MaxTitleLength);
            DateTimeOffset? published = DateTimeOffset.TryParse(ReadString(root, "publishedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : null;

            return new ReleaseInfo(
                version,
                "v" + version,
                title,
                ReadNotes(Path.Combine(folder, NotesFileName)),
                PageUrl: null,
                published,
                version.IsPrerelease,
                [
                    new ReleaseAsset(package.Name, package.Length, new Uri(package.FullName), Sha256: null),
                    new ReleaseAsset(checksums.Name, checksums.Length, new Uri(checksums.FullName), Sha256: null),
                ])
            {
                IsLocalBuild = true,
            };
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is JsonException or UriFormatException)
        {
            // Half-written (the publish script is still at work) or broken by hand: not a build, this time.
            return null;
        }
    }

    /// <summary>
    /// Reads a build's checksum list for verifying its package.
    /// </summary>
    /// <param name="checksums">The build's <c>SHA256SUMS.txt</c> asset (from <see cref="Read"/>).</param>
    /// <param name="directory">The dev-builds folder the asset must lie in.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The list's text (ASCII in practice; read as UTF-8).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="checksums"/> is <see langword="null"/>.</exception>
    /// <exception cref="UpdateException">
    /// The file is not inside <paramref name="directory"/> or is larger than a checksum list can be
    /// (<see cref="UpdateFailure.BadResponse"/>); it is gone or cannot be read (<see cref="UpdateFailure.Storage"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<string> ReadChecksumsAsync(ReleaseAsset checksums, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        var path = ResolveInside(checksums, directory);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new UpdateException(UpdateFailure.Storage, $"The build's {checksums.Name} is gone from the dev-builds folder, so the build cannot be verified. Nothing was installed.");
            }

            if (info.Length > MaxChecksumListBytes)
            {
                throw new UpdateException(UpdateFailure.BadResponse, $"The build's {checksums.Name} is larger than a checksum list can be and was not read. Nothing was installed.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            throw new UpdateException(UpdateFailure.Storage, $"The build's {checksums.Name} could not be read ({ex.Message}). Nothing was installed.", ex);
        }
    }

    /// <summary>
    /// Copies a build's package into the updater's working folder and returns the SHA-256 of what was written.
    /// </summary>
    /// <remarks>
    /// The same contract as <see cref="UpdateClient.DownloadAsync"/>, so the update flow treats both alike: the bytes go to
    /// <c>&lt;targetPath&gt;.part</c> first and are moved into place only when the whole file was copied, so
    /// <paramref name="targetPath"/> never holds half a package; the hash is computed while copying, and the caller
    /// compares it with the build's checksum before using the file. A source whose size is no longer the one
    /// <see cref="Read"/> saw was replaced in between (a build published again under the same version) and is refused.
    /// </remarks>
    /// <param name="package">The build's package asset (from <see cref="Read"/>).</param>
    /// <param name="directory">The dev-builds folder the package must lie in.</param>
    /// <param name="targetPath">Where the finished copy goes (its folder is created; an existing file is replaced).</param>
    /// <param name="progress">Told the bytes copied so far, about ten times a second and once at the end; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the copy; the partial file is deleted.</param>
    /// <returns>The SHA-256 of the copy as 64 lower-case hex digits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetPath"/> is null or blank.</exception>
    /// <exception cref="UpdateException">
    /// The package is not inside <paramref name="directory"/>, or its size changed since it was offered
    /// (<see cref="UpdateFailure.BadResponse"/>); it is gone, or it could not be read or written
    /// (<see cref="UpdateFailure.Storage"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<string> CopyPackageAsync(ReleaseAsset package, string directory, string targetPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var source = ResolveInside(package, directory);
        var partial = targetPath + ".part";
        try
        {
            FileStream input;
            try
            {
                // Read sharing only: a publish script that is rewriting this very file holds it for writing, and the open
                // then fails instead of copying half of it.
                input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException ex)
            {
                throw new UpdateException(UpdateFailure.Storage, $"{package.Name} is gone from the dev-builds folder. Publish the build again. Nothing was installed.", ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                throw new UpdateException(UpdateFailure.Storage, $"{package.Name} is gone from the dev-builds folder. Publish the build again. Nothing was installed.", ex);
            }

            await using (input)
            {
                if (package.Size > 0 && input.Length != package.Size)
                {
                    throw new UpdateException(UpdateFailure.BadResponse, $"{package.Name} changed in the dev-builds folder after it was offered. Open the update dialog again. Nothing was installed.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long copied = 0;
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1 << 16];
                    long lastReport = Environment.TickCount64;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        copied += read;

                        // The stated size is the most a copy may hold: a file that grows while it is read is being rewritten.
                        if (package.Size > 0 && copied > package.Size)
                        {
                            throw new UpdateException(UpdateFailure.BadResponse, $"{package.Name} changed in the dev-builds folder while it was copied. Nothing was installed.");
                        }

                        hash.AppendData(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        if (progress is not null && Environment.TickCount64 - lastReport >= 100)
                        {
                            lastReport = Environment.TickCount64;
                            progress.Report(copied);
                        }
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (package.Size > 0 && copied != package.Size)
                {
                    throw new UpdateException(UpdateFailure.BadResponse, $"{package.Name} changed in the dev-builds folder while it was copied. Nothing was installed.");
                }

                var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                File.Move(partial, targetPath, overwrite: true);
                progress?.Report(copied);
                return sha256;
            }
        }
        catch (Exception ex) when (ex is not UpdateException and not OperationCanceledException && IsFileFailure(ex))
        {
            TryDelete(partial);
            throw new UpdateException(UpdateFailure.Storage, $"The build could not be copied from the dev-builds folder: {ex.Message}", ex);
        }
        catch
        {
            // Cancelled, refused or failed: a partial copy is of no use to anyone.
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="file"/> is a <c>file:</c> address of a file inside <paramref name="directory"/> (any depth).
    /// </summary>
    /// <param name="directory">The dev-builds folder.</param>
    /// <param name="file">An asset's address.</param>
    /// <returns><see langword="true"/> for a file inside the folder; <see langword="false"/> for anything else, also for garbage.</returns>
    public static bool IsInside(string? directory, Uri? file)
    {
        if (string.IsNullOrWhiteSpace(directory) || file is not { IsAbsoluteUri: true, IsFile: true })
        {
            return false;
        }

        try
        {
            // Full paths on both sides: "..\" segments are resolved before the comparison, so they cannot step outside.
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(file.LocalPath);
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.Length > root.Length;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The local path of an asset that must lie inside the dev-builds folder.</summary>
    /// <param name="asset">The asset.</param>
    /// <param name="directory">The dev-builds folder.</param>
    /// <returns>The asset's full path.</returns>
    /// <exception cref="UpdateException">The asset is not a file inside <paramref name="directory"/> (<see cref="UpdateFailure.BadResponse"/>).</exception>
    private static string ResolveInside(ReleaseAsset asset, string directory)
    {
        // Assets come from Read, so this only fails on a bug or a list that was tampered with in memory: never follow it.
        if (!IsInside(directory, asset.DownloadUrl))
        {
            throw new UpdateException(UpdateFailure.BadResponse, $"{asset.Name} is not a file in the dev-builds folder, so it was not used. Nothing was installed.");
        }

        return Path.GetFullPath(asset.DownloadUrl.LocalPath);
    }

    /// <summary>Reads a build's notes, cut to what the dialog shows; a missing or unreadable file is no notes.</summary>
    /// <param name="path">The notes file.</param>
    /// <returns>The Markdown, or empty.</returns>
    private static string ReadNotes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(info.Length, MaxNotesBytes)];
            int total = 0, read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            // A cut in the middle of a UTF-8 sequence decodes to one replacement character at the end: harmless in notes.
            var text = Encoding.UTF8.GetString(buffer, 0, total).TrimStart('\uFEFF');
            return Truncate(text, GitHubReleases.MaxNotesLength);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return string.Empty;
        }
    }

    /// <summary>Reads a string member of the build file; anything else reads as <see langword="null"/>.</summary>
    /// <param name="root">The build file's object.</param>
    /// <param name="name">The member's name.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Cuts a text to a length.</summary>
    /// <param name="text">The text.</param>
    /// <param name="length">The most characters kept.</param>
    /// <returns>The text, or its start.</returns>
    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>Whether an exception from a file operation is a problem with the file or folder (not a bug).</summary>
    /// <param name="exception">The exception.</param>
    /// <returns><see langword="true"/> for disk, path and permission failures.</returns>
    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException;

    /// <summary>Deletes a partial copy, ignoring a failure (the next copy replaces it, and the app's start cleans the folder).</summary>
    /// <param name="path">The file.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Left behind; UpdateService cleans its working folder at the next start.
        }
    }
}
