namespace BetterClipboard.Core.Services;

/// <summary>What an import does with one capture of content that Windows' own clipboard history pins.</summary>
public enum WindowsPinDecision
{
    /// <summary>
    /// Import it the usual way: a new entry is inserted pinned, an existing one gets the pin. Only for a Windows pin that
    /// BetterClipboard has not brought over before.
    /// </summary>
    Apply,

    /// <summary>
    /// Skip the capture: nothing is inserted, pinned or bumped, so the entry stays as the user left it here (pinned,
    /// unpinned, or deleted).
    /// </summary>
    LeaveAsIs,
}

/// <summary>
/// The pins of Windows' own clipboard history (Win+V) that BetterClipboard has already brought over. Each one is applied
/// once; from then on, pinning, unpinning and deleting in BetterClipboard decide.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Windows keeps a pinned item on disk for good (<c>%LOCALAPPDATA%\Microsoft\Windows\Clipboard\Pinned</c>,
/// CLAUDE.md §1.4), and BetterClipboard imports Windows' history at every start. An import duplicate is left alone except
/// that a requested pin is added (<see cref="Storage.ClipStore.Upsert"/>), so before this ledger the import pinned such an
/// item again at every start: an unpin here lasted only until the next start or update. Reported 2026-11-14: a path that
/// had been pinned in Windows since March 2026 came back pinned after each of the owner's updates. An item deleted here came
/// back pinned too, once its 30-day tombstone expired.
/// </para>
/// <para>
/// <b>The rule</b> (<see cref="Decide"/>), for a capture the importer marked as pinned in Windows:
/// <list type="bullet">
/// <item>brought over by an earlier import: <see cref="WindowsPinDecision.LeaveAsIs"/>, whatever the entry is now, or
/// whether it still exists;</item>
/// <item>the first import under this rule (no ledger stored yet) and an entry with that content exists:
/// <see cref="WindowsPinDecision.LeaveAsIs"/>. Every older version already pinned that entry at its last import, so an
/// entry that is unpinned now was unpinned by the user;</item>
/// <item>otherwise (a pin Windows made since the last import, or content BetterClipboard does not have):
/// <see cref="WindowsPinDecision.Apply"/>.</item>
/// </list>
/// A capture of such content that the importer did not mark as pinned (WinRT history items are recognized as pinned by
/// their timestamp alone) is left alone as well once brought over (<see cref="WasBroughtOver"/>), so it cannot bring
/// back an entry deleted here either.
/// </para>
/// <para>
/// <b>Stored</b> in the encrypted history as integration state <see cref="StateName"/> (it describes this history: a
/// quarantined store starts over, and a fresh store brings every Windows pin over once). The value is <c>v1:</c> followed by
/// comma-separated content hashes, least recently seen first, at most <see cref="MaxEntries"/>. Every import moves the
/// hashes it saw to the end, so a pin Windows still keeps is never trimmed; only hashes Windows stopped pinning long ago
/// age out. Content hashes, never content: the same hashes are the history's own keys.
/// </para>
/// <para>
/// <b>Limits.</b> A pin that Windows drops and makes again on the same content is not brought over a second time (the
/// hash stays in the ledger); pin it here instead. On the first import under the rule, a pin made in Windows since the last
/// import of an older version, on content BetterClipboard already had unpinned, is not applied.
/// </para>
/// <para>
/// <b>Footgun:</b> a version older than this ledger ignores it and re-pins at every start again; the next newer start
/// does not undo that, because those entries are then pinned and the ledger already lists them.
/// </para>
/// <para>
/// Not thread-safe: one import uses one instance, on the history worker.
/// </para>
/// </remarks>
public sealed class WindowsPinLedger
{
    /// <summary>Name of the integration state holding the ledger (<see cref="Storage.ClipStore.GetStateValue"/>).</summary>
    public const string StateName = "windows.pins";

    /// <summary>
    /// The most content hashes kept. Windows pins far fewer items than this; the cap only bounds hashes of pins Windows
    /// dropped long ago, which are trimmed first.
    /// </summary>
    public const int MaxEntries = 1000;

    /// <summary>Format marker of the stored value. A value without it is not read (see <see cref="Parse"/>).</summary>
    private const string FormatPrefix = "v1:";

    /// <summary>Hashes brought over by earlier imports, least recently seen first.</summary>
    private readonly List<string> previous;

    /// <summary>The same hashes as <see cref="previous"/>, for lookups.</summary>
    private readonly HashSet<string> previousSet;

    /// <summary>Hashes of Windows pins seen by this import, in the order of first sight.</summary>
    private readonly List<string> seen = [];

    /// <summary>The same hashes as <see cref="seen"/>, for lookups.</summary>
    private readonly HashSet<string> seenSet = new(StringComparer.Ordinal);

    /// <summary>Content hashes counted in <see cref="KeptCount"/>, so a pin seen twice in one import counts once.</summary>
    private readonly HashSet<string> kept = new(StringComparer.Ordinal);

    /// <summary>Creates a ledger from what was stored.</summary>
    /// <param name="storedValue">The value read from the store, kept to tell whether anything changed.</param>
    /// <param name="previous">The hashes it lists, least recently seen first.</param>
    /// <param name="isFirstImport">Whether no readable ledger was stored.</param>
    private WindowsPinLedger(string? storedValue, List<string> previous, bool isFirstImport)
    {
        StoredValue = storedValue;
        this.previous = previous;
        previousSet = new HashSet<string>(previous, StringComparer.Ordinal);
        IsFirstImport = isFirstImport;
    }

    /// <summary>
    /// Whether no ledger was stored yet: the first import under this rule, which leaves every existing entry as it is
    /// (see the class remarks).
    /// </summary>
    public bool IsFirstImport { get; }

    /// <summary>The stored value this ledger was read from, or <see langword="null"/> when none was stored.</summary>
    public string? StoredValue { get; }

    /// <summary>
    /// How many captures this import left alone although their entry is unpinned or gone here: the cases the old rule
    /// would have changed. Counted once per content hash. Only for the log; never names any content.
    /// </summary>
    public int KeptCount { get; private set; }

    /// <summary>How many Windows pins this import has seen so far (distinct content).</summary>
    public int SeenCount => seen.Count;

    /// <summary>Whether <see cref="Format"/> differs from <see cref="StoredValue"/>, i.e. the store must be written.</summary>
    public bool Changed => !string.Equals(Format(), StoredValue, StringComparison.Ordinal);

    /// <summary>
    /// Reads a stored ledger.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> (never stored) and a value in a format this version does not know (a newer version's, or one
    /// edited by hand) both read as <see cref="IsFirstImport"/>: the conservative reading, which never pins an entry that
    /// exists. Tokens that are not content hashes are skipped, and a hash listed twice counts once.
    /// </remarks>
    /// <param name="stored">The value from <see cref="Storage.ClipStore.GetStateValue"/>, or <see langword="null"/>.</param>
    /// <returns>The ledger; never <see langword="null"/>.</returns>
    public static WindowsPinLedger Parse(string? stored)
    {
        if (stored is null || !stored.StartsWith(FormatPrefix, StringComparison.Ordinal))
        {
            return new WindowsPinLedger(stored, [], isFirstImport: true);
        }

        var hashes = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in stored[FormatPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsContentHash(token) && unique.Add(token))
            {
                hashes.Add(token);
            }
        }

        return new WindowsPinLedger(stored, hashes, isFirstImport: false);
    }

    /// <summary>
    /// Whether an earlier import brought this content over as a Windows pin. Such content is the user's to manage here, so
    /// every capture of it is skipped, pinned or not.
    /// </summary>
    /// <param name="contentHash">The capture's content hash (<see cref="Content.ContentHasher"/>).</param>
    /// <returns><see langword="true"/> when the ledger read from the store lists it.</returns>
    public bool WasBroughtOver(string contentHash) => previousSet.Contains(contentHash);

    /// <summary>
    /// Decides what the import does with a capture Windows pins, and records that Windows pins it (see the class remarks).
    /// </summary>
    /// <param name="contentHash">The capture's content hash.</param>
    /// <param name="entryPinned">
    /// Whether the history entry with this content is pinned now, or <see langword="null"/> when no entry has it
    /// (never kept, deleted, or pruned).
    /// </param>
    /// <returns><see cref="WindowsPinDecision.Apply"/> or <see cref="WindowsPinDecision.LeaveAsIs"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="contentHash"/> is not a 64-digit lowercase hex content hash.</exception>
    public WindowsPinDecision Decide(string contentHash, bool? entryPinned)
    {
        // Validate before recording: a malformed token would be skipped by the next Parse, and the pin would then count
        // as new again, which is exactly the revival this ledger prevents.
        if (!IsContentHash(contentHash))
        {
            throw new ArgumentException("Not a content hash (64 lowercase hex digits).", nameof(contentHash));
        }

        if (seenSet.Add(contentHash))
        {
            seen.Add(contentHash);
        }

        bool leave = previousSet.Contains(contentHash) || (IsFirstImport && entryPinned is not null);
        if (leave && entryPinned is not true && kept.Add(contentHash))
        {
            KeptCount++;
        }

        return leave ? WindowsPinDecision.LeaveAsIs : WindowsPinDecision.Apply;
    }

    /// <summary>
    /// The value to store after this import: the earlier hashes this import did not see, then the ones it saw, trimmed
    /// from the front to <see cref="MaxEntries"/>.
    /// </summary>
    /// <returns>The stored form, <c>v1:</c> and comma-separated hashes (possibly none).</returns>
    public string Format()
    {
        var order = new List<string>(previous.Count + seen.Count);
        order.AddRange(previous.Where(hash => !seenSet.Contains(hash)));
        order.AddRange(seen);

        // The pins Windows still keeps were just appended, so trimming the front only drops hashes it stopped pinning.
        int skip = Math.Max(0, order.Count - MaxEntries);
        return FormatPrefix + string.Join(',', order.Skip(skip));
    }

    /// <summary>Whether <paramref name="value"/> has the shape of <see cref="Content.ContentHasher"/>'s output.</summary>
    /// <param name="value">A token.</param>
    /// <returns><see langword="true"/> for exactly 64 lowercase hex digits.</returns>
    private static bool IsContentHash(string? value) =>
        value is { Length: 64 } && value.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
