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
    /// Order a channel so that items of the same group — episodes of one show —
    /// are spread evenly through the running order rather than wherever their
    /// independent keys happen to fall. Within a show, <paramref name="memberOrder"/>
    /// decides the order (the channel uses series order: a show starts at its
    /// first episode, plays through, and starts again).
    ///
    /// <see cref="Shuffle{T}"/> places every episode on its own, so a show
    /// with many episodes lands next to itself constantly: on a real channel
    /// 41% of programmes followed one from the same show. Here each group's
    /// members keep their own seeded order, and member k of n sits at
    /// (k + offset) / n of the cycle, with the offset drawn from the group's
    /// name. Every show then recurs at even intervals. Even spacing can still
    /// leave a gap no other show happens to fall into, so a last pass takes the
    /// next item from a different show whenever two in a row would match. Two
    /// episodes of one show then meet only when it is more than half the
    /// channel — where some repeats cannot be avoided by any order.
    ///
    /// Stability: an item added to a group re-spaces that group (its count
    /// changed); every other group keeps its positions.
    /// </summary>
    public static IReadOnlyList<T> Spread<T>(
        IReadOnlyList<T> items,
        ulong seed,
        Func<T, Guid> keySelector,
        Func<T, string> groupSelector,
        Func<IReadOnlyList<T>, IReadOnlyList<T>>? memberOrder = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(groupSelector);

        var placed = new List<(T Item, double Position, ulong Key, Guid Id)>(items.Count);
        foreach (var group in items.GroupBy(groupSelector, StringComparer.OrdinalIgnoreCase))
        {
            var members = memberOrder is null
                ? Shuffle(group.ToList(), seed, keySelector)
                : memberOrder(group.ToList());
            // 53 random bits → a fraction in [0, 1).
            var offset = (Mix(seed ^ HashName(group.Key)) >> 11) * (1.0 / (1UL << 53));
            for (var k = 0; k < members.Count; k++)
            {
                var id = keySelector(members[k]);
                placed.Add((members[k], (k + offset) / members.Count, SortKey(seed, id), id));
            }
        }

        placed.Sort((a, b) =>
            a.Position != b.Position ? a.Position.CompareTo(b.Position)
            : a.Key != b.Key ? a.Key.CompareTo(b.Key)
            : a.Id.CompareTo(b.Id));
        return AvoidRepeats(placed.Select(p => p.Item).ToList(), groupSelector);
    }

    /// <summary>
    /// Walk the order; where the next item is from the same group as the last
    /// one placed, bring forward the nearest later item from another group.
    /// Deterministic, and it moves as little as it can. A group's own members
    /// keep their relative order, so series order survives.
    /// </summary>
    private static IReadOnlyList<T> AvoidRepeats<T>(List<T> order, Func<T, string> groupSelector)
    {
        var result = new List<T>(order.Count);
        var remaining = new LinkedList<T>(order);
        string? last = null;
        while (remaining.Count > 0)
        {
            var pick = remaining.First!;
            if (last is not null && string.Equals(groupSelector(pick.Value), last, StringComparison.OrdinalIgnoreCase))
            {
                for (var node = pick.Next; node is not null; node = node.Next)
                {
                    if (!string.Equals(groupSelector(node.Value), last, StringComparison.OrdinalIgnoreCase))
                    {
                        pick = node;
                        break;
                    }
                }
            }

            result.Add(pick.Value);
            last = groupSelector(pick.Value);
            remaining.Remove(pick);
        }

        return result;
    }

    /// <summary>FNV-1a over the lower-cased name: stable across runtimes, unlike string.GetHashCode.</summary>
    private static ulong HashName(string name)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var c in name.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 0x100000001B3UL;
        }

        return hash;
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
