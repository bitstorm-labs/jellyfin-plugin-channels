using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class ChannelScheduleResolverTests
{
    private static readonly TimeZoneInfo Denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");

    private static readonly Guid Cartoon = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Sitcom = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid Drama = Guid.Parse("d0000000-0000-0000-0000-000000000003");

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ChannelSnapshot One(Guid id, double minutes) =>
        new([new ChannelProgram(id, (long)(minutes * TimeSpan.TicksPerMinute))], 1, Anchor);

    /// Morning cartoons 06:00-12:00, sitcoms 12:00-18:00, drama 18:00-24:00.
    private static List<Daypart> Schedule() =>
    [
        new Daypart("Cartoons", TimeSpan.FromHours(6), TimeSpan.FromHours(12), One(Cartoon, 30)),
        new Daypart("Sitcoms", TimeSpan.FromHours(12), TimeSpan.FromHours(18), One(Sitcom, 30)),
        new Daypart("Drama", TimeSpan.FromHours(18), TimeSpan.FromHours(24), One(Drama, 60))
    ];

    /// <summary>A local wall-clock time in Denver, as UTC.</summary>
    private static DateTime LocalDenver(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, 0), DateTimeKind.Unspecified),
            Denver);

    [Fact]
    public void the_time_of_day_decides_which_daypart_you_get()
    {
        var morning = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, LocalDenver(2026, 6, 15, 9));
        var afternoon = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, LocalDenver(2026, 6, 15, 14));
        var evening = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, LocalDenver(2026, 6, 15, 20));

        Assert.Equal(Cartoon, morning!.Value.ItemId);
        Assert.Equal(Sitcom, afternoon!.Value.ItemId);
        Assert.Equal(Drama, evening!.Value.ItemId);
    }

    [Fact]
    public void a_gap_in_the_schedule_is_off_air_rather_than_an_error()
    {
        // 00:00-06:00 is covered by nothing.
        var slot = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, LocalDenver(2026, 6, 15, 3));

        Assert.Null(slot);
    }

    [Fact]
    public void a_single_all_day_daypart_behaves_exactly_like_an_undayparted_channel()
    {
        // The ordinary case through the planner: one 24h block of 30-minute
        // cartoons from midnight, asked at 03:17, is 17 minutes into the
        // cartoon that started at 03:00.
        var snapshot = One(Cartoon, 30);
        var dayparts = new List<Daypart>
        {
            new("All day", TimeSpan.Zero, TimeSpan.FromDays(1), snapshot)
        };
        var now = LocalDenver(2026, 6, 15, 3, 17);

        var slot = ChannelScheduleResolver.Resolve(dayparts, Denver, Anchor, now)!.Value;

        Assert.Equal(Cartoon, slot.ItemId);
        Assert.Equal(LocalDenver(2026, 6, 15, 3, 0), slot.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(17).Ticks, slot.OffsetTicks);
        Assert.Equal(LocalDenver(2026, 6, 15, 3, 30), slot.EndUtc);
    }

    [Fact]
    public void a_daypart_ends_with_a_whole_programme_not_a_cut_one()
    {
        // Drama runs 60m programmes from its opening, so the last one of the
        // night ends exactly at midnight rather than being cut there.
        var at2330 = LocalDenver(2026, 6, 15, 23, 30);
        var slot = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, at2330)!.Value;

        Assert.Equal(Drama, slot.ItemId);
        Assert.Equal(LocalDenver(2026, 6, 16, 0), slot.EndUtc);
    }

    [Fact]
    public void the_cut_hands_over_to_whatever_the_next_daypart_opens_with()
    {
        // Cartoons end at 12:00 and sitcoms begin, so a cartoon straddling noon
        // is cut and the next item is the sitcom, not the next cartoon.
        var at1150 = LocalDenver(2026, 6, 15, 11, 50);
        var slot = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, at1150)!.Value;

        Assert.Equal(Cartoon, slot.ItemId);
        Assert.Equal(LocalDenver(2026, 6, 15, 12), slot.EndUtc);
        Assert.Equal(Sitcom, slot.NextItemId);
    }

    [Fact]
    public void a_daypart_only_airs_on_the_days_it_is_scheduled_for()
    {
        var weekdayOnly = new List<Daypart>
        {
            new("Weekday cartoons", TimeSpan.FromHours(6), TimeSpan.FromHours(12),
                One(Cartoon, 30), ChannelDays.Weekdays)
        };

        // 2026-06-15 is a Monday, 2026-06-13 a Saturday.
        Assert.NotNull(ChannelScheduleResolver.Resolve(weekdayOnly, Denver, Anchor, LocalDenver(2026, 6, 15, 9)));
        Assert.Null(ChannelScheduleResolver.Resolve(weekdayOnly, Denver, Anchor, LocalDenver(2026, 6, 13, 9)));
    }

    [Fact]
    public void the_same_instant_resolves_differently_in_different_timezones()
    {
        // The channel's zone is what "09:00" means, so an identical UTC instant
        // lands in different dayparts depending on the zone it is read in.
        var utcInstant = LocalDenver(2026, 6, 15, 9);
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var inDenver = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, utcInstant);
        var inNewYork = ChannelScheduleResolver.Resolve(Schedule(), newYork, Anchor, utcInstant);

        Assert.Equal(Cartoon, inDenver!.Value.ItemId);
        // 09:00 Denver is 11:00 New York — still cartoons, but prove the shift is real.
        Assert.Equal(11, TimeZoneInfo.ConvertTimeFromUtc(utcInstant, newYork).Hour);
        Assert.Equal(Cartoon, inNewYork!.Value.ItemId);

        // Two hours later it has crossed into sitcoms in New York but not Denver.
        var later = utcInstant.AddHours(1.5);
        Assert.Equal(Cartoon, ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, later)!.Value.ItemId);
        Assert.Equal(Sitcom, ChannelScheduleResolver.Resolve(Schedule(), newYork, Anchor, later)!.Value.ItemId);
    }

    [Fact]
    public void dayparts_are_resolved_across_a_daylight_saving_change()
    {
        // 2026-03-08 is the US spring-forward. The schedule is defined in local
        // wall time, so 09:00 local is still cartoons on both sides of it.
        var before = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, LocalDenver(2026, 3, 7, 9));
        var after = ChannelScheduleResolver.Resolve(Schedule(), Denver, Anchor, LocalDenver(2026, 3, 9, 9));

        Assert.Equal(Cartoon, before!.Value.ItemId);
        Assert.Equal(Cartoon, after!.Value.ItemId);
    }

    [Fact]
    public void a_daypart_with_nothing_playable_is_off_air()
    {
        var empty = new List<Daypart>
        {
            new("Empty", TimeSpan.Zero, TimeSpan.FromDays(1), new ChannelSnapshot([], 1, Anchor))
        };

        Assert.Null(ChannelScheduleResolver.Resolve(empty, Denver, Anchor, LocalDenver(2026, 6, 15, 9)));
    }

    [Fact]
    public void a_daypart_spanning_midnight_is_rejected_rather_than_guessed_at()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Daypart("Late night", TimeSpan.FromHours(22), TimeSpan.FromHours(2), One(Drama, 30)));
    }
}
