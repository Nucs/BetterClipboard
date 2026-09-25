namespace BetterClipboard.Core.Model;

/// <summary>
/// Best-effort identity of the application that put content on the clipboard (resolved from the
/// clipboard owner window, falling back to the foreground window).
/// </summary>
/// <remarks>
/// Attribution is heuristic: apps that copy via a helper process, apps that empty the clipboard with a
/// NULL owner, and elevated processes (which a non-elevated BetterClipboard cannot query) can be
/// misattributed or anonymous. Never make security decisions from it beyond the user's own
/// "ignored apps" list.
/// </remarks>
/// <param name="ProcessName">Executable name without extension (e.g. <c>Code</c>); used for the ignore list.</param>
/// <param name="ExecutablePath">Full image path when it could be queried; <see langword="null"/> for protected processes.</param>
/// <param name="DisplayName">Friendly name (file description, e.g. "Visual Studio Code"), falling back to <paramref name="ProcessName"/>.</param>
public sealed record SourceAppInfo(string ProcessName, string? ExecutablePath, string DisplayName);
