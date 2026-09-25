using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

/// <summary>Programmes start from their beginning and play to their end.</summary>
public class DayPlannerTests
{
    private static readonly DateTime Day = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc); // a Friday
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static ChannelProgram P(string group, double minutes) =>
        new(Guid.NewGuid(), (long)(minutes * TimeSpan.TicksPerMinute), group);

    private static ChannelSnapshot Snap(params ChannelProgram[] programs) => new(programs, 1, Day);

    private static Daypart Part(string name, double fromHour, double toHour, ChannelSnapshot snapshot,
        bool slot = false, int perAiring = 1, ChannelDays days = ChannelDays.All) =>
        new(name, TimeSpan.FromHours(fromHour), TimeSpan.FromHours(toHour), snapshot, days, null, slot, null, perAiring);

    private static IReadOnlyList<ProgramSlot> Guide(Daypart[] parts, double fromHour, double toHour, bool align = true) =>
        ChannelScheduleResolver.ResolveRange(parts, Utc, Day, Day.AddHours(fromHour), Day.AddHours(toHour), 500, align);

    private static List<DayPlanner.Airing> Plan(Daypart[] parts, int day = 0, bool align = true) =>
        new DayPlanner(parts, Utc, Day, align).Day(Day.AddDays(day)).ToList();

    [Fact]
    public void a_fill_slot_never_starts_a_film_it_cannot_finish()
    {
        // Premiere Night, 20:00-24:00: 98 + 100 fit; the 169-minute film does not.
        var films = Snap(P("Benny", 98), P("Coraline", 100), P("Interstellar", 169));
        var parts = new[] { Part("Premiere", 20, 24, films, slot: true, perAiring: 0), Part("Movies", 0, 24, Snap(P("X", 60), P("Y", 60))) };

        var night = Plan(parts).Where(a => a.StartUtc >= Day.AddHours(20)).ToList();
        Assert.All(night, a => Assert.True(a.EndUtc <= Day.AddHours(24)));
        var shown = night.Where(a => a.ItemId != Guid.Empty).Select(a => a.ItemId).ToList();
        Assert.Equal(new[] { films.Programs[0].ItemId, films.Programs[1].ItemId }, shown);
    }

    [Fact]
    public void after_a_slot_the_channel_starts_a_programme_from_its_beginning()
    {
        var movies = Snap(P("A", 169), P("B", 95), P("C", 110));
        var parts = new[] { Part("Premiere", 20, 24, Snap(P("New", 100)), slot: true, perAiring: 1), Part("Movies", 0, 24, movies) };

        var afterMidnight = ChannelScheduleResolver.Resolve(parts, Utc, Day, Day.AddHours(24).AddMinutes(1), true)!.Value;
        Assert.Equal(Day.AddHours(24), afterMidnight.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, afterMidnight.OffsetTicks);
    }

    [Fact]
    public void a_daypart_opening_at_six_opens_with_a_programme_at_six()
    {
        var shows = Snap(P("Gilmore", 44), P("Abbott", 22), P("Parks", 22));
        var films = Snap(P("Coraline", 100), P("Lorax", 86), P("Moonrise", 94));
        var parts = new[] { Part("Day", 0, 18, shows), Part("Evening", 18, 24, films) };

        var six = ChannelScheduleResolver.Resolve(parts, Utc, Day, Day.AddHours(18), true)!.Value;
        Assert.Equal(Day.AddHours(18), six.StartUtc);
        Assert.Equal(0, six.OffsetTicks);
        Assert.Contains(six.ItemId, films.Programs.Select(p => p.ItemId));
    }

    [Fact]
    public void nothing_the_daytime_shows_start_runs_into_the_evening()
    {
        var shows = Snap(P("Gilmore", 44), P("Abbott", 22), P("Parks", 22), P("Ghosts", 22));
        var parts = new[] { Part("Day", 0, 18, shows), Part("Evening", 18, 24, Snap(P("Film", 100))) };

        Assert.All(Plan(parts).Where(a => a.StartUtc < Day.AddHours(18)), a => Assert.True(a.EndUtc <= Day.AddHours(18)));
    }

    [Fact]
    public void programmes_start_on_the_half_hour_and_breaks_fill_the_rest()
    {
        var shows = Snap(P("Parks", 22), P("Ghosts", 22), P("Abbott", 22), P("Clip1", 11), P("Clip2", 12), P("Clip3", 9));
        var plan = Plan(new[] { Part("All", 0, 24, shows) });

        for (var i = 0; i < plan.Count; i++)
        {
            var afterBreak = i == 0 || plan[i - 1].ItemId == Guid.Empty;
            if (plan[i].ItemId != Guid.Empty && afterBreak)
            {
                Assert.Equal(0, plan[i].StartUtc.Ticks % TimeSpan.FromMinutes(30).Ticks);
            }
        }

        Assert.Contains(plan, a => a.ItemId == Guid.Empty);
    }

    [Fact]
    public void a_weekly_slot_airs_its_episode_then_a_break_to_the_hour()
    {
        var survivor = Snap(P("Survivor", 44), P("Survivor", 44));
        var parts = new[] { Part("Survivor Night", 19, 20, survivor, slot: true), Part("Reality", 0, 24, Snap(P("Other", 30))) };

        var inBreak = ChannelScheduleResolver.Resolve(parts, Utc, Day, Day.AddHours(19).AddMinutes(50), true)!.Value;
        Assert.Equal(Guid.Empty, inBreak.ItemId);
        Assert.Equal(Day.AddHours(20), inBreak.EndUtc);

        var episode = ChannelScheduleResolver.Resolve(parts, Utc, Day, Day.AddHours(19).AddMinutes(1), true)!.Value;
        Assert.Equal(survivor.Programs[0].ItemId, episode.ItemId);
        Assert.Equal(Day.AddHours(19), episode.StartUtc);
    }

    [Fact]
    public void each_day_picks_up_where_the_last_one_stopped()
    {
        var films = Snap(Enumerable.Range(0, 12).Select(i => P("F" + i, 100)).ToArray());
        var parts = new[] { Part("Evening", 18, 24, films) };

        var first = Plan(parts, 0).Where(a => a.ItemId != Guid.Empty).Select(a => a.ItemId).ToList();
        var second = Plan(parts, 1).Where(a => a.ItemId != Guid.Empty).Select(a => a.ItemId).ToList();
        Assert.Empty(first.Intersect(second));
        Assert.Equal(films.Programs[first.Count].ItemId, second[0]);
    }

    [Fact]
    public void an_all_day_channel_runs_a_film_on_past_midnight()
    {
        var films = Snap(P("A", 23 * 60 + 30), P("B", 120), P("C", 90));
        var parts = new[] { Part("All", 0, 24, films) };

        var b = ChannelScheduleResolver.Resolve(parts, Utc, Day, Day.AddHours(24).AddMinutes(30), false)!.Value;
        Assert.Equal(films.Programs[1].ItemId, b.ItemId);
        Assert.Equal(Day.AddHours(23.5), b.StartUtc);
        Assert.Single(Guide(parts, 23, 26, false), g => g.ItemId == films.Programs[1].ItemId);
    }

    [Fact]
    public void the_guide_lists_a_programme_through_its_break()
    {
        var shows = Snap(P("Parks", 22), P("Ghosts", 22));
        var parts = new[] { Part("All", 0, 24, shows) };

        var guide = Guide(parts, 0, 1);
        Assert.Equal(Day.AddMinutes(30), guide[0].EndUtc);
        Assert.Equal(Day.AddMinutes(30), guide[1].StartUtc);

        var mid = Guide(parts, 25.0 / 60, 1);
        Assert.Equal(guide[0].ItemId, mid[0].ItemId);
        Assert.Equal(Day.AddMinutes(30), mid[0].EndUtc);
    }

    [Fact]
    public void packing_never_puts_a_show_next_to_itself()
    {
        var clips = Snap(Enumerable.Range(0, 60).Select(i => P(i % 3 == 0 ? "GCN" : i % 3 == 1 ? "Builds" : "Syd", 8 + (i * 7 % 11))).ToArray());
        var plan = Plan(new[] { Part("All", 0, 24, clips) });
        var groups = clips.Programs.ToDictionary(p => p.ItemId, p => p.Group);
        var aired = plan.Where(a => a.ItemId != Guid.Empty).Select(a => groups[a.ItemId]).ToList();

        Assert.True(aired.Zip(aired.Skip(1)).Count(p => p.First == p.Second) <= 2);
    }
}

public class DayPlannerAlignmentTests
{
    private static readonly DateTime Day = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void a_film_running_past_midnight_leaves_tomorrow_on_the_half_hour()
    {
        var films = new ChannelSnapshot(new[]
        {
            new ChannelProgram(Guid.NewGuid(), TimeSpan.FromMinutes(23 * 60).Ticks, "Long"),
            new ChannelProgram(Guid.NewGuid(), TimeSpan.FromMinutes(100).Ticks, "Film"),
            new ChannelProgram(Guid.NewGuid(), TimeSpan.FromMinutes(95).Ticks, "Other"),
            new ChannelProgram(Guid.NewGuid(), TimeSpan.FromMinutes(118).Ticks, "Third"),
        }, 1, Day);
        var parts = new[] { new Daypart("All", TimeSpan.Zero, TimeSpan.FromHours(24), films) };
        var planner = new DayPlanner(parts, TimeZoneInfo.Utc, Day, alignToHalfHour: true);

        foreach (var a in planner.Day(Day).Concat(planner.Day(Day.AddDays(1))).Concat(planner.Day(Day.AddDays(2))))
        {
            if (a.ItemId != Guid.Empty)
            {
                Assert.Equal(0, a.StartUtc.Ticks % TimeSpan.FromMinutes(30).Ticks);
            }
        }
    }
}
