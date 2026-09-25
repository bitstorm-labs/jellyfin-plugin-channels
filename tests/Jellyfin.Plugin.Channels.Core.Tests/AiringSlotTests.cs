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
    public void after_the_episode_the_channel_resumes_with_a_programme_from_its_start()
    {
        // Not joined partway: the channel's own schedule starts a fresh
        // programme when the episode ends (on the half hour when aligned).
        var at = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, Local(9, 16, 19, 50))!.Value;

        Assert.Equal(Filler, at.ItemId);
        Assert.Equal(Local(9, 16, 19, 43), at.StartUtc);
    }

    [Fact]
    public void nothing_starts_before_the_slot_that_would_run_into_it()
    {
        // 25-minute filler from midnight: 18:45-19:10 would cross the slot
        // start, so it does not air; a break holds until the episode at 19:00.
        var parts = new List<Daypart>
        {
            Schedule()[0],
            new Daypart("All day", TimeSpan.Zero, TimeSpan.FromHours(24), Snapshot((Filler, 25)))
        };
        var at = ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 18, 55))!.Value;

        Assert.Equal(Guid.Empty, at.ItemId);
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

    // ---------- Starts on ----------

    private static List<Daypart> StartingOn(DateTime firstLocalDate) =>
    [
        new Daypart("Survivor", TimeSpan.FromHours(19), TimeSpan.FromHours(20),
            Snapshot((Ep(1), 43), (Ep(2), 43), (Ep(3), 43)),
            ChannelDays.Wednesday, season: null, episodePerAiring: true, firstAiring: firstLocalDate),
        new Daypart("All day", TimeSpan.Zero, TimeSpan.FromHours(24), Snapshot((Filler, 30)))
    ];

    [Fact]
    public void a_slot_with_a_start_date_airs_the_first_episode_on_that_date()
    {
        // Without a start date the anchor decides, and a slot created mid-run
        // opens partway into the series.
        var parts = StartingOn(new DateTime(2026, 9, 30));

        Assert.Equal(Ep(1), ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 30, 19, 5))!.Value.ItemId);
        Assert.Equal(Ep(2), ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(10, 7, 19, 5))!.Value.ItemId);
    }

    [Fact]
    public void before_its_start_date_the_slot_does_not_exist()
    {
        var parts = StartingOn(new DateTime(2026, 9, 30));

        Assert.Equal(Filler, ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 23, 19, 5))!.Value.ItemId);
    }

    // ---------- Marathons ----------

    private static List<Daypart> Marathon(int episodesPerAiring) =>
    [
        new Daypart("The 100", TimeSpan.FromHours(13), TimeSpan.FromHours(18),
            Snapshot(Enumerable.Range(1, 20).Select(n => (Ep(n), 43.0)).ToArray()),
            ChannelDays.Wednesday, season: null, episodePerAiring: true, episodesPerAiring: episodesPerAiring),
        new Daypart("All day", TimeSpan.Zero, TimeSpan.FromHours(24), Snapshot((Filler, 30)))
    ];

    [Fact]
    public void a_marathon_airs_its_episodes_back_to_back()
    {
        var parts = Marathon(3);

        Assert.Equal(Ep(1), ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 13, 10))!.Value.ItemId);
        Assert.Equal(Ep(2), ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 13, 50))!.Value.ItemId);
        var third = ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 14, 30))!.Value;
        Assert.Equal(Ep(3), third.ItemId);
        Assert.Equal(Local(9, 16, 14, 26), third.StartUtc);
        // After the third episode the channel's normal schedule resumes.
        Assert.Equal(Filler, ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 15, 30))!.Value.ItemId);
    }

    [Fact]
    public void next_weeks_marathon_picks_up_where_this_one_stopped()
    {
        Assert.Equal(Ep(4), ChannelScheduleResolver.Resolve(Marathon(3), Denver, Anchor, Local(9, 23, 13, 10))!.Value.ItemId);
    }

    [Fact]
    public void a_marathon_set_to_fill_airs_only_whole_episodes()
    {
        // Five hours of 43-minute episodes: six whole ones (13:00-17:18). The
        // seventh would be cut at 18:00, so it is not started; next week opens
        // with it.
        var parts = Marathon(0);

        Assert.Equal(Ep(6), ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 17, 0))!.Value.ItemId);
        Assert.NotEqual(Ep(7), ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 16, 17, 50))!.Value.ItemId);
        Assert.Equal(Ep(7), ChannelScheduleResolver.Resolve(parts, Denver, Anchor, Local(9, 23, 13, 10))!.Value.ItemId);
    }
}
