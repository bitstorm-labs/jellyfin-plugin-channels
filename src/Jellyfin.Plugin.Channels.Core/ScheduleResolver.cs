namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Maps an instant on the wall clock to the program a channel is airing.
///
/// This is deliberately a pure function over a snapshot: no Jellyfin types, no
/// clock of its own, no I/O. Everything the feature gets wrong is arithmetic,
/// and arithmetic is cheap to test exhaustively when nothing else is in the way.
/// It also means every client shares one implementation instead of each platform
/// re-deriving the schedule and drifting from the others.
/// </summary>
public static class ScheduleResolver
{
    /// <summary>
    /// Resolve what is airing at <paramref name="nowUtc"/>.
    /// </summary>
    /// <returns>
    /// The airing program, or <c>null</c> when the channel has nothing playable.
    /// </returns>
    public static ProgramSlot? Resolve(ChannelSnapshot snapshot, DateTime anchorUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.IsEmpty)
        {
            return null;
        }

        var elapsed = nowUtc.Ticks - anchorUtc.Ticks;

        // Before the anchor the channel has not begun. Rather than extrapolating
        // backwards — which would air programs "before" the channel existed and
        // give a viewer a start time in the future — hold at the first program.
        if (elapsed < 0)
        {
            elapsed = 0;
        }

        // The channel loops forever, so only the position within one cycle
        // matters. This is what keeps the cost constant no matter how far past
        // the anchor we are; ticks are long, and a cycle of any realistic length
        // divides a millennium without trouble.
        var cycleOffset = elapsed % snapshot.TotalRuntimeTicks;

        // Walk to the program containing the offset. Intervals are half-open —
        // [start, end) — so an instant landing exactly on a boundary belongs to
        // the program starting there, not the one just finished. Without that a
        // viewer tuning in on the boundary would join the previous program at an
        // offset equal to its full runtime and see nothing but its final frame.
        var index = 0;
        var consumed = 0L;
        while (consumed + snapshot.Programs[index].RuntimeTicks <= cycleOffset)
        {
            consumed += snapshot.Programs[index].RuntimeTicks;
            index++;
        }

        var program = snapshot.Programs[index];
        var offsetTicks = cycleOffset - consumed;
        // What follows is the next real programme; a break is not one.
        var next = snapshot.Programs[(index + 1) % snapshot.Programs.Count];
        for (var step = 2; SlotPacker.IsBreak(next) && step <= snapshot.Programs.Count; step++)
        {
            next = snapshot.Programs[(index + step) % snapshot.Programs.Count];
        }

        // Derive the boundaries from the instant we were asked about, so start
        // and end stay consistent with the offset even when elapsed was clamped.
        var startUtc = nowUtc.AddTicks(-offsetTicks);

        return new ProgramSlot(
            program.ItemId,
            offsetTicks,
            startUtc,
            startUtc.AddTicks(program.RuntimeTicks),
            next.ItemId);
    }
}
