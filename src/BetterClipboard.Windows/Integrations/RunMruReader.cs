using BetterClipboard.Core.Integrations;
using BetterClipboard.Windows.Interop;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Integrations;

/// <summary>
/// One look at Windows' Win+R history: the commands, most recent first, and when the key was last written.
/// </summary>
/// <param name="KeyExists">
/// Whether the key exists. It is missing until Win+R was first used on this account (and after a cleanup tool
/// deleted it), which reads as an empty list.
/// </param>
/// <param name="Commands">The commands as <see cref="RunMru.Parse"/> returns them (no show-command suffix, no duplicates).</param>
/// <param name="LastWriteUtc">
/// The key's last write — when the newest command ran, the only time Windows keeps; <see langword="null"/> when the
/// key is missing or its metadata could not be read.
/// </param>
public sealed record RunMruState(bool KeyExists, IReadOnlyList<string> Commands, DateTimeOffset? LastWriteUtc)
{
    /// <summary>The state of a missing key: no commands, no time.</summary>
    public static readonly RunMruState Missing = new(false, [], null);
}

/// <summary>
/// Reads Windows' Win+R history (<see cref="RunMru.DefaultKeyPath"/> under <c>HKEY_CURRENT_USER</c>) — read
/// only: BetterClipboard never writes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never read a half-written list.</b> Windows stores one run as two writes: the command into its letter's
/// value and the new order into <c>MRUList</c>. A read between the two sees either the evicted command at the
/// front or the new command at the back, and either one looks exactly like real runs to
/// <see cref="RunMru.RunsSince"/> (at worst all 26 commands "run again"). <see cref="ReadSettledAsync"/>
/// therefore only accepts a read when the key had been quiet for <see cref="SettleTime"/> before it and was not
/// written during it. The two writes come microseconds apart from one thread, so 150 ms is ample.
/// </para>
/// <para>
/// <b>Footgun:</b> <see cref="Read"/> skips that check; it is for tests and diagnostics that control the key.
/// </para>
/// </remarks>
public static class RunMruReader
{
    /// <summary>
    /// How long the key must have been left alone before its contents are trusted (see the class remarks).
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Most reads <see cref="ReadSettledAsync"/> tries before it accepts the last one: a key that never settles
    /// for ~1.5 s is not being written by the Run dialog (it writes once per run).
    /// </summary>
    private const int MaxSettleAttempts = 10;

    /// <summary>The value holding the letters, most recent first.</summary>
    private const string MruListValueName = "MRUList";

    /// <summary>
    /// The key to read: the override (tests and isolated dev runs point it at a scratch key, so the user's real
    /// Win+R history is never touched), or <see cref="RunMru.DefaultKeyPath"/>.
    /// </summary>
    /// <remarks>
    /// Accepts the forms people paste: <c>Software\X\RunMRU</c>, <c>HKCU\Software\X\RunMRU</c>,
    /// <c>HKCU:\Software\X\RunMRU</c> (PowerShell) and <c>HKEY_CURRENT_USER\Software\X\RunMRU</c>. Always under
    /// <c>HKEY_CURRENT_USER</c>: another hive is not a Win+R history.
    /// </remarks>
    /// <param name="overrideValue">The override (e.g. the <c>BETTERCLIPBOARD_RUNMRU_KEY</c> variable); blank = none.</param>
    /// <returns>A path relative to <c>HKEY_CURRENT_USER</c>, never empty.</returns>
    public static string ResolveKeyPath(string? overrideValue)
    {
        var path = overrideValue?.Trim() ?? string.Empty;
        foreach (var prefix in (ReadOnlySpan<string>)["HKEY_CURRENT_USER\\", "HKCU:\\", "HKCU\\"])
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[prefix.Length..];
                break;
            }
        }

        path = path.Trim('\\');
        return path.Length == 0 ? RunMru.DefaultKeyPath : path;
    }

    /// <summary>
    /// Reads the key once, without the settle check (see the class remarks for why the app must not use this).
    /// </summary>
    /// <param name="keyPath">The key, relative to <c>HKEY_CURRENT_USER</c>.</param>
    /// <returns>The state; <see cref="RunMruState.Missing"/> when the key does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="keyPath"/> is null or blank.</exception>
    /// <exception cref="System.Security.SecurityException">The key may not be read (policy).</exception>
    /// <exception cref="UnauthorizedAccessException">The key's permissions deny reading.</exception>
    /// <exception cref="IOException">The key was deleted while it was being read.</exception>
    public static RunMruState Read(string keyPath) => ReadOnce(keyPath, out _);

    /// <summary>
    /// Reads the key once it has been quiet for <see cref="SettleTime"/>, retrying a read that overlapped a write.
    /// </summary>
    /// <remarks>
    /// Waits at most about <see cref="MaxSettleAttempts"/> × <see cref="SettleTime"/>; usually it does not wait at
    /// all (the last run was long ago). A clock that moved backwards makes every write look "just now"; the
    /// write-during-read check still applies then, and the attempts are bounded.
    /// </remarks>
    /// <param name="keyPath">The key, relative to <c>HKEY_CURRENT_USER</c>.</param>
    /// <param name="time">The clock the key's last write is compared with (the system clock; tests pass fakes).</param>
    /// <param name="cancellationToken">Cancels the waiting.</param>
    /// <returns>The state; <see cref="RunMruState.Missing"/> when the key does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="keyPath"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    /// <exception cref="System.Security.SecurityException">The key may not be read (policy).</exception>
    /// <exception cref="UnauthorizedAccessException">The key's permissions deny reading.</exception>
    /// <exception cref="IOException">The key was deleted while it was being read.</exception>
    public static async Task<RunMruState> ReadSettledAsync(string keyPath, TimeProvider time, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(time);
        var state = RunMruState.Missing;
        for (int attempt = 0; attempt < MaxSettleAttempts; attempt++)
        {
            state = ReadOnce(keyPath, out bool writtenDuringRead);
            if (!state.KeyExists || state.LastWriteUtc is not { } lastWrite)
            {
                // Nothing to tear (a missing key), or no way to tell (metadata unreadable): take it as read.
                return state;
            }

            var quietFor = time.GetUtcNow() - lastWrite;

            // A last write further in the future than one settle period means the clock moved backwards; waiting
            // would not help, so only the write-during-read check applies then.
            bool settled = quietFor >= SettleTime || quietFor < -SettleTime;
            if (settled && !writtenDuringRead)
            {
                return state;
            }

            var wait = writtenDuringRead || quietFor < TimeSpan.Zero ? SettleTime : SettleTime - quietFor;
            await Task.Delay(wait < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : wait, time, cancellationToken).ConfigureAwait(false);
        }

        // Written continuously for ~1.5 s, which the Run dialog never does: the last read is the best there is.
        return state;
    }

    /// <summary>
    /// The key's last-write time (a cheap change stamp: any value write moves it).
    /// </summary>
    /// <param name="key">An open key.</param>
    /// <returns>The time, or <see langword="null"/> when the metadata could not be read.</returns>
    /// <exception cref="ObjectDisposedException"><paramref name="key"/> was already closed.</exception>
    internal static DateTimeOffset? LastWriteTime(RegistryKey key)
    {
        int error = NativeMethods.RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out long fileTime);
        if (error != 0 || fileTime <= 0)
        {
            return null;
        }

        return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
    }

    /// <summary>Reads the key once and reports whether it was written while being read.</summary>
    /// <param name="keyPath">The key, relative to <c>HKEY_CURRENT_USER</c>.</param>
    /// <param name="writtenDuringRead">Whether the last-write time moved between the start and the end of the read.</param>
    /// <returns>The state; <see cref="RunMruState.Missing"/> when the key does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="keyPath"/> is null or blank.</exception>
    /// <exception cref="System.Security.SecurityException">The key may not be read (policy).</exception>
    /// <exception cref="UnauthorizedAccessException">The key's permissions deny reading.</exception>
    /// <exception cref="IOException">The key was deleted while it was being read.</exception>
    private static RunMruState ReadOnce(string keyPath, out bool writtenDuringRead)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        writtenDuringRead = false;
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null)
        {
            return RunMruState.Missing;
        }

        var before = LastWriteTime(key);

        // Raw strings: a command typed with %VARIABLES% is stored and shown as typed, expanded only when run.
        var mruList = key.GetValue(MruListValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (char letter in mruList ?? string.Empty)
        {
            if (letter is >= 'a' and <= 'z'
                && key.GetValue(letter.ToString(), null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
            {
                values[letter.ToString()] = value;
            }
        }

        var after = LastWriteTime(key);
        writtenDuringRead = before != after;
        return new RunMruState(true, RunMru.Parse(mruList, values), after);
    }
}
