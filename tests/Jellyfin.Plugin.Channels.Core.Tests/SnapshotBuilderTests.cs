using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class SnapshotBuilderTests
{
    private static readonly DateTime Built = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record FakeItem(
        Guid Id,
        long RuntimeTicks,
        string SeriesName = "",
        int? SeasonNumber = null,
        int? EpisodeNumber = null,
        string SortName = "") : IChannelItem;

    private static FakeItem Episode(string series, int season, int episode, int minutes = 30)
    {
        var id = Guid.Parse($"{Math.Abs(series.GetHashCode()):x8}-{season:x4}-{episode:x4}-0000-000000000000");
        return new FakeItem(id, minutes * TimeSpan.TicksPerMinute, series, season, episode, $"{series} {season}x{episode}");
    }

    private static FakeItem Movie(string name, int minutes = 90) =>
        new(Guid.NewGuid(), minutes * TimeSpan.TicksPerMinute, name, null, null, name);

    [Fact]
    public void sequential_order_keeps_each_series_together_and_in_episode_order()
    {
        // A flat sort on season/episode would interleave episode 1 of every show,
        // which is not what "in order" means to anyone.
        var items = new List<IChannelItem>
        {
            Episode("Bravo", 1, 2),
            Episode("Alpha", 2, 1),
            Episode("Bravo", 1, 1),
            Episode("Alpha", 1, 1)
        };

        var snap = SnapshotBuilder.Build(items, ChannelOrdering.Sequential, 0, 1, Built);

        var order = snap.Programs.Select(p => p.ItemId).ToList();
        Assert.Equal(
            new[]
            {
                Episode("Alpha", 1, 1).Id,
                Episode("Alpha", 2, 1).Id,
                Episode("Bravo", 1, 1).Id,
                Episode("Bravo", 1, 2).Id
            },
            order);
    }

    [Fact]
    public void an_item_matched_by_two_blocks_only_airs_once()
    {
        // Overlap is the normal case: a series block and a library block will
        // both claim the same episodes.
        var shared = Episode("Alpha", 1, 1);
        var items = new List<IChannelItem> { shared, Episode("Alpha", 1, 2), shared };

        var snap = SnapshotBuilder.Build(items, ChannelOrdering.Sequential, 0, 1, Built);

        Assert.Equal(2, snap.Programs.Count);
        Assert.Single(snap.Programs, p => p.ItemId == shared.Id);
    }

    [Fact]
    public void unplayable_items_never_reach_the_running_order()
    {
        var items = new List<IChannelItem>
        {
            Episode("Alpha", 1, 1),
            new FakeItem(Guid.NewGuid(), 0, "Broken"),
            new FakeItem(Guid.NewGuid(), -5, "Worse")
        };

        var snap = SnapshotBuilder.Build(items, ChannelOrdering.Sequential, 0, 1, Built);

        Assert.Single(snap.Programs);
    }

    [Fact]
    public void the_same_seed_rebuilds_the_same_running_order()
    {
        // A rebuild after a library change must not reshuffle everything, or a
        // channel jumps around every time an episode is added or deleted.
        var items = Enumerable.Range(1, 40).Select(i => (IChannelItem)Episode("Alpha", 1, i)).ToList();

        var a = SnapshotBuilder.Build(items, ChannelOrdering.SeededShuffle, 777, 1, Built);
        var b = SnapshotBuilder.Build(items, ChannelOrdering.SeededShuffle, 777, 2, Built);

        Assert.Equal(a.Programs.Select(p => p.ItemId), b.Programs.Select(p => p.ItemId));
    }

    [Fact]
    public void shuffling_keeps_every_item_exactly_once()
    {
        var items = Enumerable.Range(1, 60).Select(i => (IChannelItem)Episode("Alpha", 1, i)).ToList();

        var snap = SnapshotBuilder.Build(items, ChannelOrdering.SeededShuffle, 1234, 1, Built);

        Assert.Equal(60, snap.Programs.Count);
        Assert.Equal(60, snap.Programs.Select(p => p.ItemId).Distinct().Count());
    }

    [Fact]
    public void the_cycle_length_is_the_sum_of_what_actually_airs()
    {
        var items = new List<IChannelItem> { Episode("Alpha", 1, 1, 30), Movie("Film", 90) };

        var snap = SnapshotBuilder.Build(items, ChannelOrdering.Sequential, 0, 1, Built);

        Assert.Equal(120 * TimeSpan.TicksPerMinute, snap.TotalRuntimeTicks);
    }

    [Fact]
    public void a_daypart_whose_blocks_matched_nothing_is_empty_rather_than_broken()
    {
        var snap = SnapshotBuilder.Build([], ChannelOrdering.SeededShuffle, 5, 1, Built);

        Assert.True(snap.IsEmpty);
        Assert.Equal(0, snap.TotalRuntimeTicks);
    }

    [Fact]
    public void movies_without_episode_numbers_order_by_name()
    {
        var items = new List<IChannelItem> { Movie("Zulu"), Movie("Alpha"), Movie("Mike") };

        var snap = SnapshotBuilder.Build(items, ChannelOrdering.Sequential, 0, 1, Built);

        var names = snap.Programs
            .Select(p => items.First(i => i.Id == p.ItemId).SortName)
            .ToArray();
        Assert.Equal(new[] { "Alpha", "Mike", "Zulu" }, names);
    }

    [Fact]
    public void a_new_episode_slots_in_without_reordering_the_rest()
    {
        // The whole point of persisting the seed is that a library change
        // "disturbs the order as little as possible". A positional shuffle
        // cannot honour that: one new download changes the list length and
        // every draw after it, so the entire channel reshuffles and whatever
        // was airing next jumps somewhere else. New content arriving is the
        // normal case here, not the exception.
        var existing = Enumerable.Range(1, 40)
            .Select(i => (IChannelItem)Episode("Syd and Macky", 1, i))
            .ToList();

        var before = SnapshotBuilder.Build(existing, ChannelOrdering.SeededShuffle, 7003, 1, Built)
            .Programs.Select(p => p.ItemId).ToList();

        var withNew = existing.Append(Episode("Syd and Macky", 1, 41)).ToList();
        var after = SnapshotBuilder.Build(withNew, ChannelOrdering.SeededShuffle, 7003, 2, Built)
            .Programs.Select(p => p.ItemId).ToList();

        var newId = Episode("Syd and Macky", 1, 41).Id;
        Assert.Contains(newId, after);
        Assert.Equal(before, after.Where(id => id != newId).ToList());
    }
}
