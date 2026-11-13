using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Startup;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.App;

/// <summary>"Start with Windows" follows the copy that runs (CLAUDE.md §2.29).</summary>
public sealed partial class AppController
{
    /// <summary>
    /// Points the user's own "Start with Windows" entry at this copy when it starts a BetterClipboard that is no longer
    /// there or is older than this one (<see cref="SignInStartup.FollowThisCopy"/>): a release extracted into a new folder,
    /// or a portable folder that was moved, would otherwise start the old version (or nothing) at the next sign-in, and this
    /// copy's switch would read "off".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs once at each start, on the UI thread before any window: two registry reads and the version resource of one
    /// program, so Settings, when it opens, already shows the entry as it now is. Not for the Microsoft Store edition (its
    /// startup task is its own) and not for an isolated instance (a test copy must never take over the user's entry).
    /// </para>
    /// <para>
    /// The entry is rewritten in place; Windows' "off" mark for it stays (an entry the user turned off in Windows' startup
    /// apps stays off), and the entry for every user is never touched. Logged with the reason; a registry that refuses is
    /// logged too, and the entry stays as it was.
    /// </para>
    /// </remarks>
    private void FollowSignInEntry()
    {
        if (IsStoreEdition || paths.InstanceScope is not null || !SemanticVersion.TryParse(Version, out var own))
        {
            return;
        }

        try
        {
            var entries = StartupRegistration.ReadEntries();
            var named = ProgramVersion.Read(SignInStartup.TargetOf(entries.UserCommand));
            if (SignInStartup.FollowThisCopy(entries, ExecutablePath, own, named, Settings.Current.SkipAllUsersStartup) is not { } change)
            {
                return;
            }

            StartupRegistration.Apply(change, ExecutablePath);
            AppLog.Info(entries.UserTargetExists
                ? $"Start with Windows now starts this copy ({own}): its entry started an older BetterClipboard ({named?.ToString() ?? "unknown version"}) from another folder."
                : $"Start with Windows now starts this copy ({own}): its entry started a BetterClipboard that is no longer there.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppLog.Info($"Start with Windows could not be pointed at this copy ({ex.GetType().Name}); its entry stays as it was.");
        }
    }
}
