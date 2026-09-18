using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class DeterministicShuffleTests
{
    private static int[] Source(int n) => Enumerable.Range(0, n).ToArray();

    [Fact]
    public void the_same_seed_always_produces_the_same_order()
    {
        var a = DeterministicShuffle.Shuffle(Source(50), 12345);
        var b = DeterministicShuffle.Shuffle(Source(50), 12345);

        Assert.Equal(a, b);
    }

    [Fact]
    public void a_different_seed_produces_a_different_order()
    {
        var a = DeterministicShuffle.Shuffle(Source(50), 1);
        var b = DeterministicShuffle.Shuffle(Source(50), 2);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void nothing_is_lost_or_duplicated()
    {
        var shuffled = DeterministicShuffle.Shuffle(Source(500), 99);

        Assert.Equal(500, shuffled.Count);
        Assert.Equal(Source(500).OrderBy(x => x), shuffled.OrderBy(x => x));
    }

    [Fact]
    public void the_order_is_pinned_to_known_values()
    {
        // The point of hand-rolling the generator is that the order survives a
        // runtime upgrade. A vector is the only thing that actually enforces
        // that: if someone swaps in Random, or the constants change, every
        // existing channel silently reshuffles and this fails first.
        var shuffled = DeterministicShuffle.Shuffle(Source(10), 42);

        Assert.Equal(new[] { 0, 9, 5, 8, 6, 4, 7, 2, 1, 3 }, shuffled);
    }

    [Fact]
    public void degenerate_inputs_are_returned_unchanged()
    {
        Assert.Empty(DeterministicShuffle.Shuffle(Array.Empty<int>(), 7));
        Assert.Equal(new[] { 42 }, DeterministicShuffle.Shuffle(new[] { 42 }, 7));
    }

    [Fact]
    public void the_input_is_not_mutated()
    {
        var input = Source(20);
        var copy = input.ToArray();

        DeterministicShuffle.Shuffle(input, 5);

        Assert.Equal(copy, input);
    }
}
