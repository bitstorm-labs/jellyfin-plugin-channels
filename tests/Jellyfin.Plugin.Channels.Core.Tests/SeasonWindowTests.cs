using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class SeasonWindowTests
{
    private static readonly TimeZoneInfo Denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
    private static readonly Guid Horror = Guid.Parse("40000000-0000-0000-0000-00000000000f");
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ChannelSnapshot One(Guid id, double minutes) =>
        new([new ChannelProgram(id, (long)(minutes * TimeSpan.TicksPerMinute))], 1, Anchor);

    private static DateTime LocalDenver(int year, int month, int day, int hour = 12) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(new DateTime(year, month, day, hour, 0, 0), DateTimeKind.Unspecified),
            Denver);

    private static DateTime Local(int month, int day) => new(2026, month, day, 12, 0, 0);

    [Fact]
    public void a_window_is_open_inside_its_dates_and_shut_outside_them()
    {
        var october = new SeasonWindow(1001, 1031);

        Assert.True(october.Covers(Local(10, 1)));
        Assert.True(october.Covers(Local(10, 17)));
        Assert.True(october.Covers(Local(10, 31)));
        Assert.False(october.Covers(Local(9, 30)));
        Assert.False(october.Covers(Local(11, 1)));
        Assert.False(october.Covers(Local(4, 12)));
    }

    [Fact]
    public void a_window_ending_before_it_starts_wraps_the_year_end()
    {
        // Christmas is the case that makes a naive start<=today<=end test wrong.
        var christmas = new SeasonWindow(1215, 105);

        Assert.True(christmas.WrapsYearEnd);
        Assert.True(christmas.Covers(Local(12, 15)));
        Assert.True(christmas.Covers(Local(12, 31)));
        Assert.True(christmas.Covers(Local(1, 1)));
        Assert.True(christmas.Covers(Local(1, 5)));
        Assert.False(christmas.Covers(Local(1, 6)));
        Assert.False(christmas.Covers(Local(7, 4)));
    }

    [Fact]
    public void a_single_day_window_is_open_on_exactly_that_day()
    {
        var halloween = new SeasonWindow(1031, 1031);

        Assert.True(halloween.Covers(Local(10, 31)));
        Assert.False(halloween.Covers(Local(10, 30)));
        Assert.False(halloween.Covers(Local(11, 1)));
    }

    [Fact]
    public void the_twenty_ninth_of_february_is_a_real_boundary()
    {
        // A recurring window carries no year, so it must not be validated
        // against one that happens not to be a leap year.
        var window = new SeasonWindow(229, 301);

        Assert.True(window.Covers(new DateTime(2024, 2, 29, 12, 0, 0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1301)]
    [InlineData(1032)]
    [InlineData(230)]
    [InlineData(-101)]
    public void a_boundary_that_is_not_a_real_date_is_rejected(int monthDay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeasonWindow(monthDay, 1231));
    }

    [Fact]
    public void neither_end_set_means_all_year()
    {
        Assert.Null(SeasonWindow.From(null, null));
    }

    [Fact]
    public void one_end_set_without_the_other_is_rejected_rather_than_guessed()
    {
        // Falling back to "always" here is how a Christmas channel ends up
        // playing in July with nothing in the log to explain it.
        Assert.Throws<ArgumentOutOfRangeException>(() => SeasonWindow.From(1001, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => SeasonWindow.From(null, 1031));
    }

    [Fact]
    public void a_daypart_out_of_season_is_off_air()
    {
        var hallows = new List<Daypart>
        {
            new("Spooky", TimeSpan.Zero, TimeSpan.FromHours(24), One(Horror, 90),
                ChannelDays.All, new SeasonWindow(1001, 1102))
        };

        var october = ChannelScheduleResolver.Resolve(hallows, Denver, Anchor, LocalDenver(2026, 10, 15));
        var december = ChannelScheduleResolver.Resolve(hallows, Denver, Anchor, LocalDenver(2026, 12, 15));

        Assert.Equal(Horror, october!.Value.ItemId);
        Assert.Null(december);
    }

    [Fact]
    public void a_daypart_with_no_season_still_airs_all_year()
    {
        // The field is new; every existing channel must behave exactly as before.
        var always = new List<Daypart>
        {
            new("Always", TimeSpan.Zero, TimeSpan.FromHours(24), One(Horror, 90))
        };

        Assert.NotNull(ChannelScheduleResolver.Resolve(always, Denver, Anchor, LocalDenver(2026, 2, 3)));
        Assert.NotNull(ChannelScheduleResolver.Resolve(always, Denver, Anchor, LocalDenver(2026, 8, 22)));
    }

    [Fact]
    public void seasons_hand_over_to_each_other_on_one_channel()
    {
        // The point of putting the window on the daypart rather than the
        // channel: one Seasonal station can carry a different theme per season.
        var spooky = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var festive = Guid.Parse("40000000-0000-0000-0000-000000000002");

        var seasonal = new List<Daypart>
        {
            new("Spooky", TimeSpan.Zero, TimeSpan.FromHours(24), One(spooky, 90),
                ChannelDays.All, new SeasonWindow(1001, 1102)),
            new("Festive", TimeSpan.Zero, TimeSpan.FromHours(24), One(festive, 90),
                ChannelDays.All, new SeasonWindow(1125, 1226))
        };

        Assert.Equal(spooky, ChannelScheduleResolver.Resolve(seasonal, Denver, Anchor, LocalDenver(2026, 10, 20))!.Value.ItemId);
        Assert.Equal(festive, ChannelScheduleResolver.Resolve(seasonal, Denver, Anchor, LocalDenver(2026, 12, 10))!.Value.ItemId);
        Assert.Null(ChannelScheduleResolver.Resolve(seasonal, Denver, Anchor, LocalDenver(2026, 6, 10)));
    }
}
