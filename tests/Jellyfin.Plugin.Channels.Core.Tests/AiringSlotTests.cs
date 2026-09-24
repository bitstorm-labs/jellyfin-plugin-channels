using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

/// <summary>
/// "Survivor, Wednesdays at 7": a daypart that airs one episode per occurrence,
/// in order, starting on the slot, with the channel's normal schedule around it.
/// </summary>
public class AiringSlotTests
{
    private static readonly TimeZoneInfo Denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");

    // Anchor on Wednesday 2026-09-16, local midnight.
    private static readonly DateTime Anchor = TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(new DateTime(2026, 9, 16), DateTimeKind.Unspecified), Denver);

    private static readonly Guid Filler = Guid.Parse("f0000000-0000-0000-0000-000000000001");
    private static Guid Ep(int n) => Guid.Parse($"e0000000-0000-0000-0000-{n:000000000000}");

    private static DateTime Local(int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(new DateTime(2026, month, day, hour, minute, 0), DateTimeKind.Unspecified), Denver);

    private static ChannelSnapshot Snapshot(params (Guid Id, double Minutes)[] programs) =>
        new(programs.Select(p => new ChannelProgram(p.Id, (long)(p.Minutes * TimeSpan.TicksPerMinute))).ToList(), 1, Anchor);

    /// Filler all day in 30-minute programmes; Survivor Wednesdays 19:00-20:00,
    /// three 43-minute episodes.
    private static List<Daypart> Schedule() =>
    [
        new Daypart("Survivor", TimeSpan.FromHours(19), TimeSpan.FromHours(20),
            Snapshot((Ep(1), 43), (Ep(2), 43), (Ep(3), 43)),
            ChannelDays.Wednesday, season: null, episodePerAiring: true),
        new Daypart("All day", TimeSpan.Zero, TimeSpan.FromHours(24), Snapshot((Filler, 30)))
    ];

    [Fact]
    public void the_first_airing_starts_the_first_episode_on_the_slot()
    {
        var at = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, Local(9, 16, 19, 10))!.Value;

        Assert.Equal(Ep(1), at.ItemId);
        Assert.Equal(Local(9, 16, 19), at.StartUtc);
        Assert.Equal(Local(9, 16, 19, 43), at.EndUtc);
        Assert.Equal(10 * 60, at.OffsetSeconds, 3);
    }

    [Fact]
    public void each_week_airs_the_next_episode_and_wraps_after_the_last()
    {
        Assert.Equal(Ep(2), ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, Local(9, 23, 19, 5))!.Value.ItemId);
        Assert.Equal(Ep(3), ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, Local(9, 30, 19, 5))!.Value.ItemId);
        Assert.Equal(Ep(1), ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, Local(10, 7, 19, 5))!.Value.ItemId);
    }

    [Fact]
    public void after_the_episode_the_channel_goes_back_to_its_normal_schedule()
    {
        var at = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, Local(9, 16, 19, 50))!.Value;

        Assert.Equal(Filler, at.ItemId);
    }

    [Fact]
    public void the_programme_before_the_slot_is_cut_at_the_slot_and_hands_over_to_the_episode()
    {
        // 25-minute filler from midnight puts one programme across the slot
        // start: 18:45-19:10.
        var parts = new List<Daypart>
        {
            Schedule()[0],
            new Daypart("All day", TimeSpan.Zero, TimeSpan.FromHours(24), Snapshot((Filler, 25)))
        };
        var at = ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 18, 55))!.Value;

        Assert.Equal(Filler, at.ItemId);
        Assert.Equal(Local(9, 16, 19), at.EndUtc);
        Assert.Equal(Ep(1), at.NextItemId);
    }

    [Fact]
    public void other_days_never_see_the_slot()
    {
        Assert.Equal(Filler, ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, Local(9, 17, 19, 10))!.Value.ItemId);
    }

    [Fact]
    public void the_guide_shows_the_episode_starting_exactly_on_the_slot()
    {
        var guide = ChannelScheduleResolver.ResolveRange(Schedule(), Denver, Anchor, Local(9, 23, 18), Local(9, 23, 21));
        var episode = guide.Single(s => s.ItemId == Ep(2));

        Assert.Equal(Local(9, 23, 19), episode.StartUtc);
        Assert.Equal(Local(9, 23, 19, 43), episode.EndUtc);
        Assert.Equal(Filler, guide.First(s => s.StartUtc >= episode.EndUtc).ItemId);
    }

    [Fact]
    public void an_episode_longer_than_its_slot_is_cut_at_the_slot_end()
    {
        var parts = new List<Daypart>
        {
            new("Movie night", TimeSpan.FromHours(19), TimeSpan.FromHours(20),
                Snapshot((Ep(1), 95)), ChannelDays.Wednesday, season: null, episodePerAiring: true),
            new("All day", TimeSpan.Zero, TimeSpan.FromHours(24), Snapshot((Filler, 30)))
        };

        Assert.Equal(Local(9, 16, 20), ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 19, 30))!.Value.EndUtc);
    }
}
