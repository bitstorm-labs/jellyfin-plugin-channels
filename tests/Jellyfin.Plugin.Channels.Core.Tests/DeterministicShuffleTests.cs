using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class DeterministicShuffleTests
{
    private static int[] Source(int n) => Enumerable.Range(0, n).ToArray();

    /// <summary>A stable id per test item, standing in for a library item's id.</summary>
    private static Guid Key(int i) => new(i, 0, 0, new byte[8]);

    private static IReadOnlyList<int> Shuffle(IReadOnlyList<int> items, ulong seed) =>
        DeterministicShuffle.Shuffle(items, seed, Key);

    [Fact]
    public void the_same_seed_always_produces_the_same_order()
    {
        Assert.Equal(Shuffle(Source(50), 12345), Shuffle(Source(50), 12345));
    }

    [Fact]
    public void a_different_seed_produces_a_different_order()
    {
        Assert.NotEqual(Shuffle(Source(50), 1), Shuffle(Source(50), 2));
    }

    [Fact]
    public void nothing_is_lost_or_duplicated()
    {
        var shuffled = Shuffle(Source(500), 99);

        Assert.Equal(500, shuffled.Count);
        Assert.Equal(Source(500).OrderBy(x => x), shuffled.OrderBy(x => x));
    }

    [Fact]
    public void the_order_does_not_depend_on_the_order_items_arrive_in()
    {
        // Block resolution concatenates blocks, so the same set can arrive in a
        // different sequence after a config edit. The channel should not care.
        var forwards = Shuffle(Source(60), 31);
        var backwards = Shuffle(Source(60).Reverse().ToArray(), 31);

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void adding_an_item_leaves_the_others_where_they_were()
    {
        // The reason the seed is persisted at all: a library that grows must not
        // reshuffle the schedule out from under whoever is watching.
        var before = Shuffle(Source(40), 7);
        var after = Shuffle(Source(41), 7);

        Assert.Contains(40, after);
        Assert.Equal(before, after.Where(i => i != 40).ToList());
    }

    [Fact]
    public void removing_an_item_leaves_the_others_where_they_were()
    {
        var before = Shuffle(Source(40), 7);
        var after = Shuffle(Source(40).Where(i => i != 17).ToArray(), 7);

        Assert.Equal(before.Where(i => i != 17).ToList(), after);
    }

    [Fact]
    public void the_order_is_pinned_to_known_values()
    {
        // The point of hand-rolling the generator is that the order survives a
        // runtime upgrade. A vector is the only thing that actually enforces
        // that: if someone swaps in Random, or the constants change, every
        // existing channel silently reshuffles and this fails first.
        Assert.Equal(new[] { 4, 2, 9, 6, 5, 3, 8, 1, 0, 7 }, Shuffle(Source(10), 42));
    }

    [Fact]
    public void degenerate_inputs_are_returned_unchanged()
    {
        Assert.Empty(Shuffle(Array.Empty<int>(), 7));
        Assert.Equal(new[] { 42 }, Shuffle(new[] { 42 }, 7));
    }

    [Fact]
    public void the_input_is_not_mutated()
    {
        var input = Source(20);
        var copy = input.ToArray();

        Shuffle(input, 5);

        Assert.Equal(copy, input);
    }
}
