namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Seeded shuffle that is stable across processes, machines and runtime versions.
///
/// <see cref="Random"/> is explicitly not suitable here: the sequence it produces
/// for a given seed is not contractually stable between .NET versions, so a
/// framework upgrade would silently reorder every existing channel and move
/// programs the schedule had already aired. A channel's running order has to
/// survive restarts and upgrades untouched, so the generator is spelled out.
/// </summary>
public static class DeterministicShuffle
{
    /// <summary>
    /// Return <paramref name="items"/> reordered by <paramref name="seed"/>.
    /// The same seed and input always produce the same output, and the result is
    /// always a permutation — nothing is dropped or duplicated.
    /// </summary>
    public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> items, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(items);

        var result = items.ToArray();
        var state = seed;

        // Fisher-Yates, drawing from splitmix64. Picked because it is a few lines,
        // has no state beyond a ulong, and is defined by its constants rather than
        // by an implementation we do not control.
        for (var i = result.Length - 1; i > 0; i--)
        {
            var j = (int)(NextUInt64(ref state) % (ulong)(i + 1));
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
    }

    private static ulong NextUInt64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
