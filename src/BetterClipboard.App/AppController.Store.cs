using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using BetterClipboard.App.ViewModels;
using BetterClipboard.App.Views;
using BetterClipboard.Core;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Editions;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Security;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using BetterClipboard.Core.Updates;
using BetterClipboard.Windows.Packaging;
using BetterClipboard.Windows.Security;
using BetterClipboard.Windows.Shell;
using BetterClipboard.Windows.Store;

namespace BetterClipboard.App;

/// <summary>
/// The Microsoft Store edition (CLAUDE.md §2.47): what a copy that runs from the Store package does differently, and its
/// setup before anything starts — replacing another copy of BetterClipboard on the PC, and the welcome whose
/// <em>Start</em> is the user's yes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a setup phase.</b> The Store's policies ask for consent before an app changes the Windows experience (taking
/// Win+V, policy 10.2.8) and before it stores sensitive data (10.5.5), and a Store package has no installer whose run
/// could count as that consent. So this edition starts nothing — no store, no clipboard listener, no shortcut, no tray
/// icon — until the welcome's <em>Start</em>. The owner's decisions (2026-11-12): the welcome comes after the language
/// picker; only one edition may be installed; the Store edition moves the user over by itself, with checks before it
/// starts, a checked copy of the history, the other copy's uninstall, and the old folder's removal at the end.
/// </para>
/// <para>
/// <b>Order of a first start:</b> other copies and their data (<see cref="EditionSetupPlan"/>, one step at a time until
/// nothing is left), then the language picker when no language was chosen, then the welcome, then the normal start.
/// A later start runs the same plan (cheap: a few registry reads and a process list), so another copy that is
/// installed afterwards is noticed.
/// </para>
/// <para>
/// <b>A move is all or nothing.</b> The copied history is marked as unfinished until the old folder is about to be
/// deleted (<see cref="EditionDataMigrator"/>). Whenever the move stops before that — the user quits, the uninstall
/// fails, the PC loses power — the copy is removed again and the next attempt starts from the original, which the other
/// copy may have been used on in the meantime.
/// </para>
/// <para>
/// <b>Never in the background, never for an isolated instance.</b> A silent restart shows nothing and skips the setup
/// (CLAUDE.md §2.46). A start by Windows (sign-in, after a Store update) ends at once while the welcome was not
/// accepted or another copy of BetterClipboard is installed: before it takes the instance lock
/// (<c>Program.ContinueStoreEditionInBackground</c>), so the other copy's own sign-in start is never turned away, and
/// that copy is the one that runs until this edition is opened by hand. An isolated instance (its own data folder) has
/// no setup at all: a test copy must never find, close or uninstall the user's installed app.
/// </para>
/// </remarks>
public sealed partial class AppController
{
    /// <summary>The page of the README that is the privacy policy (also the Store listing's privacy policy URL).</summary>
    public static readonly Uri PrivacyPage = new("https://github.com/Nucs/BetterClipboard#privacy");

    /// <summary>How long a copy that was asked to exit gets before the move gives up on it.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long the copy step waits for the closed copy's files to be free (a database closes a moment after its process).</summary>
    private static readonly TimeSpan FilesFreeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How often the setup looks whether a copy an administrator has to remove is gone.</summary>
    private static readonly TimeSpan RemovalPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The setup window while the setup runs; <see langword="null"/> before and after. UI thread only.</summary>
    private StoreSetupWindow? setupWindow;

    /// <summary>
    /// The theme of the setup's windows until the settings are loaded (<see cref="ApplySetupLook"/>); afterwards the
    /// settings decide (<see cref="ApplyTheme"/>).
    /// </summary>
    private AppTheme setupTheme = AppTheme.System;

    /// <summary>
    /// Whether a move is under way: an exit request then comes from the other copy's uninstaller (which asks every
    /// BetterClipboard of the session to exit) and is not for this process. UI thread only.
    /// </summary>
    private bool setupBusy;

    /// <summary>Whether this setup brought another edition's data over (the language picker's log line, the welcome's note).</summary>
    private bool dataBroughtOver;

    /// <summary>The user chose to go on without the old history after a failed import: it is not offered again in this run.</summary>
    private bool skipImport;

    /// <summary>The store the move's check opened; the normal start goes on with it instead of opening it again.</summary>
    private MachineBoundHistoryResult? preopenedStore;

    /// <summary>The welcome's line of good news after a move; empty for none.</summary>
    private string movedNote = string.Empty;

    /// <summary>The program of a copy this setup closed, to start it again when the user quits without moving.</summary>
    private string? closedCopyProgram;

    /// <summary>
    /// Raised once this process took the single-instance lock over from another edition: the app starts listening for
    /// what later launches ask (<c>App.Listen</c>). At most once.
    /// </summary>
    public event EventHandler? BecamePrimary;

    /// <summary>
    /// Whether this copy runs from BetterClipboard's Microsoft Store package: Windows then starts and updates it, its own
    /// registry writes stay inside the package, and Settings shows the cards that fit (<c>SettingsViewModel.Store.cs</c>).
    /// </summary>
    public static bool IsStoreEdition => PackageIdentity.IsPackaged;

    /// <summary>The folder this copy's program is in.</summary>
    private static string OwnDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>The setup's working folder: the uninstaller script and its result. Removed when the setup is done.</summary>
    private string SetupWorkDirectory => Path.Combine(paths.DataDirectory, "setup");

    /// <summary>The log of an uninstall the setup ran; its name matches the app log's, so the 14-day cleanup removes it.</summary>
    private string SetupLogPath => Path.Combine(paths.LogDirectory, "betterclipboard-edition-move.log");

    /// <summary>Opens the privacy policy in the browser.</summary>
    public void OpenPrivacyPage() => OpenWebLink(PrivacyPage);

    /// <summary>
    /// Whether this Store edition keeps its data in the folder every kind of install uses (CLAUDE.md §2.47, "Share it"):
    /// then another copy's history is this edition's own, and nothing of it is copied, moved or removed by the setup.
    /// <see langword="false"/> for every other kind of install, and for this edition while it runs from its own folder
    /// (the fallback, <see cref="StoreDataFolder"/>).
    /// </summary>
    public bool SharesDataWithOtherEditions => IsStoreEdition && paths.InstanceScope is null && paths.UsesSharedDataDirectory;

    /// <summary>
    /// Writes what the Store edition's data folder setup found and did at this start (<see cref="StoreDataFolderSetup"/>)
    /// to the log, once the log exists: the setup ran before it. Content-free lines; nothing for other kinds of install.
    /// </summary>
    private void LogStoreDataFolder()
    {
        if (StoreDataFolderSetup.Current is not { } decision)
        {
            return;
        }

        foreach (string note in decision.Notes)
        {
            AppLog.Info($"Microsoft Store edition data folder: {note}");
        }

        AppLog.Info(decision.Mode == StoreDataMode.Shared
            ? "Microsoft Store edition: the history is in the shared data folder, the one every kind of install uses; it stays when the app is uninstalled."
            : "Microsoft Store edition: the history is in this edition's own folder, which Windows removes with the app.");
    }

    /// <summary>
    /// Another launch asked this instance to quit (<c>--exit</c>). Ignored while a move runs: that request then comes from
    /// the uninstaller of the copy being replaced, and is meant for that copy.
    /// </summary>
    public void ExitOnRequest()
    {
        if (setupBusy)
        {
            AppLog.Info("An exit request during the move to the Microsoft Store edition was ignored (it was meant for the copy being replaced).");
            return;
        }

        _ = ExitAsync();
    }

    /// <summary>
    /// Whether the Store edition has something to settle before it starts: the welcome was never accepted, another copy
    /// of BetterClipboard is on the PC, or one left a history that this edition does not have yet. Undoes a move that
    /// was left unfinished first, because that decides whether this edition has a history.
    /// </summary>
    /// <param name="welcomed">Whether the welcome was accepted before (the setting, read without the store).</param>
    /// <returns><see langword="true"/> to run <see cref="RunStoreSetupAsync"/> instead of starting.</returns>
    /// <exception cref="IOException">An unfinished move could not be undone (a file of the copy is in use): the app must not start on half a copy.</exception>
    /// <exception cref="UnauthorizedAccessException">Undoing an unfinished move was refused; see above.</exception>
    private bool NeedsStoreSetup(out bool welcomed)
    {
        welcomed = false;
        if (!IsStoreEdition || paths.InstanceScope is not null)
        {
            return false;
        }

        if (EditionDataMigrator.HasPendingMove(paths.DataDirectory))
        {
            if (SharesDataWithOtherEditions)
            {
                // No move ever targets the shared folder (it is every edition's own): a marker there is stale, and undoing
                // it would delete the shared history. Only the marker goes.
                AppLog.Warn("Microsoft Store edition: a stale marker of an unfinished move was found in the shared data folder; only the marker was removed.");
                EditionDataMigrator.Commit(paths.DataDirectory);
            }
            else
            {
                // A crash or a power cut in the middle of a move. Its original still exists; the copy goes.
                AppLog.Warn("Microsoft Store edition: a move from another copy was left unfinished; what it copied is removed, and the move starts over from the original.");
                EditionDataMigrator.Rollback(paths.DataDirectory);
            }
        }

        welcomed = SettingsStore.TryReadSnapshot(paths.SettingsPath)?.StoreWelcomeAccepted == true;
        var (step, _) = EditionSetupPlan.Next(OtherEditionLocator.Find(OwnDirectory, !instance.IsPrimary), !InspectOtherData().IsEmpty, paths.HasHistory());
        return !welcomed || step != EditionSetupStep.None;
    }

    /// <summary>
    /// Lists what the other editions' data folder holds, treating a folder that cannot be read as empty (the log says so):
    /// a history that cannot be read cannot be brought over either.
    /// </summary>
    /// <returns>
    /// The data; empty when there is none, after the user chose to go on without it, and always while this edition shares
    /// that folder (<see cref="SharesDataWithOtherEditions"/>): the "other" data is then its own, and treating it as
    /// another copy's would copy the folder onto itself and remove it afterwards.
    /// </returns>
    private EditionData InspectOtherData()
    {
        if (skipImport || SharesDataWithOtherEditions)
        {
            return new EditionData(AppPaths.DefaultDataDirectory, [], 0);
        }

        try
        {
            return EditionDataMigrator.Inspect(AppPaths.DefaultDataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Another edition's data folder could not be listed ({ex.GetType().Name}); nothing is brought over from it.");
            return new EditionData(AppPaths.DefaultDataDirectory, [], 0);
        }
    }

    /// <summary>
    /// The Store edition's setup: settles other copies and their data step by step, asks for the language when none was
    /// chosen, shows the welcome, and then starts the app; or quits when the user chooses the way out.
    /// </summary>
    /// <param name="historyExisted">Whether this edition's folder held a history before this start (for the language picker's log line).</param>
    /// <returns>A task completing once the app started or is exiting; failures are logged and end the app, never thrown.</returns>
    private async Task RunStoreSetupAsync(bool historyExisted)
    {
        try
        {
            ApplySetupLook();
            var model = new StoreSetupViewModel();
            setupWindow = new StoreSetupWindow(this, model);

            // A second launch of this edition while the other copy still holds the lock asks this window to show.
            instance.ListenForSetupShow(() => ui.TryEnqueue(() =>
            {
                if (languageWindow is not null)
                {
                    languageWindow.Present();
                }
                else
                {
                    setupWindow?.Present();
                }
            }));

            while (true)
            {
                var others = OtherEditionLocator.Find(OwnDirectory, !instance.IsPrimary);
                var data = InspectOtherData();
                var (step, edition) = EditionSetupPlan.Next(others, !data.IsEmpty, paths.HasHistory());
                if (step == EditionSetupStep.None)
                {
                    break;
                }

                AppLog.Info($"Microsoft Store edition setup: {step}{(edition is null ? string.Empty : $" ({edition.Kind}, version {edition.Version ?? "unknown"}, running: {edition.IsRunning})")}.");
                bool goOn = step switch
                {
                    EditionSetupStep.MoveFromInstaller => await MoveFromInstallerAsync(model, edition!, data),
                    EditionSetupStep.GuideUninstall => await GuideUninstallAsync(model, edition!),
                    EditionSetupStep.CloseRunning => await CloseRunningAsync(model, edition!),
                    _ => await ImportDataAsync(model, data),
                };
                if (!goOn || exiting)
                {
                    await QuitSetupAsync();
                    return;
                }
            }

            // Nothing else holds the lock any more; a copy that ended meanwhile left it free.
            if (!await TakeLockAsync(TimeSpan.FromSeconds(5)))
            {
                AppLog.Warn("Microsoft Store edition setup: another BetterClipboard still holds the instance lock; quitting.");
                await QuitSetupAsync();
                return;
            }

            // The app must never start on a copy whose original still exists: every path above ended a move or undid it.
            if (EditionDataMigrator.HasPendingMove(paths.DataDirectory))
            {
                throw new InvalidOperationException("A move is still marked as unfinished after the setup's steps.");
            }

            TryDeleteDirectory(SetupWorkDirectory);

            // Only now are the settings this edition's own (a move may just have brought them over).
            settings = LoadSettings();
            if (LanguageChoice.ShouldAsk(settings.Current.LanguageChosen, settings.Current.InterfaceLanguage))
            {
                setupWindow.Conceal();
                await PickLanguageAsync(afterUpdate: historyExisted || dataBroughtOver);
                if (exiting)
                {
                    return;
                }
            }

            if (!settings.Current.StoreWelcomeAccepted && !await WelcomeAsync(model))
            {
                await QuitSetupAsync();
                return;
            }

            if (exiting)
            {
                return;
            }

            var window = setupWindow;
            setupWindow = null;
            window?.CloseWhenDone();
            StartCore(historyExisted || dataBroughtOver, preopenedStore);
        }
        catch (Exception ex)
        {
            // A setup that cannot run must not leave a half-started app behind: nothing was started yet, so quitting is safe.
            AppLog.Error("The Microsoft Store edition's setup failed.", ex);
            await QuitSetupAsync();
        }
    }

    /// <summary>
    /// The language and the theme of the setup's windows before this edition's settings are loaded: its own saved
    /// choices when there are some, else those of the copy that is being replaced (its settings are about to become
    /// this edition's), else Windows' language and theme.
    /// </summary>
    private void ApplySetupLook()
    {
        // Asked whether each file exists first: a snapshot of a missing file is the defaults, not "nothing", and the
        // defaults of an edition that has no settings yet would hide the other copy's choices (found on the test desktop:
        // a Russian copy was replaced by a setup in English).
        string otherPath = Path.Combine(AppPaths.DefaultDataDirectory, EditionDataMigrator.SettingsFileName);
        var own = File.Exists(paths.SettingsPath) ? SettingsStore.TryReadSnapshot(paths.SettingsPath) : null;
        var other = own is null && File.Exists(otherPath) ? SettingsStore.TryReadSnapshot(otherPath) : null;
        var known = own ?? other;
        UiText.Language = known?.InterfaceLanguage ?? UiText.SystemSelection;
        ApplyLanguageToWindowsText(UiText.Current);
        setupTheme = known?.Theme ?? AppTheme.System;
    }

    /// <summary>
    /// Ends a setup the user left, or one that failed: undoes a move that did not reach its end, starts the copy this
    /// setup had closed again (the user keeps it), then exits. Never throws.
    /// </summary>
    /// <returns>A task completing just before the app exits.</returns>
    private async Task QuitSetupAsync()
    {
        try
        {
            AppLog.Info("Microsoft Store edition setup: left before it was done; this edition starts nothing.");

            // The other copy stays: what was copied from it must not count as this edition's history at the next start.
            await DiscardUnfinishedMoveAsync();
            TryDeleteDirectory(SetupWorkDirectory);
            if (closedCopyProgram is { } program && File.Exists(program) && !exiting)
            {
                // The lock goes first: a copy that starts into the tray and finds BetterClipboard running ends at once.
                instance.StepDown();
                using var started = Process.Start(new ProcessStartInfo(program, StartupRegistration.BackgroundSwitch) { UseShellExecute = true });
                AppLog.Info("Microsoft Store edition setup: the copy that was closed for the move was started again.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Leaving the Microsoft Store edition's setup: the copy that was closed for the move could not be started again ({ex.GetType().Name}).");
        }

        await ExitAsync();
    }

    /// <summary>
    /// Removes what an unfinished move copied into this edition's folder, with everything this session remembers about
    /// it. Does nothing when no move is unfinished. A failure is logged: the folder then stays marked, and the next
    /// start tries again before anything else.
    /// </summary>
    /// <returns>A task completing when the copy is gone or could not be removed.</returns>
    private async Task DiscardUnfinishedMoveAsync()
    {
        // Never in the shared folder: no move targets it, and undoing one there would delete the shared history.
        if (SharesDataWithOtherEditions || !EditionDataMigrator.HasPendingMove(paths.DataDirectory))
        {
            return;
        }

        preopenedStore = null;
        dataBroughtOver = false;
        movedNote = string.Empty;
        try
        {
            await Task.Run(() => EditionDataMigrator.Rollback(paths.DataDirectory));
            AppLog.Info("Microsoft Store edition setup: the copied history was removed again (the move did not reach its end; the original is untouched).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Microsoft Store edition setup: the copied history could not be removed from this edition's folder ({ex.GetType().Name}); the next start tries again.");
        }
    }

    /// <summary>
    /// Takes the single-instance lock when another copy held it: waits until it is free, forgets commands that were
    /// meant for the copy that is gone, and tells the app to start listening.
    /// </summary>
    /// <param name="timeout">How long to wait for the lock.</param>
    /// <returns><see langword="true"/> once this process is the primary instance.</returns>
    private async Task<bool> TakeLockAsync(TimeSpan timeout)
    {
        if (instance.IsPrimary)
        {
            return true;
        }

        // On the UI thread throughout (no ConfigureAwait(false)): the lock belongs to the thread that takes it, and the
        // main thread is the one that releases it at exit.
        var waited = Stopwatch.StartNew();
        while (!instance.TryBecomePrimary())
        {
            if (waited.Elapsed > timeout || exiting)
            {
                return false;
            }

            await Task.Delay(150);
        }

        instance.DiscardPendingCommands();
        BecamePrimary?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Asks the copy that holds the lock to exit (it stores what was copied first) and takes the lock.
    /// </summary>
    /// <param name="edition">The copy, to remember its program for <see cref="QuitSetupAsync"/>.</param>
    /// <returns><see langword="true"/> once it is closed and this process holds the lock.</returns>
    private async Task<bool> CloseOtherCopyAsync(OtherEdition edition)
    {
        if (instance.IsPrimary)
        {
            return true;
        }

        if (edition.Directory.Length > 0)
        {
            closedCopyProgram = Path.Combine(edition.Directory, "BetterClipboard.exe");
        }

        instance.Signal(InstanceCommand.Exit);
        return await TakeLockAsync(CloseTimeout);
    }

    /// <summary>
    /// The copy this user installed with the installer: shows what was found and the checks, and on the user's go-ahead
    /// closes it, brings its history over, checks the copy, uninstalls it and removes its old data folder.
    /// </summary>
    /// <param name="model">The setup window's content.</param>
    /// <param name="edition">The copy.</param>
    /// <param name="data">What its data folder holds.</param>
    /// <returns><see langword="false"/> when the user chose the way out; <see langword="true"/> to look at the PC again (after a move, a failed move, or "Check again").</returns>
    private async Task<bool> MoveFromInstallerAsync(StoreSetupViewModel model, OtherEdition edition, EditionData data)
    {
        // A second attempt after a move that stopped half way (its uninstall failed): start over from the original.
        await DiscardUnfinishedMoveAsync();
        bool bringsData = EditionSetupPlan.BringsDataOver(!data.IsEmpty, paths.HasHistory());
        string version = edition.Version ?? UiText.Translate("not known");
        // The paragraph says what will really happen to the history: nothing at all when both use the shared folder,
        // brought over, nothing there to bring, or left where it is because this edition already has one (two histories
        // are never merged).
        string lead = SharesDataWithOtherEditions
            ? UiText.Format($"A copy from the installer is in {edition.Directory} (version {version}). Only one BetterClipboard can be installed, so the Microsoft Store edition takes its place and uninstalls the other copy. Your history, pins and settings stay as they are: both copies use the same folder.")
            : bringsData
                ? UiText.Format($"A copy from the installer is in {edition.Directory} (version {version}). Only one BetterClipboard can be installed, so the Microsoft Store edition takes its place: it brings your history over, checks it, and then uninstalls the other copy.")
                : data.IsEmpty
                    ? UiText.Format($"A copy from the installer is in {edition.Directory} (version {version}). Only one BetterClipboard can be installed, so the Microsoft Store edition takes its place and uninstalls the other copy.")
                    : UiText.Format($"A copy from the installer is in {edition.Directory} (version {version}). Only one BetterClipboard can be installed, so the Microsoft Store edition takes its place and uninstalls the other copy. This edition already has a history of its own, so the other copy's history is not brought over: it stays in {data.Directory}.");
        model.ShowPage(UiText.Translate("BetterClipboard is already installed on this PC"), lead);
        model.QuitText = UiText.Translate("Quit");
        model.PrimaryText = UiText.Translate("Move to this edition");
        model.IsPrimaryEnabled = false;

        // The two lines about the history are checks of a copy and of a removal: without either, they are not shown.
        var historyRow = bringsData ? model.AddRow(UiText.Translate("Your history can be read")) : null;
        var targetRow = model.AddRow(UiText.Translate("This edition's folder can be written"));
        var uninstallRow = model.AddRow(UiText.Translate("The other copy can be uninstalled from here"));
        var cleanupRow = bringsData ? model.AddRow(UiText.Translate("The old history folder can be removed afterwards")) : null;
        setupWindow!.Present();
        model.IsBusy = true;

        bool ready = true;

        // 1. The history: every file opens for reading, and the drive has room for the copy.
        if (historyRow is not null)
        {
            historyRow.State = SetupRowState.Running;
            try
            {
                await Task.Run(() => EditionDataMigrator.EnsureReadable(data));
                long needed = EditionDataMigrator.RequiredFreeBytes(data);
                long free = EditionDataMigrator.FreeBytes(paths.DataDirectory);
                if (free < needed)
                {
                    ready = false;
                    historyRow.Set(SetupRowState.Failed, UiText.Format($"Copying needs {SettingsViewModel.FormatBytes(needed)} of free space on this drive; {SettingsViewModel.FormatBytes(free)} are free."));
                }
                else
                {
                    historyRow.Set(SetupRowState.Ok, UiText.Format($"{SettingsViewModel.FormatBytes(data.TotalBytes)} of history and settings will be copied and checked."));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ready = false;
                historyRow.Set(SetupRowState.Failed, UiText.Format($"A file of the history could not be opened: {ex.Message}"));
            }
        }

        // 2. This edition's folder: the shared one stays after an uninstall, its own one goes with the app.
        ready &= CheckFolder(
            targetRow,
            paths.DataDirectory,
            create: true,
            SharesDataWithOtherEditions
                ? UiText.Translate("Every kind of BetterClipboard install uses this folder; it stays when the app is uninstalled.")
                : UiText.Translate("Windows removes this folder with the app when it is uninstalled."));

        // 3. The uninstaller: the script is in the package, PowerShell exists, a script file really runs here, and this
        // account may change the other copy's folder, shortcut and registry entries.
        uninstallRow.State = SetupRowState.Running;
        var check = await EditionUninstaller.CheckAsync(edition, SetupWorkDirectory, CancellationToken.None);
        if (check.Ok)
        {
            uninstallRow.Set(SetupRowState.Ok, UiText.Translate("It removes the other copy's program, shortcut and startup entry, and gives Win+V back to Windows (the taskbar restarts once)."));
        }
        else
        {
            ready = false;
            uninstallRow.Set(SetupRowState.Failed, DescribeUninstallFailure(check));
            AppLog.Warn($"Microsoft Store edition setup: the check before uninstalling the installer copy failed ({check.Failure}{(check.Detail is null ? string.Empty : ": " + check.Detail)}).");
        }

        // 4. The old folder, which is removed last.
        if (cleanupRow is not null)
        {
            ready &= CheckFolder(cleanupRow, data.Directory, create: false, UiText.Format($"{data.Directory} is deleted once the copied history is checked and the other copy is uninstalled."));
        }

        model.IsBusy = false;
        if (!ready)
        {
            model.PrimaryText = UiText.Translate("Check again");
            model.ErrorText = UiText.Translate("The move cannot start until the marked points are solved. Nothing was changed.");
        }

        model.IsPrimaryEnabled = true;
        if (await setupWindow.NextChoiceAsync() == SetupChoice.Quit)
        {
            return false;
        }

        // "Check again" looks at the PC anew; otherwise the move runs.
        return !ready || await RunMoveAsync(model, edition, data, bringsData);
    }

    /// <summary>Checks that a folder can be written and deleted in, and words the result on a line.</summary>
    /// <param name="row">The line.</param>
    /// <param name="directory">The folder.</param>
    /// <param name="create">Create it when missing.</param>
    /// <param name="okDetail">The detail for a folder that passes.</param>
    /// <returns>Whether it passed.</returns>
    private static bool CheckFolder(SetupRowViewModel row, string directory, bool create, string okDetail)
    {
        try
        {
            EditionDataMigrator.EnsureWritable(directory, create);
            row.Set(SetupRowState.Ok, okDetail);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            row.Set(SetupRowState.Failed, UiText.Format($"{directory} cannot be written: {ex.Message}"));
            return false;
        }
    }

    /// <summary>
    /// The move itself: close the other copy, copy and check its history, uninstall it, remove its old folder. Stops at
    /// the first failure and says what happened; the copy is undone when the user then quits or tries again.
    /// </summary>
    /// <param name="model">The setup window's content.</param>
    /// <param name="edition">The copy being replaced.</param>
    /// <param name="data">What its data folder holds.</param>
    /// <param name="bringsData">Whether its history is brought over.</param>
    /// <returns><see langword="false"/> when the user chose the way out after a failure; <see langword="true"/> otherwise.</returns>
    private async Task<bool> RunMoveAsync(StoreSetupViewModel model, OtherEdition edition, EditionData data, bool bringsData)
    {
        model.ShowPage(
            UiText.Translate("Moving to the Microsoft Store edition"),
            UiText.Translate("This takes a moment. Please do not turn the PC off."));
        model.QuitText = UiText.Translate("Quit");
        model.PrimaryText = UiText.Translate("Try again");
        model.IsPrimaryEnabled = false;
        var closeRow = model.AddRow(UiText.Translate("Close the other copy"));
        var copyRow = bringsData ? model.AddRow(UiText.Translate("Copy the history and settings")) : null;
        var checkRow = bringsData ? model.AddRow(UiText.Translate("Check the copied history")) : null;
        var uninstallRow = model.AddRow(UiText.Translate("Uninstall the other copy"));
        var cleanupRow = bringsData ? model.AddRow(UiText.Translate("Remove the old history folder")) : null;
        model.IsBusy = true;
        setupBusy = true;
        string? failure = null;
        try
        {
            // 1. Close it: a history that is being written cannot be copied, and two copies cannot share the clipboard.
            closeRow.State = SetupRowState.Running;
            if (!await CloseOtherCopyAsync(edition))
            {
                closeRow.State = SetupRowState.Failed;
                failure = UiText.Translate("The other BetterClipboard did not close. Close it from its icon in the notification area, then try again. Nothing was changed.");
            }
            else
            {
                closeRow.State = SetupRowState.Ok;
            }

            // 2 and 3. Copy, then prove the copy.
            if (failure is null && bringsData)
            {
                failure = await BringDataOverAsync(model, copyRow!, checkRow!);
            }

            // 4. Uninstall, with the installer script this package carries; proven by the copy being gone.
            if (failure is null)
            {
                uninstallRow.State = SetupRowState.Running;
                var result = await EditionUninstaller.UninstallAsync(edition, SetupWorkDirectory, SetupLogPath, CancellationToken.None);
                if (result.Ok)
                {
                    uninstallRow.State = SetupRowState.Ok;
                    closedCopyProgram = null;
                    AppLog.Info("Microsoft Store edition setup: the installer copy was uninstalled.");
                }
                else
                {
                    uninstallRow.Set(SetupRowState.Failed, DescribeUninstallFailure(result));
                    AppLog.Warn($"Microsoft Store edition setup: uninstalling the installer copy failed ({result.Failure}{(result.Detail is null ? string.Empty : ": " + result.Detail)}).");
                    failure = UiText.Translate("The other copy could not be uninstalled. Uninstall it in Windows' Installed apps, then try again.");
                    model.SecondaryText = UiText.Translate("Open Installed apps");
                }
            }

            // 5. Only now, with a proven copy and the other program gone, does the old folder go.
            if (failure is null && bringsData)
            {
                await RemoveOldDataAsync(cleanupRow!, data.Directory);
            }
        }
        finally
        {
            setupBusy = false;
            model.IsBusy = false;
        }

        if (failure is null)
        {
            return true;
        }

        model.ErrorText = failure;
        model.IsPrimaryEnabled = true;
        while (true)
        {
            var choice = await setupWindow!.NextChoiceAsync();
            if (choice == SetupChoice.Secondary)
            {
                OpenInstalledApps();
                continue;
            }

            return choice == SetupChoice.Primary;
        }
    }

    /// <summary>
    /// Copies the other editions' data into this edition's folder and proves the copy; undoes the copy when either part
    /// fails, so the PC is as before. On success the copy stays marked as an unfinished move until
    /// <see cref="RemoveOldDataAsync"/> ends it.
    /// </summary>
    /// <remarks>
    /// The files are listed here, right before they are copied, and again for every attempt: never from a list made
    /// earlier. A copy that was running when the first page looked had its database's write-ahead log on disk; closing
    /// it folds that log into the database and deletes it, so an older list names a file that is rightly gone (found on
    /// the test desktop, where the first move stopped at "Could not find file …history.db-wal").
    /// </remarks>
    /// <param name="model">The setup window's content (its progress bar follows the copy).</param>
    /// <param name="copyRow">The copy's line.</param>
    /// <param name="checkRow">The check's line.</param>
    /// <returns><see langword="null"/> on success, else the failure in the user's language.</returns>
    /// <exception cref="InvalidOperationException">
    /// This edition shares the folder the data would come from (<see cref="SharesDataWithOtherEditions"/>); never reached by
    /// the setup's plan, refused here so the shared folder can never be copied onto itself.
    /// </exception>
    private async Task<string?> BringDataOverAsync(StoreSetupViewModel model, SetupRowViewModel copyRow, SetupRowViewModel checkRow)
    {
        if (SharesDataWithOtherEditions)
        {
            // Unreachable by design (nothing is "other data" then), and never allowed: the folder would be copied onto itself.
            throw new InvalidOperationException("The shared data folder is never brought over: it is this edition's own.");
        }

        copyRow.State = SetupRowState.Running;
        model.IsProgressIndeterminate = false;
        var data = new EditionData(AppPaths.DefaultDataDirectory, [], 0);
        try
        {
            // The closed copy's database may stay open for a moment after its process went, and its files may still
            // change while it ends: list and try again for a few seconds.
            var waited = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var listed = await Task.Run(() => EditionDataMigrator.Inspect(AppPaths.DefaultDataDirectory));
                    data = listed;
                    if (listed.IsEmpty)
                    {
                        break;
                    }

                    // Created on the UI thread, so its reports arrive there.
                    long total = Math.Max(1, listed.TotalBytes);
                    var progress = new Progress<long>(bytes => model.Progress = bytes * 100.0 / total);
                    await Task.Run(() => EditionDataMigrator.CopyAsync(listed, paths.DataDirectory, progress, CancellationToken.None));
                    break;
                }
                catch (IOException) when (waited.Elapsed < FilesFreeTimeout)
                {
                    await Task.Run(() => EditionDataMigrator.Rollback(paths.DataDirectory));
                    await Task.Delay(500);
                }
            }

            if (data.IsEmpty)
            {
                // The folder was emptied since the first look (by the user, most likely): nothing to bring, nothing to check.
                copyRow.Set(SetupRowState.Ok, UiText.Translate("The other copy has no history or settings to bring over."));
                checkRow.State = SetupRowState.Ok;
                AppLog.Info("Microsoft Store edition setup: the other edition's data folder holds nothing to bring over any more.");
                return null;
            }

            copyRow.Set(SetupRowState.Ok, UiText.Format($"{SettingsViewModel.FormatBytes(data.TotalBytes)} copied; every file matches its original."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            copyRow.State = SetupRowState.Failed;
            AppLog.Warn($"Microsoft Store edition setup: copying the other edition's data failed ({ex.GetType().Name}: {ex.Message}).");
            await DiscardUnfinishedMoveAsync();
            return UiText.Format($"The history could not be copied: {ex.Message} Nothing was changed.");
        }
        finally
        {
            model.IsProgressIndeterminate = true;
        }

        checkRow.State = SetupRowState.Running;
        var check = await Task.Run(() =>
        {
            var identity = MachineIdentity.Current();
            byte[] binding = MachineBinding.Derive(identity.MachineGuid, identity.UserSid);
            try
            {
                return EditionDataMigrator.Validate(paths, new DpapiKeyProtector(), binding, DateTimeOffset.Now);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(binding);
            }
        });
        if (!check.Ok)
        {
            checkRow.State = SetupRowState.Failed;
            AppLog.Warn($"Microsoft Store edition setup: the copied history did not pass its check ({check.Reason}).");
            await DiscardUnfinishedMoveAsync();
            return UiText.Translate("The copied history did not pass its check, so it was removed again. The other copy and its history are unchanged.");
        }

        long items = check.Stats?.Count ?? 0;
        checkRow.Set(SetupRowState.Ok, UiText.Plural(items, $"It opens with this account's key and holds {items:N0} item.", $"It opens with this account's key and holds {items:N0} items."));
        preopenedStore = check.Opened;
        dataBroughtOver = true;
        movedNote = UiText.Plural(items, $"Brought over {items:N0} item and your settings from the BetterClipboard that was installed before.", $"Brought over {items:N0} items and your settings from the BetterClipboard that was installed before.");
        AppLog.Info($"Microsoft Store edition setup: {data.Files.Count} file(s), {data.TotalBytes:N0} bytes brought over and checked ({items:N0} item(s)).");
        return null;
    }

    /// <summary>
    /// The point of no return: ends the move (the copy is this edition's history from now on) and deletes the other
    /// editions' data folder. Leftovers (a file something still holds) are not a failure: the line says where they are.
    /// </summary>
    /// <param name="row">The step's line.</param>
    /// <param name="directory">The old data folder.</param>
    /// <returns>A task completing when the folder is gone or its leftovers are named.</returns>
    /// <exception cref="IOException">The move's marker could not be removed; the old folder then stays untouched, and the caller quits.</exception>
    /// <exception cref="UnauthorizedAccessException">Removing the marker was refused; see above.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="directory"/> is this edition's own data folder (the shared one while it shares it): refused before
    /// anything is touched.
    /// </exception>
    private async Task RemoveOldDataAsync(SetupRowViewModel row, string directory)
    {
        // The last line of defense for "Share it": the folder this edition reads its history from is never deleted here.
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), Path.TrimEndingDirectorySeparator(paths.DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The folder to remove is this edition's own data folder.");
        }

        row.State = SetupRowState.Running;

        // First, and never after the old folder began to go: a marker left behind would make the next start undo a copy
        // whose original no longer exists.
        EditionDataMigrator.Commit(paths.DataDirectory);
        var leftovers = await Task.Run(() => EditionDataMigrator.RemoveSource(directory));
        if (leftovers.Count == 0)
        {
            row.State = SetupRowState.Ok;
            AppLog.Info("Microsoft Store edition setup: the other edition's data folder was removed.");
        }
        else
        {
            row.Set(SetupRowState.Warning, UiText.Format($"Some files in {directory} are in use and stayed; you can delete that folder yourself later."));
            AppLog.Warn($"Microsoft Store edition setup: {leftovers.Count} file(s) of the other edition's data folder could not be removed.");
        }
    }

    /// <summary>
    /// A copy for every user, or a Chocolatey copy: says how an administrator removes it, and waits until it is gone.
    /// Such a copy belongs to the PC's administrator and to every other user of it, so this edition does not remove it.
    /// </summary>
    /// <param name="model">The setup window's content.</param>
    /// <param name="edition">The copy.</param>
    /// <returns><see langword="false"/> when the user chose the way out; <see langword="true"/> once the copy is gone.</returns>
    private async Task<bool> GuideUninstallAsync(StoreSetupViewModel model, OtherEdition edition)
    {
        bool chocolatey = edition.Kind == OtherEditionKind.Chocolatey;
        string version = edition.Version ?? UiText.Translate("not known");
        // The last sentence says what happens to the history: nothing while both copies use the shared folder, else it is
        // brought over once the other copy is gone.
        bool shared = SharesDataWithOtherEditions;
        model.ShowPage(
            UiText.Translate("BetterClipboard is already installed on this PC"),
            (chocolatey, shared) switch
            {
                (true, true) => UiText.Format($"A copy installed with Chocolatey is in {edition.Directory} (version {version}). Only one BetterClipboard can be installed. Chocolatey removes its own packages: run the command below in a terminal as administrator. Your history stays as it is: both copies use the same folder."),
                (true, false) => UiText.Format($"A copy installed with Chocolatey is in {edition.Directory} (version {version}). Only one BetterClipboard can be installed. Chocolatey removes its own packages: run the command below in a terminal as administrator. Your history stays on this PC and is brought over afterwards."),
                (false, true) => UiText.Format($"A copy for every user of this PC is in {edition.Directory} (version {version}). Only one BetterClipboard can be installed. An administrator removes that copy in Windows' Installed apps. Your history stays as it is: both copies use the same folder."),
                (false, false) => UiText.Format($"A copy for every user of this PC is in {edition.Directory} (version {version}). Only one BetterClipboard can be installed. An administrator removes that copy in Windows' Installed apps. Your history stays on this PC and is brought over afterwards."),
            });
        model.QuitText = UiText.Translate("Quit");
        model.PrimaryText = chocolatey ? UiText.Translate("Copy command") : UiText.Translate("Open Installed apps");
        model.FooterNote = UiText.Translate("Waiting for the other copy to be removed…");
        var row = model.AddRow(chocolatey ? UpdateSource.ChocolateyUninstallCommand : UiText.Translate("Uninstall the BetterClipboard that is not from the Microsoft Store"));
        row.State = SetupRowState.Running;
        setupWindow!.Present();

        // Either a button, or the poll interval: then look whether the copy is gone.
        while (true)
        {
            var click = setupWindow.NextChoiceAsync();
            if (await Task.WhenAny(click, Task.Delay(RemovalPollInterval)) == click)
            {
                var choice = await click;
                if (choice == SetupChoice.Quit)
                {
                    return false;
                }

                if (chocolatey)
                {
                    CopyTextForUser(UpdateSource.ChocolateyUninstallCommand);
                    row.Detail = UiText.Translate("Copied.");
                }
                else
                {
                    OpenInstalledApps();
                }
            }

            if (!OtherEditionLocator.IsStillInstalled(edition))
            {
                row.State = SetupRowState.Ok;
                AppLog.Info($"Microsoft Store edition setup: the {edition.Kind} copy was removed.");
                return true;
            }
        }
    }

    /// <summary>
    /// A copy with no install record is running: asks, and closes it on the user's go-ahead.
    /// </summary>
    /// <param name="model">The setup window's content.</param>
    /// <param name="edition">The copy.</param>
    /// <returns><see langword="false"/> when the user chose the way out; <see langword="true"/> to look at the PC again.</returns>
    private async Task<bool> CloseRunningAsync(StoreSetupViewModel model, OtherEdition edition)
    {
        model.ShowPage(
            UiText.Translate("Another BetterClipboard is running"),
            edition.Directory.Length > 0
                ? UiText.Format($"It runs from {edition.Directory} and is not from the Microsoft Store. Only one BetterClipboard can run at a time: this one starts once the other one is closed.")
                : UiText.Translate("It is not from the Microsoft Store. Only one BetterClipboard can run at a time: this one starts once the other one is closed."));
        model.QuitText = UiText.Translate("Quit");
        model.PrimaryText = UiText.Translate("Close it and continue");
        setupWindow!.Present();
        if (await setupWindow.NextChoiceAsync() != SetupChoice.Primary)
        {
            return false;
        }

        model.IsBusy = true;
        model.IsPrimaryEnabled = false;
        bool closed = await CloseOtherCopyAsync(edition);
        model.IsBusy = false;
        if (closed)
        {
            // The user chose to close it for good; quitting later must not start it again by itself.
            closedCopyProgram = null;
            return true;
        }

        model.ErrorText = UiText.Translate("The other BetterClipboard did not close. Close it from its icon in the notification area, then try again. Nothing was changed.");
        model.PrimaryText = UiText.Translate("Try again");
        model.IsPrimaryEnabled = true;
        return await setupWindow.NextChoiceAsync() == SetupChoice.Primary;
    }

    /// <summary>
    /// No other copy is installed, but one left its history behind and this edition has none: brings it over without
    /// asking (the owner's decision), shows the three steps, and goes on by itself when they succeed.
    /// </summary>
    /// <param name="model">The setup window's content.</param>
    /// <param name="data">What the old data folder holds.</param>
    /// <returns><see langword="false"/> when the user chose the way out after a failure; <see langword="true"/> otherwise.</returns>
    private async Task<bool> ImportDataAsync(StoreSetupViewModel model, EditionData data)
    {
        model.ShowPage(
            UiText.Translate("Bringing your history over"),
            UiText.Format($"A BetterClipboard that was installed on this PC before left its history ({SettingsViewModel.FormatBytes(data.TotalBytes)}). It is copied into this edition and checked, and then the old folder is removed."));
        model.QuitText = UiText.Translate("Quit");
        model.PrimaryText = UiText.Translate("Try again");
        model.IsPrimaryEnabled = false;
        var copyRow = model.AddRow(UiText.Translate("Copy the history and settings"));
        var checkRow = model.AddRow(UiText.Translate("Check the copied history"));
        var cleanupRow = model.AddRow(UiText.Translate("Remove the old history folder"));
        setupWindow!.Present();
        model.IsBusy = true;
        string? failure;
        try
        {
            failure = await BringDataOverAsync(model, copyRow, checkRow);
            if (failure is null)
            {
                await RemoveOldDataAsync(cleanupRow, data.Directory);
            }
        }
        finally
        {
            model.IsBusy = false;
        }

        if (failure is null)
        {
            return true;
        }

        model.ErrorText = failure;
        model.SecondaryText = UiText.Translate("Continue without it");
        model.IsPrimaryEnabled = true;
        var choice = await setupWindow.NextChoiceAsync();
        if (choice == SetupChoice.Secondary)
        {
            // The old folder stays where it is; this edition starts with an empty history.
            skipImport = true;
            AppLog.Info("Microsoft Store edition setup: the user went on without the other edition's history.");
        }

        return choice != SetupChoice.Quit;
    }

    /// <summary>
    /// The welcome: what BetterClipboard does and keeps, the two switches, and <em>Start</em>, which is the user's yes.
    /// </summary>
    /// <param name="model">The setup window's content.</param>
    /// <returns><see langword="true"/> when the user pressed <em>Start</em> (the choices are saved and applied); <see langword="false"/> for the way out.</returns>
    private async Task<bool> WelcomeAsync(StoreSetupViewModel model)
    {
        var current = Settings.Current;
        var startup = await PackagedStartup.GetStateAsync();
        static bool IsWinV(string hotkey) => AppSettings.SameHotkeyText(hotkey, "Win+V");

        model.ShowPage(
            UiText.Translate("Welcome to BetterClipboard"),
            UiText.Translate("Everything you copy, kept and searchable. Encrypted on this PC; nothing leaves it."),
            welcome: true);
        bool hadWinV = current.OpenHotkeys.Any(IsWinV);
        model.UseWinV = hadWinV;
        model.CanChangeStartup = startup is PackagedStartupState.Disabled or PackagedStartupState.Enabled;
        model.StartWithWindows = startup is PackagedStartupState.Disabled or PackagedStartupState.Enabled or PackagedStartupState.EnabledByPolicy;
        model.StartupDescription = startup switch
        {
            PackagedStartupState.DisabledByUser => UiText.Translate("Turned off in Windows' Startup apps (Settings › Apps › Startup). Turn it on there to start BetterClipboard when you sign in."),
            PackagedStartupState.DisabledByPolicy => UiText.Translate("Turned off by your organization."),
            PackagedStartupState.EnabledByPolicy => UiText.Translate("Turned on by your organization."),
            PackagedStartupState.Unavailable => UiText.Translate("Not available in this copy."),
            _ => UiText.Translate("So your copies are kept from the moment you sign in."),
        };
        // In the folder every edition shares (§2.47) an earlier copy's history is simply there: nothing was moved, so the
        // move's "Brought over …" has nothing to say, and without a line the welcome would read as a fresh start while
        // the panel then opens with the old history. The store is not opened to count it: before Start nothing of the
        // history is touched. (It can also be this edition's own history, when an older version saved the settings
        // file without StoreWelcomeAccepted; the sentence stays true then.)
        model.WelcomeNote = movedNote.Length > 0 || !SharesDataWithOtherEditions || !StoreDataFolder.HasHistory(paths.DataDirectory)
            ? movedNote
            : UiText.Translate("Your history and settings from the BetterClipboard you used before are kept: every kind of install uses the same folder.");
        model.PrimaryText = UiText.Translate("Start");
        model.QuitText = UiText.Translate("Not now");
        model.FooterNote = UiText.Translate("Nothing is recorded until you press Start.");
        setupWindow!.View.WordWelcome();
        setupWindow.Present();
        if (await setupWindow.NextChoiceAsync() != SetupChoice.Primary)
        {
            AppLog.Info("Microsoft Store edition: the welcome was left without Start; nothing was recorded.");
            return false;
        }

        // The shortcuts change only when the switch says something else than the settings: a moved user's own list stays.
        IReadOnlyList<string> hotkeys = current.OpenHotkeys;
        if (model.UseWinV && !hadWinV)
        {
            hotkeys = ["Win+V", .. hotkeys];
        }
        else if (!model.UseWinV && hadWinV)
        {
            var others = hotkeys.Where(h => !IsWinV(h)).ToList();
            hotkeys = others.Count > 0 ? others : [AppSettings.AlternativeOpenHotkey];
        }

        // In this edition Win+V is always Explorer's, so only the keyboard hook can answer it (releasing it from Explorer
        // is not this edition's to do): a user who had the hook off in the copy they came from gets it with the switch.
        bool useWinV = model.UseWinV;
        Settings.Update(s => s.WithOpenHotkeys(hotkeys) with
        {
            StoreWelcomeAccepted = true,
            UseKeyboardHookFallback = s.UseKeyboardHookFallback || useWinV,
        });
        var startupNow = startup;
        if (model.CanChangeStartup)
        {
            startupNow = await PackagedStartup.SetEnabledAsync(model.StartWithWindows);
        }

        AppLog.Info($"Microsoft Store edition: welcome accepted (Win+V: {(useWinV ? "on" : "off")}, start with Windows: {startupNow}).");
        return true;
    }

    /// <summary>Words why an uninstall, or the check before one, did not work.</summary>
    /// <param name="result">The outcome.</param>
    /// <returns>A sentence in the user's language, with the uninstaller's own words (in English) after it when there are any.</returns>
    private static string DescribeUninstallFailure(EditionUninstallResult result)
    {
        // The check's own finding is a list of fixed names, worded here; everything else may carry the uninstaller's
        // own words (in English) after the sentence.
        if (result.Failure == EditionUninstallFailure.AccessDenied && DescribeDenied(result.Detail) is { } denied)
        {
            return denied;
        }

        string text = result.Failure switch
        {
            EditionUninstallFailure.ScriptMissing => UiText.Translate("This package has no installer script, so it cannot uninstall the other copy."),
            EditionUninstallFailure.PowerShellMissing => UiText.Translate("Windows PowerShell was not found on this PC."),
            EditionUninstallFailure.ScriptsBlocked => UiText.Translate("PowerShell scripts do not run on this PC (a policy blocks them), so the other copy cannot be uninstalled from here."),
            EditionUninstallFailure.TimedOut => UiText.Translate("The uninstaller did not finish within five minutes."),
            EditionUninstallFailure.StillInstalled => UiText.Translate("The uninstaller ran, but the other copy is still there."),
            EditionUninstallFailure.WorkFolder => UiText.Translate("The working folder could not be written."),
            EditionUninstallFailure.AccessDenied => UiText.Translate("This account is not allowed to change something the uninstall has to change (the other copy's folder, its shortcut or its entries in the registry)."),
            _ => UiText.Translate("The uninstaller reported a failure."),
        };
        return result.Detail is { Length: > 0 } detail ? UiText.JoinLine(" ", [text, detail]) : text;
    }

    /// <summary>
    /// Words what the check before a move found this account may not change, from the names the check's script reports
    /// (<see cref="EditionUninstaller.CheckAsync(OtherEdition, string, CancellationToken)"/>): a person needs to know
    /// which folder or setting to look at, and the raw names ("registry:Run") say that to nobody.
    /// </summary>
    /// <param name="names">The comma-separated names; <see langword="null"/> or empty for none.</param>
    /// <returns>The sentence, or <see langword="null"/> when no known name is among them (the general sentence is used then).</returns>
    private static string? DescribeDenied(string? names)
    {
        var parts = (names ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name switch
            {
                "folder" => UiText.Translate("the other copy's folder"),
                "shortcut" => UiText.Translate("its Start menu shortcut"),
                "registry:Run" => UiText.Translate("the list of programs that start with Windows"),
                "registry:Uninstall" => UiText.Translate("its entry in Installed apps"),
                "registry:Explorer" => UiText.Translate("Explorer's settings"),
                "registry:Environment" => UiText.Translate("your PATH"),
                _ => null,
            })
            .OfType<string>()
            .Distinct()
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        // "a", "a and b", "a, b and c": the catalog's own separator and its "and".
        string list = parts[0];
        if (parts.Count > 1)
        {
            string head = string.Join(UiText.Translate(", "), parts.Take(parts.Count - 1));
            string last = parts[^1];
            list = UiText.Format($"{head} and {last}");
        }

        return UiText.Format($"This account is not allowed to change what the uninstall has to change: {list}.");
    }

    /// <summary>Opens Windows' Installed apps page, where a copy that is not from the Store is uninstalled.</summary>
    private static void OpenInstalledApps() => OpenWindowsSettingsPage("appsfeatures");

    /// <summary>
    /// Opens a page of Windows' Settings app: what the Microsoft Store edition leaves to Windows (the Startup apps
    /// list, the clipboard page, Installed apps) is one click away instead of something the app changes behind the
    /// user's back.
    /// </summary>
    /// <param name="page">
    /// The page's name after <c>ms-settings:</c> (<c>appsfeatures</c>, <c>startupapps</c>, <c>clipboard</c>). Fixed
    /// names from this app's own code only: the text becomes part of an address that Windows opens.
    /// </param>
    /// <returns>Whether Windows took the request; a failure is logged.</returns>
    /// <exception cref="ArgumentException"><paramref name="page"/> is null or blank.</exception>
    public static bool OpenWindowsSettingsPage(string page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(page);
        return OpenShellAddress($"ms-settings:{page}", "A page of Windows' Settings");
    }

    /// <summary>
    /// Opens this app's page in the Microsoft Store app (release notes, reviews, and the Store's own "Update" button).
    /// Only the Store edition knows its page: the address is built from its package family name.
    /// </summary>
    /// <returns>Whether Windows took the request; <see langword="false"/> also for a copy that is not from the Store.</returns>
    public static bool OpenStorePage() =>
        PackageIdentity.FamilyName is { } family && OpenShellAddress($"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(family)}", "The Microsoft Store page");

    /// <summary>Hands an address to Windows to open with whatever app owns its scheme.</summary>
    /// <param name="address">The address (<c>ms-settings:…</c>, <c>ms-windows-store://…</c>).</param>
    /// <param name="what">What it is, for the log line of a failure.</param>
    /// <returns>Whether Windows took the request.</returns>
    private static bool OpenShellAddress(string address, string what)
    {
        try
        {
            using var started = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Warn($"{what} could not be opened ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// Puts a text on the clipboard for the user (a command to run elsewhere). The app's own clipboard code is not
    /// started during the setup, so this goes through Windows' data package; nothing is recording yet.
    /// </summary>
    /// <param name="text">The text.</param>
    private static void CopyTextForUser(string text)
    {
        try
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            AppLog.Warn($"The command could not be copied ({ex.GetType().Name}).");
        }
    }

    /// <summary>Deletes a folder of the setup's own if it can; a leftover is harmless and is overwritten by the next setup.</summary>
    /// <param name="directory">The folder.</param>
    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: a few small files in the app's own data folder.
        }
    }

    /// <summary>
    /// Asks Windows to start this edition again, in the tray, after it closed it to install an update from the Store
    /// (<see cref="PackagedRestart"/>). Without it, an update would leave the user without a clipboard history until the
    /// next sign-in.
    /// </summary>
    private static void RegisterRestartAfterStoreUpdate()
    {
        int result = PackagedRestart.RegisterAfterUpdate(StartupRegistration.BackgroundSwitch);
        if (result != 0)
        {
            AppLog.Info($"The restart after a Store update could not be registered (0x{result:X8}); the app then comes back at the next sign-in.");
        }
    }
}
