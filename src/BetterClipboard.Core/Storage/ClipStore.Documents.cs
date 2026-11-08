using BetterClipboard.Core.Content;
using BetterClipboard.Core.Documents;
using BetterClipboard.Core.Model;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Storage;

/// <summary>
/// Tracked documents (CLAUDE.md §2.41): documents an integration follows over time — a Notepad++ "new" tab — mapped to the
/// entry that holds their text, updated in place or split into a new copy by <see cref="TrackedDocumentRule"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction per version.</b> <see cref="RecordDocument"/> reads the mapping, the entry's stored text, decides,
/// writes the entry (in place, or a new copy through <see cref="UpsertCore"/>) and moves the mapping, all in one SQLite
/// transaction: a crash leaves either the old state or the new one, never an entry without its mapping. That is the "atomic"
/// of the owner's request.
/// </para>
/// <para>
/// <b>Deletions are respected.</b> The mapping's entry reference is <c>ON DELETE SET NULL</c>: when the user deletes the entry
/// (or retention prunes it), the mapping keeps the last text's hash, so the same text is never recorded again; the
/// document's next change is recorded as a new entry, like a new copy. An import (the first activation) never resurrects
/// content the user deleted or cleared (the tombstones and the last clear decide, as for every import).
/// </para>
/// </remarks>
public sealed partial class ClipStore
{
    /// <summary>
    /// The mapping table (added by <see cref="EnsureTrackedDocumentsSchema"/>). <c>content_hash</c> is the text recorded or
    /// seen last; <c>lost_chars</c> and <c>peak_length</c> are the copy's <see cref="TrackedDocumentVersion"/>; <c>title</c>
    /// is the name recorded last; <c>seen_utc</c> when the mapping last changed.
    /// </summary>
    private const string SchemaTrackedDocuments =
        """
        CREATE TABLE IF NOT EXISTS tracked_documents (
            scope        TEXT    NOT NULL,
            doc_key      TEXT    NOT NULL,
            clip_id      INTEGER REFERENCES clips (id) ON DELETE SET NULL,
            content_hash TEXT    NOT NULL,
            lost_chars   INTEGER NOT NULL DEFAULT 0,
            peak_length  INTEGER NOT NULL DEFAULT 0,
            title        TEXT,
            seen_utc     INTEGER NOT NULL,
            PRIMARY KEY (scope, doc_key)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_tracked_documents_clip ON tracked_documents (clip_id);
        """;

    /// <summary>
    /// Records one version of a tracked document (see the class remarks and <see cref="TrackedDocumentRule"/>): the first
    /// version becomes an entry; a later one updates the entry in place, or — when the entry's copy lost more than a fifth —
    /// leaves it as it is and becomes a new copy; an unchanged text writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>In place</b> means: the entry's text, kind, preview, search text, hash, size and formats are replaced; its place in
    /// the list (<c>last_used_utc</c>), pins and groups stay; <c>edited_utc</c> is set to the version's time. When another
    /// entry already holds exactly the new text (the content hash is unique), the mapping moves to that entry instead, and the
    /// old entry keeps its text — the history then holds both versions, which loses nothing.
    /// </para>
    /// <para>
    /// <b>A new copy</b> and a first version go through <see cref="UpsertCore"/> with the document's capture: a duplicate is
    /// bumped (a new version is a new event), except during an import (<paramref name="isImport"/>: the first activation's
    /// reading of documents that existed before), which never reorders an existing entry. Content the user deleted or cleared
    /// is suppressed for both (the origin never lifts a tombstone): <see cref="TrackedDocumentOutcomeKind.Suppressed"/>.
    /// </para>
    /// </remarks>
    /// <param name="document">The document and its new text (<c>CF_UNICODETEXT</c> capture).</param>
    /// <param name="classified">The capture's classification (by <see cref="ContentClassifier.Classify"/>; text kinds only).</param>
    /// <param name="isImport">Whether this is the first activation's import (no reordering of an existing duplicate).</param>
    /// <returns>What happened, with the entry that holds the text now.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The classification carries no text (<see cref="ClassifiedClip.PlainText"/>).</exception>
    /// <exception cref="SqliteException">A read or write failed; nothing is changed.</exception>
    public TrackedDocumentOutcome RecordDocument(TrackedDocument document, ClassifiedClip classified, bool isImport)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(classified);
        var text = classified.PlainText ?? throw new ArgumentException("A tracked document's version must be text.", nameof(classified));

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var mapping = ReadMapping(connection, document.Scope, document.Key);
        long when = document.Capture.CapturedAtUtc.ToUnixTimeMilliseconds();
        TrackedDocumentOutcome outcome;

        if (mapping is { } known && string.Equals(known.Hash, classified.ContentHash, StringComparison.Ordinal))
        {
            // The text recorded (or suppressed) last: nothing to store. A renamed tab still renames its entry, and only then is
            // the entry returned (its caption changed; the caller tells the panel).
            ClipEntry? renamed = null;
            if (!string.Equals(known.Title, document.Title, StringComparison.Ordinal) && document.Title is not null)
            {
                RetitleEntry(connection, known.ClipId, document.Title);
                WriteMapping(connection, document, known.ClipId, known.Hash, known.Version, when);
                renamed = known.ClipId is { } same ? GetEntry(connection, same) : null;
            }

            outcome = new TrackedDocumentOutcome(TrackedDocumentOutcomeKind.Unchanged, renamed);
        }
        else if (mapping is { ClipId: { } clipId } existing && LoadText(connection, clipId) is { } stored)
        {
            var decision = TrackedDocumentRule.Decide(stored, text, existing.Version);
            outcome = decision.Change switch
            {
                TrackedDocumentChange.Unchanged => KeepMapping(connection, document, clipId, classified.ContentHash, existing.Version, when),
                TrackedDocumentChange.UpdateInPlace => UpdateInPlace(connection, document, classified, clipId, decision.Next, when),
                _ => StartNewCopy(connection, document, classified, clipId, isImport, when),
            };
        }
        else
        {
            // First sight, or the entry the document had is gone (deleted, pruned): its text starts an entry again.
            outcome = StartNewCopy(connection, document, classified, previousEntryId: null, isImport, when);
        }

        transaction.Commit();
        return outcome;
    }

    /// <summary>
    /// Moves a document's mapping to a new key — a Notepad++ tab renamed, whose backup file Notepad++ renames with it — and
    /// renames its entry. Does nothing when the old key is unknown or the new key is taken.
    /// </summary>
    /// <param name="scope">The integration's scope.</param>
    /// <param name="oldKey">The key the document had.</param>
    /// <param name="newKey">Its key now.</param>
    /// <param name="newTitle">Its name now, or <see langword="null"/> to keep the entry's.</param>
    /// <returns>Whether the mapping moved.</returns>
    /// <exception cref="ArgumentException">A scope or key is blank.</exception>
    /// <exception cref="SqliteException">The write failed.</exception>
    public bool RenameDocument(string scope, string oldKey, string newKey, string? newTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newKey);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (ReadMapping(connection, scope, newKey) is not null || ReadMapping(connection, scope, oldKey) is not { } mapping)
        {
            return false;
        }

        using (var move = Command(connection, "UPDATE tracked_documents SET doc_key = $new, title = coalesce($title, title) WHERE scope = $scope AND doc_key = $old;"))
        {
            move.Parameters.AddWithValue("$scope", scope);
            move.Parameters.AddWithValue("$old", oldKey);
            move.Parameters.AddWithValue("$new", newKey);
            move.Parameters.AddWithValue("$title", (object?)newTitle ?? DBNull.Value);
            move.ExecuteNonQuery();
        }

        if (newTitle is not null)
        {
            RetitleEntry(connection, mapping.ClipId, newTitle);
        }

        transaction.Commit();
        return true;
    }

    /// <summary>The mappings of a scope (the integration's bookkeeping: which documents it knows, and their entries).</summary>
    /// <param name="scope">The integration's scope.</param>
    /// <returns>The mappings, in no particular order.</returns>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is blank.</exception>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<TrackedDocumentMapping> GetDocuments(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        using var connection = Open();
        using var command = Command(connection, "SELECT doc_key, clip_id, content_hash, title FROM tracked_documents WHERE scope = $scope;");
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        var mappings = new List<TrackedDocumentMapping>();
        while (reader.Read())
        {
            mappings.Add(new TrackedDocumentMapping(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return mappings;
    }

    /// <summary>
    /// Forgets documents (their tabs were closed, or the integration was switched off): their mappings go, their entries stay
    /// in the history like any copy.
    /// </summary>
    /// <param name="scope">The integration's scope.</param>
    /// <param name="keys">The keys to forget, or <see langword="null"/> for every document of the scope.</param>
    /// <returns>How many mappings were removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is blank.</exception>
    /// <exception cref="SqliteException">The write failed.</exception>
    public int RemoveDocuments(string scope, IReadOnlyCollection<string>? keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        int removed = 0;
        if (keys is null)
        {
            using var all = Command(connection, "DELETE FROM tracked_documents WHERE scope = $scope;");
            all.Parameters.AddWithValue("$scope", scope);
            removed = all.ExecuteNonQuery();
        }
        else
        {
            foreach (var key in keys)
            {
                using var one = Command(connection, "DELETE FROM tracked_documents WHERE scope = $scope AND doc_key = $key;");
                one.Parameters.AddWithValue("$scope", scope);
                one.Parameters.AddWithValue("$key", key);
                removed += one.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return removed;
    }

    /// <summary>
    /// Adds the nullable <c>clips.source_title</c> and <c>clips.edited_utc</c> columns when missing (additive and unversioned
    /// like <see cref="EnsurePathCountColumn"/>: existing rows start without a title and never edited, the truth for them).
    /// </summary>
    /// <remarks>Idempotent; metadata-only, so instant on any history size.</remarks>
    /// <param name="connection">Open connection inside the migration transaction.</param>
    /// <exception cref="SqliteException">The schema could not be read or altered; the migration transaction rolls back.</exception>
    private static void EnsureSourceTitleAndEditedColumns(SqliteConnection connection)
    {
        foreach (var (name, type) in (ReadOnlySpan<(string, string)>)[("source_title", "TEXT"), ("edited_utc", "INTEGER")])
        {
            using var columns = Command(connection, "SELECT 1 FROM pragma_table_info('clips') WHERE name = $name;");
            columns.Parameters.AddWithValue("$name", name);
            if (columns.ExecuteScalar() is null)
            {
                // Both names and types are constants above: safe to inline (ALTER TABLE takes no parameters).
                Execute(connection, $"ALTER TABLE clips ADD COLUMN {name} {type};");
            }
        }
    }

    /// <summary>Creates the tracked documents table when missing (idempotent, see the class remarks).</summary>
    /// <param name="connection">Open connection inside the migration transaction.</param>
    /// <exception cref="SqliteException">The schema could not be created; the migration transaction rolls back.</exception>
    private static void EnsureTrackedDocumentsSchema(SqliteConnection connection) => Execute(connection, SchemaTrackedDocuments);

    /// <summary>Reads a document's mapping.</summary>
    /// <param name="connection">Open connection (inside the caller's transaction).</param>
    /// <param name="scope">The scope.</param>
    /// <param name="key">The key.</param>
    /// <returns>The mapping, or <see langword="null"/> when the document is unknown.</returns>
    private static StoredMapping? ReadMapping(SqliteConnection connection, string scope, string key)
    {
        using var command = Command(connection, "SELECT clip_id, content_hash, lost_chars, peak_length, title FROM tracked_documents WHERE scope = $scope AND doc_key = $key;");
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StoredMapping(
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.GetString(1),
                new TrackedDocumentVersion(reader.GetInt64(2), reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    /// <summary>Writes (inserts or replaces) a document's mapping.</summary>
    /// <param name="connection">Open connection inside the caller's transaction.</param>
    /// <param name="document">The document (scope, key, title).</param>
    /// <param name="clipId">The entry holding its text, or <see langword="null"/>.</param>
    /// <param name="hash">The content hash recorded.</param>
    /// <param name="version">The copy's bookkeeping.</param>
    /// <param name="when">The version's time (Unix milliseconds).</param>
    private static void WriteMapping(SqliteConnection connection, TrackedDocument document, long? clipId, string hash, TrackedDocumentVersion version, long when)
    {
        using var command = Command(connection,
            """
            INSERT OR REPLACE INTO tracked_documents (scope, doc_key, clip_id, content_hash, lost_chars, peak_length, title, seen_utc)
            VALUES ($scope, $key, $clip, $hash, $lost, $peak, $title, $when);
            """);
        command.Parameters.AddWithValue("$scope", document.Scope);
        command.Parameters.AddWithValue("$key", document.Key);
        command.Parameters.AddWithValue("$clip", (object?)clipId ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$lost", version.LostCharacters);
        command.Parameters.AddWithValue("$peak", version.PeakLength);
        command.Parameters.AddWithValue("$title", (object?)document.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$when", when);
        command.ExecuteNonQuery();
    }

    /// <summary>The text an entry holds (its <c>CF_UNICODETEXT</c>), or <see langword="null"/> when it is gone or holds none.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="clipId">The entry.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    private static string? LoadText(SqliteConnection connection, long clipId)
    {
        using var command = Command(connection, "SELECT data FROM clip_formats WHERE clip_id = $id AND name = $name ORDER BY ordinal LIMIT 1;");
        command.Parameters.AddWithValue("$id", clipId);
        command.Parameters.AddWithValue("$name", ClipFormatNames.UnicodeText);
        return command.ExecuteScalar() is byte[] data ? UnicodeTextCodec.Decode(data) : null;
    }

    /// <summary>Records the text's hash without touching the entry (the texts are equal; only the bytes' hash moved).</summary>
    /// <param name="connection">Open connection inside the caller's transaction.</param>
    /// <param name="document">The document.</param>
    /// <param name="clipId">Its entry.</param>
    /// <param name="hash">The hash of the version.</param>
    /// <param name="version">The unchanged bookkeeping.</param>
    /// <param name="when">The version's time.</param>
    /// <returns>An <see cref="TrackedDocumentOutcomeKind.Unchanged"/> outcome.</returns>
    private static TrackedDocumentOutcome KeepMapping(SqliteConnection connection, TrackedDocument document, long clipId, string hash, TrackedDocumentVersion version, long when)
    {
        WriteMapping(connection, document, clipId, hash, version, when);
        return new TrackedDocumentOutcome(TrackedDocumentOutcomeKind.Unchanged, GetEntry(connection, clipId));
    }

    /// <summary>
    /// Replaces an entry's text in place (see <see cref="RecordDocument"/>), or — when another entry already holds exactly the
    /// new text — moves the mapping there and leaves both entries as they are.
    /// </summary>
    /// <param name="connection">Open connection inside the caller's transaction.</param>
    /// <param name="document">The document and its capture.</param>
    /// <param name="classified">The capture's classification.</param>
    /// <param name="clipId">The entry to update.</param>
    /// <param name="next">The copy's bookkeeping after this update.</param>
    /// <param name="when">The version's time (Unix milliseconds).</param>
    /// <returns>An <see cref="TrackedDocumentOutcomeKind.Updated"/> outcome with the entry that holds the text now.</returns>
    private static TrackedDocumentOutcome UpdateInPlace(SqliteConnection connection, TrackedDocument document, ClassifiedClip classified, long clipId, TrackedDocumentVersion next, long when)
    {
        long? holder;
        using (var find = Command(connection, "SELECT id FROM clips WHERE content_hash = $hash AND id <> $id;"))
        {
            find.Parameters.AddWithValue("$hash", classified.ContentHash);
            find.Parameters.AddWithValue("$id", clipId);
            holder = find.ExecuteScalar() as long?;
        }

        if (holder is { } other)
        {
            // The hash is unique: the text already has an entry. Follow it there; the old entry keeps the previous version.
            WriteMapping(connection, document, other, classified.ContentHash, TrackedDocumentVersion.Start(classified.PlainText!), when);
            return new TrackedDocumentOutcome(TrackedDocumentOutcomeKind.Updated, GetEntry(connection, other));
        }

        var capture = document.Capture;
        using (var update = Command(connection,
            """
            UPDATE clips SET
                kind = $kind, preview = $preview, search_text = $search, content_hash = $hash, path_count = $paths,
                size_bytes = $size, format_names = $formats,
                edited_utc = max(coalesce(edited_utc, 0), $when),
                source_title = coalesce($title, source_title),
                source_app_name = coalesce($srcName, source_app_name),
                source_app_path = coalesce($srcPath, source_app_path),
                image_width = NULL, image_height = NULL, thumbnail = NULL
            WHERE id = $id;
            """))
        {
            update.Parameters.AddWithValue("$kind", (int)classified.Kind);
            update.Parameters.AddWithValue("$preview", classified.Preview);
            update.Parameters.AddWithValue("$search", classified.SearchText);
            update.Parameters.AddWithValue("$hash", classified.ContentHash);
            update.Parameters.AddWithValue("$paths", classified.PathCount);
            update.Parameters.AddWithValue("$size", capture.TotalBytes);
            update.Parameters.AddWithValue("$formats", string.Join(FormatNameSeparator, capture.Formats.Select(f => f.Name)));
            update.Parameters.AddWithValue("$when", when);
            update.Parameters.AddWithValue("$title", (object?)document.Title ?? DBNull.Value);
            update.Parameters.AddWithValue("$srcName", (object?)capture.Source?.DisplayName ?? DBNull.Value);
            update.Parameters.AddWithValue("$srcPath", (object?)capture.Source?.ExecutablePath ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", clipId);
            update.ExecuteNonQuery();
        }

        using (var clear = Command(connection, "DELETE FROM clip_formats WHERE clip_id = $id;"))
        {
            clear.Parameters.AddWithValue("$id", clipId);
            clear.ExecuteNonQuery();
        }

        InsertFormats(connection, clipId, capture.Formats);
        WriteMapping(connection, document, clipId, classified.ContentHash, next, when);
        return new TrackedDocumentOutcome(TrackedDocumentOutcomeKind.Updated, GetEntry(connection, clipId));
    }

    /// <summary>
    /// Stores the version as an entry of its own (a first version, or a new copy after a split) and maps the document to it.
    /// </summary>
    /// <param name="connection">Open connection inside the caller's transaction.</param>
    /// <param name="document">The document and its capture.</param>
    /// <param name="classified">The capture's classification.</param>
    /// <param name="previousEntryId">The entry that keeps the version before (a split), or <see langword="null"/> (a first version).</param>
    /// <param name="isImport">Whether an existing duplicate keeps its place (the first activation's import).</param>
    /// <param name="when">The version's time (Unix milliseconds).</param>
    /// <returns>Created / NewCopy with the entry, or Suppressed when the text is content the user deleted or cleared.</returns>
    private static TrackedDocumentOutcome StartNewCopy(SqliteConnection connection, TrackedDocument document, ClassifiedClip classified, long? previousEntryId, bool isImport, long when)
    {
        var upsert = UpsertCore(connection, document.Capture, classified, image: null, bumpIfExists: !isImport);
        var version = TrackedDocumentVersion.Start(classified.PlainText!);
        if (upsert is null)
        {
            // Deleted or cleared content: remember the text without an entry, so the same text is not offered again.
            WriteMapping(connection, document, clipId: null, classified.ContentHash, version, when);
            return new TrackedDocumentOutcome(TrackedDocumentOutcomeKind.Suppressed, null, previousEntryId);
        }

        WriteMapping(connection, document, upsert.Entry.Id, classified.ContentHash, version, when);
        return previousEntryId is null
            ? new TrackedDocumentOutcome(TrackedDocumentOutcomeKind.Created, upsert.Entry, null, upsert.IsNew)
            : new TrackedDocumentOutcome(TrackedDocumentOutcomeKind.NewCopy, upsert.Entry, previousEntryId, upsert.IsNew);
    }

    /// <summary>Renames the entry that holds a document's text (its source title), when there is one.</summary>
    /// <param name="connection">Open connection inside the caller's transaction.</param>
    /// <param name="clipId">The entry, or <see langword="null"/>.</param>
    /// <param name="title">The new name.</param>
    private static void RetitleEntry(SqliteConnection connection, long? clipId, string title)
    {
        if (clipId is not { } id)
        {
            return;
        }

        using var command = Command(connection, "UPDATE clips SET source_title = $title WHERE id = $id;");
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>A mapping row as <see cref="RecordDocument"/> needs it.</summary>
    /// <param name="ClipId">The entry, or <see langword="null"/>.</param>
    /// <param name="Hash">The content hash recorded last.</param>
    /// <param name="Version">The copy's bookkeeping.</param>
    /// <param name="Title">The name recorded last.</param>
    private sealed record StoredMapping(long? ClipId, string Hash, TrackedDocumentVersion Version, string? Title);
}
