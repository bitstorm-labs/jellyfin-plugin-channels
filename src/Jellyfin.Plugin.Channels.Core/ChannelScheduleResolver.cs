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

        // A regular slot wins while its episode is on. Once the episode ends
        // the slot falls through, and the channel's continuous schedule fills
        // the rest of the hour — the way a network fills after a short show.
        foreach (var part in dayparts)
        {
            if (!part.EpisodePerAiring || !part.Covers(localNow))
            {
                continue;
            }

            if (AiringOn(part, localNow.Date, timeZone, anchorUtc) is { } airing
                && nowUtc >= airing.StartUtc && nowUtc < airing.EndUtc)
            {
                return new ProgramSlot(
                    airing.ItemId,
                    nowUtc.Ticks - airing.StartUtc.Ticks,
                    airing.StartUtc,
                    airing.EndUtc,
                    FirstItemAfter(dayparts, timeZone, anchorUtc, airing.EndUtc));
            }
        }

        var active = dayparts.FirstOrDefault(d => !d.EpisodePerAiring && d.Covers(localNow));
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
        // leaves dead air with nothing to fill it. A regular slot starting
        // before then is a boundary too: the show goes on at 7, not whenever
        // the film before it happens to finish.
        var boundary = DaypartEndUtc(active, localNow, timeZone);
        if (NextAiringStart(dayparts, timeZone, anchorUtc, nowUtc) is { } slotStart && slotStart < boundary)
        {
            boundary = slotStart;
        }

        // Strictly inside the boundary: nothing to adjust.
        if (slot.Value.EndUtc < boundary)
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
            EndUtc = boundary,
            NextItemId = FirstItemAfter(dayparts, timeZone, anchorUtc, boundary)
        };
    }

    private readonly record struct Airing(Guid ItemId, DateTime StartUtc, DateTime EndUtc);

    /// <summary>
    /// The episode a regular slot airs on a local date, or null if it does not
    /// air that day. The Nth airing since the anchor plays the Nth programme,
    /// looping; it starts on the slot and is cut at the slot's end if longer.
    /// </summary>
    private static Airing? AiringOn(Daypart part, DateTime localDate, TimeZoneInfo timeZone, DateTime anchorUtc)
    {
        var programs = part.Snapshot.Programs;
        if (programs.Count == 0 || !part.AirsOn(localDate))
        {
            return null;
        }

        var anchorDate = TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, timeZone).Date;
        var index = AiringsBetween(part, anchorDate, localDate.Date);
        var program = programs[(int)(index % programs.Count)];

        var startUtc = ToUtc(localDate.Date + part.StartLocal, timeZone);
        var slotEndUtc = ToUtc(localDate.Date + part.EndLocal, timeZone);
        var runEndUtc = startUtc.AddTicks(program.RuntimeTicks);
        return new Airing(program.ItemId, startUtc, runEndUtc < slotEndUtc ? runEndUtc : slotEndUtc);
    }

    /// <summary>How many times the slot airs on dates in [from, to).</summary>
    private static long AiringsBetween(Daypart part, DateTime from, DateTime to)
    {
        if (to <= from)
        {
            return 0;
        }

        // Whole weeks by arithmetic, so the cost does not grow with the
        // channel's age. A season breaks the weekly pattern; walk those.
        if (part.Season is null)
        {
            var days = (int)(to - from).TotalDays;
            var perWeek = Enumerable.Range(0, 7).Count(d => part.Airs((DayOfWeek)d));
            long count = (long)(days / 7) * perWeek;
            for (var d = from.AddDays(days / 7 * 7); d < to; d = d.AddDays(1))
            {
                if (part.Airs(d.DayOfWeek))
                {
                    count++;
                }
            }

            return count;
        }

        long seasonal = 0;
        for (var d = from; d < to; d = d.AddDays(1))
        {
            if (part.AirsOn(d))
            {
                seasonal++;
            }
        }

        return seasonal;
    }

    /// <summary>The next regular-slot start strictly after an instant, if any in the next two days.</summary>
    private static DateTime? NextAiringStart(
        IReadOnlyList<Daypart> dayparts, TimeZoneInfo timeZone, DateTime anchorUtc, DateTime afterUtc)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, timeZone).Date;
        DateTime? earliest = null;
        foreach (var part in dayparts.Where(d => d.EpisodePerAiring))
        {
            for (var day = 0; day <= 1; day++)
            {
                if (AiringOn(part, today.AddDays(day), timeZone, anchorUtc) is { } airing
                    && airing.StartUtc > afterUtc
                    && (earliest is null || airing.StartUtc < earliest))
                {
                    earliest = airing.StartUtc;
                }
            }
        }

        return earliest;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone);

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
