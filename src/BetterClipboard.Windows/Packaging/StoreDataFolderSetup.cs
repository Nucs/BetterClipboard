using System.ComponentModel;
using BetterClipboard.Core;
using BetterClipboard.Core.Editions;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Packaging;

/// <summary>
/// The Microsoft Store edition's data folder for this process, decided and made ready once (<see cref="StoreDataFolder"/>):
/// <c>%LOCALAPPDATA%\BetterClipboard</c>, shared with every other kind of install, when that is proven safe on this PC;
/// the package's own folder otherwise. <see langword="null"/> for every process that is not the edition's regular instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>When it runs:</b> on first use, which is <c>Program.ResolvePaths</c> at the very start of the app, before the
/// single-instance lock, the log and the store: it may move this edition's earlier history into the shared folder, which
/// must happen before anything opens it. Every later call in the process returns the same answer.
/// </para>
/// <para>
/// <b>One at a time:</b> two processes of the package can start at once (the startup task and a start by hand, or bclip
/// starting the app), so the work runs under a named mutex of its own; the second process then finds it done.
/// </para>
/// <para>
/// <b>Never in the way:</b> any failure leaves this edition in its own folder for this start (the log says why), the
/// behavior of version 1.2.11.
/// </para>
/// </remarks>
public static class StoreDataFolderSetup
{
    /// <summary>The mutex that keeps two processes of the package from setting the folder up at once.</summary>
    private const string MutexName = @"Local\BetterClipboard.StoreDataFolder";

    /// <summary>How long a second process waits for the first one's setup (which moves at most two entries).</summary>
    private static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(30);

    /// <summary>The decision, made on first use.</summary>
    private static readonly Lazy<StoreDataDecision?> Decision = new(Decide, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// This process's decision: the folder and the log lines of its setup; <see langword="null"/> for a process without
    /// package identity, and for an isolated instance (<see cref="AppPaths.DataDirectoryVariable"/> set), which never
    /// touches the shared folder.
    /// </summary>
    public static StoreDataDecision? Current => Decision.Value;

    /// <summary>
    /// This PC's Windows build with its update revision (<c>10.0.26200.8875</c>): the proof runs again whenever it changes,
    /// since an update could change how Windows redirects a package's writes.
    /// </summary>
    /// <returns>The build text; the revision is 0 when the registry cannot say.</returns>
    public static string WindowsBuild()
    {
        var version = Environment.OSVersion.Version;
        int revision = 0;
        try
        {
            // HKLM is read through the real registry also from inside a package (measured).
            revision = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", 0) as int? ?? 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // The build alone still tells feature updates apart.
        }

        return $"{version.Major}.{version.Minor}.{version.Build}.{revision}";
    }

    /// <summary>Decides once for the process (see the class remarks).</summary>
    /// <returns>The decision, or <see langword="null"/> when this process is not the edition's regular instance.</returns>
    private static StoreDataDecision? Decide()
    {
        if (PackageIdentity.FamilyName is not { } family || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable)))
        {
            return null;
        }

        var places = StoreDataPlaces.For(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), family);
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(MutexWait);
            }
            catch (AbandonedMutexException)
            {
                // A process that set up the folder ended in the middle: the work is idempotent, so it simply runs again.
                owned = true;
            }

            if (!owned)
            {
                return new StoreDataDecision(StoreDataMode.Package, places.PackageDirectory, ["Another process kept setting up the data folder for more than 30 s; this start uses this edition's own folder."]);
            }

            return StoreDataFolder.Establish(places, new CmdOutsideFileSystem(), WindowsBuild(), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            // Establish fails only before it moved anything, so the package's own folder still holds whatever it held.
            return new StoreDataDecision(StoreDataMode.Package, places.PackageDirectory, [$"Setting up the shared data folder failed ({ex.GetType().Name}); this start uses this edition's own folder."]);
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
