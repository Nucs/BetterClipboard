using System.Diagnostics;
using System.Globalization;

namespace BetterClipboard.Core.Diagnostics;

/// <summary>
/// Minimal process-wide file logger (daily files, 14-day retention) plus debugger output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Privacy rule:</b> never log clipboard <i>content</i> — only sizes, format names, app names and
/// errors. Logs are the first thing users paste into bug reports.
/// </para>
/// <para>
/// Before <see cref="Initialize"/> (or when the log directory is unwritable) messages go only to the
/// debugger, so logging can never crash or block startup. Writes are serialized with a lock and flushed
/// per line; the volume is tiny (a few lines per copy at most).
/// </para>
/// </remarks>
public static class AppLog
{
    private static readonly Lock Gate = new();
    private static string? directory;
    private static StreamWriter? writer;
    private static DateOnly writerDate;

    /// <summary>
    /// Starts writing log files into <paramref name="logDirectory"/> and deletes files older than 14 days.
    /// </summary>
    /// <param name="logDirectory">Directory for <c>betterclipboard-yyyyMMdd.log</c> files (created if missing).</param>
    public static void Initialize(string logDirectory)
    {
        lock (Gate)
        {
            directory = logDirectory;
            try
            {
                Directory.CreateDirectory(logDirectory);
                foreach (var old in Directory.EnumerateFiles(logDirectory, "betterclipboard-*.log"))
                {
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-14))
                    {
                        File.Delete(old);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[BetterClipboard] log init failed: {ex.Message}");
            }
        }
    }

    /// <summary>Logs an informational message.</summary>
    /// <param name="message">Content-free message text.</param>
    public static void Info(string message) => Write("INF", message, null);

    /// <summary>Logs a recoverable problem.</summary>
    /// <param name="message">Content-free message text.</param>
    public static void Warn(string message) => Write("WRN", message, null);

    /// <summary>Logs a failure, with the exception's type, message and stack.</summary>
    /// <param name="message">Content-free message text.</param>
    /// <param name="exception">The exception, if any.</param>
    public static void Error(string message, Exception? exception = null) => Write("ERR", message, exception);

    /// <summary>Closes the current log file (call on exit so the last lines are flushed).</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    /// <summary>Formats and writes one line; swallows I/O failures by design.</summary>
    /// <param name="level">Three-letter level tag.</param>
    /// <param name="message">Message text.</param>
    /// <param name="exception">Optional exception.</param>
    private static void Write(string level, string message, Exception? exception)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] [{Environment.CurrentManagedThreadId,3}] {message}");
        if (exception is not null)
        {
            line += Environment.NewLine + "    " + exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ", StringComparison.Ordinal);
        }

        Debug.WriteLine(line);
        lock (Gate)
        {
            if (directory is null)
            {
                return;
            }

            try
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (writer is null || writerDate != today)
                {
                    writer?.Dispose();
                    var path = Path.Combine(directory, $"betterclipboard-{today:yyyyMMdd}.log");
                    // FileShare.ReadWrite so users can tail the log while the app runs.
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    writerDate = today;
                }

                writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down; drop the line and retry the file next time.
                writer = null;
                Debug.WriteLine($"[BetterClipboard] log write failed: {ex.Message}");
            }
        }
    }
}
