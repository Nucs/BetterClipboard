using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Documents;

/// <summary>
/// One version of a document an integration follows over time (a Notepad++ "new" tab), handed to
/// <see cref="Services.ClipHistoryService.RecordDocumentAsync"/>: which document it is, its name, and its text as a capture.
/// </summary>
/// <remarks>
/// The history keeps one entry per version of the document's text that lost too much to be updated in place
/// (<see cref="TrackedDocumentRule"/>); <paramref name="Scope"/> and <paramref name="Key"/> say which document a version
/// belongs to, and stay the same while its text changes.
/// </remarks>
/// <param name="Scope">The integration's name for its documents (<c>notepad++</c>): keys of different integrations never meet.</param>
/// <param name="Key">The document's identity within the scope (a Notepad++ tab's backup file path, compared case-insensitively by the caller).</param>
/// <param name="Title">The document's name as its app shows it ("new 3"), stored as the entry's source title; <see langword="null"/> when unknown.</param>
/// <param name="Capture">The text as a capture: <c>CF_UNICODETEXT</c>, the version's time, the source app and the origin.</param>
public sealed record TrackedDocument(string Scope, string Key, string? Title, ClipCapture Capture);

/// <summary>What recording a version of a tracked document did.</summary>
public enum TrackedDocumentOutcomeKind
{
    /// <summary>The document was seen for the first time (or its entry was gone) and its text became an entry, new or a duplicate's.</summary>
    Created,

    /// <summary>The document's entry got the new text in place (it kept its place, pins and groups).</summary>
    Updated,

    /// <summary>The text lost more than a fifth: the old entry stays as it was, and the text is a new copy.</summary>
    NewCopy,

    /// <summary>The text is the one recorded last: nothing was written (a new name may have been).</summary>
    Unchanged,

    /// <summary>
    /// The text is content the user deleted or cleared (an import never resurrects it): no entry holds it; the document is
    /// remembered with that text, so the same text is not offered again.
    /// </summary>
    Suppressed,

    /// <summary>The history's rules rejected the text (paused, ignored app, too large, not text, forgotten forever): nothing was written.</summary>
    Skipped,
}

/// <summary>The result of recording a version of a tracked document.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Entry">
/// The entry that holds the document's text now, when one does — on <see cref="TrackedDocumentOutcomeKind.Unchanged"/> only
/// when the entry was renamed (its caption changed), so callers can tell the panel exactly when something changed.
/// </param>
/// <param name="PreviousEntryId">On <see cref="TrackedDocumentOutcomeKind.NewCopy"/>, the entry that keeps the version before; otherwise <see langword="null"/>.</param>
/// <param name="IsNewEntry">Whether <paramref name="Entry"/> was inserted by this call (rather than an existing entry the text merged into).</param>
public sealed record TrackedDocumentOutcome(TrackedDocumentOutcomeKind Kind, ClipEntry? Entry, long? PreviousEntryId = null, bool IsNewEntry = false)
{
    /// <summary>An outcome without an entry (<see cref="TrackedDocumentOutcomeKind.Skipped"/>).</summary>
    public static TrackedDocumentOutcome Skipped { get; } = new(TrackedDocumentOutcomeKind.Skipped, null);
}

/// <summary>A document mapping as the store keeps it (for an integration's own bookkeeping, e.g. renames and status).</summary>
/// <param name="Key">The document's key.</param>
/// <param name="EntryId">The entry holding its last recorded text, or <see langword="null"/> when none does (deleted, suppressed).</param>
/// <param name="ContentHash">The content hash of the text recorded (or seen) last.</param>
/// <param name="Title">The name recorded last.</param>
public sealed record TrackedDocumentMapping(string Key, long? EntryId, string ContentHash, string? Title);
