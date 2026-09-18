using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class ScheduleResolverTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static long Minutes(double m) => (long)(m * TimeSpan.TicksPerMinute);

    /// A=30m, B=20m, C=10m -> one cycle is 60m.
    private static ChannelSnapshot Snapshot() => new(
        [
            new ChannelProgram(A, Minutes(30)),
            new ChannelProgram(B, Minutes(20)),
            new ChannelProgram(C, Minutes(10))
        ],
        generation: 1,
        builtAtUtc: Anchor);

    [Fact]
    public void a_channel_with_nothing_playable_airs_nothing()
    {
        var empty = new ChannelSnapshot([], 1, Anchor);
        Assert.Null(ScheduleResolver.Resolve(empty, Anchor, Anchor.AddHours(3)));
    }

    [Fact]
    public void at_the_anchor_the_first_program_starts_from_the_beginning()
    {
        var slot = ScheduleResolver.Resolve(Snapshot(), Anchor, Anchor)!.Value;

        Assert.Equal(A, slot.ItemId);
        Assert.Equal(0, slot.OffsetTicks);
        Assert.Equal(B, slot.NextItemId);
    }

    [Fact]
    public void tuning_in_mid_program_joins_at_the_elapsed_offset()
    {
        var slot = ScheduleResolver.Resolve(Snapshot(), Anchor, Anchor.AddMinutes(12))!.Value;

        Assert.Equal(A, slot.ItemId);
        Assert.Equal(12 * 60, slot.OffsetSeconds);
        // The viewer missed the first 12 minutes; the program still began on time.
        Assert.Equal(Anchor, slot.StartUtc);
        Assert.Equal(Anchor.AddMinutes(30), slot.EndUtc);
    }

    [Fact]
    public void a_boundary_instant_belongs_to_the_program_starting_there()
    {
        // The bug this guards: treating the interval as closed hands back the
        // program that just ended, at an offset equal to its whole runtime, so
        // the viewer joins on its final frame and is immediately dumped out.
        var slot = ScheduleResolver.Resolve(Snapshot(), Anchor, Anchor.AddMinutes(30))!.Value;

        Assert.Equal(B, slot.ItemId);
        Assert.Equal(0, slot.OffsetTicks);
    }

    [Fact]
    public void the_last_program_wraps_round_to_the_first()
    {
        // 55m in: A(30) + B(20) done, so C is airing and A follows.
        var slot = ScheduleResolver.Resolve(Snapshot(), Anchor, Anchor.AddMinutes(55))!.Value;

        Assert.Equal(C, slot.ItemId);
        Assert.Equal(5 * 60, slot.OffsetSeconds);
        Assert.Equal(A, slot.NextItemId);
    }

    [Fact]
    public void the_schedule_repeats_every_cycle()
    {
        var first = ScheduleResolver.Resolve(Snapshot(), Anchor, Anchor.AddMinutes(12))!.Value;
        var later = ScheduleResolver.Resolve(Snapshot(), Anchor, Anchor.AddMinutes(12 + 60))!.Value;

        Assert.Equal(first.ItemId, later.ItemId);
        Assert.Equal(first.OffsetTicks, later.OffsetTicks);
    }

    [Fact]
    public void resolving_years_later_stays_exact()
    {
        // The reason runtimes are kept in ticks: a channel is queried long after
        // its anchor, and per-program rounding would drift without bound.
        var years = Anchor.AddDays(365 * 5).AddMinutes(42);
        var slot = ScheduleResolver.Resolve(Snapshot(), Anchor, years)!.Value;

        // 365*5 days is a whole number of 60-minute cycles, so only the 42m counts:
        // A(30) done, 12m into B.
        Assert.Equal(B, slot.ItemId);
        Assert.Equal(12 * 60, slot.OffsetSeconds);
    }

    [Fact]
    public void before_the_anchor_the_channel_holds_at_its_first_program()
    {
        // Extrapolating backwards would air programs before the channel existed
        // and hand the viewer a start time in the future.
        var slot = ScheduleResolver.Resolve(Snapshot(), Anchor, Anchor.AddHours(-3))!.Value;

        Assert.Equal(A, slot.ItemId);
        Assert.Equal(0, slot.OffsetTicks);
    }

    [Fact]
    public void unplayable_items_are_dropped_rather_than_occupying_no_time()
    {
        // A zero-runtime entry left in the order either never airs or divides the
        // cycle by zero; it must not reach the resolver at all.
        var snapshot = new ChannelSnapshot(
            [
                new ChannelProgram(A, Minutes(30)),
                new ChannelProgram(B, 0),
                new ChannelProgram(C, Minutes(10))
            ],
            1,
            Anchor);

        Assert.Equal(2, snapshot.Programs.Count);
        Assert.Equal(Minutes(40), snapshot.TotalRuntimeTicks);

        var slot = ScheduleResolver.Resolve(snapshot, Anchor, Anchor.AddMinutes(35))!.Value;
        Assert.Equal(C, slot.ItemId);
        Assert.Equal(A, slot.NextItemId);
    }

    [Fact]
    public void a_single_program_channel_loops_on_itself()
    {
        var snapshot = new ChannelSnapshot([new ChannelProgram(A, Minutes(30))], 1, Anchor);
        var slot = ScheduleResolver.Resolve(snapshot, Anchor, Anchor.AddMinutes(75))!.Value;

        Assert.Equal(A, slot.ItemId);
        Assert.Equal(15 * 60, slot.OffsetSeconds);
        Assert.Equal(A, slot.NextItemId);
    }

    [Fact]
    public void the_end_of_one_program_is_the_start_of_the_next()
    {
        var now = Anchor.AddMinutes(12);
        var slot = ScheduleResolver.Resolve(Snapshot(), Anchor, now)!.Value;
        var atBoundary = ScheduleResolver.Resolve(Snapshot(), Anchor, slot.EndUtc)!.Value;

        Assert.Equal(slot.NextItemId, atBoundary.ItemId);
        Assert.Equal(slot.EndUtc, atBoundary.StartUtc);
        Assert.Equal(0, atBoundary.OffsetTicks);
    }
}
