using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Prompts;

namespace BetterClipboard.App;

/// <summary>
/// The text the panel's text viewer shows (the eye's viewer and the hover peek of a long text): the whole text behind a
/// card, read from where it is kept.
/// </summary>
public sealed partial class AppController
{
    /// <summary>The formats a stored entry's text is read from, in order: its text, else its HTML, else its RTF.</summary>
    private static readonly string[] TextFormats = [ClipFormatNames.UnicodeText, ClipFormatNames.Html, ClipFormatNames.Rtf];

    /// <summary>
    /// Reads the whole text of a stored entry for the text viewer: its <c>CF_UNICODETEXT</c>; for a file list its paths, one
    /// per line; for formatted content without text, its HTML fragment or its RTF source as they are.
    /// </summary>
    /// <param name="id">The entry's id.</param>
    /// <param name="kind">Its kind (a file list is read from its <c>CF_HDROP</c>).</param>
    /// <param name="cancellationToken">Stops the read when the viewer closes first.</param>
    /// <returns>The text, or <see langword="null"/> when the entry is gone or holds no text.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>Reads on the pool (a text can be megabytes), one format only (never the entry's images or other formats).</remarks>
    public async Task<string?> GetEntryTextAsync(long id, ClipKind kind, CancellationToken cancellationToken = default)
    {
        if (kind == ClipKind.Files)
        {
            var drop = await History.GetFirstFormatAsync(id, [ClipFormatNames.HDrop]).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return drop is null ? null : string.Join('\n', DropFilesCodec.Decode(drop.Data));
        }

        var format = await History.GetFirstFormatAsync(id, TextFormats).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return format?.Name switch
        {
            null => null,
            ClipFormatNames.UnicodeText => UnicodeTextCodec.Decode(format.Data),
            ClipFormatNames.Html => HtmlClipboardFormat.ExtractFragment(format.Data),

            // RTF is ASCII by definition (its other characters are escapes): shown as the source it is.
            _ => System.Text.Encoding.Latin1.GetString(format.Data).TrimEnd('\0'),
        };
    }

    /// <summary>The whole text of an archived prompt (a prompt tab's card); the list row holds only a preview.</summary>
    /// <param name="prompt">The archive row.</param>
    /// <returns>The text, or <see langword="null"/> when the prompt was deleted meanwhile.</returns>
    public Task<string?> GetPromptTextAsync(PromptSummary prompt) => LoadPromptTextAsync(prompt);
}
