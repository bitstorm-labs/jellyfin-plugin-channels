using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class ChannelNumberingTests
{
    private static Guid Id(int n) => new(n, 7, 11, new byte[8]);

    [Fact]
    public void numbers_are_unique_in_range_and_not_simply_one_to_n()
    {
        var channels = Enumerable.Range(1, 7).Select(n => (Id(n), (int?)null)).ToList();
        var numbers = ChannelNumbering.Assign(channels);

        Assert.Equal(7, numbers.Values.Distinct().Count());
        Assert.All(numbers.Values, n => Assert.InRange(n, ChannelNumbering.Lowest, ChannelNumbering.Highest));
        Assert.NotEqual(Enumerable.Range(1, 7), numbers.Values.OrderBy(n => n));
    }

    [Fact]
    public void a_channel_keeps_its_number_when_others_come_and_go()
    {
        var before = ChannelNumbering.Assign(new[] { (Id(1), (int?)null), (Id(2), null), (Id(3), null) });
        var after = ChannelNumbering.Assign(new[] { (Id(1), (int?)null), (Id(3), null), (Id(9), null), (Id(8), null) });

        Assert.Equal(before[Id(1)], after[Id(1)]);
        Assert.Equal(before[Id(3)], after[Id(3)]);
    }

    [Fact]
    public void a_pinned_number_wins_and_is_never_handed_to_another_channel()
    {
        var free = ChannelNumbering.Assign(new[] { (Id(1), (int?)null) });
        var taken = free[Id(1)];

        var numbers = ChannelNumbering.Assign(new[] { (Id(1), (int?)null), (Id(2), (int?)taken) });

        Assert.Equal(taken, numbers[Id(2)]);
        Assert.NotEqual(taken, numbers[Id(1)]);
    }

    [Fact]
    public void the_same_line_up_always_gets_the_same_numbers()
    {
        var channels = Enumerable.Range(1, 20).Select(n => (Id(n), (int?)null)).ToList();
        Assert.Equal(ChannelNumbering.Assign(channels), ChannelNumbering.Assign(channels.AsEnumerable().Reverse()));
    }
}
