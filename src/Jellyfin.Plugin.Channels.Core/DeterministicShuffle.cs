using System.Buffers.Binary;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Seeded shuffle that is stable across processes, machines and runtime versions,
/// and — just as importantly — stable when the library underneath it grows.
///
/// <see cref="Random"/> is explicitly not suitable here: the sequence it produces
/// for a given seed is not contractually stable between .NET versions, so a
/// framework upgrade would silently reorder every existing channel and move
/// programs the schedule had already aired. A channel's running order has to
/// survive restarts and upgrades untouched, so the generator is spelled out.
///
/// The order is decided per item rather than by position. A positional shuffle —
/// Fisher-Yates walking the list — draws its swaps from the list's *length*, so
/// appending a single new episode changes every draw and reshuffles the whole
/// channel. On a server where a downloader adds episodes continuously that is
/// not an edge case but the normal path, and it defeats the reason the seed is
/// persisted at all. Sorting by a hash of (seed, item id) gives the same
/// permutation for the same input, and inserts a new item into its own place
/// while leaving every other item where it was.
/// </summary>
public static class DeterministicShuffle
{
    /// <summary>
    /// Return <paramref name="items"/> reordered by <paramref name="seed"/>,
    /// with each item's place decided by its own identity via
    /// <paramref name="keySelector"/>. The same seed and input always produce the
    /// same output; the result is always a permutation — nothing is dropped or
    /// duplicated — and adding an item does not move the others.
    /// </summary>
    public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> items, ulong seed, Func<T, Guid> keySelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);

        var keyed = new List<(T Item, ulong Key, Guid Id)>(items.Count);
        foreach (var item in items)
        {
            var id = keySelector(item);
            keyed.Add((item, SortKey(seed, id), id));
        }

        // The id breaks ties so the order is total: two items colliding on a
        // 64-bit key must still land in a defined order, or the permutation
        // would depend on the sort's internals.
        keyed.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Id.CompareTo(b.Id));

        var result = new T[keyed.Count];
        for (var i = 0; i < keyed.Count; i++)
        {
            result[i] = keyed[i].Item;
        }

        return result;
    }

    /// <summary>
    /// Where one item sorts for a given seed. Both halves of the id are mixed in
    /// so that ids differing only in their high bits — which is how sequentially
    /// allocated ids often look — do not land next to each other.
    /// </summary>
    private static ulong SortKey(ulong seed, Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);

        var low = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var high = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);

        return Mix(seed ^ Mix(low) ^ Mix(high + 0x9E3779B97F4A7C15UL));
    }

    /// <summary>The splitmix64 finalizer, defined by its constants.</summary>
    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
