namespace BetterClipboard.Core.Storage;

/// <summary>The store's self-check, used after a history was copied from another edition (CLAUDE.md §2.47).</summary>
public sealed partial class ClipStore
{
    /// <summary>
    /// Has SQLite check the database (<c>PRAGMA quick_check</c>): every page is read and the table and index structures
    /// are verified. Cheaper than <c>integrity_check</c> (it does not compare index contents with their tables), and
    /// enough to catch what a bad copy produces: a truncated file, a page that does not decrypt, a broken tree.
    /// </summary>
    /// <remarks>
    /// Reads the whole database once (about a second per few hundred megabytes), so it runs where a copy must be proven
    /// before its source is deleted, not at every start.
    /// </remarks>
    /// <returns><c>ok</c> for a sound database, otherwise SQLite's first finding.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database could not be read at all (then it is not sound either).</exception>
    public string QuickCheck()
    {
        using var connection = Open();
        return Scalar(connection, "PRAGMA quick_check(1);") as string ?? "no answer";
    }
}
