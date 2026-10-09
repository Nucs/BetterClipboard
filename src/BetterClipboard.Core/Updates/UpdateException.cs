namespace BetterClipboard.Core.Updates;

/// <summary>Why an update check, download or installation did not finish — the dialog words each kind differently.</summary>
public enum UpdateFailure
{
    /// <summary>The release host could not be reached: no connection, no name resolution, a TLS problem, a timeout.</summary>
    Network = 0,

    /// <summary>The host refused for now because of too many requests from this address (GitHub allows 60 an hour without an account).</summary>
    RateLimited = 1,

    /// <summary>The host answered with an error status (5xx, or one the updater does not expect).</summary>
    Server = 2,

    /// <summary>The answer was not what it must be: not a release list, a file of another size, a release without its checksum list.</summary>
    BadResponse = 3,

    /// <summary>The download's SHA-256 is not the one the release states. The file is deleted and nothing is installed.</summary>
    Verification = 4,

    /// <summary>A file could not be written or read on this PC (disk full, folder not writable).</summary>
    Storage = 5,

    /// <summary>The installer could not be started, or it reported a failure. The installed version is unchanged.</summary>
    Installer = 6,
}

/// <summary>
/// An update step failed in a way the user can be told about: <see cref="Failure"/> says which kind, and
/// <see cref="Exception.Message"/> is a complete sentence for the dialog (never a stack trace, never a secret).
/// </summary>
/// <remarks>
/// Cancellation is not a failure: a cancelled step throws <see cref="OperationCanceledException"/>.
/// </remarks>
public sealed class UpdateException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="failure">The kind of failure.</param>
    /// <param name="message">A sentence for the user.</param>
    /// <param name="innerException">The cause, for the log.</param>
    /// <param name="retryAfter">For <see cref="UpdateFailure.RateLimited"/>: when the host accepts requests again, if it said so.</param>
    public UpdateException(UpdateFailure failure, string message, Exception? innerException = null, DateTimeOffset? retryAfter = null)
        : base(message, innerException)
    {
        Failure = failure;
        RetryAfter = retryAfter;
    }

    /// <summary>The kind of failure.</summary>
    public UpdateFailure Failure { get; }

    /// <summary>When the host accepts requests again (rate limit), or <see langword="null"/> when unknown or not applicable.</summary>
    public DateTimeOffset? RetryAfter { get; }
}
