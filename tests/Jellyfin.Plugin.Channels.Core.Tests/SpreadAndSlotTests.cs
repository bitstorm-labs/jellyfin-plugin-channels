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

    [Fact]
    public void every_slot_is_whole_half_hours_and_breaks_fill_the_rest()
    {
        var order = new[]
        {
            new ChannelProgram(Guid.NewGuid(), Min(22)),   // sitcom: 30-min slot, 8-min break
            new ChannelProgram(Guid.NewGuid(), Min(118)),  // film: 2-hour slot, 2-min break
            new ChannelProgram(Guid.NewGuid(), Min(11)),   // short videos share a slot
            new ChannelProgram(Guid.NewGuid(), Min(12)),
            new ChannelProgram(Guid.NewGuid(), Min(9)),    // does not fit after 23 min
            new ChannelProgram(Guid.NewGuid(), Min(30)),   // exactly a slot: no break
        };

        var packed = SlotPacker.Pack(order, SlotPacker.HalfHourTicks);

        Assert.Equal(order.Length, packed.Count(p => !SlotPacker.IsBreak(p)));
        Assert.Equal(0, packed.Sum(p => p.RuntimeTicks) % SlotPacker.HalfHourTicks);
        Assert.Equal(new[] { Min(22), Min(8), Min(118), Min(2), Min(11), Min(12), Min(7), Min(9), Min(21), Min(30) },
            packed.Select(p => p.RuntimeTicks));

        // Every programme that follows a break starts on the half hour; only
        // one packed straight after another may start mid-slot.
        long t = 0;
        for (var i = 0; i < packed.Count; i++)
        {
            if (i == 0 || SlotPacker.IsBreak(packed[i - 1]))
            {
                Assert.Equal(0, t % SlotPacker.HalfHourTicks);
            }

            t += packed[i].RuntimeTicks;
        }
    }

    [Fact]
    public void a_break_resolves_as_no_item_and_points_at_what_follows()
    {
        var sitcom = Guid.NewGuid();
        var next = Guid.NewGuid();
        var snapshot = new ChannelSnapshot(
            SlotPacker.Pack(new[] { new ChannelProgram(sitcom, Min(22)), new ChannelProgram(next, Min(22)) }, SlotPacker.HalfHourTicks),
            1, Anchor);

        var during = ScheduleResolver.Resolve(snapshot, Anchor, Anchor.AddMinutes(10))!.Value;
        Assert.Equal(sitcom, during.ItemId);
        Assert.Equal(next, during.NextItemId); // not the break

        var inBreak = ScheduleResolver.Resolve(snapshot, Anchor, Anchor.AddMinutes(25))!.Value;
        Assert.Equal(Guid.Empty, inBreak.ItemId);
        Assert.Equal(next, inBreak.NextItemId);
        Assert.Equal(Anchor.AddMinutes(30), inBreak.EndUtc);
    }

    [Fact]
    public void the_guide_lists_a_programme_through_its_break()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var snapshot = new ChannelSnapshot(
            SlotPacker.Pack(new[] { new ChannelProgram(a, Min(22)), new ChannelProgram(b, Min(22)) }, SlotPacker.HalfHourTicks),
            1, Anchor);
        var parts = new[] { new Daypart("All day", TimeSpan.Zero, TimeSpan.FromHours(24), snapshot) };

        var guide = ChannelScheduleResolver.ResolveRange(parts, TimeZoneInfo.Utc, Anchor, Anchor, Anchor.AddHours(1));
        Assert.Equal(new[] { a, b }, guide.Select(g => g.ItemId));
        Assert.Equal(Anchor.AddMinutes(30), guide[0].EndUtc);
        Assert.Equal(Anchor.AddMinutes(30), guide[1].StartUtc);

        // Opened during the break, the finished programme is still listed until :30.
        var mid = ChannelScheduleResolver.ResolveRange(parts, TimeZoneInfo.Utc, Anchor, Anchor.AddMinutes(25), Anchor.AddHours(1));
        Assert.Equal(a, mid[0].ItemId);
        Assert.Equal(Anchor.AddMinutes(30), mid[0].EndUtc);
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
        var aired = snapshot.Programs.Where(p => !SlotPacker.IsBreak(p)).Select(p => byId[p.ItemId]).ToList();

        foreach (var show in new[] { "Parks", "Ghosts", "Abbott" })
        {
            var run = aired.Where(i => i.SeriesName == show).Select(i => (i.SeasonNumber, i.EpisodeNumber)).ToList();
            Assert.Equal(run.OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber), run);
        }

        Assert.Equal(0, aired.Zip(aired.Skip(1)).Count(p => p.First.SeriesName == p.Second.SeriesName));
    }
}

public class PackingRepeatTests
{
    private sealed record Clip(Guid Id, long RuntimeTicks, string SeriesName) : IChannelItem
    {
        public int? SeasonNumber => null;
        public int? EpisodeNumber => null;
        public string SortName => SeriesName;
    }

    [Fact]
    public void packing_short_videos_does_not_put_a_channel_next_to_itself()
    {
        // Short videos from three YouTube channels: packing pulls later ones
        // forward, which must not pull the same channel's next video.
        var items = new List<IChannelItem>();
        var n = 0;
        foreach (var (name, count) in new[] { ("GCN", 40), ("Builds", 30), ("Syd", 30) })
        {
            for (var i = 0; i < count; i++)
            {
                items.Add(new Clip(new Guid(++n, 0, 0, new byte[8]), (8 + (n * 7 % 11)) * TimeSpan.TicksPerMinute, name));
            }
        }

        var snapshot = SnapshotBuilder.Build(items, ChannelOrdering.SeededShuffle, 3, 1, DateTime.UnixEpoch, alignToHalfHour: true);
        var show = items.ToDictionary(i => i.Id, i => i.SeriesName);
        var aired = snapshot.Programs.Where(p => !SlotPacker.IsBreak(p)).Select(p => show[p.ItemId]).ToList();

        Assert.Equal(items.Count, aired.Count);
        Assert.True(aired.Zip(aired.Skip(1)).Count(p => p.First == p.Second) <= 2);
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
