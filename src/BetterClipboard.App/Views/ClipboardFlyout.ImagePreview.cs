using System.Runtime.InteropServices.WindowsRuntime;
using BetterClipboard.App.Interop;
using BetterClipboard.App.ViewModels;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Presentation;
using BetterClipboard.Windows.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BetterClipboard.App.Views;

/// <summary>
/// The image overlays on the panel's cards: a hover <b>peek</b> (rest the mouse on an image to see it big; it is gone the
/// instant the mouse moves, like a tooltip) and the eye-icon <b>viewer</b> (a full-screen window with mouse-wheel zoom and
/// drag-to-pan, open until closed). Both are shown as windowed popups (<see cref="Popup.ShouldConstrainToRootBounds"/>
/// false) sized to the whole monitor work area, so they are not clipped to the small panel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why popups, not a second window.</b> A windowed popup lives in the panel's own <c>XamlRoot</c> (its theme, brushes
/// and dispatcher all apply), and <see cref="VisualTreeHelper.GetOpenPopupsForXamlRoot"/> counts it like the context menus,
/// so the existing key model (<see cref="IsPopupKey"/>) and the "don't hide while a popup is open" rule
/// (<see cref="openPopups"/>) cover it for free. A windowed popup can extend beyond the panel onto the whole screen.
/// </para>
/// <para>
/// <b>Peek dismissal.</b> The peek's surface covers the monitor and is hit-testable, so the first real mouse move over it
/// (after a tiny grace that swallows the synthetic move at open) dismisses it — "any mouse movement hides it". After it
/// hides, the pointer is back over the card, whose <see cref="ImageCard_PointerEntered"/> restarts the rest timer, so the
/// peek reappears if the mouse settles again. A key or a press dismisses it too.
/// </para>
/// <para>
/// <b>Resolution.</b> Both overlays show the full-resolution image (<see cref="AppController.GetImagePngAsync"/>), not the
/// small card thumbnail, so a big screenshot is crisp and zooms in cleanly; the last-shown image is cached so a peek
/// followed by the viewer (or repeated peeks of one card) is instant. The load is kicked off on hover, so the full image
/// is usually ready by the time the peek's delay elapses or the eye is clicked.
/// </para>
/// </remarks>
public sealed partial class ClipboardFlyout
{
    /// <summary>How long the mouse must rest on an image before the peek opens (tooltip-like), in milliseconds.</summary>
    private const double HoverPeekDelayMs = 450;

    /// <summary>
    /// How long after the peek opens a mouse move is ignored, in milliseconds: just enough to swallow the synthetic move
    /// that can arrive as the covering surface appears under the stationary cursor, so the peek is not dismissed at birth.
    /// </summary>
    private const long PeekGraceMs = 100;

    /// <summary>Zoom multiplier per mouse-wheel notch in the viewer (one notch = ×1.2 in, ÷1.2 out).</summary>
    private const double WheelZoomPerNotch = 1.2;

    /// <summary>The smallest zoom factor the viewer allows, so an image can never shrink to nothing.</summary>
    private const double MinZoomFloor = 0.1;

    /// <summary>How far past "fit" (or 100%, whichever is larger) the viewer lets the user zoom in.</summary>
    private const double MaxZoomMultiple = 8;

    /// <summary>The card whose image the pointer is currently resting on (drives the peek), or <see langword="null"/>.</summary>
    private ClipItemViewModel? hoverCard;

    /// <summary>Rest timer for the hover peek (created lazily, reused).</summary>
    private DispatcherTimer? hoverTimer;

    /// <summary>The open hover-peek popup, or <see langword="null"/>.</summary>
    private Popup? peekPopup;

    /// <summary><see cref="Environment.TickCount64"/> when the peek opened, for the <see cref="PeekGraceMs"/> window.</summary>
    private long peekShownAt;

    /// <summary>The open zoom/pan viewer popup, or <see langword="null"/>.</summary>
    private Popup? viewerPopup;

    /// <summary>The viewer's scroll host (pan + zoom); valid only while <see cref="viewerPopup"/> is open and loaded.</summary>
    private ScrollViewer? viewerScroll;

    /// <summary>The viewer's zoom-percentage label; valid only while the viewer is loaded.</summary>
    private TextBlock? viewerZoomLabel;

    /// <summary>The viewer's "fit to window" zoom factor, used by the double-click toggle and as the lower zoom anchor.</summary>
    private double viewerFitZoom = 1;

    /// <summary>The viewer's smallest and largest allowed zoom factors (derived from the fit factor on open).</summary>
    private double viewerMinZoom = MinZoomFloor, viewerMaxZoom = MaxZoomMultiple;

    /// <summary>Whether a drag-pan is in progress in the viewer.</summary>
    private bool viewerPanning;

    /// <summary>Pointer position (in the scroll host) and scroll offsets captured when a pan began.</summary>
    private global::Windows.Foundation.Point viewerPanStart;
    private double viewerPanStartH, viewerPanStartV;

    /// <summary>Cancels a viewer image load whose popup was closed before it finished.</summary>
    private CancellationTokenSource? viewerLoad;

    /// <summary>The most recently decoded full image, reused across a peek and the viewer (one-entry cache).</summary>
    private (long Id, BitmapImage Image)? imageCache;

    /// <summary>Pointer rested on an image card: remember it, start loading the full image, and begin the peek countdown.</summary>
    /// <param name="sender">The image card's border.</param>
    /// <param name="e">Pointer data.</param>
    private void ImageCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CardOf(sender) is not { Kind: ClipKind.Image } card)
        {
            return;
        }

        hoverCard = card;

        // Preload so the peek (and a viewer opened from the eye) shows the crisp full image without a wait.
        _ = PreloadImageAsync(card);
        RestartHoverTimer();
    }

    /// <summary>The pointer moved over an image card: restart the rest timer (the peek only opens once the mouse settles).</summary>
    /// <param name="sender">The image card's border.</param>
    /// <param name="e">Pointer data.</param>
    private void ImageCard_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Once the peek is open it covers this border and handles its own dismissal, so this fires only before it shows.
        if (peekPopup is null)
        {
            RestartHoverTimer();
        }
    }

    /// <summary>The pointer left an image card before the peek appeared: stop waiting.</summary>
    /// <param name="sender">The image card's border.</param>
    /// <param name="e">Pointer data.</param>
    private void ImageCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // A shown peek dismisses itself on the next move; only a pending (not-yet-shown) peek is cancelled here, so the
        // exit the covering surface itself raises does not wipe the card we are peeking.
        if (peekPopup is null)
        {
            StopHoverTimer();
            hoverCard = null;
        }
    }

    /// <summary>The eye button on an image card: open the full-screen zoom/pan viewer.</summary>
    /// <param name="sender">The eye button.</param>
    /// <param name="e">Click data.</param>
    private void ViewImage_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { Kind: ClipKind.Image } card)
        {
            return;
        }

        HidePeek();
        _ = ShowViewerAsync(card);
    }

    /// <summary>Closes both overlays (on a new summon, on dismiss, on deactivation, at exit).</summary>
    private void CloseImageOverlays()
    {
        HidePeek();
        CloseViewer();
    }

    /// <summary>The card behind an event source, or <see langword="null"/>.</summary>
    /// <param name="sender">An element in a card's template.</param>
    /// <returns>The card view model, or <see langword="null"/>.</returns>
    private static ClipItemViewModel? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItemViewModel;

    /// <summary>Whether a peek may open right now (the panel is up and nothing else — menu, viewer, drag — is in the way).</summary>
    /// <returns><see langword="true"/> when a peek is allowed.</returns>
    /// <remarks>
    /// <see cref="drag"/> is the window move, <see cref="draggedClipIds"/> a card being dragged onto a group; a peek during
    /// either would be noise. <see cref="openPopups"/> being 0 rules out an open context menu or the viewer itself.
    /// </remarks>
    private bool CanShowPeek() =>
        IsOpen && peekPopup is null && viewerPopup is null && drag is null && draggedClipIds is null && openPopups == 0 && hoverCard is { Kind: ClipKind.Image };

    /// <summary>(Re)starts the rest timer so the peek opens one <see cref="HoverPeekDelayMs"/> after the mouse last moved.</summary>
    private void RestartHoverTimer()
    {
        if (!CanShowPeek())
        {
            return;
        }

        hoverTimer ??= CreateHoverTimer();
        hoverTimer.Stop();
        hoverTimer.Start();
    }

    /// <summary>Stops the rest timer (the pointer left, or a peek is up).</summary>
    private void StopHoverTimer() => hoverTimer?.Stop();

    /// <summary>Creates the one-shot rest timer that opens the peek.</summary>
    /// <returns>The timer.</returns>
    private DispatcherTimer CreateHoverTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverPeekDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = ShowPeekAsync();
        };
        return timer;
    }

    /// <summary>Opens the hover peek for the card the mouse is resting on, if it still is by the time its image is ready.</summary>
    /// <returns>A task completing when the peek is shown or abandoned (failures are logged, never thrown).</returns>
    private async Task ShowPeekAsync()
    {
        try
        {
            if (hoverCard is not { } card || !CanShowPeek() || Root.XamlRoot is null)
            {
                return;
            }

            var bitmap = await GetFullBitmapAsync(card, CancellationToken.None).ConfigureAwait(true) ?? card.Thumbnail as BitmapImage;

            // The pointer may have moved to another card (or off the list) while the image decoded.
            if (bitmap is null || hoverCard?.Id != card.Id || !CanShowPeek() || Root.XamlRoot is null)
            {
                return;
            }

            var geometry = MonitorOverlayGeometry();

            // Size from the true pixel dimensions when known (a thumbnail fallback would otherwise shrink the peek);
            // the bitmap's own size is the fallback.
            double pixelWidth = card.Entry.ImageWidth is int iw and > 0 ? iw : bitmap.PixelWidth;
            double pixelHeight = card.Entry.ImageHeight is int ih and > 0 ? ih : bitmap.PixelHeight;
            var display = ImagePreviewLayout.FitWithin(
                pixelWidth / geometry.Scale,
                pixelHeight / geometry.Scale,
                geometry.Width * ImagePreviewLayout.PeekMaxFraction,
                geometry.Height * ImagePreviewLayout.PeekMaxFraction,
                allowUpscale: false);
            if (display.IsEmpty)
            {
                return;
            }

            ShowPeekPopup(bitmap, display, geometry);
        }
        catch (Exception ex)
        {
            // async timer callback: an escaping exception would crash the app; a missing peek is harmless.
            AppLog.Warn($"Showing the image peek failed: {ex.Message}");
        }
    }

    /// <summary>Builds and opens the peek popup around the given bitmap.</summary>
    /// <param name="bitmap">The image to show.</param>
    /// <param name="display">Its drawn size in DIPs (already fitted to 80% of the monitor).</param>
    /// <param name="geometry">The monitor-overlay placement.</param>
    private void ShowPeekPopup(BitmapImage bitmap, PreviewSize display, (double OffsetX, double OffsetY, double Width, double Height, double Scale) geometry)
    {
        // A floating card around the image: a solid surface (so a transparent PNG has a backing), a thin stroke and
        // rounded corners read as a tooltip rather than a bare picture on the desktop.
        var frame = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"],
            Child = new Image
            {
                Source = bitmap,
                Width = display.Width,
                Height = display.Height,
                Stretch = Stretch.Uniform,
            },
        };

        // Transparent (still hit-testable) so a move anywhere over the monitor dismisses the peek.
        var surface = new Grid
        {
            Width = geometry.Width,
            Height = geometry.Height,
            Background = new SolidColorBrush(Colors.Transparent),
            RequestedTheme = Root.RequestedTheme,
        };
        surface.Children.Add(frame);
        surface.PointerMoved += Peek_PointerChanged;
        surface.PointerPressed += Peek_PointerChanged;
        surface.PointerWheelChanged += Peek_PointerChanged;

        peekPopup = new Popup
        {
            XamlRoot = Root.XamlRoot,
            ShouldConstrainToRootBounds = false,
            IsLightDismissEnabled = false,
            HorizontalOffset = geometry.OffsetX,
            VerticalOffset = geometry.OffsetY,
            Child = surface,
        };

        // Count it before opening (not in a Popup.Opened handler): a windowed popup can deactivate the panel as it
        // appears, and the panel's deactivation handler hides the panel whenever the count is zero. Balanced in HidePeek.
        openPopups++;
        peekShownAt = Environment.TickCount64;
        peekPopup.IsOpen = true;
    }

    /// <summary>Any pointer activity over the peek (move after the grace, press, or wheel) dismisses it.</summary>
    /// <param name="sender">The peek surface.</param>
    /// <param name="e">Pointer data.</param>
    private void Peek_PointerChanged(object sender, PointerRoutedEventArgs e)
    {
        // Ignore the move that can arrive as the surface appears under the still cursor; a deliberate move comes later.
        if (Environment.TickCount64 - peekShownAt < PeekGraceMs)
        {
            return;
        }

        HidePeek();
    }

    /// <summary>Closes the hover peek, if any (safe to call when none is open).</summary>
    private void HidePeek()
    {
        if (peekPopup is not { } popup)
        {
            return;
        }

        // Clear the field before closing: closing re-enters nothing here, but a later move must find no popup.
        peekPopup = null;
        popup.IsOpen = false;
        openPopups = Math.Max(0, openPopups - 1);
    }

    /// <summary>Loads the full image into the cache without blocking the caller (hover preload).</summary>
    /// <param name="card">The image card.</param>
    /// <returns>A task completing when loaded or failed (always safe to ignore).</returns>
    private async Task PreloadImageAsync(ClipItemViewModel card)
    {
        try
        {
            await GetFullBitmapAsync(card, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Preloading image {card.Id} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the full-resolution image for a card, from the one-entry cache or by loading and decoding it.
    /// </summary>
    /// <param name="card">The image card.</param>
    /// <param name="token">Cancels a load whose overlay was dismissed.</param>
    /// <returns>The decoded image, or <see langword="null"/> when the entry has no decodable image.</returns>
    /// <exception cref="OperationCanceledException">The load was cancelled.</exception>
    private async Task<BitmapImage?> GetFullBitmapAsync(ClipItemViewModel card, CancellationToken token)
    {
        if (imageCache is { } cached && cached.Id == card.Id)
        {
            return cached.Image;
        }

        var png = await controller.GetImagePngAsync(card.Id, token).ConfigureAwait(true);
        if (png is null || png.Length == 0)
        {
            return null;
        }

        var bitmap = await DecodeAsync(png, token).ConfigureAwait(true);
        if (bitmap is not null)
        {
            imageCache = (card.Id, bitmap);
        }

        return bitmap;
    }

    /// <summary>Decodes PNG bytes into a bitmap on the UI thread.</summary>
    /// <param name="png">PNG bytes.</param>
    /// <param name="token">Cancellation (checked around the async steps; the decode itself is not interruptible).</param>
    /// <returns>The bitmap.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    private static async Task<BitmapImage> DecodeAsync(byte[] png, CancellationToken token)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        token.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    /// <summary>
    /// Placement of a monitor-covering overlay: the popup offset (from the panel's content origin) and the size that
    /// cover the panel monitor's work area, plus that monitor's scale for pixel→DIP conversions.
    /// </summary>
    /// <returns>Offset, size (DIPs) and scale.</returns>
    private (double OffsetX, double OffsetY, double Width, double Height, double Scale) MonitorOverlayGeometry()
    {
        var (workArea, scale) = MonitorLookup.FromWindow(hwnd);
        var (clientX, clientY) = WindowInterop.GetClientOrigin(hwnd);

        // The popup's offset is DIPs from the window content's top-left; convert the work area (screen pixels) into it.
        return ((workArea.Left - clientX) / scale, (workArea.Top - clientY) / scale, workArea.Width / scale, workArea.Height / scale, scale);
    }

    /// <summary>Opens the full-screen zoom/pan viewer for an image card (dim backdrop, Esc / click-outside / X to close).</summary>
    /// <param name="card">The image card.</param>
    /// <returns>A task completing when the image is shown or the load failed (failures are logged).</returns>
    private async Task ShowViewerAsync(ClipItemViewModel card)
    {
        if (Root.XamlRoot is null)
        {
            return;
        }

        CloseViewer();
        var geometry = MonitorOverlayGeometry();

        // A progress ring shows while the image decodes; the close button is available immediately.
        var ring = new ProgressRing { IsActive = true, Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var closeButton = new Button
        {
            Content = "", // Cancel (an X), Segoe Fluent Icons
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = 16,
            Width = 40,
            Height = 40,
            Margin = new Thickness(16),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(20),
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x8C, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(closeButton, "Close image viewer");
        ToolTipService.SetToolTip(closeButton, "Close (Esc)");
        closeButton.Click += (_, _) => CloseViewer();

        viewerZoomLabel = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 12,
            Margin = new Thickness(16),
            Padding = new Thickness(10, 4, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
        };
        var zoomChip = new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x8C, 0, 0, 0)),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16),
            Child = viewerZoomLabel,
        };

        // The dim backdrop fills the monitor; a tap on it (outside the image) closes, and it holds the Esc key.
        var dim = new Grid
        {
            Width = geometry.Width,
            Height = geometry.Height,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xD8, 0, 0, 0)),
            RequestedTheme = Root.RequestedTheme,
            IsTabStop = true,
        };
        dim.Children.Add(ring);
        dim.Children.Add(zoomChip);
        dim.Children.Add(closeButton);
        dim.Tapped += (_, _) => CloseViewer();
        dim.KeyDown += Viewer_KeyDown;

        viewerPopup = new Popup
        {
            XamlRoot = Root.XamlRoot,
            ShouldConstrainToRootBounds = false,
            IsLightDismissEnabled = false,
            HorizontalOffset = geometry.OffsetX,
            VerticalOffset = geometry.OffsetY,
            Child = dim,
        };

        // Count it before opening (see the peek, above) so a deactivation as it appears does not hide the panel;
        // Popup_Closed balances the count and returns focus to the search box.
        viewerPopup.Closed += Popup_Closed;
        openPopups++;
        viewerPopup.IsOpen = true;
        dim.Focus(FocusState.Programmatic); // so Esc reaches Viewer_KeyDown

        try
        {
            viewerLoad = new CancellationTokenSource();
            var bitmap = await GetFullBitmapAsync(card, viewerLoad.Token).ConfigureAwait(true) ?? card.Thumbnail as BitmapImage;
            if (viewerPopup is null || viewerLoad.IsCancellationRequested)
            {
                return; // closed while loading
            }

            ring.IsActive = false;
            ring.Visibility = Visibility.Collapsed;
            if (bitmap is null)
            {
                dim.Children.Add(new TextBlock
                {
                    Text = "This image could not be loaded.",
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return;
            }

            BuildViewerContent(dim, bitmap, card, geometry);
        }
        catch (OperationCanceledException)
        {
            // Closed mid-load.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Showing the image viewer failed: {ex.Message}");
            CloseViewer();
        }
    }

    /// <summary>Adds the scroll host, image and zoom chip to the viewer once its image is ready.</summary>
    /// <param name="dim">The viewer's backdrop grid.</param>
    /// <param name="bitmap">The image.</param>
    /// <param name="card">The card (for its true pixel size).</param>
    /// <param name="geometry">The monitor-overlay placement.</param>
    private void BuildViewerContent(Grid dim, BitmapImage bitmap, ClipItemViewModel card, (double OffsetX, double OffsetY, double Width, double Height, double Scale) geometry)
    {
        double pixelWidth = card.Entry.ImageWidth is int iw and > 0 ? iw : bitmap.PixelWidth;
        double pixelHeight = card.Entry.ImageHeight is int ih and > 0 ? ih : bitmap.PixelHeight;
        double naturalWidth = pixelWidth / geometry.Scale;
        double naturalHeight = pixelHeight / geometry.Scale;

        // The viewport leaves a margin so the dim backdrop frames the image (and clicking that margin closes the viewer).
        double viewportWidth = geometry.Width * 0.94;
        double viewportHeight = geometry.Height * 0.9;

        // Fit the whole image in at the start (never upscaled past 100%); allow zooming well below and far above it.
        viewerFitZoom = Math.Min(ImagePreviewLayout.FitZoom(naturalWidth, naturalHeight, viewportWidth, viewportHeight), 1.0);
        viewerMinZoom = Math.Max(MinZoomFloor, viewerFitZoom * 0.5);
        viewerMaxZoom = Math.Max(1.0, viewerFitZoom) * MaxZoomMultiple;

        var image = new Image
        {
            Source = bitmap,
            Width = naturalWidth,
            Height = naturalHeight,
            Stretch = Stretch.Uniform,
        };
        image.Tapped += (_, e) => e.Handled = true;               // a tap on the picture must not close the viewer
        image.DoubleTapped += Viewer_DoubleTapped;                 // toggle fit / zoomed-in
        image.PointerPressed += Viewer_PointerPressed;             // start a drag-pan
        image.PointerMoved += Viewer_PointerMoved;
        image.PointerReleased += Viewer_PointerReleased;
        image.PointerCanceled += Viewer_PointerReleased;
        image.PointerCaptureLost += Viewer_PointerReleased;

        viewerScroll = new ScrollViewer
        {
            Width = viewportWidth,
            Height = viewportHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent), // a click on the letterbox bubbles to the dim → close
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = (float)viewerMinZoom,
            MaxZoomFactor = (float)viewerMaxZoom,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = image,
        };
        viewerScroll.PointerWheelChanged += Viewer_PointerWheelChanged;
        viewerScroll.ViewChanged += (_, _) => UpdateZoomLabel();

        // The scroll host goes under the chrome (close button, zoom chip) so those stay clickable/readable.
        dim.Children.Insert(0, viewerScroll);

        // Apply the fit zoom and center once the host has its size (its Width/Height are explicit, so Loaded is enough).
        viewerScroll.Loaded += (_, _) => ApplyInitialViewerZoom(naturalWidth, naturalHeight, viewportWidth, viewportHeight);
        if (viewerZoomLabel is not null)
        {
            viewerZoomLabel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Sets the viewer's initial (fit) zoom and centers the image.</summary>
    /// <param name="naturalWidth">Image width in DIPs at zoom 1.</param>
    /// <param name="naturalHeight">Image height in DIPs at zoom 1.</param>
    /// <param name="viewportWidth">Scroll host width in DIPs.</param>
    /// <param name="viewportHeight">Scroll host height in DIPs.</param>
    private void ApplyInitialViewerZoom(double naturalWidth, double naturalHeight, double viewportWidth, double viewportHeight)
    {
        if (viewerScroll is null)
        {
            return;
        }

        // Offsets that center the (possibly larger than viewport) scaled content; negatives clamp to 0 for a small image.
        double offsetX = Math.Max(0, ((naturalWidth * viewerFitZoom) - viewportWidth) / 2);
        double offsetY = Math.Max(0, ((naturalHeight * viewerFitZoom) - viewportHeight) / 2);
        viewerScroll.ChangeView(offsetX, offsetY, (float)viewerFitZoom, disableAnimation: true);
        UpdateZoomLabel();
    }

    /// <summary>Mouse wheel over the viewer zooms towards the cursor instead of scrolling.</summary>
    /// <param name="sender">The scroll host.</param>
    /// <param name="e">Wheel data.</param>
    private void Viewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (viewerScroll is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(viewerScroll);
        double notches = point.Properties.MouseWheelDelta / (double)TabStripScroll.WheelDelta;
        double oldZoom = viewerScroll.ZoomFactor;
        double newZoom = ImagePreviewLayout.ClampZoom(oldZoom * Math.Pow(WheelZoomPerNotch, notches), viewerMinZoom, viewerMaxZoom);
        if (Math.Abs(newZoom - oldZoom) > 0.0001)
        {
            double offsetX = ImagePreviewLayout.ZoomOffset(oldZoom, newZoom, point.Position.X, viewerScroll.HorizontalOffset);
            double offsetY = ImagePreviewLayout.ZoomOffset(oldZoom, newZoom, point.Position.Y, viewerScroll.VerticalOffset);
            viewerScroll.ChangeView(offsetX, offsetY, (float)newZoom, disableAnimation: true);
        }

        // Always handled: otherwise the scroll host would also pan on the wheel, fighting the zoom.
        e.Handled = true;
    }

    /// <summary>Double-click toggles between fit-to-window and zoomed-in, centered on the click.</summary>
    /// <param name="sender">The image.</param>
    /// <param name="e">Tap data.</param>
    private void Viewer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (viewerScroll is null)
        {
            return;
        }

        double oldZoom = viewerScroll.ZoomFactor;
        bool fitted = oldZoom <= viewerFitZoom * 1.02;
        double newZoom = ImagePreviewLayout.ClampZoom(fitted ? Math.Max(viewerFitZoom * 2, 1.0) : viewerFitZoom, viewerMinZoom, viewerMaxZoom);
        var point = e.GetPosition(viewerScroll);
        double offsetX = ImagePreviewLayout.ZoomOffset(oldZoom, newZoom, point.X, viewerScroll.HorizontalOffset);
        double offsetY = ImagePreviewLayout.ZoomOffset(oldZoom, newZoom, point.Y, viewerScroll.VerticalOffset);
        viewerScroll.ChangeView(offsetX, offsetY, (float)newZoom, disableAnimation: true);
        e.Handled = true;
    }

    /// <summary>Begins a drag-pan of the zoomed image.</summary>
    /// <param name="sender">The image.</param>
    /// <param name="e">Pointer data.</param>
    private void Viewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (viewerScroll is null || sender is not Image image)
        {
            return;
        }

        // Only the primary button pans; a tap is swallowed so it does not reach the dim's close handler.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !e.GetCurrentPoint(image).Properties.IsLeftButtonPressed)
        {
            return;
        }

        viewerPanning = image.CapturePointer(e.Pointer);
        viewerPanStart = e.GetCurrentPoint(viewerScroll).Position;
        viewerPanStartH = viewerScroll.HorizontalOffset;
        viewerPanStartV = viewerScroll.VerticalOffset;
        e.Handled = true;
    }

    /// <summary>Pans the image with the pointer while a drag is in progress.</summary>
    /// <param name="sender">The image.</param>
    /// <param name="e">Pointer data.</param>
    private void Viewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!viewerPanning || viewerScroll is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(viewerScroll).Position;

        // Drag right → content follows the hand → scroll offset decreases (show what is to the left).
        viewerScroll.ChangeView(viewerPanStartH - (point.X - viewerPanStart.X), viewerPanStartV - (point.Y - viewerPanStart.Y), null, disableAnimation: true);
        e.Handled = true;
    }

    /// <summary>Ends a drag-pan (release, cancel or capture loss).</summary>
    /// <param name="sender">The image.</param>
    /// <param name="e">Pointer data.</param>
    private void Viewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!viewerPanning)
        {
            return;
        }

        viewerPanning = false;
        (sender as Image)?.ReleasePointerCaptures();
    }

    /// <summary>Esc closes the viewer.</summary>
    /// <param name="sender">The backdrop.</param>
    /// <param name="e">Key data.</param>
    private void Viewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            CloseViewer();
            e.Handled = true;
        }
    }

    /// <summary>Updates the zoom-percentage chip from the current zoom factor.</summary>
    private void UpdateZoomLabel()
    {
        if (viewerScroll is not null && viewerZoomLabel is not null)
        {
            viewerZoomLabel.Text = $"{viewerScroll.ZoomFactor * 100:0}%";
        }
    }

    /// <summary>Closes the zoom/pan viewer, if any (safe to call when none is open).</summary>
    private void CloseViewer()
    {
        if (viewerPopup is not { } popup)
        {
            return;
        }

        // Clear the fields first so the load continuation and any late pointer event find nothing to act on.
        viewerPopup = null;
        viewerScroll = null;
        viewerZoomLabel = null;
        viewerPanning = false;
        viewerLoad?.Cancel();
        viewerLoad = null;
        popup.IsOpen = false; // raises Closed → Popup_Closed (openPopups--, focus back to the search box)
    }
}
