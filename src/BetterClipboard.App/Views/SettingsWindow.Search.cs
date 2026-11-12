using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using BetterClipboard.App.Controls;
using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Localization;
using BetterClipboard.Core.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BetterClipboard.App.Views;

/// <summary>
/// Settings' search box (CLAUDE.md §2.43; the owner's request, 2026-11-09: "search to settings which fuzzly match to
/// anything of that setting", and the choice "Filter the page"): while there is text, the page keeps only the settings that
/// match, in their usual order under their section titles, with the matched letters highlighted in titles and
/// descriptions, and a count next to the box.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a setting is searched by:</b> its title, description, section and translator-written keywords, and everything
/// else on its card — button and option texts, status lines, list rows (<see cref="ISearchableItem"/>), accessible names
/// and placeholders — plus the English those were translated from (<see cref="UiText.EnglishOf"/>). Switches' On/Off are
/// left out: "on" would find every card. The matching itself is Core's <see cref="SettingsSearch"/>.
/// </para>
/// <para>
/// <b>When the texts are read:</b> once, then again after a status, a list or a setting changed (the view model's change
/// events mark the documents stale). While a search shows, a change searches again after a quarter second, keeping the
/// scroll position; a new query scrolls to the top.
/// </para>
/// <para>
/// <b>The page's structure is the index:</b> in the page's panel, section titles are the only <see cref="TextBlock"/>
/// children and settings are the <see cref="SettingsCard"/> children, each belonging to the title before it. A new
/// section or card needs nothing else to be searchable.
/// </para>
/// </remarks>
public sealed partial class SettingsWindow
{
    /// <summary>One setting of the page: its card, its section's title and its folded texts.</summary>
    /// <param name="Card">The card.</param>
    /// <param name="Section">The title of its section, or <see langword="null"/> above the first one.</param>
    /// <param name="Document">Its searchable texts (title at index 0, description at 1: what gets highlighted).</param>
    private sealed record SearchEntry(SettingsCard Card, TextBlock? Section, SearchDocument Document);

    /// <summary>The page's settings with their texts, or <see langword="null"/> when they must be read again.</summary>
    private List<SearchEntry>? searchIndex;

    /// <summary>Where the page was scrolled when the search started, to go back there when it ends.</summary>
    private double? offsetBeforeSearch;

    /// <summary>Searches again a quarter second after the last change of a status or setting while a search shows.</summary>
    private DispatcherQueueTimer? searchRefresh;

    /// <summary>The highlight's background (the accent, translucent), made once.</summary>
    private Brush? highlightBrush;

    /// <summary>
    /// Wires the search to the view model (created in the constructor): any change of a property or a list marks the
    /// settings' texts stale.
    /// </summary>
    private void InitializeSearch()
    {
        searchRefresh = DispatcherQueue.CreateTimer();
        searchRefresh.Interval = TimeSpan.FromMilliseconds(250);
        searchRefresh.IsRepeating = false;
        searchRefresh.Tick += SearchRefresh_Tick;
        ViewModel.PropertyChanged += OnViewModelChangedForSearch;
        ViewModel.Hotkeys.CollectionChanged += OnListChangedForSearch;
        ViewModel.Forgotten.CollectionChanged += OnListChangedForSearch;
    }

    /// <summary>
    /// Stops the search's timer and unsubscribes (window closing), the timer's <c>Tick</c> included.
    /// </summary>
    /// <remarks>
    /// The <c>Tick</c> must go, not only the timer stop: a <see cref="DispatcherQueueTimer"/> is a plain WinRT object that
    /// WinUI's reference tracking does not see through, so its handler (which holds this window) and this window's
    /// reference to the timer form a cycle across the native boundary that no garbage collection can break. Measured
    /// 2026-11-12: every Settings window ever closed stayed in memory with its whole XAML tree, 17 to 22 MB each
    /// (<see cref="OnClosed"/>).
    /// </remarks>
    private void CloseSearch()
    {
        if (searchRefresh is { } timer)
        {
            timer.Stop();
            timer.Tick -= SearchRefresh_Tick;
        }

        ViewModel.PropertyChanged -= OnViewModelChangedForSearch;
        ViewModel.Hotkeys.CollectionChanged -= OnListChangedForSearch;
        ViewModel.Forgotten.CollectionChanged -= OnListChangedForSearch;
    }

    /// <summary>The quarter second after the last change passed while a search shows: search again, keeping the position.</summary>
    /// <param name="sender">The timer.</param>
    /// <param name="args">Nothing.</param>
    private void SearchRefresh_Tick(DispatcherQueueTimer sender, object args) => ApplySearch(scrollToTop: false);

    /// <summary>A status or setting changed: its card's texts may have; search again soon if a search shows.</summary>
    /// <param name="sender">The view model.</param>
    /// <param name="e">Which property.</param>
    private void OnViewModelChangedForSearch(object? sender, PropertyChangedEventArgs e) => MarkSearchStale();

    /// <summary>A shortcut or forgotten row came or went: search again soon if a search shows.</summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">The change.</param>
    private void OnListChangedForSearch(object? sender, NotifyCollectionChangedEventArgs e) => MarkSearchStale();

    /// <summary>Drops the read texts and, while a search shows, searches again after a quarter second (changes come in bursts).</summary>
    private void MarkSearchStale()
    {
        searchIndex = null;
        if (!SettingsSearchQuery.Parse(SearchBox.Text).IsEmpty)
        {
            searchRefresh?.Stop();
            searchRefresh?.Start();
        }
    }

    /// <summary>
    /// Turns the spelling checker and text prediction off in the box's inner text box: product names a search is typed for
    /// ("claude", "sharex", "pwsh") were underlined in red as misspelled, which reads as an error in a search box (the test
    /// desktop, 2026-11-09). Done in code, not with a replaced <see cref="AutoSuggestBox.TextBoxStyle"/>: a style built on
    /// WinUI's own key would fail to load the whole window if a later WinUI renamed it, while this only finds nothing.
    /// </summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">Event data.</param>
    private void SearchBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (FindDescendant<TextBox>(SearchBox) is { } box)
        {
            box.IsSpellCheckEnabled = false;
            box.IsTextPredictionEnabled = false;
        }
    }

    /// <summary>Depth-first search of the visual tree.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="root">Where to start.</param>
    /// <returns>The first descendant of type <typeparamref name="T"/>, or <see langword="null"/>.</returns>
    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>The box's text changed (typing, the clear button, Esc): filter the page.</summary>
    /// <param name="sender">The search box.</param>
    /// <param name="args">Why it changed (every reason filters).</param>
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplySearch(scrollToTop: true);

    /// <summary>
    /// Esc in the box clears the search (the whole page comes back where it was); with the box already empty it hands the
    /// keyboard back to the page. Handled before the box's own Esc, which only closes a suggestion list it never has.
    /// </summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">The key.</param>
    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = string.Empty;
        }
        else
        {
            Scroller.Focus(FocusState.Keyboard);
        }
    }

    /// <summary>Ctrl+F or Ctrl+E anywhere in the window: into the search box.</summary>
    /// <param name="sender">The accelerator.</param>
    /// <param name="args">Marked handled, so a text box under the focus does not also act on it.</param>
    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Filters the page to the query in the box: matching settings stay (highlighted), the others and empty sections go,
    /// the hero hides, and the count shows. An empty query brings everything back.
    /// </summary>
    /// <param name="scrollToTop">Whether the results start at the top (a new query) or keep the position (a refresh).</param>
    private void ApplySearch(bool scrollToTop)
    {
        var query = SettingsSearchQuery.Parse(SearchBox.Text);
        if (query.IsEmpty)
        {
            EndSearch();
            return;
        }

        offsetBeforeSearch ??= Scroller.VerticalOffset;
        searchIndex ??= BuildSearchIndex();
        var brush = HighlightBrush();
        int matches = 0;
        var shownSections = new HashSet<TextBlock>();

        // Every setting is matched first: whether a fuzzy match shows depends on whether any setting has the words as
        // written (SettingsSearch.Shown).
        var results = searchIndex.Select(entry => SettingsSearch.Match(query, entry.Document)).ToArray();
        var shown = SettingsSearch.Shown(results);
        for (int i = 0; i < searchIndex.Count; i++)
        {
            var entry = searchIndex[i];
            var match = shown[i] ? results[i] : null;
            entry.Card.Visibility = match is null ? Visibility.Collapsed : Visibility.Visible;
            entry.Card.ApplySearchHighlights(match?.Spans[0] ?? [], match?.Spans[1] ?? [], match is null ? null : brush);
            if (match is not null)
            {
                matches++;
                if (entry.Section is { } section)
                {
                    shownSections.Add(section);
                }
            }
        }

        foreach (var child in SettingsPage.Children)
        {
            if (child is TextBlock section)
            {
                section.Visibility = shownSections.Contains(section) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        Hero.Visibility = Visibility.Collapsed;
        NoMatches.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatchesText.Text = UiText.Format($"No setting matches “{SearchBox.Text.Trim()}”.");
        Announce(matches == 0
            ? UiText.Translate("No matches")
            : UiText.Plural(matches, $"{matches:N0} setting matches", $"{matches:N0} settings match"));
        if (scrollToTop)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    /// <summary>Shows the whole page again, without highlights, scrolled back to where the search started.</summary>
    private void EndSearch()
    {
        searchRefresh?.Stop();
        foreach (var child in SettingsPage.Children)
        {
            if (child is SettingsCard card)
            {
                card.Visibility = Visibility.Visible;
                card.ApplySearchHighlights([], [], null);
            }
            else if (child is TextBlock section)
            {
                section.Visibility = Visibility.Visible;
            }
        }

        Hero.Visibility = Visibility.Visible;
        NoMatches.Visibility = Visibility.Collapsed;
        Announce(string.Empty);
        if (offsetBeforeSearch is { } offset)
        {
            offsetBeforeSearch = null;

            // The page just grew back: lay it out first, or the scroller clamps the offset to the short results page.
            Scroller.UpdateLayout();
            Scroller.ChangeView(null, offset, null, disableAnimation: true);
        }
    }

    /// <summary>
    /// Keeps the page as wide as the window allows, up to its column's <c>MaxWidth</c>, whatever it shows. The scroller
    /// lays its content out at the content's own width and centers it: with every card shown that is the whole column, but
    /// a search that leaves only short texts — or none, with the two "No setting matches" lines — made the page as narrow
    /// as those texts and pushed them to the middle of the window (seen in the test desktop, 2026-11-09).
    /// </summary>
    /// <param name="sender">The scroller.</param>
    /// <param name="e">Its new size.</param>
    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Min(SettingsPage.MaxWidth, e.NewSize.Width - Scroller.Padding.Left - Scroller.Padding.Right);

        // Width starts as NaN (auto), which no comparison catches; a half-pixel change is not worth another layout pass.
        if (width > 0 && (double.IsNaN(SettingsPage.Width) || Math.Abs(SettingsPage.Width - width) > 0.5))
        {
            SettingsPage.Width = width;
        }
    }

    /// <summary>Shows the count next to the box and tells a screen reader (a polite live region).</summary>
    /// <param name="text">"4 settings match", "No matches", or empty.</param>
    private void Announce(string text)
    {
        if (SearchCount.Text == text)
        {
            return;
        }

        SearchCount.Text = text;
        if (text.Length > 0 && (FrameworkElementAutomationPeer.FromElement(SearchCount) ?? FrameworkElementAutomationPeer.CreatePeerForElement(SearchCount)) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>
    /// The highlight's background: the system accent at about a third of its strength, so the matched letters stay legible
    /// in both themes (dark text on a light tint, light text on a dark one) without a per-theme brush.
    /// </summary>
    /// <returns>The brush.</returns>
    private Brush HighlightBrush()
    {
        if (highlightBrush is null)
        {
            var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is global::Windows.UI.Color color
                ? color
                : global::Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4); // Windows' default blue, should the resource be missing
            highlightBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x5C, accent.R, accent.G, accent.B));
        }

        return highlightBrush;
    }

    /// <summary>Reads every setting of the page with its section and texts (see the class remarks).</summary>
    /// <returns>The settings in page order.</returns>
    private List<SearchEntry> BuildSearchIndex()
    {
        var entries = new List<SearchEntry>();
        TextBlock? section = null;
        foreach (var child in SettingsPage.Children)
        {
            if (child is TextBlock header)
            {
                section = header;
            }
            else if (child is SettingsCard card)
            {
                entries.Add(new SearchEntry(card, section, DocumentOf(card, section)));
            }
        }

        return entries;
    }

    /// <summary>
    /// A setting's searchable texts: title (index 0) and description (index 1) first — what gets highlighted — then its
    /// section, keywords and everything else on the card, then the English of every translated text (not highlighted).
    /// </summary>
    /// <param name="card">The card.</param>
    /// <param name="section">Its section's title.</param>
    /// <returns>The document.</returns>
    private static SearchDocument DocumentOf(SettingsCard card, TextBlock? section)
    {
        var fields = new List<SearchField>
        {
            new(SearchFieldKind.Header, card.Header ?? string.Empty),
            new(SearchFieldKind.Description, card.Description ?? string.Empty),
        };
        if (section?.Text is { Length: > 0 } sectionText)
        {
            fields.Add(new SearchField(SearchFieldKind.Section, sectionText));
        }

        if (card.Keywords is { Length: > 0 } keywords)
        {
            fields.Add(new SearchField(SearchFieldKind.Keywords, keywords));
        }

        var others = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { card.Header ?? string.Empty, card.Description ?? string.Empty };
        CollectTexts(card.Content, others, seen, depth: 0);
        CollectTexts(card.Footer, others, seen, depth: 0);
        fields.AddRange(others.Select(text => new SearchField(SearchFieldKind.Other, text)));

        // The English of every translated text, at its text's weight: "shortcut" finds 快捷方式's card.
        foreach (var field in fields.ToArray())
        {
            if (UiText.EnglishOf(field.Text) is { } english)
            {
                fields.Add(field with { Text = english });
            }
        }

        return new SearchDocument(fields);
    }

    /// <summary>
    /// Gathers the texts an element shows or names, recursively: text blocks and boxes, button and option contents,
    /// list rows, tooltips, accessible names. Switches and number boxes give nothing (their "On", "Off" and digits would
    /// find every card).
    /// </summary>
    /// <param name="node">An element, a content object or a string.</param>
    /// <param name="into">Receives each new text once.</param>
    /// <param name="seen">The texts gathered so far (also the card's title and description), to skip repeats.</param>
    /// <param name="depth">How deep the walk is; it stops at 16 levels, far below any card's real depth.</param>
    private static void CollectTexts(object? node, List<string> into, HashSet<string> seen, int depth)
    {
        if (node is null || depth > 16)
        {
            return;
        }

        void Add(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text) && seen.Add(text))
            {
                into.Add(text);
            }
        }

        switch (node)
        {
            case string text:
                Add(text);
                return;
            case ISearchableItem row:
                foreach (var text in row.SearchTexts)
                {
                    Add(text);
                }

                return;
            case ToggleSwitch or NumberBox:
                return;
        }

        if (node is UIElement element)
        {
            Add(AutomationProperties.GetName(element));
            Add(AutomationProperties.GetHelpText(element));
            if (ToolTipService.GetToolTip(element) is string tip)
            {
                Add(tip);
            }
        }

        switch (node)
        {
            case TextBlock block:
                Add(block.Text);
                break;
            case TextBox box:
                Add(box.Text);
                Add(box.PlaceholderText);
                break;
            case ItemsControl list:
                // Options and rows from the list's source (a combo box shows only its chosen one; rows scrolled out of a
                // bounded list are not drawn, but belong to the setting all the same).
                foreach (var item in (list.ItemsSource as IEnumerable) ?? list.Items)
                {
                    CollectTexts(item is ContentControl option ? option.Content : item, into, seen, depth + 1);
                }

                break;
            case ContentControl control:
                CollectTexts(control.Content, into, seen, depth + 1);
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    CollectTexts(child, into, seen, depth + 1);
                }

                break;
            case Border border:
                CollectTexts(border.Child, into, seen, depth + 1);
                break;
        }
    }
}
