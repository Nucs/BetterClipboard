using System.Globalization;
using System.Text.Json;

namespace BetterClipboard.Core.Updates;

/// <summary>
/// Everything the installer needs to put a downloaded, verified package in place of the running copy.
/// </summary>
/// <param name="PackagePath">The release zip on disk. Its SHA-256 was checked against the release's checksum list.</param>
/// <param name="PackageSha256">That SHA-256 (64 hex digits), handed on so the installer checks the file once more right before it uses it.</param>
/// <param name="Version">The version inside the package.</param>
/// <param name="InstallDirectory">The folder of the running copy, which the installer replaces as a whole.</param>
/// <param name="WorkDirectory">A folder of the app's own for the installer's script (the updates folder in the data directory).</param>
/// <param name="ResultPath">Where the installer writes its outcome (<see cref="UpdateResult"/>), read by the app at its next start.</param>
/// <param name="LogPath">Where the installer writes what it did, for troubleshooting.</param>
public sealed record UpdateInstallRequest(
    string PackagePath,
    string PackageSha256,
    SemanticVersion Version,
    string InstallDirectory,
    string WorkDirectory,
    string ResultPath,
    string LogPath);

/// <summary>
/// An installer that was started.
/// </summary>
/// <param name="ExitCode">
/// Completes with the installer process's exit code when it ends. On a successful update it never completes inside this
/// process: the installer closes the app first. A completion while the app still runs therefore means the installer gave
/// up before replacing anything.
/// </param>
public sealed record UpdateInstallerRun(Task<int> ExitCode);

/// <summary>
/// Starts the program that replaces the running copy with a new version. Implemented in BetterClipboard.Windows (it
/// runs the new release's own <c>install.ps1</c>); an interface so the update flow is testable without replacing anything.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>
    /// Starts the installer for <paramref name="request"/> and returns once its process runs.
    /// </summary>
    /// <param name="request">What to install, and where.</param>
    /// <param name="cancellationToken">Cancels the preparation; a started installer is not stopped.</param>
    /// <returns>The started installer.</returns>
    /// <exception cref="UpdateException">
    /// The package holds no installer script, or the installer could not be started (<see cref="UpdateFailure.Installer"/>).
    /// Nothing was changed.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the installer started.</exception>
    Task<UpdateInstallerRun> StartAsync(UpdateInstallRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome <c>install.ps1 -Update</c> leaves behind for the app (its <c>-ResultPath</c>): whether the new version is
/// in place, and why not when it is not.
/// </summary>
/// <remarks>
/// The installer runs after the app has closed, so this file is the only way its outcome reaches the user: the next
/// start reads it once and tells them "updated" or "the update failed".
/// </remarks>
/// <param name="Version">The version the installer was asked to install; empty when the file does not say.</param>
/// <param name="Succeeded">Whether the new version is in place.</param>
/// <param name="Error">The installer's error message when it failed; empty otherwise. Shown to the user as is.</param>
/// <param name="FinishedAt">When the installer finished, or <see langword="null"/> when the file does not say.</param>
public sealed record UpdateResult(string Version, bool Succeeded, string Error, DateTimeOffset? FinishedAt)
{
    /// <summary>The longest error text kept from the file (an installer message is a sentence or two).</summary>
    private const int MaxErrorLength = 2000;

    /// <summary>
    /// Reads a result file.
    /// </summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>
    /// The outcome, or <see langword="null"/> when there is no file or it is not a result (unreadable, not JSON, no
    /// <c>ok</c> member). Never throws.
    /// </returns>
    public static UpdateResult? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // ReadAllText drops the byte order mark Windows PowerShell 5.1 writes with -Encoding UTF8.
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var error = ReadString(root, "error");
            return new UpdateResult(
                ReadString(root, "version"),
                ok.ValueKind == JsonValueKind.True,
                error.Length <= MaxErrorLength ? error : error[..MaxErrorLength],
                DateTimeOffset.TryParse(ReadString(root, "finishedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var finished) ? finished : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Reads a string member; anything else is empty.</summary>
    /// <param name="root">The result object.</param>
    /// <param name="name">The member's name.</param>
    /// <returns>The trimmed text, or empty.</returns>
    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;
}
