namespace BetterClipboard.Core.Editions;

/// <summary>
/// How another copy of BetterClipboard, found by the Microsoft Store edition, got onto this PC — which decides what the
/// Store edition may do about it (<see cref="EditionSetupPlan"/>).
/// </summary>
public enum OtherEditionKind
{
    /// <summary>
    /// Installed for this user by <c>install.ps1</c> (the README's command): this user's Installed-apps entry names it.
    /// The Store edition uninstalls it itself, with the installer script it carries, after the history was moved.
    /// </summary>
    Installer = 0,

    /// <summary>
    /// Installed for every user of the PC (<c>install.ps1 -AllUsers</c>): the PC's Installed-apps entry names it. Removing
    /// it takes an administrator and takes it from every other user too, so the Store edition only says how.
    /// </summary>
    AllUsers = 1,

    /// <summary>
    /// The Chocolatey package (<c>…\lib\betterclipboard\tools\app</c>). Chocolatey must stay the one that changes its
    /// files, so the Store edition only names the command.
    /// </summary>
    Chocolatey = 2,

    /// <summary>
    /// A copy that only runs: extracted by hand or a build output, with no install record. There is nothing to
    /// uninstall; it is asked to close, since two copies cannot share the clipboard listener and Win+V.
    /// </summary>
    Unregistered = 3,
}

/// <summary>
/// Another copy of BetterClipboard on this PC, as the Microsoft Store edition found it
/// (<c>Windows.Store.OtherEditionLocator</c>).
/// </summary>
/// <param name="Kind">How it was installed.</param>
/// <param name="Directory">The folder its <c>BetterClipboard.exe</c> is in (the record's, which may no longer exist).</param>
/// <param name="Version">Its version as its install record or its exe states it, or <see langword="null"/> when unknown.</param>
/// <param name="IsRunning">Whether a process of it runs in this session right now.</param>
public sealed record OtherEdition(OtherEditionKind Kind, string Directory, string? Version, bool IsRunning);

/// <summary>What the Microsoft Store edition does next about other copies and their data, one step at a time.</summary>
public enum EditionSetupStep
{
    /// <summary>Nothing stands in the way: go on with the language picker and the welcome window.</summary>
    None = 0,

    /// <summary>
    /// A copy this user installed with the installer: after the checks and the user's go-ahead, close it, move its
    /// history, check the move, uninstall it and remove its old data.
    /// </summary>
    MoveFromInstaller = 1,

    /// <summary>
    /// A copy for every user, or a Chocolatey copy: the user (an administrator) removes it; the Store edition says how
    /// and waits.
    /// </summary>
    GuideUninstall = 2,

    /// <summary>A copy with no install record is running: ask it to close.</summary>
    CloseRunning = 3,

    /// <summary>
    /// No other copy is left, but one left its history behind and this edition has none yet: bring it over (copy,
    /// check, remove the old folder).
    /// </summary>
    ImportData = 4,
}

/// <summary>
/// Decides the Microsoft Store edition's next step about other copies of BetterClipboard on the PC. Pure; the caller
/// asks again after each step until the answer is <see cref="EditionSetupStep.None"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one edition at a time</b> (the owner's decision, 2026-11-12): two installed copies both start with Windows
/// and share one single-instance lock, so whichever starts first is the one that runs, each with its own history.
/// The Store edition therefore does not go on while another copy is installed.
/// </para>
/// <para>
/// <b>Order.</b> The copy the Store edition can remove itself comes first, then the copies an administrator must
/// remove, then a copy that merely runs, and the left-over data last: data is only brought over once nothing else
/// writes to it.
/// </para>
/// </remarks>
public static class EditionSetupPlan
{
    /// <summary>
    /// The next step.
    /// </summary>
    /// <param name="others">The other copies found (any order).</param>
    /// <param name="otherDataExists">
    /// Whether the folder the other editions keep their data in holds anything worth bringing over
    /// (<see cref="EditionDataMigrator.Inspect"/>: settings or a history).
    /// </param>
    /// <param name="ownHistoryExists">Whether this edition's own folder already holds a history (<see cref="AppPaths.HasHistory"/>).</param>
    /// <returns>The step, and the copy it is about (<see langword="null"/> for <see cref="EditionSetupStep.ImportData"/> and <see cref="EditionSetupStep.None"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="others"/> is <see langword="null"/>.</exception>
    public static (EditionSetupStep Step, OtherEdition? Edition) Next(IReadOnlyList<OtherEdition> others, bool otherDataExists, bool ownHistoryExists)
    {
        ArgumentNullException.ThrowIfNull(others);
        if (others.FirstOrDefault(e => e.Kind == OtherEditionKind.Installer) is { } installer)
        {
            return (EditionSetupStep.MoveFromInstaller, installer);
        }

        if (others.FirstOrDefault(e => e.Kind is OtherEditionKind.AllUsers or OtherEditionKind.Chocolatey) is { } guided)
        {
            return (EditionSetupStep.GuideUninstall, guided);
        }

        if (others.FirstOrDefault(e => e.Kind == OtherEditionKind.Unregistered && e.IsRunning) is { } running)
        {
            return (EditionSetupStep.CloseRunning, running);
        }

        return otherDataExists && !ownHistoryExists ? (EditionSetupStep.ImportData, null) : (EditionSetupStep.None, null);
    }

    /// <summary>
    /// Whether the history of a copy that is being removed is brought over: only into an edition that has none yet,
    /// because two histories cannot be merged into one store.
    /// </summary>
    /// <param name="otherDataExists">Whether the other editions' folder holds settings or a history.</param>
    /// <param name="ownHistoryExists">Whether this edition already has a history.</param>
    /// <returns><see langword="true"/> to bring it over.</returns>
    public static bool BringsDataOver(bool otherDataExists, bool ownHistoryExists) => otherDataExists && !ownHistoryExists;
}
