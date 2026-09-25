namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Plans a channel one local day at a time, the way a broadcaster builds a
/// schedule: every programme starts from its beginning and plays to its end.
///
/// A continuous timeline — each daypart looping over its running order on the
/// wall clock — cannot promise that. A daypart that opens at 18:00 joins
/// whatever its timeline has reached, often an hour into a film, and one that
/// closes at midnight cuts whatever is running. So each day is laid out here:
///
/// - Each block (a run of one daypart between boundaries) opens with a
///   programme at its start time. A daypart keeps its place in its running
///   order from one block to the next, so it never repeats or skips.
/// - A programme is started only if it ends before the block does. Otherwise
///   the next few in order are tried for one that fits; failing that, the rest
///   of the block is a break (clients show "up next").
/// - With half-hour alignment, a programme's slot is rounded up to the half
///   hour: short programmes that fit are packed in (never from the show just
///   aired) and what remains is a break.
/// - A regular slot ("Survivor, Wednesdays at 7") airs its episodes — each
///   starting on the half hour when aligned — and a break fills the rest of the
///   slot, rather than dropping into the channel mid-programme.
/// - A daypart that runs to midnight and resumes at midnight is one timeline:
///   its last programme may run past midnight rather than being held back,
///   which is what an all-day channel expects.
///
/// Day N depends on day N-1 (where each daypart had reached), so plans are
/// built forward from the anchor date and memoised; a guide or tune-in costs a
/// dictionary lookup once the days around it exist. Instances are not
/// thread-safe on their own; <see cref="ChannelScheduleResolver"/> locks.
/// </summary>
public sealed class DayPlanner
{
    private const int Lookahead = 8;
    private static readonly long HalfHour = TimeSpan.FromMinutes(30).Ticks;

    private readonly IReadOnlyList<Daypart> _dayparts;
    private readonly TimeZoneInfo _timeZone;
    private readonly DateTime _anchorDate;
    private readonly DateTime _anchorUtc;
    private readonly bool _align;
    private readonly Dictionary<DateTime, DayPlan> _plans = new();
    private DateTime _lastPlanned;

    public DayPlanner(IReadOnlyList<Daypart> dayparts, TimeZoneInfo timeZone, DateTime anchorUtc, bool alignToHalfHour)
    {
        _dayparts = dayparts;
        _timeZone = timeZone;
        _anchorDate = TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, timeZone).Date;
        _anchorUtc = anchorUtc;
        _align = alignToHalfHour;
        _lastPlanned = _anchorDate.AddDays(-1);
    }

    /// <summary>One scheduled airing: a programme, or a break (<see cref="Guid.Empty"/>).</summary>
    public readonly record struct Airing(Guid ItemId, DateTime StartUtc, DateTime EndUtc);

    private sealed class DayPlan
    {
        public List<Airing> Airings { get; } = [];
        public Dictionary<Daypart, Cursor> Cursors { get; init; } = [];
        public DateTime CarryUntil { get; set; }
    }

    /// <summary>Where a daypart is in its running order: skipped items first, then onward.</summary>
    private sealed class Cursor
    {
        public List<long> Deferred { get; init; } = [];
        public long Next { get; set; }

        public Cursor Copy() => new() { Deferred = [.. Deferred], Next = Next };
    }

    /// <summary>The airings of one local date, in order (a programme carried in from the day before is not repeated).</summary>
    public IReadOnlyList<Airing> Day(DateTime localDate) => Plan(localDate.Date).Airings;

    private DayPlan Plan(DateTime date)
    {
        if (date < _anchorDate)
        {
            // Before the channel began nothing aired.
            return new DayPlan();
        }

        while (_lastPlanned < date)
        {
            var next = _lastPlanned.AddDays(1);
            var previous = _plans.GetValueOrDefault(_lastPlanned);
            var cursors = previous?.Cursors.ToDictionary(kv => kv.Key, kv => kv.Value.Copy()) ?? [];
            _plans[next] = Build(next, cursors, previous?.CarryUntil ?? DateTime.MinValue);
            _lastPlanned = next;
        }

        return _plans[date];
    }

    private DayPlan Build(DateTime date, Dictionary<Daypart, Cursor> cursors, DateTime carryUntil)
    {
        var plan = new DayPlan { Cursors = cursors };
        var dayStart = ToUtc(date);
        var dayEnd = ToUtc(date.AddDays(1));
        var t = carryUntil > dayStart ? carryUntil : dayStart;
        if (date == _anchorDate && _anchorUtc > t)
        {
            // The channel's first day begins when the channel does.
            t = _anchorUtc;
        }

        var slots = _dayparts
            .Where(p => p.EpisodePerAiring && p.AirsOn(date))
            .Select(p => (Part: p, Start: ToUtc(date + p.StartLocal), End: ToUtc(date + p.EndLocal)))
            .OrderBy(s => s.Start)
            .ToList();

        var guard = 0;
        while (t < dayEnd && guard++ < 10_000)
        {
            var slot = slots.FirstOrDefault(s => s.Start <= t && t < s.End);
            if (slot.Part is not null)
            {
                // Once its episodes have aired the slot is done; the channel's
                // own schedule takes the rest of it, starting fresh.
                t = PlanRegularSlot(plan, slot.Part, date, slot.Start > t ? slot.Start : t, slot.End);
                slots.Remove(slot);
                continue;
            }

            var local = ToLocal(t);
            var owner = _dayparts.FirstOrDefault(d => !d.EpisodePerAiring && d.Covers(local));
            var nextSlotStart = slots.Where(s => s.Start > t).Select(s => (DateTime?)s.Start).FirstOrDefault();
            if (owner is null)
            {
                // Off air: step to whatever opens next today.
                t = NextOpening(date, t) is { } opens && opens < dayEnd ? opens : dayEnd;
                if (nextSlotStart is { } ns && ns < t)
                {
                    t = ns;
                }

                continue;
            }

            var ownerEnd = ToUtc(date + owner.EndLocal);
            var blockEnd = ownerEnd;
            if (nextSlotStart is { } slotStart && slotStart < blockEnd)
            {
                blockEnd = slotStart;
            }

            // The same daypart carrying on past midnight is one timeline.
            var runsOn = blockEnd == dayEnd
                && _dayparts.FirstOrDefault(d => !d.EpisodePerAiring && d.Covers(ToLocal(dayEnd))) == owner
                && !_dayparts.Any(d => d.EpisodePerAiring && d.AirsOn(date.AddDays(1)) && d.StartLocal == TimeSpan.Zero);

            t = PlanBlock(plan, owner, t, blockEnd, runsOn);
            if (t > dayEnd)
            {
                plan.CarryUntil = t;
            }
        }

        return plan;
    }

    /// <summary>Lay one daypart's programmes into [start, end). Returns where it stopped.</summary>
    private DateTime PlanBlock(DayPlan plan, Daypart part, DateTime start, DateTime end, bool mayRunOn)
    {
        var programs = part.Snapshot.Programs;
        if (programs.Count == 0)
        {
            return end;
        }

        if (!plan.Cursors.TryGetValue(part, out var cursor))
        {
            cursor = new Cursor();
            plan.Cursors[part] = cursor;
        }

        var t = start;
        string? last = null;
        while (t < end)
        {
            // A block that runs on past midnight takes the next programme as it
            // comes; any other only starts one that ends in time.
            var room = end.Ticks - t.Ticks;
            Func<ChannelProgram, bool> fits = mayRunOn ? _ => true : p => p.RuntimeTicks <= room;
            var pick = Take(cursor, programs, fits, last, avoidSame: true) ?? Take(cursor, programs, fits, last);
            if (pick is null)
            {
                Add(plan, Guid.Empty, t, end);
                return end;
            }

            var program = pick.Value;
            var programEnd = t.AddTicks(program.RuntimeTicks);
            Add(plan, program.ItemId, t, programEnd);
            last = program.Group;
            // The slot ends on the next half hour of the clock — not half hours
            // counted from this programme's start, or one late start would
            // leave every programme after it off the half hour for good.
            var slotEnd = _align ? ToHalfHour(programEnd) : programEnd;
            t = programEnd;

            // Pack short programmes into the rest of the slot.
            while (_align && t < slotEnd && t < end)
            {
                var gap = Math.Min(slotEnd.Ticks, end.Ticks) - t.Ticks;
                var filler = Take(cursor, programs, p => p.RuntimeTicks <= gap, last, avoidSame: true);
                if (filler is null)
                {
                    break;
                }

                Add(plan, filler.Value.ItemId, t, t.AddTicks(filler.Value.RuntimeTicks));
                last = filler.Value.Group;
                t = t.AddTicks(filler.Value.RuntimeTicks);
            }

            if (programEnd > end)
            {
                // Ran on past midnight: its break still takes it to the half
                // hour, where tomorrow's schedule picks up.
                if (_align && t < slotEnd)
                {
                    Add(plan, Guid.Empty, t, slotEnd);
                    t = slotEnd;
                }

                return t;
            }

            var breakEnd = slotEnd < end ? slotEnd : end;
            if (_align && t < breakEnd)
            {
                Add(plan, Guid.Empty, t, breakEnd);
                t = breakEnd;
            }
        }

        return t;
    }

    /// <summary>
    /// A regular slot: its episodes from the slot's start, each on the half hour
    /// when aligned, and only whole ones — except the first, which always airs
    /// (cut at the end if longer than the slot), or a slot shorter than its show
    /// would be empty forever. Returns where the episodes finished; the channel's
    /// own schedule fills the rest, starting a programme only if it fits.
    /// </summary>
    private DateTime PlanRegularSlot(DayPlan plan, Daypart part, DateTime date, DateTime start, DateTime end)
    {
        var programs = part.Snapshot.Programs;
        if (programs.Count == 0)
        {
            return end;
        }

        var slotTicks = end.Ticks - ToUtc(date + part.StartLocal).Ticks;
        var index = FirstEpisodeOf(part, AiringsBefore(part, date), slotTicks);
        var t = start;
        var aired = 0;
        while (t < end && (part.EpisodesPerAiring == 0 || aired < part.EpisodesPerAiring))
        {
            var program = programs[(int)(index % programs.Count)];
            var programEnd = t.AddTicks(program.RuntimeTicks);
            if (programEnd > end && aired > 0)
            {
                break;
            }

            Add(plan, program.ItemId, t, programEnd < end ? programEnd : end);
            aired++;
            index++;
            t = programEnd < end ? programEnd : end;
            if (_align)
            {
                var next = t.AddTicks(RoundUpFrom(start, t));
                t = next < end ? next : end;
                if (plan.Airings[^1].EndUtc < t)
                {
                    Add(plan, Guid.Empty, plan.Airings[^1].EndUtc, t);
                }
            }
        }

        return t;
    }

    /// <summary>How many episodes the slot's earlier airings used, by replaying them with the same rules.</summary>
    private long FirstEpisodeOf(Daypart part, long airingIndex, long slotTicks)
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
            var aired = 0;
            while (true)
            {
                var runtime = programs[(int)(index % programs.Count)].RuntimeTicks;
                if (aired > 0 && used + runtime > slotTicks)
                {
                    break;
                }

                used += _align ? RoundUp(runtime) : runtime;
                index++;
                aired++;
                if (used >= slotTicks)
                {
                    break;
                }
            }
        }

        return index;
    }

    private long AiringsBefore(Daypart part, DateTime date)
    {
        var from = part.FirstAiring is { } first && first > _anchorDate ? first : _anchorDate;
        long count = 0;
        for (var d = from; d < date; d = d.AddDays(1))
        {
            if (part.AirsOn(d))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The first of the daypart's next few programmes (skipped ones first) that
    /// passes <paramref name="fits"/>. Anything passed over is kept, to air
    /// first next time, so nothing is lost by picking out of order.
    /// </summary>
    private static ChannelProgram? Take(
        Cursor cursor, IReadOnlyList<ChannelProgram> programs, Func<ChannelProgram, bool> fits, string? last, bool avoidSame = false)
    {
        bool Ok(ChannelProgram p) => fits(p) && !(avoidSame && last is not null && p.Group == last);

        for (var i = 0; i < cursor.Deferred.Count; i++)
        {
            var p = programs[(int)(cursor.Deferred[i] % programs.Count)];
            if (Ok(p))
            {
                cursor.Deferred.RemoveAt(i);
                return p;
            }
        }

        // Never wrap round to a programme already set aside: with few programmes
        // that picked the same film again and again while another waited.
        var reach = Math.Min(Lookahead, programs.Count - cursor.Deferred.Count);
        for (var k = 0; k < reach; k++)
        {
            var p = programs[(int)((cursor.Next + k) % programs.Count)];
            if (!Ok(p))
            {
                continue;
            }

            for (var skipped = 0; skipped < k; skipped++)
            {
                cursor.Deferred.Add(cursor.Next + skipped);
            }

            cursor.Next += k + 1;
            return p;
        }

        return null;
    }

    private DateTime? NextOpening(DateTime date, DateTime afterUtc)
    {
        DateTime? earliest = null;
        foreach (var part in _dayparts)
        {
            if (!part.AirsOn(date))
            {
                continue;
            }

            var opens = ToUtc(date + part.StartLocal);
            if (opens > afterUtc && (earliest is null || opens < earliest))
            {
                earliest = opens;
            }
        }

        return earliest;
    }

    private static void Add(DayPlan plan, Guid item, DateTime start, DateTime end)
    {
        if (end > start)
        {
            plan.Airings.Add(new Airing(item, start, end));
        }
    }

    private static long RoundUp(long ticks) => (ticks + HalfHour - 1) / HalfHour * HalfHour;

    /// <summary>The next half hour of the clock at or after an instant (UTC half hours are local ones wherever the offset is whole half hours).</summary>
    private static DateTime ToHalfHour(DateTime utc) => new(RoundUp(utc.Ticks), DateTimeKind.Utc);

    /// <summary>Ticks from <paramref name="t"/> to the next half hour counted from <paramref name="origin"/>.</summary>
    private static long RoundUpFrom(DateTime origin, DateTime t)
    {
        var elapsed = t.Ticks - origin.Ticks;
        return RoundUp(elapsed) - elapsed;
    }

    private DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _timeZone);

    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, _timeZone);
}
