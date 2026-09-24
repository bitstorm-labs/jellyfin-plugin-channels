using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

/// <summary>
/// The live Nebula marathon configuration, kept as regression tests: a future
/// slot must appear both when resolved directly and when a week-long guide
/// walks into it from days earlier.
/// </summary>
public class MarathonGuideTests
{
    [Fact]
    public void saturday_marathon_as_configured_live()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        var anchor = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
        var eps = Enumerable.Range(1, 100).Select(n => new ChannelProgram(Guid.Parse($"e0000000-0000-0000-0000-{n:000000000000}"), 25_000_000_000)).ToList();
        var filler = new ChannelProgram(Guid.Parse("f0000000-0000-0000-0000-000000000001"), 18_000_000_000);
        var parts = new List<Daypart>
        {
            new("The 100 Marathon", TimeSpan.FromMinutes(780), TimeSpan.FromMinutes(1080), new ChannelSnapshot(eps, 1, anchor),
                ChannelDays.Saturday, null, episodePerAiring: true, firstAiring: new DateTime(2026, 9, 26), episodesPerAiring: 0),
            new("Series", TimeSpan.Zero, TimeSpan.FromMinutes(1200), new ChannelSnapshot([filler], 1, anchor))
        };
        var at = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(new DateTime(2026, 9, 26, 13, 5, 0), DateTimeKind.Unspecified), tz);
        var slot = ChannelScheduleResolver.Resolve(parts, tz, anchor, at);
        Assert.Equal(eps[0].ItemId, slot!.Value.ItemId);
    }

    [Fact]
    public void saturday_marathon_appears_in_a_guide_that_starts_two_days_earlier()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        var anchor = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
        var eps = Enumerable.Range(1, 100).Select(n => new ChannelProgram(Guid.Parse($"e0000000-0000-0000-0000-{n:000000000000}"), 25_000_000_000)).ToList();
        var fill = Enumerable.Range(1, 40).Select(n => new ChannelProgram(Guid.Parse($"f0000000-0000-0000-0000-{n:000000000000}"), (20 + n % 30) * TimeSpan.TicksPerMinute)).ToList();
        var parts = new List<Daypart>
        {
            new("The 100 Marathon", TimeSpan.FromMinutes(780), TimeSpan.FromMinutes(1080), new ChannelSnapshot(eps, 1, anchor),
                ChannelDays.Saturday, null, episodePerAiring: true, firstAiring: new DateTime(2026, 9, 26), episodesPerAiring: 0),
            new("Series", TimeSpan.Zero, TimeSpan.FromMinutes(1200), new ChannelSnapshot(fill, 1, anchor)),
            new("Features", TimeSpan.FromMinutes(1200), TimeSpan.FromMinutes(1440), new ChannelSnapshot(fill, 1, anchor))
        };
        var from = new DateTime(2026, 9, 24, 21, 0, 0, DateTimeKind.Utc);
        var guide = ChannelScheduleResolver.ResolveRange(parts, tz, anchor, from, from.AddHours(168), 6720);
        var sat13 = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(new DateTime(2026, 9, 26, 13, 0, 0), DateTimeKind.Unspecified), tz);
        var at = guide.First(s => s.StartUtc >= sat13);
        Assert.Equal(sat13, at.StartUtc);
        Assert.Equal(eps[0].ItemId, at.ItemId);
    }
}
