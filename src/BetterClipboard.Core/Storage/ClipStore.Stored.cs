using System.Globalization;
using System.Text;
using BetterClipboard.Core.Model;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Storage;

/// <summary>How much one union of slices holds (<see cref="ClipStore.SummarizeStored"/>).</summary>
/// <param name="Count">Entries.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="Newest">The newest entry's last use; <see langword="null"/> when there are none.</param>
/// <param name="Oldest">The oldest entry's last use; <see langword="null"/> when there are none.</param>
public sealed record StoredStats(long Count, long Bytes, DateTimeOffset? Newest, DateTimeOffset? Oldest)
{
    /// <summary>Nothing.</summary>
    public static readonly StoredStats Empty = new(0, 0, null, null);
}

/// <summary>
/// <em>Delete stored…</em> (CLAUDE.md §2.43): counting, showing and deleting the history entries an integration stored, as
/// unions of <see cref="StoredSelection"/> slices.
/// </summary>
public sealed partial class ClipStore
{
    /// <summary>
    /// The "not protected" predicate over alias <c>c</c> (<see cref="Unprotected"/> is written over the bare table name):
    /// neither pinned nor in any group.
    /// </summary>
    private const string UnprotectedOverC =
        "c.is_pinned = 0 AND NOT EXISTS (SELECT 1 FROM clip_groups cg WHERE cg.clip_id = c.id)";

    /// <summary>
    /// Counts several unions of slices in one pass over the table: how many entries each holds, their size, and their
    /// newest and oldest use. One pass, because Settings asks for every integration at once and a tab's filter may scan
    /// the whole table anyway (CLAUDE.md §2.36 measured 150 ms for 12,000 entries).
    /// </summary>
    /// <param name="unions">Each union's slices (an entry in two slices of one union counts once).</param>
    /// <returns>One result per union, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="unions"/> or one of its lists is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<StoredStats> SummarizeStored(IReadOnlyList<IReadOnlyList<StoredSelection>> unions)
    {
        ArgumentNullException.ThrowIfNull(unions);
        if (unions.Count == 0)
        {
            return [];
        }

        // Each row's protection is read once (one group lookup) and each distinct tab filter evaluated once (the LIKEs on the
        // source path are the dear part), then each union's condition — plain comparisons of those columns and the origin —
        // once per row into a 0/1 column; the aggregates only read those. Written naively (every aggregate repeating its
        // union's whole condition), 27 unions took 0.9 s over 20,000 entries (measured 2026-11-09). MATERIALIZED keeps
        // SQLite from folding the CTEs back into the outer query, which would repeat the work again.
        if (unions.Any(union => union is null))
        {
            throw new ArgumentNullException(nameof(unions));
        }

        var filters = unions.SelectMany(union => union).Select(slice => slice.Filter).Distinct().ToList();
        string FilterColumn(ClipFilter filter) => $"c.f{filters.IndexOf(filter)} = 1";
        var filterColumns = string.Concat(filters.Select((filter, k) =>
            string.Create(CultureInfo.InvariantCulture, $", coalesce({FilterPredicate(filter) ?? "1"}, 0) AS f{k}")));
        var flags = new StringBuilder();
        var aggregates = new StringBuilder();
        for (int i = 0; i < unions.Count; i++)
        {
            string condition = StoredCondition(unions[i], protectionColumn: true, filterColumn: FilterColumn);
            flags.Append(CultureInfo.InvariantCulture, $", ({condition}) AS m{i}");
            if (i > 0)
            {
                aggregates.Append(", ");
            }

            // CASE without ELSE is NULL, which count/sum/min/max skip: each column aggregates only its union's rows.
            aggregates.Append(CultureInfo.InvariantCulture,
                $"count(CASE WHEN m{i} THEN 1 END), coalesce(sum(CASE WHEN m{i} THEN b END), 0), max(CASE WHEN m{i} THEN u END), min(CASE WHEN m{i} THEN u END)");
        }

        string sql =
            $"WITH base AS MATERIALIZED (SELECT c.origin, c.size_bytes, c.last_used_utc, ({UnprotectedOverC}) AS unprotected{filterColumns} FROM clips c), " +
            $"flags AS MATERIALIZED (SELECT c.size_bytes AS b, c.last_used_utc AS u{flags} FROM base c) " +
            $"SELECT {aggregates} FROM flags;";
        using var connection = Open();
        using var command = Command(connection, sql);
        using var reader = command.ExecuteReader();
        reader.Read();
        var result = new StoredStats[unions.Count];
        for (int i = 0; i < unions.Count; i++)
        {
            int column = i * 4;
            result[i] = new StoredStats(
                reader.GetInt64(column),
                reader.GetInt64(column + 1),
                reader.IsDBNull(column + 2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(column + 2)),
                reader.IsDBNull(column + 3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(column + 3)));
        }

        return result;
    }

    /// <summary>The newest entries of a union of slices, most recently used first: the examples a part shows.</summary>
    /// <param name="union">The slices.</param>
    /// <param name="limit">How many (1 or more).</param>
    /// <returns>The entries (empty when the union holds none).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="union"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than 1.</exception>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<ClipEntry> GetStoredExamples(IReadOnlyList<StoredSelection> union, int limit)
    {
        ArgumentNullException.ThrowIfNull(union);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        using var connection = Open();
        using var command = Command(connection, $"SELECT {EntryColumns} FROM clips c WHERE {StoredCondition(union)} ORDER BY c.last_used_utc DESC, c.id DESC LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var entries = new List<ClipEntry>();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    /// <summary>
    /// Deletes every entry of a union of slices and tombstones its content, exactly as deleting the entries one by one from
    /// the panel would: an import, a screenshot catch-up or Windows' Win+R list cannot bring one back (tombstones last
    /// <see cref="TombstoneLifetime"/>; a new live copy of the same content is recorded again). Groups lose the members;
    /// tracked documents (Notepad++ tabs) keep their last text, so that text is not recorded again until it changes.
    /// </summary>
    /// <remarks>
    /// One transaction: either every entry goes, or none. Pins and groups protect nothing here: the slices say which
    /// entries go (<see cref="StoredProtection.Protected"/> ones only when the user ticked them). The file shrinks
    /// afterwards (incremental vacuum), like after a clear.
    /// </remarks>
    /// <param name="union">The slices.</param>
    /// <param name="now">The tombstones' time.</param>
    /// <returns>How many entries were deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="union"/> is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The write failed (nothing was deleted).</exception>
    public int DeleteStored(IReadOnlyList<StoredSelection> union, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(union);
        if (union.Count == 0)
        {
            return 0;
        }

        var hashes = new List<string>();
        using var connection = Open();
        using (var transaction = connection.BeginTransaction())
        {
            // Through the id subquery: the slices' predicates are written over alias c, which a DELETE's target does not take.
            using (var delete = Command(connection, $"DELETE FROM clips WHERE id IN (SELECT c.id FROM clips c WHERE {StoredCondition(union)}) RETURNING content_hash;"))
            using (var reader = delete.ExecuteReader())
            {
                while (reader.Read())
                {
                    hashes.Add(reader.GetString(0));
                }
            }

            using (var tomb = Command(connection, "INSERT OR REPLACE INTO deleted_hashes (content_hash, deleted_utc) VALUES ($hash, $now);"))
            {
                var hash = tomb.Parameters.Add("$hash", SqliteType.Text);
                tomb.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                foreach (var each in hashes)
                {
                    hash.Value = each;
                    tomb.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        if (hashes.Count > 0)
        {
            // Must run outside a transaction; returns the freed pages to the OS (pictures are the usual bulk).
            Execute(connection, "PRAGMA incremental_vacuum;");
        }

        return hashes.Count;
    }

    /// <summary>
    /// The SQL condition (over alias <c>c</c>) "the entry is in one of these slices". Built from enum constants only — the
    /// tabs' filters (<see cref="FilterPredicate"/>), origin numbers and the protection predicate — so it is safe to inline.
    /// </summary>
    /// <remarks>
    /// A tab's filter can be NULL for an entry it does not list (ShareX's <c>LIKE</c> on an entry without a source path);
    /// <c>coalesce</c> turns that into "no", or <c>NOT</c> of a protected slice could take entries of other tabs.
    /// </remarks>
    /// <param name="union">The slices; none means no entry.</param>
    /// <param name="protectionColumn">
    /// Whether the rows carry their protection precomputed as <c>c.unprotected</c> (<see cref="SummarizeStored"/>'s CTE),
    /// instead of looking up their groups in each slice.
    /// </param>
    /// <param name="filterColumn">
    /// For rows that carry each tab filter precomputed (<see cref="SummarizeStored"/>'s CTE): the condition reading a
    /// filter's column; <see langword="null"/> to inline the filter's predicate.
    /// </param>
    /// <returns>The condition, never empty.</returns>
    private static string StoredCondition(IReadOnlyList<StoredSelection> union, bool protectionColumn = false, Func<ClipFilter, string>? filterColumn = null)
    {
        if (union.Count == 0)
        {
            return "0";
        }

        string unprotected = protectionColumn ? "c.unprotected = 1" : UnprotectedOverC;

        var slices = union.Select(slice =>
        {
            var parts = new List<string> { filterColumn?.Invoke(slice.Filter) ?? $"coalesce({FilterPredicate(slice.Filter) ?? "1"}, 0)" };
            if (slice.Origins is { Count: > 0 } origins)
            {
                string list = string.Join(", ", origins.Distinct().Select(o => ((int)o).ToString(CultureInfo.InvariantCulture)));
                parts.Add(slice.ExcludeOrigins ? $"c.origin NOT IN ({list})" : $"c.origin IN ({list})");
            }

            parts.Add(slice.Protection == StoredProtection.Unprotected ? $"({unprotected})" : $"NOT ({unprotected})");
            return "(" + string.Join(" AND ", parts) + ")";
        });
        return "(" + string.Join(" OR ", slices) + ")";
    }
}
