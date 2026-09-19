namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Resolves a channel that may be split into dayparts.
///
/// Dayparting only decides *which* timeline you are watching at this time of
/// day; each daypart runs its own continuous schedule, so the per-daypart
/// arithmetic is the already-tested <see cref="ScheduleResolver"/> untouched.
/// There is deliberately no notion of a daypart's clock pausing while it is off
/// air: the feature's whole premise is that a timeline runs whether or not
/// anyone is watching, and a daypart is just a channel that is only visible for
/// part of the day.
/// </summary>
public static class ChannelScheduleResolver
{
    /// <summary>
    /// Resolve what the channel is airing at <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="timeZone">
    /// The channel's timezone, which decides what "06:00" means. Passed in
    /// explicitly and never taken from the host: a server's own zone is easy to
    /// get wrong — and has been — and a schedule that silently shifts by an hour
    /// is worse than one that refuses to guess.
    /// </param>
    /// <returns>
    /// The airing program, or <c>null</c> when no daypart covers this instant
    /// (the channel is off air) or the covering daypart has nothing playable.
    /// </returns>
    public static ProgramSlot? Resolve(
        IReadOnlyList<Daypart> dayparts,
        TimeZoneInfo timeZone,
        DateTime anchorUtc,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(dayparts);
        ArgumentNullException.ThrowIfNull(timeZone);

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);

        var active = dayparts.FirstOrDefault(d => d.Covers(localNow));
        if (active is null)
        {
            return null;
        }

        var slot = ScheduleResolver.Resolve(active.Snapshot, anchorUtc, nowUtc);
        if (slot is null)
        {
            return null;
        }

        // A program that would run past the end of its daypart is cut off there,
        // the way a broadcast schedule cuts away regardless of what is on. The
        // alternatives are worse: overrunning makes dayparts drift out of their
        // slots over time, and refusing to start anything that will not fit
        // leaves dead air with nothing to fill it.
        var daypartEndUtc = DaypartEndUtc(active, localNow, timeZone);

        // Strictly inside the daypart: nothing to adjust.
        if (slot.Value.EndUtc < daypartEndUtc)
        {
            return slot;
        }

        // Ending exactly ON the boundary still hands over — at that instant the
        // next daypart is on air, so what follows is its opening program, not
        // this daypart's next one. Treating only the overrun case as a handover
        // silently reports the wrong next program every time a runtime happens
        // to divide the daypart evenly, which is far from rare.
        return slot.Value with
        {
            EndUtc = slot.Value.EndUtc < daypartEndUtc ? slot.Value.EndUtc : daypartEndUtc,
            NextItemId = FirstItemAfter(dayparts, timeZone, anchorUtc, daypartEndUtc)
        };
    }

    /// <summary>
    /// Every programme airing between two instants — the guide.
    ///
    /// Built by walking the same single-instant resolver rather than by a
    /// second scheduling implementation: a guide that could disagree with what
    /// actually plays would be worse than no guide at all.
    /// </summary>
    /// <param name="maxPrograms">
    /// A stop so a pathological schedule cannot spin. A three-hour window of
    /// very short items is the realistic worst case, not thousands of entries.
    /// </param>
    public static IReadOnlyList<ProgramSlot> ResolveRange(
        IReadOnlyList<Daypart> dayparts,
        TimeZoneInfo timeZone,
        DateTime anchorUtc,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPrograms = 200)
    {
        ArgumentNullException.ThrowIfNull(dayparts);
        ArgumentNullException.ThrowIfNull(timeZone);

        var slots = new List<ProgramSlot>();
        var cursor = fromUtc;

        while (cursor < toUtc && slots.Count < maxPrograms)
        {
            var slot = Resolve(dayparts, timeZone, anchorUtc, cursor);
            if (slot is null)
            {
                // Off air. Step forward rather than stopping, because a gap in
                // the broadcast day is followed by more schedule, and a guide
                // that gave up at the first gap would show nothing after it.
                cursor = cursor.AddMinutes(1);
                continue;
            }

            // Clamp the start to the cursor. A daypart runs its own continuous
            // timeline, so the first programme after a daypart opens is joined
            // in progress and reports a notional start from *before* the
            // daypart existed. That is right for tuning in and wrong for a
            // guide, where it draws a block reaching backwards over the one
            // before it and makes time appear to run backwards.
            var entry = slot.Value.StartUtc < cursor
                ? slot.Value with { StartUtc = cursor }
                : slot.Value;
            slots.Add(entry);

            // A programme that does not end after the cursor would walk the
            // loop forever; nothing should produce that, which is exactly why
            // it is worth refusing to trust.
            cursor = entry.EndUtc > cursor ? entry.EndUtc : cursor.AddMinutes(1);
        }

        return slots;
    }

    /// <summary>The UTC instant the given daypart stops airing today.</summary>
    private static DateTime DaypartEndUtc(Daypart daypart, DateTime localNow, TimeZoneInfo timeZone)
    {
        var localEnd = localNow.Date + daypart.EndLocal;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), timeZone);
    }

    /// <summary>
    /// What airs immediately after a daypart boundary, so a client can prepare
    /// the cut rather than discovering it late and showing dead air.
    /// </summary>
    private static Guid FirstItemAfter(
        IReadOnlyList<Daypart> dayparts,
        TimeZoneInfo timeZone,
        DateTime anchorUtc,
        DateTime boundaryUtc)
    {
        // Resolve just past the boundary so the lookup lands in the next daypart
        // rather than on the closing edge of the one ending.
        var next = Resolve(dayparts, timeZone, anchorUtc, boundaryUtc.AddTicks(1));
        return next?.ItemId ?? Guid.Empty;
    }
}
