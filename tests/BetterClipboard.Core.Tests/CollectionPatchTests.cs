using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BetterClipboard.Core.Presentation;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="CollectionPatch"/>, the list patching behind the panel's reloads: the result always holds exactly
/// the wanted instances in order, and the common changes cost the few notifications a list control can apply without
/// recreating the other cards.
/// </summary>
public sealed class CollectionPatchTests
{
    /// <summary>An unchanged list raises nothing: a refresh of an up-to-date panel touches no card.</summary>
    [Fact]
    public void Apply_SameList_RaisesNothing()
    {
        var (items, cards) = Make(5);
        var log = Record(items);

        CollectionPatch.Apply(items, cards);

        Assert.Empty(log);
        Assert.Equal(cards, items);
    }

    /// <summary>
    /// A new copy on top of a full page (the newest at index 0, the oldest falling off the end): one insert and one remove,
    /// the other cards stay.
    /// </summary>
    [Fact]
    public void Apply_NewCopyOnAFullPage_InsertsOneAndRemovesOne()
    {
        var (items, cards) = Make(5);
        var log = Record(items);
        var fresh = new Card("new");
        var wanted = new[] { fresh }.Concat(cards.Take(4)).ToArray();

        CollectionPatch.Apply(items, wanted);

        Assert.Equal(wanted, items);
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], log.Select(e => e.Action));
    }

    /// <summary>An item copied again moves to the top: one move, no card recreated.</summary>
    [Fact]
    public void Apply_ItemUsedAgain_MovesIt()
    {
        var (items, cards) = Make(5);
        var log = Record(items);
        var wanted = new[] { cards[3], cards[0], cards[1], cards[2], cards[4] };

        CollectionPatch.Apply(items, wanted);

        Assert.Equal(wanted, items);
        Assert.Equal([NotifyCollectionChangedAction.Move], log.Select(e => e.Action));
    }

    /// <summary>A deleted item is only removed.</summary>
    [Fact]
    public void Apply_ItemDeleted_RemovesIt()
    {
        var (items, cards) = Make(5);
        var log = Record(items);
        var wanted = new[] { cards[0], cards[1], cards[3], cards[4] };

        CollectionPatch.Apply(items, wanted);

        Assert.Equal(wanted, items);
        Assert.Equal([NotifyCollectionChangedAction.Remove], log.Select(e => e.Action));
    }

    /// <summary>A whole new list (another search) replaces every item; an empty one empties the collection, and back.</summary>
    [Fact]
    public void Apply_DisjointAndEmptyLists_EndExactlyAsWanted()
    {
        var (items, _) = Make(4);
        var (_, other) = Make(3);

        CollectionPatch.Apply(items, other);
        Assert.Equal(other, items);

        CollectionPatch.Apply(items, []);
        Assert.Empty(items);

        CollectionPatch.Apply(items, other);
        Assert.Equal(other, items);
    }

    /// <summary>
    /// Instances are matched by reference, not by <c>Equals</c>: an equal but different instance (a record with the same
    /// values) replaces the old one — the panel decides which cards to keep, not the patch.
    /// </summary>
    [Fact]
    public void Apply_MatchesByReferenceNotByEquals()
    {
        var old = new Card("a");
        var items = new ObservableCollection<Card> { old };
        var equal = new Card("a");
        Assert.Equal(old, equal);

        CollectionPatch.Apply(items, [equal]);

        Assert.Same(equal, Assert.Single(items));
    }

    /// <summary>A collection that somehow holds an instance twice ends with it once, where it is wanted.</summary>
    [Fact]
    public void Apply_DuplicateInCollection_EndsOnce()
    {
        var a = new Card("a");
        var b = new Card("b");
        var items = new ObservableCollection<Card> { a, b, a };

        CollectionPatch.Apply(items, [b, a]);

        Assert.Equal([b, a], items);
    }

    /// <summary>The same instance twice in the wanted list, or a null, is refused before anything changes.</summary>
    [Fact]
    public void Apply_BadWantedList_ThrowsAndChangesNothing()
    {
        var (items, cards) = Make(3);
        var before = items.ToArray();

        Assert.Throws<ArgumentException>(() => CollectionPatch.Apply(items, [cards[0], cards[0]]));
        Assert.Throws<ArgumentException>(() => CollectionPatch.Apply(items, [cards[0], null!]));
        Assert.Throws<ArgumentNullException>(() => CollectionPatch.Apply(null!, cards));
        Assert.Throws<ArgumentNullException>(() => CollectionPatch.Apply(items, null!));
        Assert.Equal(before, items);
    }

    /// <summary>
    /// Random reorders, removals and additions (fixed seeds, so a failure repeats): the collection always ends as exactly
    /// the wanted instances, and never needs more changes than a clear-and-refill would have made.
    /// </summary>
    /// <param name="seed">The random seed.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(42)]
    [InlineData(2026)]
    public void Apply_RandomChanges_EndExactlyAsWanted(int seed)
    {
        var random = new Random(seed);
        for (int round = 0; round < 200; round++)
        {
            var pool = Enumerable.Range(0, 30).Select(i => new Card($"c{i}")).ToArray();
            var items = new ObservableCollection<Card>(pool.OrderBy(_ => random.Next()).Take(random.Next(0, 25)));
            var wanted = pool.OrderBy(_ => random.Next()).Take(random.Next(0, 25)).ToArray();
            var log = Record(items);
            int clearAndRefill = items.Count + wanted.Length;

            CollectionPatch.Apply(items, wanted);

            Assert.Equal(wanted, items);
            Assert.True(log.Count <= clearAndRefill, $"round {round}: {log.Count} changes, more than {clearAndRefill}");
        }
    }

    /// <summary>A collection of <paramref name="count"/> distinct cards, and the same cards as an array.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The collection and its cards.</returns>
    private static (ObservableCollection<Card> Items, Card[] Cards) Make(int count)
    {
        var cards = Enumerable.Range(0, count).Select(i => new Card($"card {i}")).ToArray();
        return (new ObservableCollection<Card>(cards), cards);
    }

    /// <summary>Records the collection's change notifications from now on.</summary>
    /// <param name="items">The collection.</param>
    /// <returns>The list the notifications are added to.</returns>
    private static List<NotifyCollectionChangedEventArgs> Record(ObservableCollection<Card> items)
    {
        var log = new List<NotifyCollectionChangedEventArgs>();
        items.CollectionChanged += (_, e) => log.Add(e);
        return log;
    }

    /// <summary>A stand-in for a card: a record, so two instances with the same name are equal but not the same.</summary>
    /// <param name="Name">Its name (only for failure messages).</param>
    private sealed record Card(string Name);
}
