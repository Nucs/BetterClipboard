namespace BetterClipboard.Core.NotepadPlusPlus;

/// <summary>
/// Decides which files one running Notepad++ has just opened, from what its plugin messages report: the ids of its open
/// documents (buffer ids) and, for the ones that need it, their paths (CLAUDE.md §2.41). One tracker per Notepad++ process.
/// </summary>
/// <remarks>
/// <para>
/// <b>What counts as opened.</b> A document that was not open at the previous look and is a file on disk (a full path; an
/// untitled "new 3" never counts). Switching between tabs opens nothing. A document whose path changed while it stayed open
/// (Save As, Rename) counts too: that file is now open in Notepad++. Closing a file and opening it again counts again — that is
/// what moves its entry from its old date to now.
/// </para>
/// <para>
/// <b>The session Notepad++ restores is not opened.</b> Notepad++ loads its last session before it shows its window
/// (<c>Notepad_plus_Window::init</c>: <c>loadLastSession</c>, then <c>ShowWindow</c>, then the command line's files). So the
/// tracker learns nothing until the window is visible, and the documents open at that first look are a baseline: not
/// recorded — except the files the process was started with (<see cref="NotepadPlusPlusCommandLine"/>), when the process
/// started while BetterClipboard watched. A Notepad++ that was already running when watching began has every document in the
/// baseline.
/// </para>
/// <para>
/// <b>Ids are addresses.</b> Notepad++'s buffer id is the document's memory address, which a later document may reuse. A
/// document closed and reopened between two looks with the same address is missed; looks follow every change of
/// Notepad++'s title, so that takes a close and a reopen in the background within moments.
/// </para>
/// <para>Not thread-safe: one caller (the watcher's thread) drives it.</para>
/// </remarks>
public sealed class NotepadPlusPlusOpenTracker
{
    /// <summary>Paths of the documents open at the previous look, by buffer id (<see langword="null"/> = untitled or unread).</summary>
    private readonly Dictionary<long, string?> known = [];

    /// <summary>The files the process was started with, still to be matched (case-insensitive).</summary>
    private readonly HashSet<string> startupFiles;

    /// <summary>Whether the baseline was taken (the window was visible at a look).</summary>
    private bool hasBaseline;

    /// <summary>Creates the tracker for one Notepad++ process.</summary>
    /// <param name="startupFiles">
    /// The files the process was started to open (<see cref="NotepadPlusPlusCommandLine.FilesToOpen"/>) when it started while
    /// BetterClipboard watched; empty otherwise (a Notepad++ found already running has nothing to add).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="startupFiles"/> is <see langword="null"/>.</exception>
    public NotepadPlusPlusOpenTracker(IEnumerable<string> startupFiles)
    {
        ArgumentNullException.ThrowIfNull(startupFiles);
        this.startupFiles = new HashSet<string>(startupFiles.Select(Normalize), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether the baseline was taken: the window has been seen visible.</summary>
    public bool HasBaseline => hasBaseline;

    /// <summary>
    /// How many documents were open at the last look: files, untitled tabs, and the empty document Notepad++ keeps in its
    /// second view while that view is hidden (its own document count includes it). For people, <see cref="OpenFileCount"/>.
    /// </summary>
    public int OpenCount => known.Count;

    /// <summary>
    /// How many files were open at the last look, each counted once: the number a person sees as files in Notepad++'s tabs.
    /// </summary>
    /// <remarks>
    /// Untitled tabs are left out (the unsaved-tabs half covers them), and so is the hidden second view's empty document,
    /// which made a status line read "6 documents open" over 5 tabs (found in the live check, 2026-11-08). A file shown in
    /// both views counts once.
    /// </remarks>
    public int OpenFileCount => known.Values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// Takes one look: the documents open now and the one in front, and returns the files opened since the previous look.
    /// </summary>
    /// <param name="bufferIds">Every open document's buffer id, both views (an id cloned into both views may appear twice).</param>
    /// <param name="currentId">The buffer id of the document in front (its path is read again, to see a Save As).</param>
    /// <param name="windowVisible">Whether Notepad++'s main window is visible (it is hidden while the session loads).</param>
    /// <param name="resolvePath">Reads a document's path by buffer id; <see langword="null"/> when it cannot (closed meanwhile, unreachable).</param>
    /// <returns>The full paths opened since the previous look, in tab order, without duplicates.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bufferIds"/> or <paramref name="resolvePath"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<string> Observe(IReadOnlyList<long> bufferIds, long currentId, bool windowVisible, Func<long, string?> resolvePath)
    {
        ArgumentNullException.ThrowIfNull(bufferIds);
        ArgumentNullException.ThrowIfNull(resolvePath);
        if (!windowVisible && !hasBaseline)
        {
            // The session is still loading (or Notepad++ starts in the tray): nothing is learned yet.
            return [];
        }

        var opened = new List<string>();
        var seenNow = new HashSet<long>();
        bool takingBaseline = !hasBaseline;
        foreach (var id in bufferIds)
        {
            if (id == 0 || !seenNow.Add(id))
            {
                continue;
            }

            bool isNew = !known.TryGetValue(id, out var previousPath);
            if (!isNew && id != currentId)
            {
                // A document seen before and not in front: no need to read its path again.
                continue;
            }

            var path = FileOrNull(resolvePath(id));
            known[id] = path;
            if (path is null)
            {
                continue;
            }

            if (takingBaseline)
            {
                // Restored from the session: opened only when the process was started to open it.
                if (startupFiles.Remove(Normalize(path)))
                {
                    AddOnce(opened, path);
                }
            }
            else if (isNew || !string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase))
            {
                AddOnce(opened, path);
            }
        }

        // Documents closed since the previous look are forgotten, so a later document at the same address counts as new.
        foreach (var gone in known.Keys.Where(id => !seenNow.Contains(id)).ToList())
        {
            known.Remove(gone);
        }

        hasBaseline = true;
        return opened;
    }

    /// <summary>
    /// A cheaper look when only Notepad++'s title changed and its document count did not: only the document in front is looked
    /// at. A Save As of it counts; a document not seen before needs a full look (<see cref="Observe"/>).
    /// </summary>
    /// <remarks>
    /// Switching tabs changes the title every time; with hundreds of documents open a full look costs hundreds of messages, so
    /// a switch costs one id and one path instead.
    /// </remarks>
    /// <param name="currentId">The buffer id of the document in front.</param>
    /// <param name="resolvePath">Reads a document's path by buffer id.</param>
    /// <returns>The paths opened (none, or the document in front saved under a new name), or <see langword="null"/> when a full look is needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolvePath"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<string>? ObserveFront(long currentId, Func<long, string?> resolvePath)
    {
        ArgumentNullException.ThrowIfNull(resolvePath);
        if (!hasBaseline || currentId == 0 || !known.TryGetValue(currentId, out var previousPath))
        {
            return null;
        }

        var path = FileOrNull(resolvePath(currentId));
        known[currentId] = path;
        return path is not null && !string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase) ? [path] : [];
    }

    /// <summary>The path when it names a file on disk (a full path); <see langword="null"/> for an untitled tab or nothing.</summary>
    /// <param name="path">A document's path as Notepad++ reports it.</param>
    /// <returns>The path, or <see langword="null"/>.</returns>
    private static string? FileOrNull(string? path) =>
        string.IsNullOrWhiteSpace(path) || NotepadPlusPlusSession.IsUntitledName(path) || !Path.IsPathFullyQualified(path) ? null : path;

    /// <summary>Normalizes a path for the comparison with the command line's files.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The trimmed path without a trailing separator.</returns>
    private static string Normalize(string path) => path.Trim().TrimEnd('\\', '/');

    /// <summary>Adds a path unless it is already in the list (case-insensitive).</summary>
    /// <param name="list">The list.</param>
    /// <param name="path">The path.</param>
    private static void AddOnce(List<string> list, string path)
    {
        if (!list.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(path);
        }
    }
}
