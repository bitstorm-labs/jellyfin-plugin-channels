namespace Jellyfin.Plugin.Channels;

/// <summary>
/// A channel's running order, frozen at a point in time.
///
/// The schedule is never materialised as a list of airings — that would mean
/// writing (and invalidating) a row per program per day, forever. Instead this
/// ordered list plus an anchor is enough for <see cref="DayPlanner"/> to
/// compute what is airing at any instant, so a channel costs one small record
/// regardless of how far ahead or behind it is queried.
///
/// Snapshots are versioned because the library underneath them moves: media gets
/// added and (with a cleanup tool running) deleted. Rebuilding produces a new
/// <see cref="Generation"/> so a client can tell that the running order it was
/// handed is no longer the current one.
/// </summary>
public sealed class ChannelSnapshot
{
    public ChannelSnapshot(IReadOnlyList<ChannelProgram> programs, int generation, DateTime builtAtUtc)
    {
        ArgumentNullException.ThrowIfNull(programs);

        // A zero- or negative-runtime item would occupy no time on the timeline.
        // Left in, it either never airs or divides the cycle by zero, so it is
        // dropped once here instead of being special-cased at every read.
        Programs = programs.Where(p => p.RuntimeTicks > 0).ToArray();
        Generation = generation;
        BuiltAtUtc = builtAtUtc;
        TotalRuntimeTicks = Programs.Sum(p => p.RuntimeTicks);
    }

    public IReadOnlyList<ChannelProgram> Programs { get; }

    public int Generation { get; }

    public DateTime BuiltAtUtc { get; }

    /// <summary>Length of one full cycle of the channel.</summary>
    public long TotalRuntimeTicks { get; }

    /// <summary>A channel with nothing playable cannot be tuned to.</summary>
    public bool IsEmpty => Programs.Count == 0;
}
