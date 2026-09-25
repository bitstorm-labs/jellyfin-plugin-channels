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

            var episodes = AiringOn(part, localNow.Date, timeZone, anchorUtc);
            for (var i = 0; i < episodes.Count; i++)
            {
                var airing = episodes[i];
                if (nowUtc < airing.StartUtc || nowUtc >= airing.EndUtc)
                {
                    continue;
                }

                // Within a marathon the next programme is the next episode;
                // after the last one it is whatever the channel airs next.
                var next = i + 1 < episodes.Count
                    ? episodes[i + 1].ItemId
                    : FirstItemAfter(dayparts, timeZone, anchorUtc, airing.EndUtc);
                return new ProgramSlot(
                    airing.ItemId,
                    nowUtc.Ticks - airing.StartUtc.Ticks,
                    airing.StartUtc,
                    airing.EndUtc,
                    next);
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

        // A daypart that runs to midnight and airs again the next day does not
        // end at midnight — its own timeline simply carries on. Cutting there
        // split every programme spanning midnight in two, and the guide listed
        // the same film twice, 23:30-24:00 and again from 00:00.
        var localBoundary = TimeZoneInfo.ConvertTimeFromUtc(boundary, timeZone);
        if (dayparts.FirstOrDefault(d => !d.EpisodePerAiring && d.Covers(localBoundary)) == active)
        {
            boundary = DateTime.MaxValue;
        }

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
    /// What a regular slot airs on a local date — one episode, or a marathon of
    /// them back to back — or nothing if it does not air that day. The Nth
    /// airing since the slot began continues the series where the previous
    /// airing stopped, looping; everything is cut at the slot's end.
    /// </summary>
    private static IReadOnlyList<Airing> AiringOn(Daypart part, DateTime localDate, TimeZoneInfo timeZone, DateTime anchorUtc)
    {
        var programs = part.Snapshot.Programs;
        if (programs.Count == 0 || !part.AirsOn(localDate))
        {
            return [];
        }

        var anchorDate = TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, timeZone).Date;
        var from = part.FirstAiring is { } first && first > anchorDate ? first : anchorDate;
        var airingIndex = AiringsBetween(part, from, localDate.Date);
        var startUtc = ToUtc(localDate.Date + part.StartLocal, timeZone);
        var slotEndUtc = ToUtc(localDate.Date + part.EndLocal, timeZone);
        var slotTicks = slotEndUtc.Ticks - startUtc.Ticks;

        var index = FirstEpisodeOf(part, airingIndex, slotTicks);
        var airings = new List<Airing>();
        var cursor = startUtc;
        while (cursor < slotEndUtc && (part.EpisodesPerAiring == 0 || airings.Count < part.EpisodesPerAiring))
        {
            var program = programs[(int)(index % programs.Count)];
            var end = cursor.AddTicks(program.RuntimeTicks);
            airings.Add(new Airing(program.ItemId, cursor, end < slotEndUtc ? end : slotEndUtc));
            cursor = end;
            index++;
        }

        return airings;
    }

    /// <summary>
    /// Where in the series the Nth airing starts. A fixed count is arithmetic;
    /// "fill the slot" depends on runtimes, so it replays the earlier airings —
    /// cheap, because it only walks runtimes, and it is what keeps next week
    /// starting after the episode that was cut rather than after a guess.
    /// </summary>
    private static long FirstEpisodeOf(Daypart part, long airingIndex, long slotTicks)
    {
        if (part.EpisodesPerAiring > 0)
        {
            return airingIndex * part.EpisodesPerAiring;
        }

        var programs = part.Snapshot.Programs;
        long index = 0;
        for (long a = 0; a < airingIndex; a++)
        {
            long used = 0;
            do
            {
                used += programs[(int)(index % programs.Count)].RuntimeTicks;
                index++;
            }
            while (used < slotTicks);
        }

        return index;
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
                var episodes = AiringOn(part, today.AddDays(day), timeZone, anchorUtc);
                if (episodes.Count > 0
                    && episodes[0].StartUtc > afterUtc
                    && (earliest is null || episodes[0].StartUtc < earliest))
                {
                    earliest = episodes[0].StartUtc;
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

            // A break belongs to the programme before it, the way a listing
            // reads "7:00-7:30" for a 22-minute show. It is never a row of its
            // own; one the guide opens inside is simply skipped.
            if (entry.ItemId == Guid.Empty)
            {
                if (slots.Count > 0 && slots[^1].EndUtc == entry.StartUtc)
                {
                    slots[^1] = slots[^1] with { EndUtc = entry.EndUtc, NextItemId = entry.NextItemId };
                }
                else if (slots.Count == 0
                         && Resolve(dayparts, timeZone, anchorUtc, slot.Value.StartUtc.AddTicks(-1)) is { } before
                         && before.ItemId != Guid.Empty)
                {
                    // The guide opens during a break: the programme that just
                    // finished is still "on" until its slot ends, as a listing
                    // shows it, rather than leaving nothing at now.
                    slots.Add(before with
                    {
                        StartUtc = cursor,
                        OffsetTicks = cursor.Ticks - before.StartUtc.Ticks,
                        EndUtc = entry.EndUtc,
                        NextItemId = entry.NextItemId
                    });
                }

                cursor = entry.EndUtc > cursor ? entry.EndUtc : cursor.AddMinutes(1);
                continue;
            }

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
        if (next is { } slot && slot.ItemId == Guid.Empty)
        {
            return slot.NextItemId;
        }

        return next?.ItemId ?? Guid.Empty;
    }
}
