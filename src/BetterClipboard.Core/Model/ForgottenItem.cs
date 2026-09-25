namespace BetterClipboard.Core.Model;

/// <summary>
/// One entry of the "Forget forever" list: content the user asked never to record again. It holds no
/// content at all — only what the Settings list needs to tell entries apart (kind, size, source app, times).
/// The fingerprint that does the matching stays inside the store.
/// </summary>
/// <param name="Id">Stable id (<c>forgotten.id</c>, never reused) for "Allow again".</param>
/// <param name="Kind">What the forgotten item was.</param>
/// <param name="TextLength">Characters of its text for text kinds; <see langword="null"/> otherwise.</param>
/// <param name="FileCount">Number of files for <see cref="ClipKind.Files"/>; <see langword="null"/> otherwise.</param>
/// <param name="ImageWidth">Pixel width for images, when known.</param>
/// <param name="ImageHeight">Pixel height for images, when known.</param>
/// <param name="SourceAppName">The app it had last been copied from, when known (imports carry none).</param>
/// <param name="ForgottenUtc">When the user forgot it.</param>
/// <param name="BlockedCount">
/// How many new copies of it were kept out since (live copies, ShareX screenshots, <c>bclip put</c>). Windows'
/// history offering the same item again at each start is not a new copy and is not counted.
/// </param>
/// <param name="LastBlockedUtc">When the last of those was kept out; <see langword="null"/> when none was.</param>
public sealed record ForgottenItem(
    long Id,
    ClipKind Kind,
    int? TextLength,
    int? FileCount,
    int? ImageWidth,
    int? ImageHeight,
    string? SourceAppName,
    DateTimeOffset ForgottenUtc,
    long BlockedCount,
    DateTimeOffset? LastBlockedUtc);

/// <summary>Outcome of forgetting a history entry forever.</summary>
/// <param name="Item">The list entry that now keeps its content out (an existing one when the content was already forgotten).</param>
/// <param name="RemovedIds">
/// History entries deleted: the entry itself first, then stored look-alikes with the same fingerprint (the same
/// text with other line endings or surrounding whitespace, which the cards show identically).
/// </param>
public sealed record ForgetResult(ForgottenItem Item, IReadOnlyList<long> RemovedIds);
