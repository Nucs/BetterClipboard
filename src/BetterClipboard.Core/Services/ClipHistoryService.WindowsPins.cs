using BetterClipboard.Core.Diagnostics;
using BetterClipboard.Core.Model;

namespace BetterClipboard.Core.Services;

/// <summary>
/// Windows' own pins in an import: each comes over once (<see cref="WindowsPinLedger"/>), so a pin, unpin or delete here
/// survives the next start.
/// </summary>
/// <remarks>
/// <see cref="ImportAsync"/> reads the ledger, <see cref="StoreAsync"/> asks it about every capture of Windows' history after
/// the forget check, and <see cref="SaveWindowsPins"/> writes it back and logs what it kept. Everything runs on the worker,
/// inside the import's one queue slot. The log names counts only, never content.
/// </remarks>
public sealed partial class ClipHistoryService
{
    /// <summary>
    /// Whether a capture comes from Windows' own clipboard history: its WinRT history items and its pinned store, the only
    /// imports that carry Windows' pins.
    /// </summary>
    /// <param name="origin">The capture's origin.</param>
    /// <returns><see langword="true"/> for <see cref="ClipOrigin.WindowsHistory"/> and <see cref="ClipOrigin.WindowsPinned"/>.</returns>
    private static bool IsFromWindowsHistory(ClipOrigin origin) => origin is ClipOrigin.WindowsHistory or ClipOrigin.WindowsPinned;

    /// <summary>
    /// Writes the ledger after an import when it changed, and logs the first import under the rule and the pins it left as
    /// the user keeps them.
    /// </summary>
    /// <remarks>
    /// A failed write is logged and swallowed: the captures are stored already, and the ledger read next time is the
    /// previous one. After a failed first write that is still a first import, which leaves every existing entry alone; after
    /// a later one, a pin made in Windows since may be applied a second time.
    /// </remarks>
    /// <param name="windowsPins">The ledger the import filled.</param>
    private void SaveWindowsPins(WindowsPinLedger windowsPins)
    {
        if (windowsPins.IsFirstImport)
        {
            AppLog.Info($"Windows pins {windowsPins.SeenCount} clipboard item{(windowsPins.SeenCount == 1 ? "" : "s")}: from now on each " +
                        "comes over once, and pinning, unpinning or deleting it here stands.");
        }

        if (windowsPins.KeptCount > 0)
        {
            AppLog.Info($"Left {windowsPins.KeptCount} item{(windowsPins.KeptCount == 1 ? "" : "s")} as {(windowsPins.KeptCount == 1 ? "it is" : "they are")} " +
                        "here although Windows still pins " + (windowsPins.KeptCount == 1 ? "it" : "them") + " (unpinned or deleted in BetterClipboard).");
        }

        if (!windowsPins.Changed)
        {
            return;
        }

        try
        {
            store.SetStateValue(WindowsPinLedger.StateName, windowsPins.Format());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not remember Windows' pins: {ex.Message}");
        }
    }
}
