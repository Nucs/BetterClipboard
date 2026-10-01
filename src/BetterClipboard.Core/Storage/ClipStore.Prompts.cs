using System.Globalization;
using System.Runtime.ExceptionServices;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Prompts;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Storage;

/// <summary>
/// The prompt archive half of the store (CLAUDE.md §2.21): every prompt the user sent to Claude Code and Codex, read from
/// the agents' own files and kept here, encrypted, after the agents prune them — plus how far each agent file was read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not history entries.</b> <c>prompts</c> holds one row per send (17,000+ for Claude Code on this PC): as clip
/// entries they would bury the clipboard history in "All" and spend its item limit. A prompt becomes a history entry only
/// when the user pastes, copies, pins or groups it in its tab (<see cref="Model.ClipOrigin.ClaudeCode"/>,
/// <see cref="Model.ClipOrigin.Codex"/>). No retention prunes the archive; "Delete" on a card, "Forget forever" and
/// Settings' "Delete stored prompts" do.
/// </para>
/// <para>
/// <b>Identity.</b> <c>prompt_key</c> (<see cref="AgentPrompt.Key"/>) is unique: a prompt read again — after its file was
/// rewritten, from a forked Codex thread that copied it — merges into its row, which keeps the earliest send time. The
/// two records of one Codex prompt (CLI history line, session file) have different keys and are merged by session, text
/// hash and <see cref="PromptRules.CodexMergeWindow"/> instead, in either order to the same row: the session file's key and
/// source, the earlier of the two times.
/// </para>
/// <para>
/// <b>Deleting.</b> Delete removes every send of a text and leaves a tombstone (<c>prompt_tombstones</c>: agent, text
/// hash, when): a re-read never brings those sends back, while sending the text again (a later send time) does. "Forget
/// forever" deletes by fingerprint, and the ingest skips forgotten fingerprints for good.
/// </para>
/// <para>
/// <b>Files.</b> <c>prompt_files</c> holds each agent file's <see cref="TailCheckpoint"/> (and a Codex session file's
/// thread), written in the same transaction as the prompts read up to it. Everything here is added idempotently at every
/// <see cref="Initialize"/>, like the groups: an older build opens the file and ignores the tables.
/// </para>
/// </remarks>
public sealed partial class ClipStore
{
    /// <summary>Most text hashes a query excludes (<see cref="PromptQuery.ExcludeTextHashes"/>).</summary>
    private const int MaxExcludedHashes = 500;

    /// <summary>The archive columns a <see cref="PromptSummary"/> is read from, in <see cref="ReadPromptSummary"/>'s order.</summary>
    private const string PromptSummaryColumns =
        "p.id, p.agent, p.text_hash, p.preview, p.project, p.session_id, p.image_count, p.is_command, length(p.text)";

    /// <summary>
    /// Stores one read of one agent file (see <see cref="PromptBatch"/>): its prompts, merged and filtered by the archive's
    /// rules, and — when <paramref name="writeCheckpoint"/> — its checkpoint, all in one transaction.
    /// </summary>
    /// <param name="batch">The read.</param>
    /// <param name="maxPromptBytes">Prompts larger than this (UTF-16 bytes, the history's item size limit) are skipped.</param>
    /// <param name="writeCheckpoint">
    /// Store the checkpoint. <see langword="false"/> for the leading slices of a large read stored in several transactions:
    /// only the last slice moves the checkpoint, so a crash in between re-reads the file and the keys merge what was stored.
    /// </param>
    /// <param name="now">The clock (for the checkpoint row's update time).</param>
    /// <returns>What was added, merged and skipped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The write failed; nothing of the batch was stored.</exception>
    public PromptIngestResult IngestPrompts(PromptBatch batch, long maxPromptBytes, bool writeCheckpoint, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        int added = 0, merged = 0, skipped = 0;
        var highWater = batch.Checkpoint.HighWaterUtc;
        if (batch.SkipPrompts)
        {
            skipped = batch.Prompts.Count;
        }
        else
        {
            // Read once per batch: the per-prompt "forgotten?" lookup is skipped entirely while the list is empty.
            bool anyForgotten = Convert.ToInt64(Scalar(connection, "SELECT EXISTS (SELECT 1 FROM forgotten);"), CultureInfo.InvariantCulture) != 0;
            var notNewBefore = batch.RewrittenAfterUtc is { } rewrittenAfter ? rewrittenAfter - PromptRules.RewriteMargin : (DateTimeOffset?)null;
            foreach (var prompt in batch.Prompts)
            {
                switch (StorePrompt(connection, prompt, maxPromptBytes, anyForgotten, notNewBefore))
                {
                    case PromptOutcome.Added:
                        added++;
                        break;
                    case PromptOutcome.Merged:
                        merged++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
        }

        // The mark moves past every prompt read, stored or not: a prompt skipped while paused was decided, not missed.
        foreach (var prompt in batch.Prompts)
        {
            if (highWater is null || prompt.SentUtc > highWater)
            {
                highWater = prompt.SentUtc;
            }
        }

        if (writeCheckpoint)
        {
            UpsertPromptFile(connection, batch, batch.Checkpoint with { HighWaterUtc = highWater }, now);
        }

        transaction.Commit();
        return new PromptIngestResult(added, merged, skipped);
    }

    /// <summary>
    /// Lists one page of the archive (see <see cref="PromptQuery"/>), searched like the history (<see cref="Query(ClipQuery, CancellationToken)"/>).
    /// </summary>
    /// <param name="query">Agent, search, paging, mode.</param>
    /// <param name="includeText">Also load each row's full text (the command line's <c>--full</c>).</param>
    /// <param name="cancellationToken">Stops a toggled search at the next prompt it would test.</param>
    /// <returns>The rows, newest send first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="SearchPatternException">The search is a regular expression that does not parse.</exception>
    /// <exception cref="SearchTooSlowException">The regular expression ran out of time on a prompt.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled during a toggled search.</exception>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<PromptSummary> QueryPrompts(PromptQuery query, bool includeText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Compiled before the connection opens, like the history's search: a broken pattern fails before any work.
        var matcher = query.SearchOptions == SearchOptions.None ? null : SearchMatcher.Create(query.SearchText, query.SearchOptions);
        using var connection = Open();
        using var command = connection.CreateCommand();
        var predicates = new List<string>();
        if (query.Agent is { } agent)
        {
            predicates.Add("p.agent = $agent");
            command.Parameters.AddWithValue("$agent", (int)agent);
        }

        var (fts, likes) = matcher is null
            ? SearchQueryBuilder.Build(query.SearchText)
            : SearchQueryBuilder.BuildPrefilter(query.SearchText, query.SearchOptions);
        if (fts is not null)
        {
            predicates.Add("p.id IN (SELECT rowid FROM prompts_fts WHERE prompts_fts MATCH $fts)");
            command.Parameters.AddWithValue("$fts", fts);
        }

        for (int i = 0; i < likes.Count; i++)
        {
            predicates.Add($"p.search_text LIKE $like{i} ESCAPE '{SearchQueryBuilder.LikeEscape}'");
            command.Parameters.AddWithValue($"$like{i}", likes[i]);
        }

        if (query.SentSince is { } since)
        {
            predicates.Add("p.sent_utc >= $since");
            command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        }

        if (query.ExcludeTextHashes is { Count: > 0 } excluded)
        {
            var names = new List<string>();
            foreach (var hash in excluded.Distinct(StringComparer.Ordinal).Take(MaxExcludedHashes))
            {
                var name = $"$x{names.Count}";
                names.Add(name);
                command.Parameters.AddWithValue(name, hash);
            }

            predicates.Add($"p.text_hash NOT IN ({string.Join(", ", names)})");
        }

        Exception? matchFailure = null;
        if (matcher is not null)
        {
            connection.CreateFunction<string?, bool>(SearchMatchFunction, text =>
            {
                try
                {
                    return matcher.IsMatch(text, cancellationToken);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SearchTooSlowException)
                {
                    // As in Query: SQLite only carries the message back; keep the exception for the caller.
                    matchFailure = ex;
                    throw;
                }
            });
            predicates.Add($"{SearchMatchFunction}(p.search_text)");
        }

        var where = predicates.Count > 0 ? " WHERE " + string.Join(" AND ", predicates) : string.Empty;
        var text = includeText ? ", p.text" : ", NULL";
        command.CommandText = query.Distinct
            // Group first (each text once, with its newest and oldest send and count), page the groups, then fetch the
            // newest send's row per group through ix_prompts_text — never sort every row of a 17,000-prompt archive by hand.
            ? $"""
               SELECT {PromptSummaryColumns}{text}, g.last_sent, g.first_sent, g.sends
               FROM (SELECT p.agent AS agent, p.text_hash AS text_hash, max(p.sent_utc) AS last_sent,
                            min(p.sent_utc) AS first_sent, count(*) AS sends, max(p.id) AS last_id
                     FROM prompts p{where}
                     GROUP BY p.agent, p.text_hash
                     ORDER BY last_sent DESC, last_id DESC
                     LIMIT $limit OFFSET $offset) g
               JOIN prompts p ON p.id = (SELECT q.id FROM prompts q WHERE q.agent = g.agent AND q.text_hash = g.text_hash
                                          ORDER BY q.sent_utc DESC, q.id DESC LIMIT 1)
               ORDER BY g.last_sent DESC, g.last_id DESC;
               """
            : $"""
               SELECT {PromptSummaryColumns}{text}, p.sent_utc, p.sent_utc, 1
               FROM prompts p{where}
               ORDER BY p.sent_utc DESC, p.id DESC
               LIMIT $limit OFFSET $offset;
               """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 1000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

        var result = new List<PromptSummary>();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadPromptSummary(reader));
            }
        }
        catch (SqliteException) when (matchFailure is not null)
        {
            ExceptionDispatchInfo.Throw(matchFailure);
        }

        return result;
    }

    /// <summary>One archived send with its full text.</summary>
    /// <param name="id">The archive row id (<see cref="PromptSummary.Id"/>).</param>
    /// <returns>The row (with <see cref="PromptSummary.Text"/>), or <see langword="null"/> when it does not exist.</returns>
    /// <exception cref="SqliteException">The read failed.</exception>
    public PromptSummary? GetPrompt(long id)
    {
        using var connection = Open();
        using var command = Command(connection,
            $"SELECT {PromptSummaryColumns}, p.text, p.sent_utc, p.sent_utc, 1 FROM prompts p WHERE p.id = $id;");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPromptSummary(reader) : null;
    }

    /// <summary>Counts over the archive of one agent (or both).</summary>
    /// <param name="agent">The agent, or <see langword="null"/> for both.</param>
    /// <returns>Sends, distinct texts, tracked files and the newest send.</returns>
    /// <exception cref="SqliteException">The read failed.</exception>
    public PromptStats GetPromptStats(PromptAgent? agent)
    {
        using var connection = Open();
        var filter = agent is null ? string.Empty : " WHERE agent = $agent";
        using var counts = Command(connection, $"SELECT count(*), count(DISTINCT text_hash), max(sent_utc) FROM prompts{filter};");
        using var files = Command(connection, $"SELECT count(*) FROM prompt_files{filter};");
        if (agent is { } a)
        {
            counts.Parameters.AddWithValue("$agent", (int)a);
            files.Parameters.AddWithValue("$agent", (int)a);
        }

        long sends, distinct;
        DateTimeOffset? newest;
        using (var reader = counts.ExecuteReader())
        {
            reader.Read();
            sends = reader.GetInt64(0);
            distinct = reader.GetInt64(1);
            newest = reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        }

        return new PromptStats(sends, distinct, Convert.ToInt64(files.ExecuteScalar(), CultureInfo.InvariantCulture), newest);
    }

    /// <summary>The tracked files of an agent, with their checkpoints.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>Each tracked file.</returns>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<PromptFile> GetPromptFiles(PromptAgent agent)
    {
        using var connection = Open();
        using var command = Command(connection,
            """
            SELECT agent, file_key, kind, path, read_offset, file_length, last_write_utc, file_id, head_hash, anchor_hash, high_water_utc,
                   rewrites, thread_id, thread_cwd, thread_started_utc, thread_excluded
            FROM prompt_files WHERE agent = $agent;
            """);
        command.Parameters.AddWithValue("$agent", (int)agent);
        var files = new List<PromptFile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var checkpoint = new TailCheckpoint
            {
                Offset = reader.GetInt64(4),
                Length = reader.GetInt64(5),
                LastWriteUtc = new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
                FileId = reader.IsDBNull(7) ? null : reader.GetString(7),
                HeadHash = reader.GetString(8),
                AnchorHash = reader.GetString(9),
                HighWaterUtc = reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
                Rewrites = reader.GetInt32(11),
            };
            var thread = reader.IsDBNull(12)
                ? null
                : new CodexThread(
                    reader.GetString(12),
                    reader.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(15) ? null : reader.GetString(15));
            files.Add(new PromptFile((PromptAgent)reader.GetInt32(0), (PromptSourceKind)reader.GetInt32(2), reader.GetString(1), reader.GetString(3), checkpoint, thread));
        }

        return files;
    }

    /// <summary>
    /// Deletes every send of a text from an agent's archive and tombstones it: no re-read brings those sends back, a new
    /// send (later than now) does — "Delete until sent again".
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="textHash">The text hash (<see cref="PromptSummary.TextHash"/>).</param>
    /// <param name="now">When it was deleted (sends at or before it stay out).</param>
    /// <returns>How many sends were deleted.</returns>
    /// <exception cref="ArgumentException"><paramref name="textHash"/> is null or blank.</exception>
    /// <exception cref="SqliteException">The write failed.</exception>
    public int DeletePrompts(PromptAgent agent, string textHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textHash);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        int deleted;
        using (var delete = Command(connection, "DELETE FROM prompts WHERE agent = $agent AND text_hash = $hash;"))
        {
            delete.Parameters.AddWithValue("$agent", (int)agent);
            delete.Parameters.AddWithValue("$hash", textHash);
            deleted = delete.ExecuteNonQuery();
        }

        using (var tomb = Command(connection,
            """
            INSERT INTO prompt_tombstones (agent, text_hash, deleted_utc) VALUES ($agent, $hash, $now)
            ON CONFLICT (agent, text_hash) DO UPDATE SET deleted_utc = max(deleted_utc, excluded.deleted_utc);
            """))
        {
            tomb.Parameters.AddWithValue("$agent", (int)agent);
            tomb.Parameters.AddWithValue("$hash", textHash);
            tomb.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            tomb.ExecuteNonQuery();
        }

        transaction.Commit();
        return deleted;
    }

    /// <summary>
    /// Deletes an agent's whole archive: its prompts, its tracked files and tombstones, and its first-import mark — the
    /// next read of the agent's files is a first import again. The agent's own files are never touched.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>How many prompts were deleted.</returns>
    /// <exception cref="SqliteException">The write failed.</exception>
    public int ClearPrompts(PromptAgent agent)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        int deleted;
        using (var delete = Command(connection, "DELETE FROM prompts WHERE agent = $agent;"))
        {
            delete.Parameters.AddWithValue("$agent", (int)agent);
            deleted = delete.ExecuteNonQuery();
        }

        foreach (var sql in new[] { "DELETE FROM prompt_files WHERE agent = $agent;", "DELETE FROM prompt_tombstones WHERE agent = $agent;" })
        {
            using var command = Command(connection, sql);
            command.Parameters.AddWithValue("$agent", (int)agent);
            command.ExecuteNonQuery();
        }

        using (var mark = Command(connection, "DELETE FROM meta WHERE key = $key;"))
        {
            mark.Parameters.AddWithValue("$key", StateKey(PromptArchiveState.ImportedStateName(agent)));
            mark.ExecuteNonQuery();
        }

        transaction.Commit();
        if (deleted > 0)
        {
            Execute(connection, "PRAGMA incremental_vacuum;");
        }

        return deleted;
    }

    /// <summary>
    /// Deletes archived prompts with a "Forget forever" fingerprint (both agents) — what forgetting a text must also do to
    /// the archive, or the tabs would keep showing it.
    /// </summary>
    /// <param name="connection">An open connection (inside the forget's transaction).</param>
    /// <param name="fingerprint">The fingerprint.</param>
    /// <returns>How many sends were deleted.</returns>
    /// <exception cref="SqliteException">The write failed.</exception>
    private static int DeleteForgottenPrompts(SqliteConnection connection, string fingerprint)
    {
        using var delete = Command(connection, "DELETE FROM prompts WHERE fingerprint = $fp;");
        delete.Parameters.AddWithValue("$fp", fingerprint);
        return delete.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds the archive's objects when missing (additive and unversioned like the groups, see the class remarks).
    /// </summary>
    /// <remarks>Idempotent. Runs inside the caller's migration transaction.</remarks>
    /// <param name="connection">Open connection inside the migration transaction.</param>
    private static void EnsurePromptsSchema(SqliteConnection connection) => Execute(connection, SchemaPrompts);

    /// <summary>
    /// Applies the archive's rules to one prompt and stores it (see the class remarks): too large, not new after a rewrite,
    /// forgotten or deleted → skipped; already archived by key or (Codex) by its twin record → merged; else added.
    /// </summary>
    /// <param name="connection">Open connection inside the batch's transaction.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="maxPromptBytes">The size limit.</param>
    /// <param name="anyForgotten">Whether the "Forget forever" list has entries.</param>
    /// <param name="notNewBefore">After a rewrite: prompts sent at or before this are not new; otherwise <see langword="null"/>.</param>
    /// <returns>What happened to it.</returns>
    /// <exception cref="SqliteException">A read or write failed.</exception>
    private static PromptOutcome StorePrompt(SqliteConnection connection, AgentPrompt prompt, long maxPromptBytes, bool anyForgotten, DateTimeOffset? notNewBefore)
    {
        long sent = prompt.SentUtc.ToUnixTimeMilliseconds();
        if (prompt.SizeBytes > maxPromptBytes || (notNewBefore is { } cutoff && prompt.SentUtc <= cutoff))
        {
            return PromptOutcome.Skipped;
        }

        if (anyForgotten)
        {
            using var forgotten = Command(connection, "SELECT EXISTS (SELECT 1 FROM forgotten WHERE fingerprint = $fp);");
            forgotten.Parameters.AddWithValue("$fp", prompt.Fingerprint);
            if (Convert.ToInt64(forgotten.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                return PromptOutcome.Skipped;
            }
        }

        using (var tomb = Command(connection, "SELECT deleted_utc FROM prompt_tombstones WHERE agent = $agent AND text_hash = $hash;"))
        {
            tomb.Parameters.AddWithValue("$agent", (int)prompt.Agent);
            tomb.Parameters.AddWithValue("$hash", prompt.TextHash);
            if (tomb.ExecuteScalar() is long deletedAt && sent <= deletedAt)
            {
                return PromptOutcome.Skipped;
            }
        }

        // Read again (a rewritten file, a fork's copy, a compressed session file): the earliest send time wins.
        using (var known = Command(connection, "UPDATE prompts SET sent_utc = min(sent_utc, $sent) WHERE prompt_key = $key;"))
        {
            known.Parameters.AddWithValue("$sent", sent);
            known.Parameters.AddWithValue("$key", prompt.Key);
            if (known.ExecuteNonQuery() > 0)
            {
                return PromptOutcome.Merged;
            }
        }

        if (prompt.SessionId is { } session && TryMergeCodexTwin(connection, prompt, session, sent))
        {
            return PromptOutcome.Merged;
        }

        using var insert = Command(connection,
            """
            INSERT INTO prompts (agent, prompt_key, source, session_id, project, sent_utc, text, search_text, preview, text_hash,
                                 fingerprint, image_count, is_command)
            VALUES ($agent, $key, $source, $session, $project, $sent, $text, $search, $preview, $hash, $fp, $images, $command);
            """);
        insert.Parameters.AddWithValue("$agent", (int)prompt.Agent);
        insert.Parameters.AddWithValue("$key", prompt.Key);
        insert.Parameters.AddWithValue("$source", (int)prompt.Source);
        insert.Parameters.AddWithValue("$session", (object?)prompt.SessionId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$project", (object?)prompt.Project ?? DBNull.Value);
        insert.Parameters.AddWithValue("$sent", sent);
        insert.Parameters.AddWithValue("$text", prompt.Text);
        insert.Parameters.AddWithValue("$search", prompt.Text.Length > ContentClassifier.SearchMaxChars ? prompt.Text[..ContentClassifier.SearchMaxChars] : prompt.Text);
        insert.Parameters.AddWithValue("$preview", ContentClassifier.BuildTextPreview(prompt.Text));
        insert.Parameters.AddWithValue("$hash", prompt.TextHash);
        insert.Parameters.AddWithValue("$fp", prompt.Fingerprint);
        insert.Parameters.AddWithValue("$images", prompt.ImageCount);
        insert.Parameters.AddWithValue("$command", prompt.IsCommand ? 1 : 0);
        insert.ExecuteNonQuery();
        return PromptOutcome.Added;
    }

    /// <summary>
    /// Merges a Codex prompt into its twin record from the other Codex source, when one is archived: a CLI history line
    /// is dropped when its session file record is there; a session file record replaces a history line's (its key, its
    /// source and the earlier time), keeping the row.
    /// </summary>
    /// <param name="connection">Open connection inside the batch's transaction.</param>
    /// <param name="prompt">The prompt (a Codex one; anything else is never merged this way).</param>
    /// <param name="session">Its session (thread) id.</param>
    /// <param name="sent">Its send time, Unix ms.</param>
    /// <returns>Whether it was merged.</returns>
    /// <exception cref="SqliteException">A read or write failed.</exception>
    private static bool TryMergeCodexTwin(SqliteConnection connection, AgentPrompt prompt, string session, long sent)
    {
        if (prompt.Source is not (PromptSourceKind.CodexHistory or PromptSourceKind.CodexRollout))
        {
            return false;
        }

        long window = (long)PromptRules.CodexMergeWindow.TotalMilliseconds;

        // A history line merges with any record of its prompt; a session file record only replaces a history line's.
        using var twin = Command(connection,
            $"""
             SELECT id FROM prompts
             WHERE agent = $agent AND session_id = $session AND text_hash = $hash AND sent_utc BETWEEN $from AND $to
                   {(prompt.Source == PromptSourceKind.CodexRollout ? "AND source = $historySource" : string.Empty)}
             ORDER BY abs(sent_utc - $sent) LIMIT 1;
             """);
        twin.Parameters.AddWithValue("$agent", (int)PromptAgent.Codex);
        twin.Parameters.AddWithValue("$session", session);
        twin.Parameters.AddWithValue("$hash", prompt.TextHash);
        twin.Parameters.AddWithValue("$from", sent - window);
        twin.Parameters.AddWithValue("$to", sent + window);
        twin.Parameters.AddWithValue("$sent", sent);
        twin.Parameters.AddWithValue("$historySource", (int)PromptSourceKind.CodexHistory);
        if (twin.ExecuteScalar() is not long id)
        {
            return false;
        }

        if (prompt.Source == PromptSourceKind.CodexHistory)
        {
            // Read after its session record: the row stays the session record's, but takes the earlier time, so both
            // orders end with the same row (the history line's whole second is the earlier of the two).
            using var earlier = Command(connection, "UPDATE prompts SET sent_utc = min(sent_utc, $sent) WHERE id = $id;");
            earlier.Parameters.AddWithValue("$sent", sent);
            earlier.Parameters.AddWithValue("$id", id);
            earlier.ExecuteNonQuery();
        }
        else
        {
            using var upgrade = Command(connection,
                """
                UPDATE prompts SET prompt_key = $key, source = $source, sent_utc = min(sent_utc, $sent),
                                   project = coalesce($project, project), image_count = max(image_count, $images)
                WHERE id = $id;
                """);
            upgrade.Parameters.AddWithValue("$key", prompt.Key);
            upgrade.Parameters.AddWithValue("$source", (int)prompt.Source);
            upgrade.Parameters.AddWithValue("$sent", sent);
            upgrade.Parameters.AddWithValue("$project", (object?)prompt.Project ?? DBNull.Value);
            upgrade.Parameters.AddWithValue("$images", prompt.ImageCount);
            upgrade.Parameters.AddWithValue("$id", id);
            upgrade.ExecuteNonQuery();
        }

        return true;
    }

    /// <summary>Writes a file's checkpoint (and thread) row.</summary>
    /// <param name="connection">Open connection inside the batch's transaction.</param>
    /// <param name="batch">The batch (agent, kind, key, path, thread).</param>
    /// <param name="checkpoint">The checkpoint to store.</param>
    /// <param name="now">The update time.</param>
    /// <exception cref="SqliteException">The write failed.</exception>
    private static void UpsertPromptFile(SqliteConnection connection, PromptBatch batch, TailCheckpoint checkpoint, DateTimeOffset now)
    {
        using var upsert = Command(connection,
            """
            INSERT INTO prompt_files (agent, file_key, kind, path, read_offset, file_length, last_write_utc, file_id, head_hash, anchor_hash,
                                      high_water_utc, rewrites, thread_id, thread_cwd, thread_started_utc, thread_excluded, updated_utc)
            VALUES ($agent, $key, $kind, $path, $offset, $length, $write, $fileId, $head, $anchor, $high, $rewrites, $threadId,
                    $threadCwd, $threadStarted, $threadExcluded, $now)
            ON CONFLICT (agent, file_key) DO UPDATE SET
                kind = excluded.kind, path = excluded.path, read_offset = excluded.read_offset, file_length = excluded.file_length,
                last_write_utc = excluded.last_write_utc, file_id = excluded.file_id, head_hash = excluded.head_hash,
                anchor_hash = excluded.anchor_hash, high_water_utc = excluded.high_water_utc, rewrites = excluded.rewrites,
                thread_id = coalesce(excluded.thread_id, thread_id), thread_cwd = coalesce(excluded.thread_cwd, thread_cwd),
                thread_started_utc = coalesce(excluded.thread_started_utc, thread_started_utc),
                thread_excluded = CASE WHEN excluded.thread_id IS NULL THEN thread_excluded ELSE excluded.thread_excluded END,
                updated_utc = excluded.updated_utc;
            """);
        upsert.Parameters.AddWithValue("$agent", (int)batch.Agent);
        upsert.Parameters.AddWithValue("$key", batch.FileKey);
        upsert.Parameters.AddWithValue("$kind", (int)batch.Kind);
        upsert.Parameters.AddWithValue("$path", batch.Path);
        upsert.Parameters.AddWithValue("$offset", checkpoint.Offset);
        upsert.Parameters.AddWithValue("$length", checkpoint.Length);
        upsert.Parameters.AddWithValue("$write", checkpoint.LastWriteUtc.UtcTicks);
        upsert.Parameters.AddWithValue("$fileId", (object?)checkpoint.FileId ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$head", checkpoint.HeadHash);
        upsert.Parameters.AddWithValue("$anchor", checkpoint.AnchorHash);
        upsert.Parameters.AddWithValue("$high", checkpoint.HighWaterUtc is { } high ? high.ToUnixTimeMilliseconds() : DBNull.Value);
        upsert.Parameters.AddWithValue("$rewrites", checkpoint.Rewrites);
        upsert.Parameters.AddWithValue("$threadId", (object?)batch.Thread?.Id ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$threadCwd", (object?)batch.Thread?.Cwd ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$threadStarted", batch.Thread?.StartedUtc is { } started ? started.ToUnixTimeMilliseconds() : DBNull.Value);
        upsert.Parameters.AddWithValue("$threadExcluded", (object?)batch.Thread?.ExcludedAs ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        upsert.ExecuteNonQuery();
    }

    /// <summary>Reads a row selected with <see cref="PromptSummaryColumns"/>, the text (or NULL), last and first send and count.</summary>
    /// <param name="reader">The reader on a row.</param>
    /// <returns>The summary.</returns>
    private static PromptSummary ReadPromptSummary(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Agent = (PromptAgent)reader.GetInt32(1),
        TextHash = reader.GetString(2),
        Preview = reader.GetString(3),
        Project = reader.IsDBNull(4) ? null : reader.GetString(4),
        SessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
        ImageCount = reader.GetInt32(6),
        IsCommand = reader.GetInt64(7) != 0,
        TextLength = reader.GetInt32(8),
        Text = reader.IsDBNull(9) ? null : reader.GetString(9),
        LastSentUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
        FirstSentUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
        Sends = reader.GetInt32(12),
    };

    /// <summary>What <see cref="StorePrompt"/> did with one prompt.</summary>
    private enum PromptOutcome
    {
        /// <summary>A new row.</summary>
        Added,

        /// <summary>Already archived (same key, or its Codex twin): merged.</summary>
        Merged,

        /// <summary>Not stored (see <see cref="StorePrompt"/>).</summary>
        Skipped,
    }

    /// <summary>
    /// The archive's tables (see the class remarks). <c>ix_prompts_text</c> serves the distinct listing and its newest-send
    /// lookup, <c>ix_prompts_session</c> the Codex twin merge, <c>ix_prompts_fingerprint</c> "Forget forever".
    /// </summary>
    private const string SchemaPrompts =
        """
        CREATE TABLE IF NOT EXISTS prompts (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            agent       INTEGER NOT NULL,
            prompt_key  TEXT    NOT NULL UNIQUE,
            source      INTEGER NOT NULL,
            session_id  TEXT,
            project     TEXT,
            sent_utc    INTEGER NOT NULL,
            text        TEXT    NOT NULL,
            search_text TEXT    NOT NULL,
            preview     TEXT    NOT NULL,
            text_hash   TEXT    NOT NULL,
            fingerprint TEXT    NOT NULL,
            image_count INTEGER NOT NULL DEFAULT 0,
            is_command  INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_prompts_text ON prompts (agent, text_hash, sent_utc DESC);
        CREATE INDEX IF NOT EXISTS ix_prompts_sent ON prompts (agent, sent_utc DESC);
        CREATE INDEX IF NOT EXISTS ix_prompts_session ON prompts (agent, session_id, text_hash, sent_utc);
        CREATE INDEX IF NOT EXISTS ix_prompts_fingerprint ON prompts (fingerprint);

        CREATE VIRTUAL TABLE IF NOT EXISTS prompts_fts USING fts5 (
            search_text,
            content = 'prompts',
            content_rowid = 'id',
            tokenize = 'trigram'
        );

        CREATE TRIGGER IF NOT EXISTS prompts_fts_ai AFTER INSERT ON prompts BEGIN
            INSERT INTO prompts_fts (rowid, search_text) VALUES (new.id, new.search_text);
        END;

        CREATE TRIGGER IF NOT EXISTS prompts_fts_ad AFTER DELETE ON prompts BEGIN
            INSERT INTO prompts_fts (prompts_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
        END;

        CREATE TRIGGER IF NOT EXISTS prompts_fts_au AFTER UPDATE OF search_text ON prompts BEGIN
            INSERT INTO prompts_fts (prompts_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
            INSERT INTO prompts_fts (rowid, search_text) VALUES (new.id, new.search_text);
        END;

        CREATE TABLE IF NOT EXISTS prompt_files (
            agent              INTEGER NOT NULL,
            file_key           TEXT    NOT NULL,
            kind               INTEGER NOT NULL,
            path               TEXT    NOT NULL,
            read_offset        INTEGER NOT NULL,
            file_length        INTEGER NOT NULL,
            last_write_utc     INTEGER NOT NULL,
            file_id            TEXT,
            head_hash          TEXT    NOT NULL,
            anchor_hash        TEXT    NOT NULL,
            high_water_utc     INTEGER,
            rewrites           INTEGER NOT NULL DEFAULT 0,
            thread_id          TEXT,
            thread_cwd         TEXT,
            thread_started_utc INTEGER,
            thread_excluded    TEXT,
            updated_utc        INTEGER NOT NULL,
            PRIMARY KEY (agent, file_key)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS prompt_tombstones (
            agent       INTEGER NOT NULL,
            text_hash   TEXT    NOT NULL,
            deleted_utc INTEGER NOT NULL,
            PRIMARY KEY (agent, text_hash)
        ) WITHOUT ROWID;
        """;
}
