using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static BetterClipboard.Windows.Interop.NativeMethods;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// Decides whether the process behind an "Everything" IPC window really is voidtools Everything before
/// BetterClipboard sends it anything or shows what it answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Any process, a low-integrity one included, can create a window with Everything's class name.
/// Such an impostor would receive the panel's search words and could answer with made-up files, which the tab
/// would offer to paste. So the window's owner must pass three checks:
/// <list type="number">
/// <item>its image is named <c>Everything*.exe</c>;</item>
/// <item>the file carries a valid Authenticode signature (<c>WinVerifyTrust</c>: intact and chained to a trusted
/// root), checked without UI and without network access (no revocation lookup; certificates from local caches
/// only), so it never stalls offline;</item>
/// <item>the signer's organization is voidtools: <c>voidtools</c> (2019–2024 builds) or <c>voidtools PTY LTD</c>
/// (since 2025) — verified on 1.4.1.935, 1005, 1026, 1032 and 1.5.0.1423b.</item>
/// </list>
/// </para>
/// <para>
/// <b>Cost.</b> Hashing a 2–5 MB executable takes tens of milliseconds, so verdicts are cached per file version
/// (path, size, last write time): an update of Everything is verified again, a restart of the same build is not.
/// Thread-safe.
/// </para>
/// </remarks>
public static class EverythingOwnerVerifier
{
    /// <summary>Signer organizations (subject <c>O=</c>) Everything has been signed with, compared case-insensitively.</summary>
    public static readonly IReadOnlySet<string> TrustedOrganizations =
        new HashSet<string>(["voidtools", "voidtools PTY LTD"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Verdicts by file version (path|size|last write ticks).</summary>
    private static readonly ConcurrentDictionary<string, EverythingTrust> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks the process behind a window handle's process id (see class remarks).
    /// </summary>
    /// <param name="processId">The owner of the IPC window.</param>
    /// <returns>The verdict with its reason; never throws.</returns>
    public static EverythingTrust VerifyProcess(uint processId)
    {
        var path = Clipboard.SourceAppResolver.TryGetImagePath(processId);
        return path is null
            ? new EverythingTrust(false, null, null, "its process could not be inspected (it may be elevated or gone)")
            : VerifyFile(path);
    }

    /// <summary>
    /// Checks an executable file: name, Authenticode signature and signer organization (see class remarks).
    /// </summary>
    /// <param name="path">Full path of the executable.</param>
    /// <returns>The verdict with its reason; never throws (an unreadable file is simply not trusted).</returns>
    public static EverythingTrust VerifyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = Path.GetFileName(path);
        if (!name.StartsWith("Everything", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new EverythingTrust(false, path, null, $"its process is {name}, not Everything.exe");
        }

        string key;
        try
        {
            var info = new FileInfo(path);
            key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new EverythingTrust(false, path, null, "its executable could not be read");
        }

        return Cache.GetOrAdd(key, _ => Check(path));
    }

    /// <summary>Runs the signature and signer checks (uncached).</summary>
    /// <param name="path">Executable path.</param>
    /// <returns>The verdict.</returns>
    private static EverythingTrust Check(string path)
    {
        int status = VerifySignature(path);
        if (status != 0)
        {
            return new EverythingTrust(false, path, null, $"its executable has no valid signature (0x{status:X8})");
        }

        string? organization;
        try
        {
            // The signature was verified above; this only reads who signed it.
#pragma warning disable SYSLIB0057 // CreateFromSignedFile reads the embedded signer; there is no loader replacement for it.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            organization = Organization(signer);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new EverythingTrust(false, path, null, "its signer could not be read");
        }

        return organization is not null && TrustedOrganizations.Contains(organization)
            ? new EverythingTrust(true, path, organization, "signed by " + organization)
            : new EverythingTrust(false, path, organization, $"it is signed by {organization ?? "an unknown organization"}, not voidtools");
    }

    /// <summary>
    /// <c>WinVerifyTrust</c> with the Authenticode policy: no UI, no revocation check, chain from local caches only.
    /// </summary>
    /// <param name="path">File to verify.</param>
    /// <returns>0 when valid and trusted; the error code otherwise.</returns>
    private static int VerifySignature(string path)
    {
        // The path must stay valid for the whole call: pinned unmanaged copy, freed in finally.
        nint pathPointer = Marshal.StringToHGlobalUni(path);
        try
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pathPointer,
            };
            unsafe
            {
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = (nint)(&file),
                    dwStateAction = WTD_STATEACTION_IGNORE,
                    dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
                };
                return WinVerifyTrust(0, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    /// <summary>The certificate subject's organization (<c>O=</c>, OID 2.5.4.10), or <see langword="null"/>.</summary>
    /// <param name="certificate">The signer certificate.</param>
    /// <returns>The organization.</returns>
    private static string? Organization(X509Certificate2 certificate)
    {
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.GetSingleElementType().Value == "2.5.4.10")
            {
                return rdn.GetSingleElementValue();
            }
        }

        return null;
    }
}

/// <summary>Whether an executable is trusted as voidtools Everything, and why (for logs and the Settings status).</summary>
/// <param name="IsTrusted">Name, signature and signer all passed.</param>
/// <param name="ExecutablePath">The checked executable, when known.</param>
/// <param name="Organization">The signer's organization, when it could be read.</param>
/// <param name="Reason">Short reason, e.g. "signed by voidtools PTY LTD" or "its process is x.exe, not Everything.exe".</param>
public sealed record EverythingTrust(bool IsTrusted, string? ExecutablePath, string? Organization, string Reason);
