#:property PublishAot=false
using System.Runtime.InteropServices;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: can the current user decrypt Windows' pinned clipboard blobs (DPAPI-NG, descriptor LOCAL=user)?
// Walks %LOCALAPPDATA%\Microsoft\Windows\Clipboard\Pinned, Base64-decodes each payload file name into
// its clipboard format name and calls NCryptUnprotectSecret on it.
//
// PRIVACY: prints only format names, plaintext lengths and the Locale LCID — never the pinned content.
// Run:     dotnet run tools/probes/probe_dpapi.cs
// Verified 2026-09-25 on Windows 11 25H2 (26200.8875): Locale → 4 bytes (0x0409), Text → UTF-16LE.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Clipboard\Pinned");
if (!Directory.Exists(root))
{
    Console.WriteLine($"No pinned store at {root} (clipboard history never used or nothing pinned).");
    return;
}

foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
{
    var name = Path.GetFileName(file);
    if (name == "metadata.json")
    {
        continue;
    }

    string format;
    try
    {
        format = Encoding.UTF8.GetString(Convert.FromBase64String(name));
    }
    catch (FormatException)
    {
        format = "?";
    }

    var blob = File.ReadAllBytes(file);
    int status = Native.NCryptUnprotectSecret(IntPtr.Zero, 0x40 /* NCRYPT_SILENT_FLAG */, blob, (uint)blob.Length, IntPtr.Zero, IntPtr.Zero, out var data, out var length);
    if (status != 0)
    {
        Console.WriteLine($"{format}: FAILED status=0x{status:X8}");
        continue;
    }

    var plain = new byte[length];
    Marshal.Copy(data, plain, 0, (int)length);
    Native.LocalFree(data);
    string extra = format == "Locale" && length >= 4 ? $" LCID=0x{BitConverter.ToUInt32(plain, 0):X4}" : "";
    Console.WriteLine($"{format}: OK plaintext={length} bytes, zeroBytes={plain.Count(b => b == 0)}{extra}");
}

/// <summary>ncrypt/kernel32 imports used by the probe.</summary>
static class Native
{
    /// <summary>Decrypts a DPAPI-NG blob (descriptor read from the blob).</summary>
    /// <param name="phDescriptor">Optional descriptor out-handle (pass zero).</param>
    /// <param name="dwFlags">Flags (NCRYPT_SILENT_FLAG).</param>
    /// <param name="pbProtectedBlob">Blob.</param>
    /// <param name="cbProtectedBlob">Blob length.</param>
    /// <param name="pMemPara">Allocator (zero = LocalAlloc).</param>
    /// <param name="hWnd">UI parent (zero).</param>
    /// <param name="ppbData">Plaintext (free with LocalFree).</param>
    /// <param name="pcbData">Plaintext length.</param>
    /// <returns>SECURITY_STATUS.</returns>
    [DllImport("ncrypt.dll")]
    public static extern int NCryptUnprotectSecret(IntPtr phDescriptor, uint dwFlags, byte[] pbProtectedBlob, uint cbProtectedBlob, IntPtr pMemPara, IntPtr hWnd, out IntPtr ppbData, out uint pcbData);

    /// <summary>Frees LocalAlloc memory.</summary>
    /// <param name="h">Handle.</param>
    /// <returns>Zero on success.</returns>
    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr h);
}
