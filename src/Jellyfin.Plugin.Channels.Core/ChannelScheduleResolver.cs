using System.Runtime.CompilerServices;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Answers what a channel airs at an instant, and across a window (the guide),
/// from the day plans <see cref="DayPlanner"/> builds. One planner per built
/// channel — keyed on its daypart list, which is replaced whenever the channel
/// is rebuilt — so the plans are made once and shared by every request.
/// </summary>
public static class ChannelScheduleResolver
{
    private static readonly ConditionalWeakTable<IReadOnlyList<Daypart>, Dictionary<(string, DateTime, bool), DayPlanner>> Planners = new();

    private static DayPlanner PlannerFor(IReadOnlyList<Daypart> dayparts, TimeZoneInfo timeZone, DateTime anchorUtc, bool align)
    {
        var byKey = Planners.GetOrCreateValue(dayparts);
        lock (byKey)
        {
            var key = (timeZone.Id, anchorUtc, align);
            if (!byKey.TryGetValue(key, out var planner))
            {
                planner = new DayPlanner(dayparts, timeZone, anchorUtc, align);
                byKey[key] = planner;
            }

            return planner;
        }
    }

    /// <summary>
    /// What the channel airs at <paramref name="nowUtc"/>: a programme (joined
    /// <c>OffsetTicks</c> in), a break (<see cref="Guid.Empty"/>, pointing at
    /// what follows), or <c>null</c> when it is off air.
    /// </summary>
    /// <param name="timeZone">
    /// The channel's timezone, which decides what "06:00" means. Passed in
    /// explicitly and never taken from the host.
    /// </param>
    public static ProgramSlot? Resolve(
        IReadOnlyList<Daypart> dayparts,
        TimeZoneInfo timeZone,
        DateTime anchorUtc,
        DateTime nowUtc,
        bool alignToHalfHour = false)
    {
        ArgumentNullException.ThrowIfNull(dayparts);
        ArgumentNullException.ThrowIfNull(timeZone);

        var planner = PlannerFor(dayparts, timeZone, anchorUtc, alignToHalfHour);
        lock (planner)
        {
            var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone).Date;
            var airings = planner.Day(today.AddDays(-1)).Concat(planner.Day(today)).ToList();
            var index = airings.FindIndex(a => a.StartUtc <= nowUtc && nowUtc < a.EndUtc);
            if (index < 0)
            {
                return null;
            }

            var airing = airings[index];
            return new ProgramSlot(
                airing.ItemId,
                nowUtc.Ticks - airing.StartUtc.Ticks,
                airing.StartUtc,
                airing.EndUtc,
                NextProgramme(planner, airings, index, today));
        }
    }

    /// <summary>
    /// Every programme airing between two instants — the guide. A break is
    /// never a row: it extends the programme before it, the way a listing
    /// reads "7:00-7:30" for a 22-minute show.
    /// </summary>
    public static IReadOnlyList<ProgramSlot> ResolveRange(
        IReadOnlyList<Daypart> dayparts,
        TimeZoneInfo timeZone,
        DateTime anchorUtc,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPrograms = 200,
        bool alignToHalfHour = false)
    {
        ArgumentNullException.ThrowIfNull(dayparts);
        ArgumentNullException.ThrowIfNull(timeZone);

        var planner = PlannerFor(dayparts, timeZone, anchorUtc, alignToHalfHour);
        lock (planner)
        {
            var first = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, timeZone).Date.AddDays(-1);
            var last = TimeZoneInfo.ConvertTimeFromUtc(toUtc, timeZone).Date.AddDays(1);
            var airings = new List<DayPlanner.Airing>();
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                airings.AddRange(planner.Day(d));
            }

            var slots = new List<ProgramSlot>();
            for (var i = 0; i < airings.Count && slots.Count < maxPrograms; i++)
            {
                var a = airings[i];
                if (a.EndUtc <= fromUtc || a.StartUtc >= toUtc)
                {
                    continue;
                }

                if (a.ItemId == Guid.Empty)
                {
                    if (slots.Count > 0 && slots[^1].EndUtc == a.StartUtc)
                    {
                        slots[^1] = slots[^1] with { EndUtc = a.EndUtc };
                    }
                    else if (slots.Count == 0 && i > 0 && airings[i - 1].ItemId != Guid.Empty && airings[i - 1].EndUtc == a.StartUtc)
                    {
                        // The guide opens during a break: the programme that
                        // just finished is still "on" until its slot ends.
                        var before = airings[i - 1];
                        slots.Add(new ProgramSlot(before.ItemId, fromUtc.Ticks - before.StartUtc.Ticks, fromUtc, a.EndUtc, Guid.Empty));
                    }

                    continue;
                }

                // Clamp to the window: the entry in progress at its start is
                // listed from the start, joined that far in.
                var start = a.StartUtc < fromUtc ? fromUtc : a.StartUtc;
                slots.Add(new ProgramSlot(a.ItemId, start.Ticks - a.StartUtc.Ticks, start, a.EndUtc, Guid.Empty));
            }

            // Each entry's successor is the next entry.
            for (var i = 0; i < slots.Count; i++)
            {
                var next = i + 1 < slots.Count ? slots[i + 1].ItemId : Guid.Empty;
                slots[i] = slots[i] with { NextItemId = next };
            }

            return slots;
        }
    }

    private static Guid NextProgramme(DayPlanner planner, List<DayPlanner.Airing> airings, int index, DateTime today)
    {
        for (var i = index + 1; i < airings.Count; i++)
        {
            if (airings[i].ItemId != Guid.Empty)
            {
                return airings[i].ItemId;
            }
        }

        foreach (var a in planner.Day(today.AddDays(1)).Concat(planner.Day(today.AddDays(2))))
        {
            if (a.ItemId != Guid.Empty)
            {
                return a.ItemId;
            }
        }

        return Guid.Empty;
    }
}
