using System.Security.Principal;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Security;

/// <summary>
/// The two OS identifiers the history is bound to: Windows' per-installation <c>MachineGuid</c> and the
/// current user's SID. Feed them to <see cref="Core.Security.MachineBinding.Derive"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>MachineGuid</c> (<c>HKLM\SOFTWARE\Microsoft\Cryptography</c>) is generated at Windows setup and
/// survives updates and feature upgrades, but <b>not</b> a clean reinstall or an unprepared disk clone — both
/// change the binding and therefore start a new store (see <see cref="Core.Storage.MachineBoundHistory"/>).
/// </para>
/// <para>
/// Neither value is secret, but together they identify the device and user, so <see cref="ToString"/> is
/// redacted to keep them out of logs.
/// </para>
/// </remarks>
/// <param name="MachineGuid">Windows installation id.</param>
/// <param name="UserSid">Current user's SID.</param>
public sealed record MachineIdentity(string MachineGuid, string UserSid)
{
    /// <summary>
    /// Reads the identity of the running process: the 64-bit registry view's MachineGuid and the process
    /// token's user SID.
    /// </summary>
    /// <returns>The identity.</returns>
    /// <exception cref="InvalidOperationException">MachineGuid is missing (damaged or unusual Windows image) or the token has no user SID.</exception>
    /// <exception cref="System.Security.SecurityException">The registry key cannot be read.</exception>
    public static MachineIdentity Current()
    {
        // Explicit 64-bit view: a 32-bit process would otherwise be redirected to Wow6432Node, which has no
        // MachineGuid — the x86 and x64 builds must bind to the same value.
        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cryptography = localMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var machineGuid = cryptography?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(machineGuid))
        {
            throw new InvalidOperationException(@"HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid is missing; cannot bind the history to this machine.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The process token has no user SID.");
        return new MachineIdentity(machineGuid, sid);
    }

    /// <summary>Redacted text so the identifiers never end up in a log by accident.</summary>
    /// <returns>A fixed placeholder.</returns>
    public override string ToString() => "MachineIdentity { redacted }";
}
