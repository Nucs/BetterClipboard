using BetterClipboard.Core.Model;
using Microsoft.Data.Sqlite;

namespace BetterClipboard.Core.Storage;

/// <summary>
/// A picture already in the history with no file known for it, from a tab that lists screenshots (ShareX's, the Snipping
/// tab's): a candidate of the one-time search for the files of screenshots stored before BetterClipboard kept files
/// (<see cref="ClipStore.FindPicturesWithoutFile"/>).
/// </summary>
/// <param name="Id">The entry's id.</param>
/// <param name="ContentHash">Its content hash: the picture's pixel hash (or, for a picture too large to hash by pixels, its bytes').</param>
/// <param name="CreatedUtc">When the entry was first stored: the file's write time for a screenshot read from its folder, the copy's time for one copied first.</param>
public sealed record PictureWithoutFile(long Id, string ContentHash, DateTimeOffset CreatedUtc);

/// <summary>The store's part for a picture's source file (the file columns of <c>clips</c>; see the class remarks).</summary>
public sealed partial class ClipStore
{
    /// <summary>The source-file columns, in the order <see cref="AddSourceFileParameters"/> names their values.</summary>
    private static readonly (string Name, string Type)[] SourceFileColumns =
    [
        ("file_path", "TEXT"), ("file_size", "INTEGER"), ("file_created_utc", "INTEGER"), ("file_modified_utc", "INTEGER"), ("file_format", "TEXT"),
    ];

    /// <summary>
    /// Records the file of a picture already in the history (the one-time search for earlier screenshots' files), unless
    /// the entry has a file already.
    /// </summary>
    /// <remarks>
    /// Never replaces a file: one an entry has came from a newer save (a live screenshot, or a bump), and the search only
    /// fills what is missing — so running it again, or racing a live import, can never take a newer path away. The list
    /// order (<c>last_used_utc</c>) is not touched: knowing a picture's file is not using it.
    /// </remarks>
    /// <param name="id">The entry.</param>
    /// <param name="file">The file found for it, with its details.</param>
    /// <returns>The entry as it is now when the file was recorded; <see langword="null"/> when the entry is gone or already had a file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">The write failed (disk full, lock timeout).</exception>
    public ClipEntry? SetSourceFile(long id, SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var connection = Open();
        return WriteSourceFile(connection, id, file, onlyIfMissing: true) ? GetEntry(connection, id) : null;
    }

    /// <summary>
    /// Lists the pictures of a tab that has screenshots (<see cref="ClipFilter.ShareX"/>, <see cref="ClipFilter.Snipping"/>) for
    /// which no file is known, newest first: the candidates of the one-time search for earlier screenshots' files.
    /// </summary>
    /// <remarks>
    /// The tab's own filter decides (origin, or the app that copied the picture), so a screenshot copied by ShareX before its
    /// file was read is a candidate too. Content-free: ids, hashes and times only.
    /// </remarks>
    /// <param name="filter">The tab whose pictures are listed.</param>
    /// <returns>The candidates (possibly none).</returns>
    /// <exception cref="ArgumentException"><paramref name="filter"/> is a tab without a filter of its own (<see cref="ClipFilter.All"/>).</exception>
    /// <exception cref="SqliteException">The read failed.</exception>
    public IReadOnlyList<PictureWithoutFile> FindPicturesWithoutFile(ClipFilter filter)
    {
        var predicate = FilterPredicate(filter) ?? throw new ArgumentException("A tab with a filter of its own is required.", nameof(filter));
        using var connection = Open();
        using var command = Command(connection,
            $"SELECT c.id, c.content_hash, c.created_utc FROM clips c " +
            $"WHERE c.kind = {(int)ClipKind.Image} AND c.file_path IS NULL AND coalesce({predicate}, 0) " +
            "ORDER BY c.created_utc DESC, c.id DESC;");
        using var reader = command.ExecuteReader();
        var found = new List<PictureWithoutFile>();
        while (reader.Read())
        {
            found.Add(new PictureWithoutFile(reader.GetInt64(0), reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        }

        return found;
    }

    /// <summary>
    /// Adds the nullable source-file columns of <c>clips</c> when missing (additive and unversioned like the groups' column,
    /// see the class remarks). Existing rows start with no file, which is the truth for every row of a store from before.
    /// </summary>
    /// <remarks>Idempotent. Runs inside the caller's migration transaction; nullable columns without defaults are metadata-only changes, instant on any history size.</remarks>
    /// <param name="connection">Open connection inside the migration transaction.</param>
    /// <exception cref="SqliteException">The schema could not be read or altered (lock timeout, disk full); the migration transaction rolls back.</exception>
    private static void EnsureSourceFileColumns(SqliteConnection connection)
    {
        foreach (var (name, type) in SourceFileColumns)
        {
            using (var columns = Command(connection, "SELECT 1 FROM pragma_table_info('clips') WHERE name = $name;"))
            {
                columns.Parameters.AddWithValue("$name", name);
                if (columns.ExecuteScalar() is not null)
                {
                    continue;
                }
            }

            // Names and types come from the fixed list above, never from input: safe to splice.
            Execute(connection, $"ALTER TABLE clips ADD COLUMN {name} {type};");
        }
    }

    /// <summary>
    /// Binds a file's values as <c>$filePath</c>, <c>$fileSize</c>, <c>$fileCreated</c>, <c>$fileModified</c> and
    /// <c>$fileFormat</c> (all <c>NULL</c> without a file), for the insert and bump of <see cref="UpsertCore"/>.
    /// </summary>
    /// <param name="command">The command whose text uses those names.</param>
    /// <param name="file">The capture's file, or <see langword="null"/>.</param>
    private static void AddSourceFileParameters(SqliteCommand command, SourceFile? file)
    {
        command.Parameters.AddWithValue("$filePath", (object?)file?.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileSize", (object?)file?.SizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileCreated", (object?)file?.CreatedUtc?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileModified", (object?)file?.ModifiedUtc?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileFormat", (object?)file?.Format ?? DBNull.Value);
    }

    /// <summary>Writes a file's columns on one row, as a whole.</summary>
    /// <param name="connection">Open connection (inside the caller's transaction, when it has one).</param>
    /// <param name="id">The row.</param>
    /// <param name="file">The file.</param>
    /// <param name="onlyIfMissing">Write only when the row has no file yet (an import, the one-time search).</param>
    /// <returns>Whether a row was written.</returns>
    /// <exception cref="SqliteException">The write failed.</exception>
    private static bool WriteSourceFile(SqliteConnection connection, long id, SourceFile file, bool onlyIfMissing)
    {
        using var command = Command(connection,
            "UPDATE clips SET file_path = $filePath, file_size = $fileSize, file_created_utc = $fileCreated, " +
            "file_modified_utc = $fileModified, file_format = $fileFormat WHERE id = $id" +
            (onlyIfMissing ? " AND file_path IS NULL;" : ";"));
        AddSourceFileParameters(command, file);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }
}
