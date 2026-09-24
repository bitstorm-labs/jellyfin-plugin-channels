using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class ChannelLogoTests
{
    private static ChannelDefinition Solstice() => new()
    {
        Logo = "solstice",
        Dayparts =
        [
            new DaypartDefinition { Name = "Autumn", SeasonStartMonthDay = 915, SeasonEndMonthDay = 1124 },
            new DaypartDefinition { Name = "Yule", SeasonStartMonthDay = 1125, SeasonEndMonthDay = 1226, Logo = "solstice-winter" }
        ]
    };

    [Fact]
    public void the_channel_logo_shows_outside_any_seasonal_override()
    {
        Assert.Equal("solstice", ChannelLogo.Resolve(Solstice(), new DateTime(2026, 10, 3)));
        Assert.Equal("solstice", ChannelLogo.Resolve(Solstice(), new DateTime(2026, 7, 1)));
    }

    [Fact]
    public void a_seasonal_daypart_logo_replaces_it_for_the_whole_season()
    {
        Assert.Equal("solstice-winter", ChannelLogo.Resolve(Solstice(), new DateTime(2026, 11, 25)));
        Assert.Equal("solstice-winter", ChannelLogo.Resolve(Solstice(), new DateTime(2026, 12, 26, 8, 0, 0)));
        Assert.Equal("solstice", ChannelLogo.Resolve(Solstice(), new DateTime(2026, 12, 27)));
    }

    [Fact]
    public void a_daypart_without_a_season_never_overrides()
    {
        var channel = new ChannelDefinition
        {
            Logo = "nebula",
            Dayparts = [new DaypartDefinition { Name = "All day", Logo = "the-reel" }]
        };

        Assert.Equal("nebula", ChannelLogo.Resolve(channel, new DateTime(2026, 3, 1)));
    }

    [Fact]
    public void no_logo_is_null_not_empty()
    {
        Assert.Null(ChannelLogo.Resolve(new ChannelDefinition { Logo = " " }, new DateTime(2026, 3, 1)));
    }
}
