using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

/// <summary>No show back to back; every slot starting on the half hour.</summary>
public class SpreadAndSlotTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static long Min(double m) => (long)(m * TimeSpan.TicksPerMinute);

    private sealed record Item(Guid Id, long RuntimeTicks, string SeriesName) : IChannelItem
    {
        public int? SeasonNumber => null;
        public int? EpisodeNumber => null;
        public string SortName => SeriesName;
    }

    private static List<IChannelItem> Shows(params (string Name, int Count)[] shows)
    {
        var items = new List<IChannelItem>();
        var n = 0;
        foreach (var (name, count) in shows)
        {
            for (var i = 0; i < count; i++)
            {
                items.Add(new Item(new Guid(++n, 0, 0, new byte[8]), Min(22), name));
            }
        }

        return items;
    }

    private static int BackToBack(IReadOnlyList<IChannelItem> order) =>
        order.Zip(order.Skip(1)).Count(p => p.First.SeriesName == p.Second.SeriesName);

    [Fact]
    public void no_show_airs_back_to_back_when_none_is_half_the_channel()
    {
        var items = Shows(("Survivor", 30), ("Big Brother", 20), ("Love is Blind", 15), ("Drag Race", 12));
        var order = DeterministicShuffle.Spread(items, 42, i => i.Id, i => i.SeriesName);

        Assert.Equal(0, BackToBack(order));
        Assert.Equal(items.Count, order.Distinct().Count());
    }

    [Fact]
    public void the_old_per_item_shuffle_did_put_shows_back_to_back()
    {
        // The regression this replaces: the same input, shuffled item by item.
        var items = Shows(("Survivor", 30), ("Big Brother", 20), ("Love is Blind", 15), ("Drag Race", 12));
        var order = DeterministicShuffle.Shuffle(items, 42, i => i.Id);

        Assert.True(BackToBack(order) > 5);
    }

    [Fact]
    public void a_dominant_show_repeats_only_as_often_as_it_must()
    {
        // 60 of 100: at least 60 - 40 - 1 = 19 repeats are unavoidable.
        var items = Shows(("Grand Prix", 60), ("Builds", 25), ("Syd", 15));
        var order = DeterministicShuffle.Spread(items, 7, i => i.Id, i => i.SeriesName);

        Assert.InRange(BackToBack(order), 19, 24);
    }

    [Fact]
    public void spreading_is_deterministic_and_one_new_episode_leaves_other_shows_in_place()
    {
        var items = Shows(("A", 10), ("B", 8), ("C", 6));
        var first = DeterministicShuffle.Spread(items, 9, i => i.Id, i => i.SeriesName);
        Assert.Equal(first, DeterministicShuffle.Spread(items, 9, i => i.Id, i => i.SeriesName));

        var grown = items.Append(new Item(Guid.NewGuid(), Min(22), "A")).ToList();
        var after = DeterministicShuffle.Spread(grown, 9, i => i.Id, i => i.SeriesName);
        Assert.Equal(
            first.Where(i => i.SeriesName != "A").Select(i => i.Id),
            after.Where(i => i.SeriesName != "A").Select(i => i.Id));
    }
}

public class SeriesOrderTests
{
    private sealed record Ep(Guid Id, long RuntimeTicks, string SeriesName, int? SeasonNumber, int? EpisodeNumber) : IChannelItem
    {
        public string SortName => SeriesName;
    }

    [Fact]
    public void each_show_plays_from_its_first_episode_in_order_and_loops()
    {
        var items = new List<IChannelItem>();
        foreach (var show in new[] { "Parks", "Ghosts", "Abbott" })
        {
            for (var s = 2; s >= 1; s--)
            {
                for (var e = 6; e >= 1; e--)
                {
                    items.Add(new Ep(Guid.NewGuid(), 22 * TimeSpan.TicksPerMinute, show, s, e));
                }
            }
        }

        var snapshot = SnapshotBuilder.Build(items, ChannelOrdering.SeededShuffle, 5, 1, DateTime.UnixEpoch);
        var byId = items.ToDictionary(i => i.Id);
        var aired = snapshot.Programs.Select(p => byId[p.ItemId]).ToList();

        foreach (var show in new[] { "Parks", "Ghosts", "Abbott" })
        {
            var run = aired.Where(i => i.SeriesName == show).Select(i => (i.SeasonNumber, i.EpisodeNumber)).ToList();
            Assert.Equal(run.OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber), run);
        }

        Assert.Equal(0, aired.Zip(aired.Skip(1)).Count(p => p.First.SeriesName == p.Second.SeriesName));
    }
}

public class MidnightTests
{
    [Fact]
    public void an_all_day_channel_does_not_split_a_programme_at_midnight()
    {
        var anchor = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var film = Guid.NewGuid();
        var other = Guid.NewGuid();
        var snapshot = new ChannelSnapshot(new[]
        {
            new ChannelProgram(other, TimeSpan.FromMinutes(23 * 60 + 30).Ticks),
            new ChannelProgram(film, TimeSpan.FromMinutes(120).Ticks)
        }, 1, anchor);
        var parts = new[] { new Daypart("All day", TimeSpan.Zero, TimeSpan.FromHours(24), snapshot) };

        var guide = ChannelScheduleResolver.ResolveRange(parts, TimeZoneInfo.Utc, anchor, anchor.AddHours(23), anchor.AddHours(26));
        Assert.Single(guide, g => g.ItemId == film);
        Assert.Equal(anchor.AddHours(25.5), guide.Single(g => g.ItemId == film).EndUtc);
    }
}
