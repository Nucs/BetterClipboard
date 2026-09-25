using System.Text;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Model;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Storage;

/// <summary>
/// Outcome of <see cref="ClipStore.Upsert"/>.
/// </summary>
/// <param name="Entry">The stored entry as it is now.</param>
/// <param name="IsNew">A new row was inserted (as opposed to merging into an existing duplicate).</param>
/// <param name="Changed">Something visible changed (new row, bumped recency, new pin); <see langword="false"/> for no-op imports.</param>
public sealed record UpsertResult(ClipEntry Entry, bool IsNew, bool Changed);

/// <summary>
/// Aggregate numbers about the history, for the settings page and tray tooltip.
/// </summary>
/// <param name="Count">Total entries.</param>
/// <param name="PinnedCount">Pinned entries.</param>
/// <param name="TotalBytes">Sum of stored payload sizes (excludes SQLite overhead, index and thumbnails).</param>
public sealed record StoreStats(long Count, long PinnedCount, long TotalBytes);

/// <summary>
/// SQLite-backed persistent clipboard history — the thing Windows' Win+V never had.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema.</b> <c>clips</c> holds one row per distinct content (unique <c>content_hash</c>) with display
/// metadata and a PNG thumbnail; <c>clip_formats</c> holds the raw payload of every stored clipboard format
/// (cascade-deleted with the clip); <c>clips_fts</c> is an external-content FTS5 <b>trigram</b> index over
/// <c>search_text</c>, kept in sync by triggers; <c>deleted_hashes</c> tombstones content the user deleted
/// so the next startup import from Windows' history does not resurrect it; <c>meta</c> stores
/// <c>last_clear_utc</c> for the same reason after "clear history".
/// </para>
/// <para>
/// <b>Threading.</b> Every public method opens its own pooled connection, so the store may be used from
/// any thread. WAL journaling lets the UI read while the history worker writes. Writers are expected to
/// be serialized by <see cref="Services.ClipHistoryService"/>; concurrent writers are still correct
/// (SQLite serializes them with busy-waiting up to the command timeout) but slower.
/// </para>
/// <para>
/// <b>Encryption at rest.</b> When constructed with a key, the whole file — every page, the WAL, the FTS
/// index and the thumbnails — is encrypted by SQLite3 Multiple Ciphers (ChaCha20-Poly1305, authenticated per
/// page), so search keeps working on decrypted pages in memory while nothing readable ever reaches the disk.
/// Temp tables/sorts are forced into memory (<c>temp_store = MEMORY</c>) because SQLite temp files are not
/// covered by the cipher. A pre-encryption (plaintext) database is encrypted in place on
/// <see cref="Initialize"/>. Where the key comes from is not this class's business (see
/// <see cref="Security.HistoryKeyVault"/>); without a key the store is plaintext, which only tests use.
/// </para>
/// </remarks>
public sealed class ClipStore
{
    /// <summary>Current schema version, stored in <c>PRAGMA user_version</c>.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Required encryption key length in bytes (256-bit).</summary>
    public const int KeyLength = 32;

    /// <summary>SQLite's "file is not a database" result code — what a wrong or missing key produces.</summary>
    private const int SqliteNotADatabase = 26;

    /// <summary>The first 16 bytes of every <b>unencrypted</b> SQLite file; encrypted files start with a random salt instead.</summary>
    private static readonly byte[] PlaintextHeader = "SQLite format 3\0"u8.ToArray();

    /// <summary>Separator for <c>clips.format_names</c>; the ASCII unit separator cannot appear in real format names.</summary>
    private const char FormatNameSeparator = '\u001F';

    /// <summary>Tombstones older than this are purged; Windows' own history never outlives a reboot anyway.</summary>
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromDays(30);

    private const string EntryColumns =
        "c.id, c.kind, c.preview, c.content_hash, c.created_utc, c.last_used_utc, c.use_count, c.is_pinned, " +
        "c.origin, c.source_app_name, c.source_app_path, c.size_bytes, c.format_names, c.image_width, " +
        "c.image_height, (c.thumbnail IS NOT NULL) AS has_thumbnail";

    private readonly string connectionString;

    /// <summary>
    /// The passphrase handed to SQLite3MC (hex of the key), or <see langword="null"/> for a plaintext store.
    /// Kept only for the one-time plaintext→encrypted migration, which needs it outside the connection string.
    /// </summary>
    private readonly string? passphrase;

    /// <summary>
    /// Binds Microsoft.Data.Sqlite to the SQLite3 Multiple Ciphers engine before the first connection.
    /// </summary>
    /// <remarks>
    /// The <c>.Core</c> flavour of Microsoft.Data.Sqlite ships no engine and only auto-initializes the
    /// stock <c>SQLitePCLRaw.batteries_v2</c> assembly by name, which SQLite3MC's bundle does not use —
    /// without this call the first <see cref="SqliteConnection.Open"/> would fail with "call
    /// SQLitePCL.raw.SetProvider()". <c>Init</c> is idempotent, so a static constructor is enough.
    /// </remarks>
    static ClipStore() => SQLitePCL.Batteries_V2.Init();

    /// <summary>
    /// Creates a store over the database file at <paramref name="databasePath"/>. Nothing touches the disk
    /// until <see cref="Initialize"/> is called.
    /// </summary>
    /// <param name="databasePath">Full path of the SQLite file; its directory is created on initialize.</param>
    /// <param name="encryptionKey">
    /// 32-byte key that encrypts the file (see the class remarks), or <see langword="null"/> for a plaintext
    /// database (tests only). The same key must be supplied on every later open — a different key makes
    /// the history unreadable (<see cref="HistoryUnreadableException"/>), there is no recovery.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is null or blank, or the key is not <see cref="KeyLength"/> bytes.</exception>
    public ClipStore(string databasePath, byte[]? encryptionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (encryptionKey is not null && encryptionKey.Length != KeyLength)
        {
            throw new ArgumentException($"The encryption key must be {KeyLength} bytes.", nameof(encryptionKey));
        }

        DatabasePath = Path.GetFullPath(databasePath);

        // Hex keeps the 256-bit key printable for PRAGMA key (which Microsoft.Data.Sqlite issues from the
        // Password keyword on every new physical connection); SQLite3MC then runs it through its KDF once
        // per physical connection, which pooling amortizes.
        passphrase = encryptionKey is null ? null : Convert.ToHexStringLower(encryptionKey);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            // Seconds SQLite keeps retrying on SQLITE_BUSY before throwing; generous because a write can
            // briefly wait on an in-flight import transaction.
            DefaultTimeout = 30,
            Password = passphrase,
        }.ToString();
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Whether the file is encrypted (a key was supplied).</summary>
    public bool IsEncrypted => passphrase is not null;

    /// <summary>
    /// Whether the last <see cref="Initialize"/> found a plaintext database from before encryption and
    /// encrypted it in place (for the log and the settings page).
    /// </summary>
    public bool MigratedFromPlaintext { get; private set; }

    /// <summary>
    /// Creates or migrates the schema, and encrypts a legacy plaintext file in place when this store has a
    /// key. Idempotent; call once at startup before any other member.
    /// </summary>
    /// <exception cref="HistoryUnreadableException">
    /// The file exists but does not decrypt with this store's key (another machine/user's key, a replaced key
    /// file, an encrypted file opened without a key) or is not a SQLite database at all.
    /// </exception>
    /// <exception cref="InvalidOperationException">The database was written by a newer BetterClipboard (downgrade refused to avoid corrupting it).</exception>
    /// <exception cref="SqliteException">The file is locked by another process beyond the timeout, or the disk is full.</exception>
    /// <exception cref="IOException">
    /// The data directory could not be created, the file header could not be read, or a plaintext database
    /// that needs encrypting is still open elsewhere.
    /// </exception>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        MigratedFromPlaintext = false;
        if (passphrase is not null && HasPlaintextHeader(DatabasePath))
        {
            EncryptPlaintextDatabase();
            MigratedFromPlaintext = true;
        }

        try
        {
            InitializeSchema();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADatabase)
        {
            // With a cipher engine a wrong key and garbage look identical (page 1 fails authentication), so
            // both are reported as one "unreadable" condition the caller can quarantine. The failed
            // connection went back to the pool still holding the file open, which would make that
            // quarantine (a folder rename) fail with a sharing violation — so the pool is emptied first.
            using (var pooled = new SqliteConnection(connectionString))
            {
                SqliteConnection.ClearPool(pooled);
            }

            throw new HistoryUnreadableException(
                IsEncrypted
                    ? $"The history database '{DatabasePath}' cannot be decrypted with this machine's key (or is not a database)."
                    : $"The history database '{DatabasePath}' is encrypted or is not a database.",
                ex);
        }
    }

    /// <summary>
    /// Moves a database file and its SQLite sidecars (<c>-wal</c>, <c>-shm</c>, <c>-journal</c>) to a new
    /// path, e.g. to adopt the pre-encryption <c>history.db</c> into a machine-bound store folder.
    /// </summary>
    /// <remarks>
    /// The <c>-wal</c> file holds committed transactions that are not yet in the main file — moving the
    /// main file without it would silently lose the newest history, hence the sidecars travel with it.
    /// Must only be called while no connection to either path is open (startup, single instance).
    /// </remarks>
    /// <param name="sourcePath">Existing database file.</param>
    /// <param name="destinationPath">Target path; must not exist.</param>
    /// <returns><see langword="false"/> when there was nothing to move (source missing).</returns>
    /// <exception cref="IOException">The destination exists or a file could not be moved.</exception>
    /// <exception cref="UnauthorizedAccessException">A file is not accessible.</exception>
    public static bool MoveDatabaseFiles(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        // Sidecars first: if a move fails midway, the main file is still at the source and the next start
        // retries; the reverse order could leave a main file at the destination with its WAL behind.
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (File.Exists(sourcePath + suffix))
            {
                File.Move(sourcePath + suffix, destinationPath + suffix, overwrite: true);
            }
        }

        File.Move(sourcePath, destinationPath, overwrite: false);
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is an existing <b>unencrypted</b> SQLite database.</summary>
    /// <param name="path">Database path.</param>
    /// <returns><see langword="true"/> for a plaintext header; <see langword="false"/> for missing, empty, short or encrypted files.</returns>
    /// <exception cref="IOException">The file exists but could not be read.</exception>
    internal static bool HasPlaintextHeader(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // FileShare.ReadWrite: another (pooled) connection of ours may still hold the file open.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length &&
               header.SequenceEqual(PlaintextHeader);
    }

    /// <summary>Creates or upgrades the schema inside one transaction (see <see cref="Initialize"/>).</summary>
    private void InitializeSchema()
    {
        using var connection = Open();

        // auto_vacuum only takes effect on an empty database (before the first table), and journal_mode
        // cannot change inside a transaction — hence both run first, outside the migration transaction.
        Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL;");
        Execute(connection, "PRAGMA journal_mode = WAL;");

        using var transaction = connection.BeginTransaction();
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"History database schema v{version} is newer than this build supports (v{SchemaVersion}). Update BetterClipboard.");
        }

        if (version < 1)
        {
            Execute(connection, SchemaV1);
            Execute(connection, $"PRAGMA user_version = {SchemaVersion};");
        }

        transaction.Commit();
        ApplyDataFixups(connection);
    }

    /// <summary>
    /// Encrypts the existing plaintext database in place with this store's key (<c>PRAGMA rekey</c>).
    /// </summary>
    /// <remarks>
    /// SQLite3MC cannot rekey a WAL-mode database, so the WAL is checkpointed away by switching to a rollback
    /// journal first; <see cref="InitializeSchema"/> switches back to WAL afterwards. The rekey rewrites every
    /// page inside one transaction, so a crash midway leaves the (still plaintext) file intact and the next
    /// start simply retries. A private, unpooled connection is used so no plaintext handle outlives it.
    /// </remarks>
    /// <exception cref="IOException">Another connection still has the database open, so WAL cannot be left.</exception>
    /// <exception cref="SqliteException">The file is locked or the rewrite failed.</exception>
    private void EncryptPlaintextDatabase()
    {
        var plaintext = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();

        using var connection = new SqliteConnection(plaintext);
        connection.Open();

        // Leaving WAL needs every other connection closed; SQLite then silently keeps "wal" instead of
        // failing, and the rekey below would fail with a far less helpful message.
        var mode = Scalar(connection, "PRAGMA journal_mode = DELETE;") as string;
        if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"The plaintext history '{DatabasePath}' is in use by another connection and cannot be encrypted (journal mode stayed '{mode}').");
        }


        // PRAGMA takes no parameters; quote() produces the same literal Microsoft.Data.Sqlite uses for
        // PRAGMA key, so the rekeyed file opens with the Password in connectionString.
        using var quote = Command(connection, "SELECT quote($p);");
        quote.Parameters.AddWithValue("$p", passphrase);
        Execute(connection, $"PRAGMA rekey = {(string)quote.ExecuteScalar()!};");
    }

    /// <summary>
    /// Stores a capture: inserts new content, or merges into the existing row with the same content hash.
    /// </summary>
    /// <remarks>
    /// Merge semantics depend on <paramref name="bumpIfExists"/>:
    /// <list type="bullet">
    /// <item><see langword="true"/> (live copy): the existing row moves to the top (recency, use count),
    /// takes the new source app, and its formats are <b>replaced</b> by this capture's — paste replays what the
    /// user copied most recently.</item>
    /// <item><see langword="false"/> (import): the existing row is left untouched except that a requested
    /// pin is added. Imported content that the user deleted (tombstone) or that predates the last
    /// "clear history" is skipped entirely.</item>
    /// </list>
    /// A live copy removes any tombstone for its hash: copying something again is explicit consent to keep it.
    /// <see cref="ClipOrigin.ShareX"/> is the hybrid. The caller bumps its duplicates (it is a new
    /// screenshot), but it never lifts a tombstone and is skipped when older than the last clear. Only
    /// <see cref="ClipOrigin.Captured"/> lifts tombstones, because the startup catch-up can rediscover a
    /// screenshot file the user deleted from history.
    /// </remarks>
    /// <param name="capture">The raw capture (formats, time, source, origin, pin request).</param>
    /// <param name="classified">Its classification from <see cref="ContentClassifier.Classify"/>.</param>
    /// <param name="image">Image analysis for image clips (dimensions, thumbnail); may be <see langword="null"/>.</param>
    /// <param name="bumpIfExists">Whether a duplicate should be treated as a fresh copy (see remarks).</param>
    /// <returns>The result, or <see langword="null"/> when an import was skipped (tombstoned or cleared).</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The database write failed (disk full, corruption, lock timeout).</exception>
    public UpsertResult? Upsert(ClipCapture capture, ClassifiedClip classified, ImageAnalysis? image, bool bumpIfExists)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(classified);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        long when = capture.CapturedAtUtc.ToUnixTimeMilliseconds();
        var hash = classified.ContentHash;
        var preview = classified.Kind == ClipKind.Image ? ContentClassifier.DescribeImage(image?.Width, image?.Height) : classified.Preview;
        var formatNames = string.Join(FormatNameSeparator, capture.Formats.Select(f => f.Name));

        long? existingId = null;
        bool existingPinned = false;
        using (var find = Command(connection, "SELECT id, is_pinned FROM clips WHERE content_hash = $hash;"))
        {
            find.Parameters.AddWithValue("$hash", hash);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                existingId = reader.GetInt64(0);
                existingPinned = reader.GetInt64(1) != 0;
            }
        }

        if (capture.Origin == ClipOrigin.Captured)
        {
            using var untomb = Command(connection, "DELETE FROM deleted_hashes WHERE content_hash = $hash;");
            untomb.Parameters.AddWithValue("$hash", hash);
            untomb.ExecuteNonQuery();
        }
        else if (existingId is null && IsSuppressedImport(connection, hash, when))
        {
            // An import must never undo the user's explicit delete/clear.
            transaction.Rollback();
            return null;
        }

        bool isNew = false;
        bool changed = false;
        long id;
        if (existingId is null)
        {
            using var insert = Command(connection,
                """
                INSERT INTO clips (kind, preview, search_text, content_hash, created_utc, last_used_utc, use_count,
                                   is_pinned, pinned_utc, origin, source_app_name, source_app_path, size_bytes,
                                   format_names, image_width, image_height, thumbnail)
                VALUES ($kind, $preview, $search, $hash, $when, $when, 1, $pinned, $pinnedUtc, $origin, $srcName,
                        $srcPath, $size, $formats, $width, $height, $thumb)
                RETURNING id;
                """);
            insert.Parameters.AddWithValue("$kind", (int)classified.Kind);
            insert.Parameters.AddWithValue("$preview", preview);
            insert.Parameters.AddWithValue("$search", classified.SearchText);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$when", when);
            insert.Parameters.AddWithValue("$pinned", capture.Pin ? 1 : 0);
            insert.Parameters.AddWithValue("$pinnedUtc", capture.Pin ? when : DBNull.Value);
            insert.Parameters.AddWithValue("$origin", (int)capture.Origin);
            insert.Parameters.AddWithValue("$srcName", (object?)capture.Source?.DisplayName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$srcPath", (object?)capture.Source?.ExecutablePath ?? DBNull.Value);
            insert.Parameters.AddWithValue("$size", capture.TotalBytes);
            insert.Parameters.AddWithValue("$formats", formatNames);
            insert.Parameters.AddWithValue("$width", (object?)image?.Width ?? DBNull.Value);
            insert.Parameters.AddWithValue("$height", (object?)image?.Height ?? DBNull.Value);
            insert.Parameters.Add("$thumb", SqliteType.Blob).Value = (object?)image?.ThumbnailPng ?? DBNull.Value;
            id = (long)insert.ExecuteScalar()!;
            InsertFormats(connection, id, capture.Formats);
            isNew = changed = true;
        }
        else if (bumpIfExists)
        {
            id = existingId.Value;
            using var update = Command(connection,
                """
                UPDATE clips SET
                    kind = $kind, preview = $preview, search_text = $search,
                    last_used_utc = max(last_used_utc, $when), use_count = use_count + 1,
                    is_pinned = max(is_pinned, $pinned),
                    pinned_utc = CASE WHEN $pinned = 1 THEN coalesce(pinned_utc, $when) ELSE pinned_utc END,
                    source_app_name = coalesce($srcName, source_app_name),
                    source_app_path = coalesce($srcPath, source_app_path),
                    size_bytes = $size, format_names = $formats,
                    image_width = coalesce($width, image_width), image_height = coalesce($height, image_height),
                    thumbnail = coalesce($thumb, thumbnail)
                WHERE id = $id;
                """);
            update.Parameters.AddWithValue("$kind", (int)classified.Kind);
            update.Parameters.AddWithValue("$preview", preview);
            update.Parameters.AddWithValue("$search", classified.SearchText);
            update.Parameters.AddWithValue("$when", when);
            update.Parameters.AddWithValue("$pinned", capture.Pin ? 1 : 0);
            update.Parameters.AddWithValue("$srcName", (object?)capture.Source?.DisplayName ?? DBNull.Value);
            update.Parameters.AddWithValue("$srcPath", (object?)capture.Source?.ExecutablePath ?? DBNull.Value);
            update.Parameters.AddWithValue("$size", capture.TotalBytes);
            update.Parameters.AddWithValue("$formats", formatNames);
            update.Parameters.AddWithValue("$width", (object?)image?.Width ?? DBNull.Value);
            update.Parameters.AddWithValue("$height", (object?)image?.Height ?? DBNull.Value);
            update.Parameters.Add("$thumb", SqliteType.Blob).Value = (object?)image?.ThumbnailPng ?? DBNull.Value;
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();

            // Latest copy wins: replay exactly what the user copied most recently.
            using (var clear = Command(connection, "DELETE FROM clip_formats WHERE clip_id = $id;"))
            {
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
            }

            InsertFormats(connection, id, capture.Formats);
            changed = true;
        }
        else
        {
            id = existingId.Value;
            if (capture.Pin && !existingPinned)
            {
                using var pin = Command(connection, "UPDATE clips SET is_pinned = 1, pinned_utc = coalesce(pinned_utc, $when) WHERE id = $id;");
                pin.Parameters.AddWithValue("$when", when);
                pin.Parameters.AddWithValue("$id", id);
                pin.ExecuteNonQuery();
                changed = true;
            }
        }

        transaction.Commit();
        var entry = GetEntry(connection, id) ?? throw new InvalidOperationException($"Clip {id} vanished inside its own transaction.");
        return new UpsertResult(entry, isNew, changed);
    }

    /// <summary>
    /// Returns one page of history.
    /// </summary>
    /// <param name="query">Search text, filter, ordering and paging.</param>
    /// <returns>The matching entries (possibly empty), without payloads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<ClipEntry> Query(ClipQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var connection = Open();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder($"SELECT {EntryColumns} FROM clips c");
        var predicates = new List<string>();

        var (fts, likes) = SearchQueryBuilder.Build(query.SearchText);
        if (fts is not null)
        {
            predicates.Add("c.id IN (SELECT rowid FROM clips_fts WHERE clips_fts MATCH $fts)");
            command.Parameters.AddWithValue("$fts", fts);
        }

        for (int i = 0; i < likes.Count; i++)
        {
            predicates.Add($"c.search_text LIKE $like{i} ESCAPE '{SearchQueryBuilder.LikeEscape}'");
            command.Parameters.AddWithValue($"$like{i}", likes[i]);
        }

        var filter = FilterPredicate(query.Filter);
        if (filter is not null)
        {
            predicates.Add(filter);
        }

        if (query.UsedSince is { } since)
        {
            predicates.Add("c.last_used_utc >= $since");
            command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        }

        if (predicates.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", predicates);
        }

        sql.Append(query.PinnedFirst ? " ORDER BY c.is_pinned DESC, c.last_used_utc DESC, c.id DESC" : " ORDER BY c.last_used_utc DESC, c.id DESC");
        sql.Append(" LIMIT $limit OFFSET $offset;");
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 1000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        command.CommandText = sql.ToString();

        var result = new List<ClipEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadEntry(reader));
        }

        return result;
    }

    /// <summary>
    /// Returns the indexed search text of the most recently used entries, for regular-expression scans
    /// that the trigram index cannot answer (the command line's <c>grep</c>).
    /// </summary>
    /// <remarks>
    /// The search text is the entry's full text (or its file paths) cut at
    /// <see cref="ContentClassifier.SearchMaxChars"/>; callers that must see past the cap reload the
    /// payload when a text is exactly that long. Images carry no text and are skipped here.
    /// </remarks>
    /// <param name="filter">The slice to scan.</param>
    /// <param name="limit">Most recent entries to return, clamped to 1–100,000.</param>
    /// <returns>(Id, Text) pairs, most recently used first.</returns>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<(long Id, string Text)> GetSearchTexts(ClipFilter filter, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var predicate = FilterPredicate(filter);
        command.CommandText =
            $"SELECT c.id, c.search_text FROM clips c WHERE c.search_text <> ''{(predicate is null ? string.Empty : " AND " + predicate)} " +
            "ORDER BY c.last_used_utc DESC, c.id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));
        var result = new List<(long, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>
    /// Reads one entry.
    /// </summary>
    /// <param name="id">The entry id.</param>
    /// <returns>The entry, or <see langword="null"/> when it does not exist (e.g. deleted meanwhile).</returns>
    /// <exception cref="SqliteException">The read failed.</exception>
    public ClipEntry? GetEntry(long id)
    {
        using var connection = Open();
        return GetEntry(connection, id);
    }

    /// <summary>
    /// Loads the stored payloads of an entry, in replay order.
    /// </summary>
    /// <param name="id">The entry id.</param>
    /// <returns>The formats; empty when the entry does not exist.</returns>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<ClipFormatData> GetFormats(long id)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT name, data FROM clip_formats WHERE clip_id = $id ORDER BY ordinal;");
        command.Parameters.AddWithValue("$id", id);
        var formats = new List<ClipFormatData>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            formats.Add(new ClipFormatData(reader.GetString(0), (byte[])reader.GetValue(1)));
        }

        return formats;
    }

    /// <summary>
    /// Loads an entry's PNG thumbnail.
    /// </summary>
    /// <param name="id">The entry id.</param>
    /// <returns>PNG bytes, or <see langword="null"/> when the entry has no thumbnail or does not exist.</returns>
    /// <exception cref="SqliteException">The read failed.</exception>
    public byte[]? GetThumbnail(long id)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT thumbnail FROM clips WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as byte[];
    }

    /// <summary>
    /// Pins or unpins an entry. Pinned entries ignore retention and survive <see cref="ClearUnpinned"/>.
    /// </summary>
    /// <param name="id">The entry id.</param>
    /// <param name="pinned">The new state.</param>
    /// <param name="now">Timestamp recorded as the pin time.</param>
    /// <returns><see langword="true"/> when the entry exists (whether or not the state actually changed).</returns>
    /// <exception cref="SqliteException">The write failed.</exception>
    public bool SetPinned(long id, bool pinned, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = Command(connection,
            "UPDATE clips SET is_pinned = $p, pinned_utc = CASE WHEN $p = 1 THEN coalesce(pinned_utc, $now) ELSE NULL END WHERE id = $id;");
        command.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Marks an entry as just used (pasted from history): moves it to the top and bumps its use count.
    /// </summary>
    /// <param name="id">The entry id.</param>
    /// <param name="now">The use time.</param>
    /// <returns><see langword="true"/> when the entry exists.</returns>
    /// <exception cref="SqliteException">The write failed.</exception>
    public bool Touch(long id, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = Command(connection, "UPDATE clips SET last_used_utc = max(last_used_utc, $now), use_count = use_count + 1 WHERE id = $id;");
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Deletes an entry and tombstones its content hash so imports from Windows' history cannot bring it back.
    /// </summary>
    /// <param name="id">The entry id.</param>
    /// <param name="now">Tombstone timestamp (tombstones expire after 30 days).</param>
    /// <returns><see langword="true"/> when something was deleted.</returns>
    /// <exception cref="SqliteException">The write failed.</exception>
    public bool Delete(long id, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string? hash;
        using (var delete = Command(connection, "DELETE FROM clips WHERE id = $id RETURNING content_hash;"))
        {
            delete.Parameters.AddWithValue("$id", id);
            hash = delete.ExecuteScalar() as string;
        }

        if (hash is null)
        {
            return false;
        }

        using (var tomb = Command(connection, "INSERT OR REPLACE INTO deleted_hashes (content_hash, deleted_utc) VALUES ($hash, $now);"))
        {
            tomb.Parameters.AddWithValue("$hash", hash);
            tomb.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            tomb.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Deletes all unpinned entries (the Win+V "Clear all" semantics) and records the clear time so
    /// imports skip anything older.
    /// </summary>
    /// <param name="now">The clear time.</param>
    /// <returns>Number of entries deleted.</returns>
    /// <exception cref="SqliteException">The write failed.</exception>
    public int ClearUnpinned(DateTimeOffset now) => Clear("DELETE FROM clips WHERE is_pinned = 0;", now);

    /// <summary>
    /// Deletes every entry including pinned ones, and records the clear time.
    /// </summary>
    /// <param name="now">The clear time.</param>
    /// <returns>Number of entries deleted.</returns>
    /// <exception cref="SqliteException">The write failed.</exception>
    public int ClearAll(DateTimeOffset now) => Clear("DELETE FROM clips;", now);

    /// <summary>
    /// Applies retention to unpinned entries: age first, then count, then total size (oldest-used first).
    /// </summary>
    /// <remarks>
    /// Runs in one transaction; afterwards an incremental vacuum returns freed pages to the OS so the file
    /// actually shrinks (large images are the usual culprit). Also expires old tombstones.
    /// </remarks>
    /// <param name="policy">The limits.</param>
    /// <param name="now">Reference time for the age limit.</param>
    /// <returns>Number of entries removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The write failed.</exception>
    public int Prune(RetentionPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        int removed = 0;
        using var connection = Open();
        using (var transaction = connection.BeginTransaction())
        {
            if (policy.MaxAge is { } maxAge && maxAge > TimeSpan.Zero)
            {
                using var byAge = Command(connection, "DELETE FROM clips WHERE is_pinned = 0 AND last_used_utc < $cutoff;");
                byAge.Parameters.AddWithValue("$cutoff", (now - maxAge).ToUnixTimeMilliseconds());
                removed += byAge.ExecuteNonQuery();
            }

            if (policy.MaxItems > 0)
            {
                using var byCount = Command(connection,
                    "DELETE FROM clips WHERE id IN (SELECT id FROM clips WHERE is_pinned = 0 ORDER BY last_used_utc DESC, id DESC LIMIT -1 OFFSET $max);");
                byCount.Parameters.AddWithValue("$max", policy.MaxItems);
                removed += byCount.ExecuteNonQuery();
            }

            if (policy.MaxTotalBytes > 0)
            {
                removed += PruneBySize(connection, policy.MaxTotalBytes);
            }

            using (var tombs = Command(connection, "DELETE FROM deleted_hashes WHERE deleted_utc < $cutoff;"))
            {
                tombs.Parameters.AddWithValue("$cutoff", (now - TombstoneLifetime).ToUnixTimeMilliseconds());
                tombs.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        if (removed > 0)
        {
            // Must run outside a transaction; frees up to all unused pages.
            Execute(connection, "PRAGMA incremental_vacuum;");
        }

        return removed;
    }

    /// <summary>
    /// Returns aggregate counts and sizes.
    /// </summary>
    /// <returns>The statistics.</returns>
    /// <exception cref="SqliteException">The read failed.</exception>
    public StoreStats GetStats()
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT count(*), coalesce(sum(is_pinned), 0), coalesce(sum(size_bytes), 0) FROM clips;");
        using var reader = command.ExecuteReader();
        reader.Read();
        return new StoreStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>
    /// Reads a small piece of integration state kept with the history (e.g. the newest ShareX screenshot
    /// already handled), stored in <c>meta</c> under <c>state.&lt;name&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Kept in the encrypted store rather than <c>settings.json</c> because it describes <i>this</i> history:
    /// when a store is quarantined and a fresh one starts, the state resets with it — a new store then
    /// starts ShareX from "now" instead of re-importing screenshots its predecessor had.
    /// </remarks>
    /// <param name="name">State name (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>).</param>
    /// <returns>The value, or <see langword="null"/> when never set.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid state name.</exception>
    /// <exception cref="SqliteException">The read failed.</exception>
    public string? GetStateValue(string name)
    {
        // Validate before opening: a bad name must fail without touching the database.
        var key = StateKey(name);
        using var connection = Open();
        using var command = Command(connection, "SELECT value FROM meta WHERE key = $key;");
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Writes a piece of integration state (see <see cref="GetStateValue"/>).
    /// </summary>
    /// <param name="name">State name.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid state name.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The write failed.</exception>
    public void SetStateValue(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var key = StateKey(name);
        using var connection = Open();
        using var command = Command(connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);");
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Maps a state name to its <c>meta</c> key. The fixed <c>state.</c> prefix keeps callers away from the
    /// store's own keys (<c>last_clear_utc</c>, <c>fixup.*</c>), which drive import suppression and migrations.
    /// </summary>
    /// <param name="name">State name.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException">Blank, too long, or with characters outside <c>[A-Za-z0-9._-]</c>.</exception>
    private static string StateKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
        {
            throw new ArgumentException($"'{name}' is not a valid state name.", nameof(name));
        }

        return "state." + name;
    }

    /// <summary>Opens a pooled connection with per-connection pragmas applied.</summary>
    /// <returns>An open connection the caller must dispose.</returns>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            // foreign_keys is per-connection and OFF by default — without it clip_formats would not cascade.
            // synchronous=NORMAL is durable enough under WAL (only the last transaction can be lost on power
            // failure, never corruption) and much faster than FULL for frequent small captures.
            // temp_store=MEMORY keeps sorter/temp-index spill files (which the cipher does not cover) off disk.
            Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
            return connection;
        }
        catch
        {
            // With a wrong key these pragmas are the first statements to touch page 1 and fail with
            // SQLITE_NOTADB. An undisposed connection would keep the file open until finalization, which
            // breaks the caller's quarantine (folder rename); disposing hands it to the pool, which
            // Initialize then clears.
            connection.Dispose();
            throw;
        }
    }

    /// <summary>SQL predicate (over alias <c>c</c>) for a filter pill, or <see langword="null"/> for <see cref="ClipFilter.All"/>.</summary>
    /// <param name="filter">The filter.</param>
    /// <returns>The predicate; built from enum constants only, so safe to inline.</returns>
    private static string? FilterPredicate(ClipFilter filter) => filter switch
    {
        ClipFilter.Pinned => "c.is_pinned = 1",
        ClipFilter.Text => $"c.kind IN ({(int)ClipKind.Text}, {(int)ClipKind.RichText}, {(int)ClipKind.Color})",
        ClipFilter.Images => $"c.kind = {(int)ClipKind.Image}",
        ClipFilter.Links => $"c.kind = {(int)ClipKind.Link}",
        ClipFilter.Files => $"c.kind = {(int)ClipKind.Files}",

        // Both ways ShareX content arrives: picked up from its folders, or copied to the clipboard by
        // ShareX.exe (then only the source app tells). LIKE is ASCII case-insensitive and treats '\' literally.
        ClipFilter.ShareX => $"(c.origin = {(int)ClipOrigin.ShareX} OR c.source_app_path LIKE '%\\ShareX.exe' OR c.source_app_name = 'ShareX')",
        _ => null,
    };

    /// <summary>
    /// One-time data fix-ups for rows written by earlier builds, each guarded by a <c>meta</c> flag so it
    /// runs exactly once per database (no schema version bump — older builds can still open the file).
    /// </summary>
    /// <remarks>
    /// <c>fixup.import_source.v1</c>: v0.1.0 stamped every imported item with a made-up source app named
    /// "Windows clipboard history" (Windows does not report the real one), which the panel then showed on
    /// every imported card. Imports now carry no source; this clears the stored label from older rows.
    /// A row that was later re-copied live has a real app path and is left alone.
    /// </remarks>
    /// <param name="connection">Open connection (outside any transaction).</param>
    private static void ApplyDataFixups(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var flag = Command(connection, "INSERT OR IGNORE INTO meta (key, value) VALUES ('fixup.import_source.v1', '1');"))
        {
            // Zero rows inserted = already applied on an earlier start.
            if (flag.ExecuteNonQuery() == 0)
            {
                return;
            }
        }

        Execute(connection,
            $"""
            UPDATE clips SET source_app_name = NULL
            WHERE origin IN ({(int)ClipOrigin.WindowsHistory}, {(int)ClipOrigin.WindowsPinned})
              AND source_app_path IS NULL
              AND source_app_name = '{LegacyImportSourceName}';
            """);
        transaction.Commit();
    }

    /// <summary>The synthetic source-app name v0.1.0 gave imported items (see <see cref="ApplyDataFixups"/>).</summary>
    private const string LegacyImportSourceName = "Windows clipboard history";

    /// <summary>Returns whether an import of <paramref name="hash"/> copied at <paramref name="when"/> must be skipped.</summary>
    /// <param name="connection">Open connection inside the caller's transaction.</param>
    /// <param name="hash">Content hash of the import.</param>
    /// <param name="when">Original copy time of the import (Unix ms).</param>
    /// <returns><see langword="true"/> when the content is tombstoned or predates the last clear.</returns>
    private static bool IsSuppressedImport(SqliteConnection connection, string hash, long when)
    {
        using var command = Command(connection,
            """
            SELECT EXISTS (SELECT 1 FROM deleted_hashes WHERE content_hash = $hash)
                OR $when <= coalesce((SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'last_clear_utc'), -1);
            """);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$when", when);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    /// <summary>Runs a clearing DELETE and records <c>last_clear_utc</c> in one transaction.</summary>
    /// <param name="deleteSql">The DELETE statement.</param>
    /// <param name="now">The clear time.</param>
    /// <returns>Rows deleted.</returns>
    private int Clear(string deleteSql, DateTimeOffset now)
    {
        int removed;
        using var connection = Open();
        using (var transaction = connection.BeginTransaction())
        {
            using (var delete = Command(connection, deleteSql))
            {
                removed = delete.ExecuteNonQuery();
            }

            using (var meta = Command(connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ('last_clear_utc', $now);"))
            {
                meta.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                meta.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        if (removed > 0)
        {
            Execute(connection, "PRAGMA incremental_vacuum;");
        }

        return removed;
    }

    /// <summary>Deletes least-recently-used unpinned rows until their total size fits <paramref name="maxBytes"/>.</summary>
    /// <param name="connection">Open connection inside the caller's transaction.</param>
    /// <param name="maxBytes">The size cap.</param>
    /// <returns>Rows deleted.</returns>
    private static int PruneBySize(SqliteConnection connection, long maxBytes)
    {
        long total = Convert.ToInt64(Scalar(connection, "SELECT coalesce(sum(size_bytes), 0) FROM clips WHERE is_pinned = 0;"));
        if (total <= maxBytes)
        {
            return 0;
        }

        var victims = new List<long>();
        using (var oldest = Command(connection, "SELECT id, size_bytes FROM clips WHERE is_pinned = 0 ORDER BY last_used_utc ASC, id ASC;"))
        using (var reader = oldest.ExecuteReader())
        {
            while (total > maxBytes && reader.Read())
            {
                victims.Add(reader.GetInt64(0));
                total -= reader.GetInt64(1);
            }
        }

        using var delete = Command(connection, "DELETE FROM clips WHERE id = $id;");
        var idParameter = delete.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in victims)
        {
            idParameter.Value = id;
            delete.ExecuteNonQuery();
        }

        return victims.Count;
    }

    /// <summary>Inserts the payload rows of a clip, preserving their order as ordinals.</summary>
    /// <param name="connection">Open connection inside the caller's transaction.</param>
    /// <param name="clipId">Owning clip id.</param>
    /// <param name="formats">Formats in replay order.</param>
    private static void InsertFormats(SqliteConnection connection, long clipId, IReadOnlyList<ClipFormatData> formats)
    {
        using var insert = Command(connection, "INSERT INTO clip_formats (clip_id, ordinal, name, data) VALUES ($clip, $ordinal, $name, $data);");
        insert.Parameters.AddWithValue("$clip", clipId);
        var ordinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var data = insert.Parameters.Add("$data", SqliteType.Blob);
        for (int i = 0; i < formats.Count; i++)
        {
            ordinal.Value = i;
            name.Value = formats[i].Name;
            data.Value = formats[i].Data;
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Reads one entry through an existing connection.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="id">The entry id.</param>
    /// <returns>The entry or <see langword="null"/>.</returns>
    private static ClipEntry? GetEntry(SqliteConnection connection, long id)
    {
        using var command = Command(connection, $"SELECT {EntryColumns} FROM clips c WHERE c.id = $id;");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    /// <summary>Materializes the current row of a reader selecting <see cref="EntryColumns"/>.</summary>
    /// <param name="reader">A reader positioned on a row.</param>
    /// <returns>The entry snapshot.</returns>
    private static ClipEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Kind = (ClipKind)reader.GetInt32(1),
        Preview = reader.GetString(2),
        ContentHash = reader.GetString(3),
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        LastUsedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
        UseCount = reader.GetInt32(6),
        IsPinned = reader.GetInt64(7) != 0,
        Origin = (ClipOrigin)reader.GetInt32(8),
        SourceAppName = reader.IsDBNull(9) ? null : reader.GetString(9),
        SourceAppPath = reader.IsDBNull(10) ? null : reader.GetString(10),
        SizeBytes = reader.GetInt64(11),
        FormatNames = reader.GetString(12).Split(FormatNameSeparator, StringSplitOptions.RemoveEmptyEntries),
        ImageWidth = reader.IsDBNull(13) ? null : reader.GetInt32(13),
        ImageHeight = reader.IsDBNull(14) ? null : reader.GetInt32(14),
        HasThumbnail = reader.GetInt64(15) != 0,
    };

    /// <summary>Creates a command bound to <paramref name="connection"/>.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="sql">Statement text.</param>
    /// <returns>The command (caller disposes).</returns>
    private static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    /// <summary>Executes a statement without results.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="sql">Statement text (may contain several statements).</param>
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.ExecuteNonQuery();
    }

    /// <summary>Executes a statement returning one value.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="sql">Statement text.</param>
    /// <returns>The first column of the first row.</returns>
    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return command.ExecuteScalar();
    }

    /// <summary>Schema v1. External-content FTS5 with triggers; see the class remarks.</summary>
    private const string SchemaV1 =
        """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS clips (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            kind            INTEGER NOT NULL,
            preview         TEXT    NOT NULL,
            search_text     TEXT    NOT NULL DEFAULT '',
            content_hash    TEXT    NOT NULL UNIQUE,
            created_utc     INTEGER NOT NULL,
            last_used_utc   INTEGER NOT NULL,
            use_count       INTEGER NOT NULL DEFAULT 1,
            is_pinned       INTEGER NOT NULL DEFAULT 0,
            pinned_utc      INTEGER,
            origin          INTEGER NOT NULL DEFAULT 0,
            source_app_name TEXT,
            source_app_path TEXT,
            size_bytes      INTEGER NOT NULL DEFAULT 0,
            format_names    TEXT    NOT NULL DEFAULT '',
            image_width     INTEGER,
            image_height    INTEGER,
            thumbnail       BLOB
        );

        CREATE INDEX IF NOT EXISTS ix_clips_recency ON clips (is_pinned DESC, last_used_utc DESC);
        CREATE INDEX IF NOT EXISTS ix_clips_kind ON clips (kind, last_used_utc DESC);

        CREATE TABLE IF NOT EXISTS clip_formats (
            clip_id INTEGER NOT NULL REFERENCES clips (id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            name    TEXT    NOT NULL,
            data    BLOB    NOT NULL,
            PRIMARY KEY (clip_id, ordinal)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS deleted_hashes (
            content_hash TEXT PRIMARY KEY,
            deleted_utc  INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE VIRTUAL TABLE IF NOT EXISTS clips_fts USING fts5 (
            search_text,
            content = 'clips',
            content_rowid = 'id',
            tokenize = 'trigram'
        );

        CREATE TRIGGER IF NOT EXISTS clips_fts_ai AFTER INSERT ON clips BEGIN
            INSERT INTO clips_fts (rowid, search_text) VALUES (new.id, new.search_text);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_fts_ad AFTER DELETE ON clips BEGIN
            INSERT INTO clips_fts (clips_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
        END;

        CREATE TRIGGER IF NOT EXISTS clips_fts_au AFTER UPDATE OF search_text ON clips BEGIN
            INSERT INTO clips_fts (clips_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
            INSERT INTO clips_fts (rowid, search_text) VALUES (new.id, new.search_text);
        END;
        """;
}
