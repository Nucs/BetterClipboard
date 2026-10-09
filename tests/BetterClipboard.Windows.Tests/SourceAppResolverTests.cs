using BetterClipboard.Windows.Clipboard;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for who a copy is attributed to: the clipboard owner's process, and — for a copy without an owner window — the
/// foreground window's, never BetterClipboard's own (found in a QA pass on 2026-10-09: copies made by a script while the
/// panel was open were labeled "BetterClipboard").
/// </summary>
public sealed class SourceAppResolverTests
{
    /// <summary>
    /// A window of this process names this process when it owns the clipboard (a copy from BetterClipboard's own text box),
    /// but not as the foreground guess for an owner-less copy; no window resolves to nothing.
    /// </summary>
    [Fact]
    public void ForegroundGuess_NeverNamesThisProcess()
    {
        var resolver = new SourceAppResolver();
        using var ownWindow = new MessageWindowThread("SourceTest", messageOnly: false);

        var asOwner = resolver.Resolve(ownWindow.Handle);
        Assert.NotNull(asOwner);
        Assert.Equal(Path.GetFileNameWithoutExtension(Environment.ProcessPath), asOwner.ProcessName, ignoreCase: true);

        Assert.Null(resolver.ResolveForegroundGuess(ownWindow.Handle));
        Assert.Null(resolver.ResolveForegroundGuess(0));
        Assert.Null(resolver.Resolve(0));
    }
}
