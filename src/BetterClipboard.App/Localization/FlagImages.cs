using System.Runtime.InteropServices.WindowsRuntime;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Localization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BetterClipboard.App.Localization;

/// <summary>
/// The flags of the interface languages as image sources, drawn from the SVGs embedded with the catalogs
/// (<see cref="UiLanguage.FlagSvg"/>). One source per flag, made on first use and shared by every image that shows it:
/// the first-start picker and Settings › Language show the same few flags again and again.
/// </summary>
/// <remarks>
/// UI thread only: an image source belongs to the thread that made it, and the cache is not locked. An SVG is drawn at
/// the size of the element that shows it (<see cref="SvgImageSource"/> rasterizes for its layout size), so one source
/// serves a 36 × 24 row and a 20 × 14 combo-box item alike, sharp at every display scale.
/// </remarks>
internal static class FlagImages
{
    /// <summary>Sources by flag code; filled on the UI thread only.</summary>
    private static readonly Dictionary<string, SvgImageSource> Sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the flag of a language. The source is returned at once and fills in when its SVG has been read (a few
    /// milliseconds); a flag that cannot be drawn stays empty and is logged, since the names next to it say everything.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns>The shared image source of its flag.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="language"/> is <see langword="null"/>.</exception>
    public static ImageSource For(UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (!Sources.TryGetValue(language.Flag, out var source))
        {
            source = new SvgImageSource();
            Sources[language.Flag] = source;
            _ = LoadAsync(source, language);
        }

        return source;
    }

    /// <summary>Reads a flag's SVG into its source. Never throws: a missing flag is cosmetic.</summary>
    /// <param name="source">The source to fill.</param>
    /// <param name="language">The language whose flag it is.</param>
    /// <returns>A task completing when the source is filled or the failure is logged.</returns>
    private static async Task LoadAsync(SvgImageSource source, UiLanguage language)
    {
        try
        {
            // SvgImageSource reads WinRT streams only; the bytes are copied, so the shared buffer is never handed out.
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(language.FlagSvg.ToArray().AsBuffer());
            stream.Seek(0);
            var status = await source.SetSourceAsync(stream);
            if (status != SvgImageSourceLoadStatus.Success)
            {
                AppLog.Warn($"The {language.Flag} flag could not be drawn ({status}).");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Loading the {language.Flag} flag failed: {ex.Message}");
        }
    }
}
