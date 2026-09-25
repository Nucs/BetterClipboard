namespace BetterClipboard.Core.Storage;

/// <summary>
/// The history database exists but cannot be decrypted/read with the key it was opened with — typically an
/// encrypted database opened with another machine's/user's key (or none), a key file that was replaced, or
/// a file that is not a database at all (SQLite <c>SQLITE_NOTADB</c>, error 26).
/// </summary>
/// <remarks>
/// Deliberately distinct from transient failures (locks, disk full): callers react to this one by setting
/// the store aside (quarantine) and starting fresh, which would be wrong for a merely busy database.
/// </remarks>
public sealed class HistoryUnreadableException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The underlying SQLite error.</param>
    public HistoryUnreadableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
