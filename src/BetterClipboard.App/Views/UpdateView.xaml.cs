using BetterClipboard.Core.Presentation;
using BetterClipboard.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.Views;

/// <summary>
/// The update dialog's content: the state of updates in words, the decision buttons, and below them the release notes.
/// </summary>
/// <remarks>
/// <para>
/// <b>It shows, it does not act.</b> <see cref="Render"/> turns an <see cref="UpdateSnapshot"/> into what is on screen;
/// every button only raises an event. The host (<see cref="UpdateDialog"/>) turns the events into actions, so the view
/// needs neither the controller nor the network and can be shown from the panel and from Settings alike.
/// </para>
/// <para>
/// <b>Nothing is installed by opening it.</b> The only control that starts an installation is the accent button of an
/// offered update, and it is never focused by the view itself, so a stray Enter does not approve an update.
/// </para>
/// <para>
/// <b>The notes are redrawn only when they changed.</b> A download reports progress up to ten times a second, and every
/// report renders again; the release notes — up to ten releases of Markdown — are kept as they are unless their text
/// differs, which also keeps the reader's scroll position when a check in the background confirms the same list.
/// </para>
/// <para>UI thread only.</para>
/// </remarks>
public sealed partial class UpdateView : UserControl
{
    /// <summary>
    /// The most Markdown blocks drawn per release. Real notes have a few dozen; the limit keeps notes that are thousands
    /// of lines long from freezing the dialog while it builds an element for each.
    /// </summary>
    private const int MaxBlocksPerRelease = 400;

    /// <summary>How long a button says "Copied" after it copied a command.</summary>
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1.6);

    /// <summary>What the notes area shows when there are no notes to show.</summary>
    private enum NotesState
    {
        /// <summary>Nothing was drawn yet.</summary>
        Unknown,

        /// <summary>Release notes are shown.</summary>
        Notes,

        /// <summary>The release list is being asked for.</summary>
        Loading,

        /// <summary>The release list could not be loaded.</summary>
        Failed,

        /// <summary>This copy does not check by itself and was not asked to yet.</summary>
        NotLoaded,

        /// <summary>The release list is known and holds no release with notes.</summary>
        Empty,
    }

    private NotesState notesState = NotesState.Unknown;

    /// <summary>The changelog whose notes are on screen (compared by text, see <see cref="SameNotes"/>).</summary>
    private Changelog? shownChangelog;

    /// <summary>The last state rendered, so that the "Copied" feedback can put the buttons back.</summary>
    private UpdateSnapshot? lastSnapshot;

    /// <summary>What each button does in the current state; <see langword="null"/> for a hidden button.</summary>
    private Action? primaryAction, secondaryAction, tertiaryAction;

    /// <summary>Whether the skip link skips the offered version (<see langword="true"/>) or stops skipping it.</summary>
    private bool skipLinkSkips = true;

    /// <summary>Where "View on GitHub" leads in the current state.</summary>
    private Uri releasePage = DefaultReleasesPage;

    /// <summary>Puts the buttons back after <see cref="CopiedFeedback"/>.</summary>
    private DispatcherQueueTimer? copiedTimer;

    /// <summary>Creates the view; it shows nothing until <see cref="Render"/>.</summary>
    public UpdateView()
    {
        InitializeComponent();
    }

    /// <summary>The page listing every release, used until the host sets <see cref="ReleasesPage"/>.</summary>
    private static Uri DefaultReleasesPage => UpdateSource.GitHub.ReleasesPage;

    /// <summary>The user approved the offered update ("Update and restart").</summary>
    public event EventHandler? InstallRequested;

    /// <summary>The user declined for now ("Later", "Close"): the host closes the dialog.</summary>
    public event EventHandler? LaterRequested;

    /// <summary>The user asked for a check ("Check now").</summary>
    public event EventHandler? CheckRequested;

    /// <summary>The user cancelled a download in progress ("Cancel").</summary>
    public event EventHandler? CancelRequested;

    /// <summary>
    /// The user chose to skip the offered version (<see langword="true"/>) or to be offered it again
    /// (<see langword="false"/>).
    /// </summary>
    public event EventHandler<bool>? SkipRequested;

    /// <summary>The user clicked a web link: the release's page, the download page, or a link inside the notes.</summary>
    public event EventHandler<Uri>? LinkRequested;

    /// <summary>The user asked for a command on the clipboard (copies that do not update themselves).</summary>
    public event EventHandler<string>? CopyRequested;

    /// <summary>
    /// The page listing every release: where "View on GitHub" and "Download page" lead when no single release is meant.
    /// </summary>
    public Uri ReleasesPage { get; set; } = DefaultReleasesPage;

    /// <summary>
    /// Shows a state. Cheap to call for every progress report: the notes are only rebuilt when their text changed.
    /// </summary>
    /// <param name="snapshot">The state of updates.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public void Render(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lastSnapshot = snapshot;

        // A new state replaces the "Copied" label at once: the buttons below are about the new state.
        copiedTimer?.Stop();

        TitleText.Text = UpdateText.Title(snapshot);
        DetailText.Text = UpdateText.Detail(snapshot, DateTimeOffset.Now);
        RenderProblem(snapshot);
        RenderProgress(snapshot);
        RenderButtons(snapshot);
        RenderNotes(snapshot);
    }

    /// <summary>Scrolls the release notes back to their start (the host calls it when the dialog opens).</summary>
    public void ScrollNotesToTop() => NotesScroller.ChangeView(null, 0, null, disableAnimation: true);

    /// <summary>
    /// Shows why the last installation or the last check failed. An installation problem wins: it is about what the user
    /// just asked for. A check problem is hidden while a new check runs, which may be about to clear it.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    private void RenderProblem(UpdateSnapshot snapshot)
    {
        var install = snapshot.InstallProblem;
        var check = snapshot.Phase == UpdatePhase.Checking ? null : snapshot.CheckProblem;
        if ((install ?? check) is not { } problem)
        {
            ProblemBar.IsOpen = false;
            return;
        }

        // A failed installation is an error; a failed check (usually no connection) only means the list may be old.
        ProblemBar.Severity = install is not null ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        ProblemBar.Message = problem.Message;
        ProblemBar.IsOpen = true;
    }

    /// <summary>Shows the progress bar while something runs: a measured bar for a download of known size, else a moving one.</summary>
    /// <param name="snapshot">The state.</param>
    private void RenderProgress(UpdateSnapshot snapshot)
    {
        if (snapshot.Phase == UpdatePhase.Idle)
        {
            Progress.Visibility = Visibility.Collapsed;
            return;
        }

        bool measured = snapshot.Phase == UpdatePhase.Downloading && snapshot.TotalBytes > 0;
        Progress.IsIndeterminate = !measured;
        Progress.Value = measured ? snapshot.DownloadFraction * 100 : 0;
        Progress.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Sets the decision buttons for the state: approve or decline an offered update (how depends on how this copy was
    /// installed), cancel a download, or check now.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    private void RenderButtons(UpdateSnapshot snapshot)
    {
        primaryAction = secondaryAction = tertiaryAction = null;
        string? primary = null, secondary = null, tertiary = null;
        string? secondaryTip = null, primaryTip = null;
        bool accent = false, primaryEnabled = true, canSkip = false;

        if (snapshot.Phase == UpdatePhase.Installing)
        {
            // The installer owns the update now; nothing here can stop or hurry it.
        }
        else if (snapshot.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying)
        {
            secondary = "Cancel";
            secondaryAction = () => CancelRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (snapshot.Offer is { } offer)
        {
            accent = true;
            canSkip = true;
            switch (snapshot.InstallKind)
            {
                case UpdateInstallKind.Installer:
                    primary = "Update and restart";
                    primaryAction = () => InstallRequested?.Invoke(this, EventArgs.Empty);
                    secondary = "Later";
                    secondaryAction = () => LaterRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case UpdateInstallKind.Chocolatey:
                    primary = "Copy command";
                    primaryTip = UpdateSource.ChocolateyCommand;
                    primaryAction = () => Copy(PrimaryButton, UpdateSource.ChocolateyCommand);
                    secondary = "Later";
                    secondaryAction = () => LaterRequested?.Invoke(this, EventArgs.Empty);
                    break;

                default:
                    // A copy extracted by hand: the new version comes from its release page, or the installer command
                    // installs it properly (and then it updates itself from here on).
                    var page = offer.Release.PageUrl ?? ReleasesPage;
                    primary = "Download page";
                    primaryTip = page.AbsoluteUri;
                    primaryAction = () => LinkRequested?.Invoke(this, page);
                    secondary = "Copy install command";
                    secondaryTip = UpdateSource.InstallCommand;
                    secondaryAction = () => Copy(SecondaryButton, UpdateSource.InstallCommand);
                    tertiary = "Later";
                    tertiaryAction = () => LaterRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        else
        {
            primary = "Check now";
            primaryEnabled = snapshot.Phase != UpdatePhase.Checking;
            primaryAction = () => CheckRequested?.Invoke(this, EventArgs.Empty);
            secondary = "Close";
            secondaryAction = () => LaterRequested?.Invoke(this, EventArgs.Empty);
        }

        ApplyButton(PrimaryButton, primary, primaryTip, primaryEnabled);
        ApplyButton(SecondaryButton, secondary, secondaryTip, enabled: true);
        ApplyButton(TertiaryButton, tertiary, tip: null, enabled: true);

        // Whole styles, so the accent colors resolve in the flyout's own theme.
        PrimaryButton.Style = (Style)Application.Current.Resources[accent ? "AccentButtonStyle" : "DefaultButtonStyle"];
        ButtonRow.Visibility = primary is null && secondary is null && tertiary is null ? Visibility.Collapsed : Visibility.Visible;

        skipLinkSkips = !snapshot.IsSkipped;
        SkipLink.Content = snapshot.IsSkipped ? "Stop skipping this version" : "Skip this version";
        ToolTipService.SetToolTip(SkipLink, snapshot.IsSkipped
            ? "Highlight the update button for this version again."
            : "Do not highlight the update button for this version. The next version highlights it again.");
        SkipLink.Visibility = canSkip ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows or hides one button and sets its words.</summary>
    /// <param name="button">The button.</param>
    /// <param name="text">Its label, or <see langword="null"/> to hide it.</param>
    /// <param name="tip">Its tooltip (the command it copies, the page it opens), or <see langword="null"/> for none.</param>
    /// <param name="enabled">Whether it can be pressed.</param>
    private static void ApplyButton(Button button, string? text, string? tip, bool enabled)
    {
        button.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is null)
        {
            return;
        }

        button.Content = text;
        button.IsEnabled = enabled;
        ToolTipService.SetToolTip(button, tip);
    }

    /// <summary>
    /// Asks the host to copy a command and lets the button say "Copied" for a moment, so the click has a visible answer
    /// (the dialog stays open: the user still has to paste the command somewhere).
    /// </summary>
    /// <param name="button">The button that was pressed.</param>
    /// <param name="command">The command to copy.</param>
    private void Copy(Button button, string command)
    {
        CopyRequested?.Invoke(this, command);
        button.Content = "Copied";
        copiedTimer ??= CreateCopiedTimer();
        copiedTimer.Stop();
        copiedTimer.Start();
    }

    /// <summary>Creates the timer that ends the "Copied" feedback by rendering the last state again.</summary>
    /// <returns>The timer, stopped.</returns>
    private DispatcherQueueTimer CreateCopiedTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = CopiedFeedback;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (lastSnapshot is { } snapshot)
            {
                RenderButtons(snapshot);
            }
        };
        return timer;
    }

    /// <summary>
    /// Shows the release notes of the state's changelog, or says why there are none. Rebuilt only when the notes' text or
    /// the reason changed.
    /// </summary>
    /// <param name="snapshot">The state.</param>
    private void RenderNotes(UpdateSnapshot snapshot)
    {
        var changelog = snapshot.Changelog;
        releasePage = snapshot.Offer?.Release.PageUrl
                      ?? (changelog.Releases.Count > 0 ? changelog.Releases[0].PageUrl : null)
                      ?? ReleasesPage;

        if (changelog.Kind == ChangelogKind.None || changelog.Releases.Count == 0)
        {
            var state = snapshot.Phase == UpdatePhase.Checking ? NotesState.Loading
                : snapshot.CheckProblem is not null ? NotesState.Failed
                : snapshot.LastChecked is null && !snapshot.AutomaticChecks ? NotesState.NotLoaded
                : snapshot.LastChecked is null ? NotesState.Loading
                : NotesState.Empty;
            if (state == notesState)
            {
                return;
            }

            notesState = state;
            shownChangelog = null;
            NotesHeading.Text = "Release notes";
            NotesPanel.Children.Clear();
            NotesPanel.Children.Add(CreateStateLine(state));
            return;
        }

        if (notesState == NotesState.Notes && shownChangelog is not null && SameNotes(shownChangelog, changelog))
        {
            return;
        }

        notesState = NotesState.Notes;
        shownChangelog = changelog;
        NotesHeading.Text = UpdateText.ChangelogHeading(changelog);
        NotesPanel.Children.Clear();
        var styles = new MarkdownStyles(
            (Style)Resources["NotesTextStyle"],
            (Style)Resources["NotesCodeBorderStyle"],
            (Style)Resources["NotesQuoteBorderStyle"],
            (Style)Resources["NotesRuleStyle"],
            (Style)Resources["NotesTableCellStyle"],
            (FontFamily)Application.Current.Resources["MonoFontFamily"]);

        // One release needs no line saying which release it is twice: the dialog's title names it. Several do.
        bool several = changelog.Releases.Count > 1 || changelog.Kind != ChangelogKind.NewerReleases;
        foreach (var release in changelog.Releases)
        {
            if (several)
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text = UpdateText.ReleaseHeading(release),
                    Style = (Style)Resources["NotesReleaseHeadingStyle"],
                    Margin = new Thickness(0, NotesPanel.Children.Count == 0 ? 0 : 10, 0, 0),
                });
            }

            var blocks = MarkdownParser.Parse(release.Notes);
            if (blocks.Count == 0)
            {
                NotesPanel.Children.Add(SecondaryLine("This release has no notes."));
                continue;
            }

            int leftOut = MarkdownRenderer.Append(NotesPanel, blocks, styles, link => LinkRequested?.Invoke(this, link), MaxBlocksPerRelease);
            if (leftOut > 0)
            {
                NotesPanel.Children.Add(SecondaryLine("These notes are long: the rest is on GitHub."));
            }
        }

        if (changelog.OlderReleasesLeftOut > 0)
        {
            int older = changelog.OlderReleasesLeftOut;
            NotesPanel.Children.Add(SecondaryLine($"{older} older release{(older == 1 ? string.Empty : "s")} in between: see GitHub."));
        }

        ScrollNotesToTop();
    }

    /// <summary>
    /// Whether two changelogs would draw the same notes: same kind, same releases in the same order, same text. A check
    /// that confirms the list hands over new objects with the old text; redrawing them would throw the reader back to
    /// the top.
    /// </summary>
    /// <param name="a">One changelog.</param>
    /// <param name="b">The other.</param>
    /// <returns><see langword="true"/> when nothing visible differs.</returns>
    private static bool SameNotes(Changelog a, Changelog b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Kind != b.Kind || a.OlderReleasesLeftOut != b.OlderReleasesLeftOut || a.Releases.Count != b.Releases.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Releases.Count; i++)
        {
            var x = a.Releases[i];
            var y = b.Releases[i];
            if (x.Version != y.Version || x.PublishedAt != y.PublishedAt || !string.Equals(x.Notes, y.Notes, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Creates the line shown instead of release notes.</summary>
    /// <param name="state">Why there are no notes.</param>
    /// <returns>The element.</returns>
    private FrameworkElement CreateStateLine(NotesState state)
    {
        if (state != NotesState.Loading)
        {
            return SecondaryLine(state switch
            {
                NotesState.Failed => "The release notes could not be loaded. Check again when you are online, or read them on GitHub.",
                NotesState.NotLoaded => "Check now loads the release notes from GitHub.",
                _ => "No release notes are published yet.",
            });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        row.Children.Add(SecondaryLine("Loading the release notes from GitHub…"));
        return row;
    }

    /// <summary>Creates a line of secondary text for the notes area.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text block.</returns>
    private TextBlock SecondaryLine(string text) => new() { Text = text, Style = (Style)Resources["NotesSecondaryTextStyle"] };

    /// <summary>Runs the primary button's action for the current state.</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Event data.</param>
    private void PrimaryButton_Click(object sender, RoutedEventArgs e) => primaryAction?.Invoke();

    /// <summary>Runs the secondary button's action for the current state.</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Event data.</param>
    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => secondaryAction?.Invoke();

    /// <summary>Runs the third button's action for the current state.</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">Event data.</param>
    private void TertiaryButton_Click(object sender, RoutedEventArgs e) => tertiaryAction?.Invoke();

    /// <summary>Skips the offered version, or stops skipping it.</summary>
    /// <param name="sender">The link.</param>
    /// <param name="e">Event data.</param>
    private void SkipLink_Click(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(this, skipLinkSkips);

    /// <summary>Opens the release's page (or the list of releases) in the browser, through the host.</summary>
    /// <param name="sender">The link.</param>
    /// <param name="e">Event data.</param>
    private void ReleasePageLink_Click(object sender, RoutedEventArgs e) => LinkRequested?.Invoke(this, releasePage);
}
