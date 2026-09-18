namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Turns the items a daypart's blocks resolved to into a running order.
///
/// Block resolution is the server's job; everything after it — de-duplication,
/// ordering, dropping the unplayable — is ordinary rules that belong somewhere
/// they can be tested without a library behind them.
/// </summary>
public static class SnapshotBuilder
{
    public static ChannelSnapshot Build(
        IReadOnlyList<IChannelItem> items,
        ChannelOrdering ordering,
        ulong seed,
        int generation,
        DateTime builtAtUtc)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Blocks overlap constantly — a series block and a library block will
        // both claim the same episodes — and an item appearing twice would air
        // twice per cycle for no reason the user asked for. First occurrence
        // wins so the result does not depend on hash ordering.
        var unique = new List<IChannelItem>();
        var seen = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (item.RuntimeTicks > 0 && seen.Add(item.Id))
            {
                unique.Add(item);
            }
        }

        var ordered = ordering == ChannelOrdering.SeededShuffle
            ? DeterministicShuffle.Shuffle(unique, seed)
            : SortSequentially(unique);

        var programs = ordered.Select(i => new ChannelProgram(i.Id, i.RuntimeTicks)).ToArray();
        return new ChannelSnapshot(programs, generation, builtAtUtc);
    }

    /// <summary>
    /// Series order: grouped by show, then season, then episode, with movies
    /// falling back to their sort name. Grouping by show first matters — a flat
    /// sort by season number would interleave episode 1 of every series, which
    /// is not what "in order" means to anyone.
    /// </summary>
    private static IReadOnlyList<IChannelItem> SortSequentially(IReadOnlyList<IChannelItem> items) =>
        items
            .OrderBy(i => i.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.SeasonNumber ?? int.MaxValue)
            .ThenBy(i => i.EpisodeNumber ?? int.MaxValue)
            .ThenBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
