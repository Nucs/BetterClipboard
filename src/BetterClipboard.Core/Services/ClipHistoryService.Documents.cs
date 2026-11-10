using BetterClipboard.Core.Content;
using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Documents;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Storage;

namespace BetterClipboard.Core.Services;

/// <summary>
/// Tracked documents through the history worker (CLAUDE.md §2.41): versions of a document an integration follows (a Notepad++
/// "new" tab) are recorded by <see cref="ClipStore.RecordDocument"/> under the same rules as any capture.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rules.</b> Pause skips a version unless it is the first activation's import (the tabs Notepad++ already remembered, like
/// Windows' own history); ignored apps, the item size limit and "Forget forever" apply always; only text is tracked.
/// </para>
/// <para>
/// <b>Events.</b> A new entry raises <see cref="ClipChangeKind.Added"/> (and retention runs), a text replaced in place or
/// merged into an existing entry raises <see cref="ClipChangeKind.Updated"/>, an unchanged version raises nothing (unless its
/// entry was renamed). A batch (<see cref="RecordDocumentsAsync"/>) raises one <see cref="ClipChangeKind.Reset"/> instead.
/// </para>
/// </remarks>
public sealed partial class ClipHistoryService
{
    /// <summary>
    /// Records one version of a tracked document through the worker (see the class remarks and
    /// <see cref="ClipStore.RecordDocument"/>).
    /// </summary>
    /// <param name="document">The document and its new text.</param>
    /// <returns>What happened; <see cref="TrackedDocumentOutcome.Skipped"/> when a rule rejected the text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The store failed; nothing was changed.</exception>
    public Task<TrackedDocumentOutcome> RecordDocumentAsync(TrackedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return EnqueueAsync(() => Task.FromResult(RecordDocumentCore(document, isImport: false, raiseEvents: true)));
    }

    /// <summary>
    /// Records several versions in one queue slot — a first activation's import, or a start's catch-up — with one
    /// <see cref="ClipChangeKind.Reset"/> when anything changed instead of an event per document. One document that fails is
    /// logged and skipped; the rest are still recorded.
    /// </summary>
    /// <param name="documents">The documents (each its own transaction).</param>
    /// <param name="isImport">
    /// <see langword="true"/> for the first activation's import: pause does not apply and an existing duplicate keeps its place;
    /// <see langword="false"/> for versions found later (a catch-up), which are new events.
    /// </param>
    /// <returns>The outcomes, in the order of <paramref name="documents"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<IReadOnlyList<TrackedDocumentOutcome>> RecordDocumentsAsync(IReadOnlyList<TrackedDocument> documents, bool isImport)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return EnqueueAsync(() =>
        {
            var outcomes = new List<TrackedDocumentOutcome>(documents.Count);
            bool anyChange = false;
            foreach (var document in documents)
            {
                try
                {
                    var outcome = RecordDocumentCore(document, isImport, raiseEvents: false);
                    anyChange |= outcome.Kind is TrackedDocumentOutcomeKind.Created or TrackedDocumentOutcomeKind.Updated or TrackedDocumentOutcomeKind.NewCopy
                        || outcome.Entry is not null;
                    outcomes.Add(outcome);
                }
                catch (Exception ex)
                {
                    // One unreadable document must not cost the others; content-free like every log line.
                    AppLog.Error("Failed to record one tracked document.", ex);
                    outcomes.Add(TrackedDocumentOutcome.Skipped);
                }
            }

            if (anyChange)
            {
                store.Prune(rulesProvider().Retention, time.GetUtcNow());
                Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));

                // The batch raised no event per entry: a tab that had nothing may have something now.
                FindMissingTabContent();
            }

            return Task.FromResult<IReadOnlyList<TrackedDocumentOutcome>>(outcomes);
        });
    }

    /// <summary>
    /// Moves a document's mapping to a new key and renames its entry (a renamed Notepad++ tab, see
    /// <see cref="ClipStore.RenameDocument"/>), through the worker.
    /// </summary>
    /// <param name="scope">The integration's scope.</param>
    /// <param name="oldKey">The key the document had.</param>
    /// <param name="newKey">Its key now.</param>
    /// <param name="newTitle">Its name now, or <see langword="null"/>.</param>
    /// <returns>Whether the mapping moved.</returns>
    /// <exception cref="ArgumentException">A scope or key is blank.</exception>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<bool> RenameDocumentAsync(string scope, string oldKey, string newKey, string? newTitle) => EnqueueAsync(() =>
    {
        bool moved = store.RenameDocument(scope, oldKey, newKey, newTitle);
        if (moved && newTitle is not null)
        {
            // The entry's caption names the tab: one reset is cheaper than looking the entry up for an event.
            Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));
        }

        return Task.FromResult(moved);
    });

    /// <summary>Reads a scope's document mappings on the thread pool (see <see cref="ClipStore.GetDocuments"/>).</summary>
    /// <param name="scope">The integration's scope.</param>
    /// <returns>The mappings.</returns>
    public Task<IReadOnlyList<TrackedDocumentMapping>> GetDocumentsAsync(string scope) => Task.Run(() => store.GetDocuments(scope));

    /// <summary>Forgets documents' mappings through the worker; their entries stay (see <see cref="ClipStore.RemoveDocuments"/>).</summary>
    /// <param name="scope">The integration's scope.</param>
    /// <param name="keys">The keys, or <see langword="null"/> for every document of the scope.</param>
    /// <returns>How many mappings were removed.</returns>
    /// <exception cref="InvalidOperationException">The service is shutting down.</exception>
    public Task<int> RemoveDocumentsAsync(string scope, IReadOnlyCollection<string>? keys)
    {
        // Copied on the caller's thread: the caller may keep changing its collection.
        IReadOnlyCollection<string>? copy = keys is null ? null : [.. keys];
        return EnqueueAsync(() => Task.FromResult(store.RemoveDocuments(scope, copy)));
    }

    /// <summary>
    /// Applies the capture rules to one version and records it (worker thread only).
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="isImport">Whether pause is ignored and duplicates keep their place (the first activation).</param>
    /// <param name="raiseEvents">Whether to raise per-document events (false inside a batch).</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The store failed.</exception>
    private TrackedDocumentOutcome RecordDocumentCore(TrackedDocument document, bool isImport, bool raiseEvents)
    {
        var rules = rulesProvider();
        var capture = document.Capture;
        if (!isImport && rules.IsPaused)
        {
            return TrackedDocumentOutcome.Skipped;
        }

        if (rules.IsIgnored(capture.Source))
        {
            return TrackedDocumentOutcome.Skipped;
        }

        if (capture.TotalBytes > rules.MaxItemBytes)
        {
            // Content-free: the size and the limit only.
            AppLog.Info($"Skipped a {capture.TotalBytes:N0}-byte tracked document (limit {rules.MaxItemBytes:N0}).");
            return TrackedDocumentOutcome.Skipped;
        }

        var classified = ContentClassifier.Classify(capture);
        if (classified?.PlainText is null)
        {
            // Empty or not text: a document's version is only ever recorded as text.
            return TrackedDocumentOutcome.Skipped;
        }

        anyForgotten ??= store.CountForgotten() > 0;
        if (anyForgotten.Value)
        {
            var fingerprint = ForgetFingerprint.Of(classified);
            if (store.IsForgotten(fingerprint))
            {
                if (!isImport && store.RecordForgottenBlock(fingerprint, capture.CapturedAtUtc))
                {
                    AppLog.Info("Skipped a tracked document's text that was forgotten forever.");
                    RaiseForgottenChanged();
                }

                return TrackedDocumentOutcome.Skipped;
            }
        }

        var outcome = store.RecordDocument(document, classified, isImport);
        if (!raiseEvents || outcome.Entry is not { } entry)
        {
            return outcome;
        }

        if (outcome.IsNewEntry)
        {
            Raise(new ClipChangedEventArgs(ClipChangeKind.Added, entry));
            if (store.Prune(rules.Retention, time.GetUtcNow()) > 0)
            {
                Raise(new ClipChangedEventArgs(ClipChangeKind.Reset));
            }
        }
        else
        {
            Raise(new ClipChangedEventArgs(ClipChangeKind.Updated, entry));
        }

        return outcome;
    }
}
