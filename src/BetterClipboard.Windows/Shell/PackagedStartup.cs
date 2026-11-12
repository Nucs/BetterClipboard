using System.Runtime.InteropServices;
using BetterClipboard.Core.Diagnostics;

namespace BetterClipboard.Windows.Shell;

/// <summary>What Windows says about the Microsoft Store edition's "start with Windows" task.</summary>
public enum PackagedStartupState
{
    /// <summary>The process has no package, or the task could not be asked: nothing to show or change.</summary>
    Unavailable = 0,

    /// <summary>Off; the app may switch it on.</summary>
    Disabled = 1,

    /// <summary>
    /// Switched off by the user in Windows (Task Manager or Settings › Apps › Startup). The app cannot switch it on
    /// again: only the user can, there.
    /// </summary>
    DisabledByUser = 2,

    /// <summary>Switched off by an administrator's policy; neither the app nor the user can switch it on.</summary>
    DisabledByPolicy = 3,

    /// <summary>On: BetterClipboard starts when this user signs in.</summary>
    Enabled = 4,

    /// <summary>On by an administrator's policy; it cannot be switched off.</summary>
    EnabledByPolicy = 5,
}

/// <summary>
/// "Start with Windows" for the Microsoft Store edition: the startup task its package declares, read and switched
/// through <c>Windows.ApplicationModel.StartupTask</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the Run key</b> (<see cref="StartupRegistration"/>, what every other edition uses): inside a package, a
/// write to <c>HKEY_CURRENT_USER</c> lands in the package's private registry, which Windows' sign-in never reads
/// (measured, CLAUDE.md §2.47). The startup task is the supported way, and the user sees and controls it in Windows'
/// own Startup apps list like any other entry.
/// </para>
/// <para>
/// <b>Consent.</b> The task is declared off (<c>Enabled="false"</c> in the manifest) and switched on only by the user:
/// in the welcome window, or with the Settings switch. For a desktop package, <c>RequestEnableAsync</c> switches it on
/// without a prompt of Windows' own (measured), so that click is the consent.
/// </para>
/// <para>
/// <b>How the task starts the app:</b> Windows runs <c>BetterClipboard.exe</c> without arguments, and the app tells
/// from its activation that the task started it (<see cref="WasStartedByTask"/>) and stays in the tray.
/// </para>
/// </remarks>
public static class PackagedStartup
{
    /// <summary>The task's id; must equal <c>desktop:StartupTask/@TaskId</c> in <c>packaging/msix/Package.appxmanifest</c>.</summary>
    public const string TaskId = "BetterClipboardStartup";

    /// <summary>
    /// Reads the task's state.
    /// </summary>
    /// <returns>The state; <see cref="PackagedStartupState.Unavailable"/> without a package or when Windows refuses the question. Never throws.</returns>
    public static async Task<PackagedStartupState> GetStateAsync()
    {
        if (!Packaging.PackageIdentity.IsPackaged)
        {
            return PackagedStartupState.Unavailable;
        }

        try
        {
            var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
            return Map(task.State);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            // An id the manifest does not declare (a test package) answers with an ArgumentException.
            AppLog.Warn($"The startup task could not be read ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            return PackagedStartupState.Unavailable;
        }
    }

    /// <summary>
    /// Switches the task on or off.
    /// </summary>
    /// <param name="enabled">The wanted state.</param>
    /// <returns>
    /// The state afterwards, which is not the wanted one when the user or a policy decided otherwise in Windows
    /// (<see cref="PackagedStartupState.DisabledByUser"/>, the policy states). Never throws.
    /// </returns>
    public static async Task<PackagedStartupState> SetEnabledAsync(bool enabled)
    {
        if (!Packaging.PackageIdentity.IsPackaged)
        {
            return PackagedStartupState.Unavailable;
        }

        try
        {
            var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                // Answers the state it reached: Enabled, or the user's or a policy's "no", which only they can lift.
                return Map(await task.RequestEnableAsync());
            }

            task.Disable();
            return Map(task.State);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Warn($"The startup task could not be switched {(enabled ? "on" : "off")} ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            return PackagedStartupState.Unavailable;
        }
    }

    /// <summary>
    /// Whether this process was started by the startup task (at sign-in), as opposed to the Start menu, the alias or
    /// another program: such a start stays in the tray.
    /// </summary>
    /// <returns><see langword="true"/> for a start by the task; <see langword="false"/> otherwise and whenever Windows cannot say.</returns>
    public static bool WasStartedByTask()
    {
        if (!Packaging.PackageIdentity.IsPackaged)
        {
            return false;
        }

        try
        {
            // The classic activation query, which a packaged desktop process may ask once at start.
            var activation = global::Windows.ApplicationModel.AppInstance.GetActivatedEventArgs();
            return activation?.Kind == global::Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Maps Windows' state onto ours.</summary>
    /// <param name="state">Windows' state.</param>
    /// <returns>The same fact as a <see cref="PackagedStartupState"/>.</returns>
    private static PackagedStartupState Map(global::Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        global::Windows.ApplicationModel.StartupTaskState.Enabled => PackagedStartupState.Enabled,
        global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => PackagedStartupState.EnabledByPolicy,
        global::Windows.ApplicationModel.StartupTaskState.DisabledByUser => PackagedStartupState.DisabledByUser,
        global::Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => PackagedStartupState.DisabledByPolicy,
        _ => PackagedStartupState.Disabled,
    };
}
