using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

/// The guide must agree with what actually plays, so it is built by walking the
/// single-instant resolver. These pin that agreement.
public class GuideTests
{
    private static readonly TimeZoneInfo Denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static long Minutes(double m) => (long)(m * TimeSpan.TicksPerMinute);

    /// A=30m, B=60m, all day.
    private static List<Daypart> AllDay() =>
    [
        new Daypart("All day", TimeSpan.Zero, TimeSpan.FromDays(1),
            new ChannelSnapshot(
                [new ChannelProgram(A, Minutes(30)), new ChannelProgram(B, Minutes(60))],
                1, Anchor))
    ];

    [Fact]
    public void a_three_hour_window_lists_the_programmes_in_it()
    {
        // 10h past the anchor is 60 minutes into the 90-minute cycle, so the
        // window opens midway through B rather than neatly on a boundary.
        var from = Anchor.AddHours(10);
        var to = from.AddHours(3);
        var slots = ChannelScheduleResolver.ResolveRange(AllDay(), Denver, Anchor, from, to);

        Assert.Equal(new[] { B, A, B, A, B }, slots.Select(s => s.ItemId).ToArray());
        Assert.Equal(30 * TimeSpan.TicksPerMinute, slots[0].OffsetTicks);
    }

    [Fact]
    public void a_programme_straddling_the_end_of_the_window_is_included()
    {
        // The guide has to draw the block spanning its right edge, or the last
        // column is empty for however long that programme runs.
        var from = Anchor.AddHours(10);
        var to = from.AddHours(3);
        var slots = ChannelScheduleResolver.ResolveRange(AllDay(), Denver, Anchor, from, to);

        var last = slots[^1];
        Assert.True(last.StartUtc < to, "the last entry must begin inside the window");
        Assert.True(last.EndUtc > to, "and this one is expected to run past it");
    }

    [Fact]
    public void each_programme_starts_where_the_previous_one_ended()
    {
        var from = Anchor.AddHours(10);
        var slots = ChannelScheduleResolver.ResolveRange(AllDay(), Denver, Anchor, from, from.AddHours(3));

        // Gaps or overlaps here would draw a guide that does not line up.
        foreach (var (earlier, later) in slots.Zip(slots.Skip(1)))
        {
            Assert.Equal(earlier.EndUtc, later.StartUtc);
        }
    }

    [Fact]
    public void the_first_entry_is_whatever_is_airing_at_the_start_of_the_window()
    {
        var from = Anchor.AddHours(10).AddMinutes(7);
        var slots = ChannelScheduleResolver.ResolveRange(AllDay(), Denver, Anchor, from, from.AddHours(3));
        var now = ChannelScheduleResolver.Resolve(AllDay(), Denver, Anchor, from);

        Assert.Equal(now!.Value.ItemId, slots[0].ItemId);
        Assert.Equal(now.Value.OffsetTicks, slots[0].OffsetTicks);
    }

    [Fact]
    public void an_off_air_gap_is_stepped_over_rather_than_ending_the_guide()
    {
        // Airs 06:00-07:00 and 09:00-10:00 local, dark in between. A guide that
        // gave up at the first gap would show nothing after it.
        var dayparts = new List<Daypart>
        {
            new("Morning", TimeSpan.FromHours(6), TimeSpan.FromHours(7),
                new ChannelSnapshot([new ChannelProgram(A, Minutes(30))], 1, Anchor)),
            new("Late", TimeSpan.FromHours(9), TimeSpan.FromHours(10),
                new ChannelSnapshot([new ChannelProgram(B, Minutes(30))], 1, Anchor))
        };

        var from = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(new DateTime(2026, 6, 15, 6, 0, 0), DateTimeKind.Unspecified), Denver);

        var slots = ChannelScheduleResolver.ResolveRange(dayparts, Denver, Anchor, from, from.AddHours(4));

        Assert.Contains(slots, s => s.ItemId == A);
        Assert.Contains(slots, s => s.ItemId == B);
    }

    [Fact]
    public void a_channel_with_nothing_playable_yields_an_empty_guide()
    {
        var empty = new List<Daypart>
        {
            new("All day", TimeSpan.Zero, TimeSpan.FromDays(1), new ChannelSnapshot([], 1, Anchor))
        };

        Assert.Empty(ChannelScheduleResolver.ResolveRange(
            empty, Denver, Anchor, Anchor, Anchor.AddHours(3)));
    }

    [Fact]
    public void the_walk_is_bounded()
    {
        // Very short programmes over a long window must not produce an
        // unbounded list; the cap is what makes the endpoint safe to expose.
        var dayparts = new List<Daypart>
        {
            new("All day", TimeSpan.Zero, TimeSpan.FromDays(1),
                new ChannelSnapshot([new ChannelProgram(A, Minutes(1))], 1, Anchor))
        };

        var slots = ChannelScheduleResolver.ResolveRange(
            dayparts, Denver, Anchor, Anchor, Anchor.AddDays(7), maxPrograms: 50);

        Assert.Equal(50, slots.Count);
    }

    [Fact]
    public void a_guide_never_runs_backwards_across_a_daypart_change()
    {
        // Each daypart has its own continuous timeline, so the first programme
        // after one opens is joined in progress and reports a start from before
        // the daypart existed. Drawn literally, that block reaches back over the
        // one before it. Observed live: a film listed 18:30-20:42 directly after
        // an episode ending at 20:00.
        var dayparts = new List<Daypart>
        {
            new("Series", TimeSpan.Zero, TimeSpan.FromHours(20),
                new ChannelSnapshot([new ChannelProgram(A, Minutes(30))], 1, Anchor)),
            new("Films", TimeSpan.FromHours(20), TimeSpan.FromDays(1),
                new ChannelSnapshot([new ChannelProgram(B, Minutes(130))], 1, Anchor))
        };

        var from = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(new DateTime(2026, 6, 15, 19, 0, 0), DateTimeKind.Unspecified), Denver);
        var slots = ChannelScheduleResolver.ResolveRange(dayparts, Denver, Anchor, from, from.AddHours(3));

        foreach (var (earlier, later) in slots.Zip(slots.Skip(1)))
        {
            Assert.True(later.StartUtc >= earlier.EndUtc,
                $"{later.StartUtc:HH:mm} begins before {earlier.EndUtc:HH:mm} ended");
        }
        Assert.All(slots, s => Assert.True(s.StartUtc >= from, "no entry may start before the window"));
    }
}
