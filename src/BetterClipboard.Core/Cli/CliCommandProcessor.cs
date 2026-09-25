using System.Text;
using System.Text.RegularExpressions;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Cli;

/// <summary>
/// Executes <c>bclip</c> requests against the history — the server half of the command line, free of
/// pipes and Win32 so every command is unit-tested directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same semantics as the panel.</b> Reads go through <see cref="ClipHistoryService"/> (the panel's
/// substring search, newest first); <c>copy</c> replays formats exactly like choosing an item
/// (<see cref="ReplayFormats.Prepare"/>, "cut" neutralized, optional plain text) and honors
/// <see cref="AppSettings.MoveToTopOnPaste"/>; <c>put</c> is recorded like a live copy (it respects
/// pause and size limits) with <c>bclip</c> as its source.
/// </para>
/// <para>
/// <b>Untrusted input.</b> Callers are any program running as the user, e.g. an AI agent: regular
/// expressions run with a match timeout, sizes and counts are clamped, <c>delete</c> needs an explicit
/// target, and failures come back as <see cref="CliResponse"/> errors instead of exceptions.
/// </para>
/// </remarks>
public sealed class CliCommandProcessor
{
    /// <summary>Items returned when the request names no limit.</summary>
    public const int DefaultLimit = 20;

    /// <summary>How far back <c>grep</c> scans (most recently used first).</summary>
    public const int GrepScanLimit = 10_000;

    /// <summary>Largest text <c>put</c> accepts (characters).</summary>
    public const int MaxPutChars = 16 * 1024 * 1024;

    /// <summary>Per-line regex budget: a pathological pattern fails fast instead of pinning a core.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Where <c>put</c> text says it came from.</summary>
    private static readonly SourceAppInfo CommandLineSource = new("bclip", null, "bclip");

    private const int MaxMatchesPerItem = 20;
    private const int MaxMatchLineChars = 400;

    private readonly ClipHistoryService history;
    private readonly IClipboardWriter clipboard;
    private readonly IImageExporter? images;
    private readonly Func<AppSettings> settings;
    private readonly Func<CliCaptureStats?> captureStats;
    private readonly string version;
    private readonly TimeProvider time;

    /// <summary>
    /// Creates the processor.
    /// </summary>
    /// <param name="history">The running history service.</param>
    /// <param name="clipboard">Clipboard writer (the monitor, so our own writes are not re-captured).</param>
    /// <param name="images">DIB → PNG exporter for <c>get --format png</c>; <see langword="null"/> serves only items already stored as PNG.</param>
    /// <param name="settings">Current settings snapshot (read per request).</param>
    /// <param name="captureStats">Listener accounting for <c>status</c>, or <see langword="null"/> when unavailable.</param>
    /// <param name="version">App version reported by <c>status</c>.</param>
    /// <param name="time">Clock (tests inject a fake).</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public CliCommandProcessor(
        ClipHistoryService history,
        IClipboardWriter clipboard,
        IImageExporter? images,
        Func<AppSettings> settings,
        Func<CliCaptureStats?> captureStats,
        string version,
        TimeProvider? time = null)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        this.images = images;
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.captureStats = captureStats ?? throw new ArgumentNullException(nameof(captureStats));
        this.version = version ?? throw new ArgumentNullException(nameof(version));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Executes one request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels long-running commands (<c>wait</c>, big <c>grep</c> scans) when the client disconnects or the app exits.</param>
    /// <returns>The response; command failures are reported in it, never thrown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<CliResponse> ExecuteAsync(CliRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != CliEndpoint.ProtocolVersion)
        {
            return CliResponse.Fail(CliErrorCodes.BadRequest,
                $"bclip speaks protocol v{request.ProtocolVersion}, BetterClipboard {version} speaks v{CliEndpoint.ProtocolVersion}. Use the bclip that ships with the app.");
        }

        try
        {
            return (request.Command ?? string.Empty).ToLowerInvariant() switch
            {
                CliCommands.List => await ListAsync(request, search: null, cancellationToken).ConfigureAwait(false),
                CliCommands.Search => string.IsNullOrWhiteSpace(request.Query)
                    ? CliResponse.Fail(CliErrorCodes.BadRequest, "search needs text to look for.")
                    : await ListAsync(request, request.Query, cancellationToken).ConfigureAwait(false),
                CliCommands.Grep => await GrepAsync(request, cancellationToken).ConfigureAwait(false),
                CliCommands.Get => await GetAsync(request, cancellationToken).ConfigureAwait(false),
                CliCommands.Copy => await CopyAsync(request, cancellationToken).ConfigureAwait(false),
                CliCommands.Put => await PutAsync(request).ConfigureAwait(false),
                CliCommands.Pin => await SetPinnedAsync(request, pinned: true, cancellationToken).ConfigureAwait(false),
                CliCommands.Unpin => await SetPinnedAsync(request, pinned: false, cancellationToken).ConfigureAwait(false),
                CliCommands.Delete => await DeleteAsync(request, cancellationToken).ConfigureAwait(false),
                CliCommands.Wait => await WaitAsync(request, cancellationToken).ConfigureAwait(false),
                CliCommands.Status => await StatusAsync().ConfigureAwait(false),
                _ => CliResponse.Fail(CliErrorCodes.BadRequest, $"Unknown command '{request.Command}'. Run 'bclip help'."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException ex)
        {
            // The only I/O a command does outside SQLite is the clipboard write.
            return CliResponse.Fail(CliErrorCodes.ClipboardBusy, ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Command-line request '{request.Command}' failed.", ex);
            return CliResponse.Fail(CliErrorCodes.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Converts an entry into its wire form.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The item (without full text or matches).</returns>
    public static CliItem ToItem(ClipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new CliItem
        {
            Id = entry.Id,
            Kind = KindName(entry.Kind),
            Preview = entry.Preview,
            Pinned = entry.IsPinned,
            Source = string.IsNullOrWhiteSpace(entry.SourceAppName) ? null : entry.SourceAppName,
            SourcePath = entry.SourceAppPath,
            Origin = entry.Origin switch
            {
                ClipOrigin.WindowsHistory => "windows-history",
                ClipOrigin.WindowsPinned => "windows-pinned",
                ClipOrigin.ShareX => "sharex",
                _ => "copied",
            },
            FirstCopied = entry.CreatedUtc,
            LastUsed = entry.LastUsedUtc,
            UseCount = entry.UseCount,
            SizeBytes = entry.SizeBytes,
            Formats = entry.FormatNames,
            ImageWidth = entry.ImageWidth,
            ImageHeight = entry.ImageHeight,
        };
    }

    /// <summary>The wire name of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><c>text</c>, <c>rich-text</c>, <c>link</c>, <c>color</c>, <c>image</c> or <c>files</c>.</returns>
    public static string KindName(ClipKind kind) => kind switch
    {
        ClipKind.RichText => "rich-text",
        ClipKind.Link => "link",
        ClipKind.Color => "color",
        ClipKind.Image => "image",
        ClipKind.Files => "files",
        _ => "text",
    };

    /// <summary>Parses a filter name.</summary>
    /// <param name="name">Wire value (case-insensitive) or <see langword="null"/>.</param>
    /// <param name="filter">The filter.</param>
    /// <returns>Whether the name is known.</returns>
    public static bool TryParseFilter(string? name, out ClipFilter filter)
    {
        filter = (name ?? "all").Trim().ToLowerInvariant() switch
        {
            "" or "all" => ClipFilter.All,
            "pinned" => ClipFilter.Pinned,
            "text" => ClipFilter.Text,
            "images" or "image" => ClipFilter.Images,
            "links" or "link" => ClipFilter.Links,
            "files" or "file" => ClipFilter.Files,
            "sharex" => ClipFilter.ShareX,
            _ => (ClipFilter)(-1),
        };
        return Enum.IsDefined(filter);
    }

    /// <summary><c>list</c> and <c>search</c>.</summary>
    /// <param name="request">Request.</param>
    /// <param name="search">Search words, or <see langword="null"/> for a plain listing.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    private async Task<CliResponse> ListAsync(CliRequest request, string? search, CancellationToken cancellationToken)
    {
        if (!TryParseFilter(request.Filter, out var filter))
        {
            return BadFilter(request.Filter);
        }

        var entries = await history.QueryAsync(new ClipQuery
        {
            SearchText = search,
            Filter = filter,
            Limit = Math.Clamp(request.Limit ?? DefaultLimit, 1, 1000),
            Offset = Math.Max(0, request.Offset ?? 0),
            // History order for machines: newest first, pins not hoisted (the panel's pin-first view is a UI choice).
            PinnedFirst = false,
            UsedSince = request.Since,
        }, cancellationToken).ConfigureAwait(false);

        var items = new List<CliItem>(entries.Count);
        foreach (var entry in entries)
        {
            items.Add(request.Full ? ToItem(entry) with { Text = await FullTextAsync(entry).ConfigureAwait(false) } : ToItem(entry));
        }

        return search is not null && items.Count == 0
            ? CliResponse.Fail(CliErrorCodes.NotFound, $"Nothing in history matches '{search}'.") with { Items = items }
            : new CliResponse { Ok = true, Items = items };
    }

    /// <summary><c>grep</c>: line-wise regular expression over the most recent entries' text.</summary>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    private async Task<CliResponse> GrepAsync(CliRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Query))
        {
            return CliResponse.Fail(CliErrorCodes.BadRequest, "grep needs a pattern.");
        }

        if (!TryParseFilter(request.Filter, out var filter))
        {
            return BadFilter(request.Filter);
        }

        Regex regex;
        try
        {
            var options = RegexOptions.CultureInvariant | (request.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(request.Query, options, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return CliResponse.Fail(CliErrorCodes.BadRequest, $"Invalid pattern: {ex.Message}");
        }

        int wanted = Math.Clamp(request.Limit ?? DefaultLimit, 1, 1000);
        var texts = await history.GetSearchTextsAsync(filter, GrepScanLimit).ConfigureAwait(false);
        var items = new List<CliItem>();
        int scanned = 0;
        try
        {
            foreach (var (id, indexed) in texts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;

                // The index stops at SearchMaxChars; only such (rare, huge) items pay for a payload reload.
                var text = indexed.Length >= ContentClassifier.SearchMaxChars
                    ? await FullTextAsync(id).ConfigureAwait(false) ?? indexed
                    : indexed;
                var matches = MatchLines(regex, text);
                if (matches.Count == 0)
                {
                    continue;
                }

                var entry = await history.GetEntryAsync(id).ConfigureAwait(false);
                if (entry is null || (request.Since is { } since && entry.LastUsedUtc < since))
                {
                    continue;
                }

                items.Add(ToItem(entry) with { Matches = matches, Text = request.Full ? text : null });
                if (items.Count >= wanted)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return CliResponse.Fail(CliErrorCodes.BadRequest, "The pattern is too slow to evaluate (catastrophic backtracking?); simplify it.");
        }

        return items.Count == 0
            ? CliResponse.Fail(CliErrorCodes.NotFound, $"No line matches /{request.Query}/ in the last {scanned:N0} items.") with { Items = items, Scanned = scanned }
            : new CliResponse { Ok = true, Items = items, Scanned = scanned };
    }

    /// <summary><c>get</c>: one item in the requested representation.</summary>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    private async Task<CliResponse> GetAsync(CliRequest request, CancellationToken cancellationToken)
    {
        var (entry, failure) = await ResolveAsync(request, allowLatest: true, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return failure!;
        }

        var formats = await history.GetFormatsAsync(entry.Id).ConfigureAwait(false);
        var format = (request.Format ?? "auto").Trim().ToLowerInvariant();
        CliContent? content = format switch
        {
            "auto" => entry.Kind switch
            {
                ClipKind.Image => await PngContentAsync(formats, cancellationToken).ConfigureAwait(false),
                ClipKind.Files => FilesContent(formats),
                _ => TextContent(formats) ?? HtmlContent(formats) ?? RtfContent(formats),
            },
            "text" => TextContent(formats) ?? FilesContent(formats),
            "html" => HtmlContent(formats),
            "rtf" => RtfContent(formats),
            "files" => FilesContent(formats),
            "png" or "image" => await PngContentAsync(formats, cancellationToken).ConfigureAwait(false),
            "formats" => new CliContent
            {
                Format = "formats",
                MediaType = "application/json",
                StoredFormats = formats.Select(f => new CliFormatInfo(f.Name, f.Data.Length)).ToArray(),
                Bytes = formats.Sum(f => (long)f.Data.Length),
            },
            _ => null,
        };

        if (content is null)
        {
            return format is "auto" or "text" or "html" or "rtf" or "files" or "png" or "image" or "formats"
                ? CliResponse.Fail(CliErrorCodes.Unsupported, $"Item {entry.Id} ({KindName(entry.Kind)}) has no {format} representation. Try --format formats.") with { Item = ToItem(entry) }
                : CliResponse.Fail(CliErrorCodes.BadRequest, $"Unknown format '{request.Format}' (use auto, text, html, rtf, files, png or formats).");
        }

        return new CliResponse { Ok = true, Item = ToItem(entry), Content = content };
    }

    /// <summary><c>copy</c>: puts an item back on the clipboard, like choosing it in the panel.</summary>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    /// <exception cref="IOException">The clipboard stayed locked (mapped to <see cref="CliErrorCodes.ClipboardBusy"/>).</exception>
    private async Task<CliResponse> CopyAsync(CliRequest request, CancellationToken cancellationToken)
    {
        var (entry, failure) = await ResolveAsync(request, allowLatest: true, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return failure!;
        }

        var replay = ReplayFormats.Prepare(await history.GetFormatsAsync(entry.Id).ConfigureAwait(false), request.PlainText);
        if (replay.Count == 0)
        {
            return CliResponse.Fail(CliErrorCodes.Unsupported, $"Item {entry.Id} ({KindName(entry.Kind)}) has no plain-text form.") with { Item = ToItem(entry) };
        }

        await clipboard.WriteAsync(replay).ConfigureAwait(false);
        if (settings().MoveToTopOnPaste)
        {
            await history.MarkUsedAsync(entry.Id).ConfigureAwait(false);
            entry = await history.GetEntryAsync(entry.Id).ConfigureAwait(false) ?? entry;
        }

        return new CliResponse
        {
            Ok = true,
            Item = ToItem(entry),
            Message = $"Copied item {entry.Id}{(request.PlainText ? " as plain text" : string.Empty)} to the clipboard.",
        };
    }

    /// <summary><c>put</c>: places new text on the clipboard and records it like a live copy.</summary>
    /// <param name="request">Request.</param>
    /// <returns>The response.</returns>
    /// <exception cref="IOException">The clipboard stayed locked (mapped to <see cref="CliErrorCodes.ClipboardBusy"/>).</exception>
    private async Task<CliResponse> PutAsync(CliRequest request)
    {
        if (string.IsNullOrEmpty(request.Text))
        {
            return CliResponse.Fail(CliErrorCodes.BadRequest, "put needs text: pass it as an argument or pipe it in.");
        }

        if (request.Text.Length > MaxPutChars)
        {
            return CliResponse.Fail(CliErrorCodes.BadRequest, $"put accepts at most {MaxPutChars:N0} characters.");
        }

        ClipFormatData[] formats = [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(request.Text))];
        await clipboard.WriteAsync(formats).ConfigureAwait(false);

        // The monitor ignores its own write (echo suppression), so the text is recorded here instead —
        // through the normal pipeline, which applies pause, size limits and duplicate merging.
        var stored = await history.AddAsync(new ClipCapture
        {
            Formats = formats,
            CapturedAtUtc = time.GetUtcNow(),
            Source = CommandLineSource,
            Origin = ClipOrigin.Captured,
        }).ConfigureAwait(false);

        return stored is null
            ? new CliResponse { Ok = true, Message = "Copied to the clipboard (not saved to history: capture is paused or the text exceeds the size limit)." }
            : new CliResponse { Ok = true, Item = ToItem(stored), Message = $"Copied to the clipboard and saved as item {stored.Id}." };
    }

    /// <summary><c>pin</c> / <c>unpin</c>.</summary>
    /// <param name="request">Request.</param>
    /// <param name="pinned">New state.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    private async Task<CliResponse> SetPinnedAsync(CliRequest request, bool pinned, CancellationToken cancellationToken)
    {
        var (entry, failure) = await ResolveAsync(request, allowLatest: true, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return failure!;
        }

        await history.SetPinnedAsync(entry.Id, pinned).ConfigureAwait(false);
        entry = await history.GetEntryAsync(entry.Id).ConfigureAwait(false) ?? entry;
        return new CliResponse { Ok = true, Item = ToItem(entry), Message = $"{(pinned ? "Pinned" : "Unpinned")} item {entry.Id}." };
    }

    /// <summary><c>delete</c>: requires an explicit id or <c>--recent</c>, never defaults to the latest item.</summary>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    private async Task<CliResponse> DeleteAsync(CliRequest request, CancellationToken cancellationToken)
    {
        var (entry, failure) = await ResolveAsync(request, allowLatest: false, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return failure!;
        }

        await history.DeleteAsync(entry.Id).ConfigureAwait(false);
        return new CliResponse { Ok = true, Item = ToItem(entry), Message = $"Deleted item {entry.Id}." };
    }

    /// <summary><c>wait</c>: completes with the next item that lands on top of history (a new copy, a re-copy or a paste from history).</summary>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation (client gone, app exiting).</param>
    /// <returns>The response.</returns>
    private async Task<CliResponse> WaitAsync(CliRequest request, CancellationToken cancellationToken)
    {
        int seconds = Math.Clamp(request.TimeoutSeconds ?? 60, 1, 3600);

        // Millisecond precision like the store, so a copy in the same millisecond as the start still counts.
        var start = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var arrived = new TaskCompletionSource<ClipEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, ClipChangedEventArgs e)
        {
            // Pin toggles are updates too, but they do not move LastUsed — so they are ignored.
            if (e.Entry is { } entry && (e.Kind == ClipChangeKind.Added || (e.Kind == ClipChangeKind.Updated && entry.LastUsedUtc >= start)))
            {
                arrived.TrySetResult(entry);
            }
        }

        history.Changed += OnChanged;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            var entry = await arrived.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return new CliResponse { Ok = true, Item = ToItem(entry) with { Text = await FullTextAsync(entry).ConfigureAwait(false) } };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CliResponse.Fail(CliErrorCodes.Timeout, $"Nothing new was copied within {seconds} s.");
        }
        finally
        {
            history.Changed -= OnChanged;
        }
    }

    /// <summary><c>status</c>.</summary>
    /// <returns>The response.</returns>
    private async Task<CliResponse> StatusAsync()
    {
        var stats = await history.GetStatsAsync().ConfigureAwait(false);
        return new CliResponse
        {
            Ok = true,
            Status = new CliStatus
            {
                Version = version,
                Items = stats.Count,
                Pinned = stats.PinnedCount,
                TotalBytes = stats.TotalBytes,
                CapturePaused = settings().IsCapturePaused,
                Capture = captureStats(),
            },
        };
    }

    /// <summary>Finds the item a request targets: <see cref="CliRequest.Id"/>, else <see cref="CliRequest.Recent"/>, else (when allowed) the latest.</summary>
    /// <param name="request">Request.</param>
    /// <param name="allowLatest">Whether an untargeted request means "the most recent item".</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The entry, or a failure response.</returns>
    private async Task<(ClipEntry? Entry, CliResponse? Failure)> ResolveAsync(CliRequest request, bool allowLatest, CancellationToken cancellationToken)
    {
        if (request.Id is { } id)
        {
            var entry = await history.GetEntryAsync(id).ConfigureAwait(false);
            return entry is null ? (null, CliResponse.Fail(CliErrorCodes.NotFound, $"There is no history item {id}.")) : (entry, null);
        }

        if (request.Recent is null && !allowLatest)
        {
            return (null, CliResponse.Fail(CliErrorCodes.BadRequest, $"{request.Command} needs an item id (or --recent N)."));
        }

        int recent = request.Recent ?? 1;
        if (recent < 1)
        {
            return (null, CliResponse.Fail(CliErrorCodes.BadRequest, "--recent counts from 1 (the latest item)."));
        }

        var page = await history.QueryAsync(new ClipQuery { Limit = 1, Offset = recent - 1, PinnedFirst = false }, cancellationToken).ConfigureAwait(false);
        return page.Count == 0
            ? (null, CliResponse.Fail(CliErrorCodes.NotFound, recent == 1 ? "History is empty." : $"History has fewer than {recent} items."))
            : (page[0], null);
    }

    /// <summary>The full text of an entry: its Unicode text, or its file paths one per line.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The text, or <see langword="null"/> for images and rich-only content.</returns>
    private async Task<string?> FullTextAsync(ClipEntry entry) =>
        entry.Kind == ClipKind.Image ? null : await FullTextAsync(entry.Id).ConfigureAwait(false);

    /// <summary>The full text of an entry by id (see <see cref="FullTextAsync(ClipEntry)"/>).</summary>
    /// <param name="id">Entry id.</param>
    /// <returns>The text or <see langword="null"/>.</returns>
    private async Task<string?> FullTextAsync(long id)
    {
        var formats = await history.GetFormatsAsync(id).ConfigureAwait(false);
        return TextContent(formats)?.Text ?? FilesContent(formats)?.Text;
    }

    /// <summary>Line-wise matching like <c>grep -n</c>.</summary>
    /// <param name="regex">The pattern.</param>
    /// <param name="text">Text to scan.</param>
    /// <returns>Up to <see cref="MaxMatchesPerItem"/> matching lines.</returns>
    /// <exception cref="RegexMatchTimeoutException">The pattern exceeded its time budget on a line.</exception>
    private static List<CliMatch> MatchLines(Regex regex, string text)
    {
        var matches = new List<CliMatch>();
        int lineNumber = 0;
        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            lineNumber++;
            if (regex.IsMatch(rawLine))
            {
                var line = rawLine.ToString();
                matches.Add(new CliMatch(lineNumber, line.Length > MaxMatchLineChars ? line[..MaxMatchLineChars] + "…" : line));
                if (matches.Count >= MaxMatchesPerItem)
                {
                    break;
                }
            }
        }

        return matches;
    }

    /// <summary><c>CF_UNICODETEXT</c> as text.</summary>
    /// <param name="formats">Stored formats.</param>
    /// <returns>The content, or <see langword="null"/> when absent or empty.</returns>
    private static CliContent? TextContent(IReadOnlyList<ClipFormatData> formats)
    {
        var format = formats.FirstOrDefault(f => f.Name == ClipFormatNames.UnicodeText);
        var text = format is null ? null : UnicodeTextCodec.Decode(format.Data);
        return string.IsNullOrEmpty(text) ? null : new CliContent { Format = "text", MediaType = "text/plain", Text = text, Bytes = Encoding.UTF8.GetByteCount(text) };
    }

    /// <summary>The copied HTML fragment.</summary>
    /// <param name="formats">Stored formats.</param>
    /// <returns>The content, or <see langword="null"/>.</returns>
    private static CliContent? HtmlContent(IReadOnlyList<ClipFormatData> formats)
    {
        var format = formats.FirstOrDefault(f => f.Name == ClipFormatNames.Html);
        if (format is null)
        {
            return null;
        }

        var html = HtmlClipboardFormat.ExtractFragment(format.Data);
        return new CliContent { Format = "html", MediaType = "text/html", Text = html, Bytes = Encoding.UTF8.GetByteCount(html) };
    }

    /// <summary>The RTF document.</summary>
    /// <param name="formats">Stored formats.</param>
    /// <returns>The content, or <see langword="null"/>.</returns>
    private static CliContent? RtfContent(IReadOnlyList<ClipFormatData> formats)
    {
        var format = formats.FirstOrDefault(f => f.Name == ClipFormatNames.Rtf);
        if (format is null)
        {
            return null;
        }

        // RTF is 7-bit with escapes; Latin-1 maps every byte to the same code point, losslessly.
        var rtf = Encoding.Latin1.GetString(format.Data).TrimEnd('\0');
        return new CliContent { Format = "rtf", MediaType = "text/rtf", Text = rtf, Bytes = format.Data.Length };
    }

    /// <summary>The copied file paths.</summary>
    /// <param name="formats">Stored formats.</param>
    /// <returns>The content, or <see langword="null"/>.</returns>
    private static CliContent? FilesContent(IReadOnlyList<ClipFormatData> formats)
    {
        var format = formats.FirstOrDefault(f => f.Name == ClipFormatNames.HDrop);
        if (format is null || DropFilesCodec.Decode(format.Data) is not { Count: > 0 } paths)
        {
            return null;
        }

        var text = string.Join('\n', paths);
        return new CliContent { Format = "files", MediaType = "text/uri-list", Files = paths, Text = text, Bytes = Encoding.UTF8.GetByteCount(text) };
    }

    /// <summary>A full-resolution PNG: stored PNG bytes as-is, else a WIC encode of the DIB.</summary>
    /// <param name="formats">Stored formats.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The content, or <see langword="null"/> when there is no image.</returns>
    private async Task<CliContent?> PngContentAsync(IReadOnlyList<ClipFormatData> formats, CancellationToken cancellationToken)
    {
        var png = formats.FirstOrDefault(f => f.Name is ClipFormatNames.Png or ClipFormatNames.PngMime)?.Data;
        if (png is null && images is not null)
        {
            png = await images.ToPngAsync(formats, cancellationToken).ConfigureAwait(false);
        }

        return png is null
            ? null
            : new CliContent { Format = "png", MediaType = "image/png", DataBase64 = Convert.ToBase64String(png), Bytes = png.Length };
    }

    /// <summary>Failure for an unknown filter name.</summary>
    /// <param name="filter">The name given.</param>
    /// <returns>The response.</returns>
    private static CliResponse BadFilter(string? filter) =>
        CliResponse.Fail(CliErrorCodes.BadRequest, $"Unknown filter '{filter}' (use all, pinned, text, images, links, files or sharex).");
}
