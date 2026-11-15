namespace BetterClipboard.App.Views;

/// <summary>
/// The panel's part in an instant paste (CLAUDE.md §2.52): the selected card's formats are read while the user looks at it,
/// so Enter finds them in memory (<c>AppController.PrefetchFormats</c>). The paste itself, and why its steps come in their
/// order, is <c>AppController.Paste.cs</c>; the panel's put-away around it is <see cref="PutAwayForPaste"/> and
/// <see cref="FinishPuttingAway"/>.
/// </summary>
public sealed partial class ClipboardFlyout
{
    /// <summary>Reads ahead whenever the selection moves (the arrow keys, the mouse over a row, a load arming its card).</summary>
    private void InitializePaste() => ItemsList.SelectionChanged += (_, _) => PrefetchSelected();

    /// <summary>
    /// Starts reading the selected card's formats, while the panel is on screen and the card is a stored entry. A card of
    /// something not stored (an Everything pick, a shell command, an archived prompt) has nothing in the store to read.
    /// </summary>
    /// <remarks>
    /// Not while the panel is hidden: the view reset after every use arms a card there too, and reading it then would hold
    /// its formats in memory for as long as the panel stays hidden. A summon reads its armed card once it is on screen
    /// (<see cref="ShowAt"/>).
    /// </remarks>
    private void PrefetchSelected()
    {
        if (IsOpen && Selected is { Pick: null, Command: null, Prompt: null } card)
        {
            controller.PrefetchFormats(card.Entry);
        }
    }
}
