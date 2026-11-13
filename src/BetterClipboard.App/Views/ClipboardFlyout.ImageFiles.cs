using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Windows.Shell;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.Views;

/// <summary>
/// A picture's file in the panel (CLAUDE.md §2.49): the card menu's file items and the image viewer's details band.
/// </summary>
/// <remarks>
/// <para>
/// <b>The menu</b> (the owner's request, 2026-11-13): a picture whose file is known gets a dimmed line naming the file (its
/// format, size and last change, or "moved or deleted"), then — only while the file is on disk — <em>Copy path</em>,
/// <em>Copy image as a file</em>, <em>Open file</em> and <em>Open folder</em>; every picture gets <em>Save to temp and ›</em>
/// with the same four items acting on a temporary PNG written from the history first. Whether the file is there is asked
/// when the menu opens (<see cref="FileOnDisk.Check"/>: local drives are checked, a network path never is) and again when an
/// item is chosen, since the file may go while the menu is open; a file found gone keeps the panel up with a word in the
/// footer.
/// </para>
/// <para>
/// <b>The viewer</b> (the owner's choice: "Both", with "must fit to the left of the image or above it cleanly, less rows. and
/// path and file are clickable"): two rows above the picture — the file's name and facts, the folder and dates — that the
/// picture never covers (the scroll host starts below them, <see cref="viewerTopReserve"/>); the name opens the file, the
/// folder opens it in File Explorer, while the file is there.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>Segoe Fluent Icons E62F (a box holding "\\.."), <em>Copy path</em>; checked by rendering.</summary>
    private const string CopyPathGlyph = "\uE62F";

    /// <summary>Segoe Fluent Icons E8A5 (Document), <em>Copy image as a file</em>, the glyph of a file elsewhere in the panel.</summary>
    private const string CopyAsFileGlyph = "\uE8A5";

    /// <summary>Segoe Fluent Icons E8E5 (OpenFile), <em>Open file</em>, as the Everything tab's <em>Open</em>.</summary>
    private const string OpenFileGlyph = "\uE8E5";

    /// <summary>Segoe Fluent Icons E838 (FolderOpen), <em>Open folder</em>; checked by rendering.</summary>
    private const string OpenFolderGlyph = "\uE838";

    /// <summary>Segoe Fluent Icons E78C (SaveLocal: a disk with an arrow), <em>Save to temp and</em>; checked by rendering.</summary>
    private const string SaveToTempGlyph = "\uE78C";

    /// <summary>The viewer band's distance from the screen's top and the gap to the picture below it, in DIPs.</summary>
    private const double ViewerBandMargin = 16;

    /// <summary>How far the band stays from the screen's sides: clear of the close button at the top right (40 DIPs plus its margin).</summary>
    private const double ViewerBandSideMargin = 72;

    /// <summary>
    /// The height, in DIPs, the open viewer keeps free above its scroll host for the file details band (0 without one), so
    /// the picture never covers the band, zoomed or not. Set by <see cref="ShowViewerAsync"/>, read when the picture is laid out.
    /// </summary>
    private double viewerTopReserve;

    /// <summary>What a file item does with its file.</summary>
    private enum ImageFileAction
    {
        /// <summary>Put the path on the clipboard as text.</summary>
        CopyPath,

        /// <summary>Put the file on the clipboard (<c>CF_HDROP</c>, like Explorer's Copy).</summary>
        CopyFile,

        /// <summary>Open the file with its default app.</summary>
        OpenFile,

        /// <summary>Open its folder in File Explorer with the file selected.</summary>
        OpenFolder,
    }

    /// <summary>
    /// Adds a picture card's file items to its menu (see the class remarks); nothing for any other card.
    /// </summary>
    /// <param name="menu">The card's menu, being built.</param>
    /// <param name="item">The card.</param>
    private void AddImageFileItems(MenuFlyout menu, ClipItemViewModel item)
    {
        if (item.Kind != ClipKind.Image || item.Pick is not null || item.Command is not null || item.Prompt is not null)
        {
            return;
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        if (item.Entry.File is { } file)
        {
            // Asked now, while the menu opens: a local drive answers in microseconds, a network path is not asked at all.
            bool there = FileOnDisk.Check(file.Path) != FilePresence.Missing;
            var label = new MenuFlyoutItem { Text = SourceFileText.MenuLabel(file, there, DateTimeOffset.UtcNow), IsEnabled = false };
            AutomationProperties.SetName(label, label.Text.Replace('\n', ' '));
            menu.Items.Add(label);
            if (there)
            {
                AddFileActions(menu.Items, item, () => Task.FromResult<string?>(file.Path), original: true);
            }
        }

        var temp = new MenuFlyoutSubItem { Text = UiText.Translate("Save to temp and"), Icon = new FontIcon { Glyph = SaveToTempGlyph } };
        AddFileActions(temp.Items, item, () => SaveToTempAsync(item), original: false);
        menu.Items.Add(temp);
    }

    /// <summary>Adds the four file items (<see cref="ImageFileAction"/>) to a menu or submenu.</summary>
    /// <param name="items">Where they go.</param>
    /// <param name="item">The card.</param>
    /// <param name="file">Gives the file to act on: the picture's own, or a temporary copy written when an item is chosen.</param>
    /// <param name="original">Whether it is the picture's own file (checked again when chosen: it may be gone by then).</param>
    private void AddFileActions(IList<MenuFlyoutItemBase> items, ClipItemViewModel item, Func<Task<string?>> file, bool original)
    {
        items.Add(MenuItem(UiText.Translate("Copy path"), CopyPathGlyph, null, () => _ = ActOnFileAsync(item, file, ImageFileAction.CopyPath, original)));
        items.Add(MenuItem(UiText.Translate("Copy image as a file"), CopyAsFileGlyph, null, () => _ = ActOnFileAsync(item, file, ImageFileAction.CopyFile, original)));
        items.Add(MenuItem(UiText.Translate("Open file"), OpenFileGlyph, null, () => _ = ActOnFileAsync(item, file, ImageFileAction.OpenFile, original)));
        items.Add(MenuItem(UiText.Translate("Open folder"), OpenFolderGlyph, null, () => _ = ActOnFileAsync(item, file, ImageFileAction.OpenFolder, original)));
    }

    /// <summary>
    /// Runs a file item: gets the file (writing the temporary copy first for <em>Save to temp and</em>), makes sure the
    /// picture's own file is still there, and acts. A failure keeps the panel up with the reason in its footer.
    /// </summary>
    /// <param name="item">The card.</param>
    /// <param name="file">Gives the file (or <see langword="null"/> after reporting why there is none).</param>
    /// <param name="action">What to do.</param>
    /// <param name="original">Whether it is the picture's own file.</param>
    /// <returns>A task completing when done; failures are reported in the footer and logged, never thrown (fire-and-forget safe).</returns>
    private async Task ActOnFileAsync(ClipItemViewModel item, Func<Task<string?>> file, ImageFileAction action, bool original)
    {
        try
        {
            if (await file() is not { } path)
            {
                return;
            }

            // The file may have gone since the menu opened (deleted, moved, its drive removed).
            if (original && FileOnDisk.Check(path) == FilePresence.Missing)
            {
                ShowFileProblem(UiText.Translate("The file was moved or deleted."));
                return;
            }

            string? problem = action switch
            {
                ImageFileAction.CopyPath or ImageFileAction.CopyFile =>
                    await controller.CopyImageFileAsync(item.Entry, path, asText: action == ImageFileAction.CopyPath)
                        ? null
                        : UiText.Translate("Could not put it on the clipboard."),
                ImageFileAction.OpenFile => await controller.OpenImageFileAsync(path),
                _ => await controller.OpenImageFolderAsync(path),
            };
            if (problem is not null)
            {
                ShowFileProblem(problem);
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.AppLog.Warn($"A file action on entry {item.Id} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the card's picture to the temporary pictures folder for <em>Save to temp and</em> (see
    /// <see cref="AppController.SaveImageToTempAsync"/>); reports in the footer when it cannot.
    /// </summary>
    /// <param name="item">The card.</param>
    /// <returns>The saved file's path, or <see langword="null"/> when it could not be saved (reported).</returns>
    private async Task<string?> SaveToTempAsync(ClipItemViewModel item)
    {
        var saved = await controller.SaveImageToTempAsync(item.Entry);
        if (saved.Path is null)
        {
            ShowFileProblem(saved.Problem ?? UiText.Translate("This image could not be loaded."));
        }

        return saved.Path;
    }

    /// <summary>Puts a file item's failure in the footer while the panel is up (a closed panel has none to show).</summary>
    /// <param name="problem">The words (translated).</param>
    private void ShowFileProblem(string problem)
    {
        if (IsOpen)
        {
            ViewModel.StatusText = problem;
        }
    }

    /// <summary>
    /// Builds the image viewer's file details band: two rows above the picture — the file's name (a link that opens the file)
    /// with its format, size and pixel size; its folder (a link that opens it with the file selected) with its dates. Without
    /// the file on disk the name and folder are plain text and the first row says it was moved or deleted.
    /// </summary>
    /// <param name="card">The picture's card.</param>
    /// <param name="file">Its file.</param>
    /// <param name="maxWidth">The widest the band may be, in DIPs (the screen's width less its side margins).</param>
    /// <returns>The band, not yet measured or placed.</returns>
    private Border BuildFileDetailsBand(ClipItemViewModel card, SourceFile file, double maxWidth)
    {
        bool there = FileOnDisk.Check(file.Path) != FilePresence.Missing;
        var facts = SourceFileText.Facts(file, card.Entry.ImageWidth, card.Entry.ImageHeight).ToList();
        if (!there)
        {
            facts.Add(UiText.Translate("moved or deleted"));
        }

        var dates = SourceFileText.Dates(file, TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);

        // The links take at most 60% of the band, trimmed at their end, so the facts after them always stay readable.
        double linkWidth = Math.Max(120, (maxWidth - 24) * 0.6);
        var rows = new StackPanel { Spacing = 2 };
        rows.Children.Add(DetailsRow(file.Name, there ? () => _ = OpenFromViewerAsync(file.Path, folder: false) : null, UiText.Translate("Open file"), facts, linkWidth, bold: true));
        rows.Children.Add(DetailsRow(file.Folder ?? file.Path, there ? () => _ = OpenFromViewerAsync(file.Path, folder: true) : null, UiText.Translate("Open folder"), dates, linkWidth, bold: false));

        var band = new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xA6, 0, 0, 0)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8, 14, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(ViewerBandSideMargin, ViewerBandMargin, ViewerBandSideMargin, 0),
            MaxWidth = maxWidth,
            Child = rows,
        };

        // A tap on the band must not reach the dim backdrop, whose tap closes the viewer.
        band.Tapped += (_, e) => e.Handled = true;
        return band;
    }

    /// <summary>One row of the viewer's details band: a name or folder (a link when it can be opened), then facts after it.</summary>
    /// <param name="text">The name or folder.</param>
    /// <param name="open">What a click on it does, or <see langword="null"/> for plain text (the file is gone).</param>
    /// <param name="openLabel">The link's tooltip and accessible help ("Open file", "Open folder").</param>
    /// <param name="facts">The pieces after it, joined with " · ".</param>
    /// <param name="linkWidth">The widest the name or folder may be before it is trimmed, in DIPs.</param>
    /// <param name="bold">Whether the name is set in semibold (the first row's file name).</param>
    /// <returns>The row.</returns>
    private static StackPanel DetailsRow(string text, Action? open, string openLabel, IReadOnlyList<string> facts, double linkWidth, bool bold)
    {
        var lead = new TextBlock
        {
            FontSize = 12.5,
            MaxWidth = linkWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(Colors.White),
        };
        if (open is not null)
        {
            // A light link on the dark band (the theme's accent would be too dark on it in the light theme).
            var link = new Hyperlink { Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x9F, 0xD8, 0xFF)), UnderlineStyle = UnderlineStyle.None };
            link.Inlines.Add(new Run { Text = text });
            link.Click += (_, _) => open();
            lead.Inlines.Add(link);
            ToolTipService.SetToolTip(lead, openLabel);
            AutomationProperties.SetHelpText(lead, openLabel);
        }
        else
        {
            lead.Text = text;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(lead);
        if (facts.Count > 0)
        {
            row.Children.Add(new TextBlock
            {
                FontSize = 12.5,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0xD0, 0xD0, 0xD0)),
                Text = " · " + UiText.JoinLine(" · ", facts),
            });
        }

        return row;
    }

    /// <summary>
    /// A click on the viewer's name or folder: closes the viewer and opens the file or its folder (the panel then goes away);
    /// a file gone meanwhile, or a failure, is reported in the panel's footer instead.
    /// </summary>
    /// <remarks>
    /// The start is awaited, never run inside the link's Click: Windows' shell ran a message loop of its own while it
    /// activated Photos, and WinUI ended the process from inside that click (the controller's
    /// <see cref="AppController.OpenImageFileAsync"/> remarks have the crash dump's evidence).
    /// </remarks>
    /// <param name="path">The picture's file.</param>
    /// <param name="folder">Open its folder with it selected, instead of the file.</param>
    /// <returns>A task completing when it was opened or reported; failures are logged, never thrown (fire-and-forget safe).</returns>
    private async Task OpenFromViewerAsync(string path, bool folder)
    {
        try
        {
            CloseViewer();
            if (FileOnDisk.Check(path) == FilePresence.Missing)
            {
                ShowFileProblem(UiText.Translate("The file was moved or deleted."));
                return;
            }

            if ((folder ? await controller.OpenImageFolderAsync(path) : await controller.OpenImageFileAsync(path)) is { } problem)
            {
                ShowFileProblem(problem);
            }
        }
        catch (Exception ex)
        {
            // Content-free: never the path.
            Core.Diagnostics.AppLog.Warn($"Opening a picture's file from the image viewer failed: {ex.Message}");
        }
    }
}
