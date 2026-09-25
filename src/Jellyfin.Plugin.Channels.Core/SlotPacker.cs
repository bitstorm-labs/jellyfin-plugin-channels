namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Lays a running order into slots so every slot starts on the half hour.
///
/// A slot opens with the next programme and is as many half hours as it needs.
/// Short programmes that still fit are pulled in after it — the next few in the
/// order, so the order barely moves — and whatever time is left is a break,
/// which clients fill with an "up next" card. A 22-minute sitcom is a 30-minute
/// slot with an 8-minute break; three short videos share one slot; a 118-minute
/// film is a two-hour slot. Because every slot is a whole number of half hours,
/// a timeline anchored on the half hour starts every slot on :00 or :30.
/// </summary>
public static class SlotPacker
{
    public static readonly long HalfHourTicks = TimeSpan.FromMinutes(30).Ticks;

    /// <summary>How far ahead to look for a short programme that fits the rest of a slot.</summary>
    private const int Lookahead = 8;

    /// <summary>A break is a program with no item: the gap before the next slot.</summary>
    public static ChannelProgram Break(long ticks) => new(Guid.Empty, ticks);

    public static bool IsBreak(ChannelProgram program) => program.ItemId == Guid.Empty;

    /// <param name="groupOf">
    /// The show a programme belongs to. Packing pulls programmes forward, and
    /// without this it happily pulled the next video from the channel that had
    /// just played — 123 repeats on a channel of YouTube videos. With it, a
    /// slot is only ever opened or topped up by a different show than the one
    /// before, whenever one is to hand.
    /// </param>
    public static IReadOnlyList<ChannelProgram> Pack(
        IReadOnlyList<ChannelProgram> order, long slotTicks, Func<Guid, string>? groupOf = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        var taken = new bool[order.Count];
        var packed = new List<ChannelProgram>(order.Count * 2);
        string? last = null;
        bool Same(int index) => groupOf is not null && last is not null
            && string.Equals(groupOf(order[index].ItemId), last, StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < order.Count; i++)
        {
            if (taken[i])
            {
                continue;
            }

            // Open the slot with the first free programme not from the show
            // that just played, looking a little way ahead; else take this one.
            var open = i;
            for (int j = i, scanned = 0; Same(open) && j < order.Count && scanned < Lookahead; j++)
            {
                if (taken[j])
                {
                    continue;
                }

                scanned++;
                if (!Same(j))
                {
                    open = j;
                }
            }

            taken[open] = true;
            packed.Add(order[open]);
            last = groupOf?.Invoke(order[open].ItemId);
            var used = order[open].RuntimeTicks;
            var end = (used + slotTicks - 1) / slotTicks * slotTicks;

            var looked = 0;
            for (var j = i + 1; j < order.Count && looked < Lookahead && used < end; j++)
            {
                if (taken[j])
                {
                    continue;
                }

                looked++;
                if (used + order[j].RuntimeTicks <= end && !Same(j))
                {
                    taken[j] = true;
                    packed.Add(order[j]);
                    last = groupOf?.Invoke(order[j].ItemId);
                    used += order[j].RuntimeTicks;
                }
            }

            // The slot was opened ahead of i: i is still free, and the loop
            // must come back to it rather than step past it.
            if (open != i)
            {
                i--;
            }

            if (end > used)
            {
                packed.Add(Break(end - used));
            }
        }

        return packed;
    }
}
