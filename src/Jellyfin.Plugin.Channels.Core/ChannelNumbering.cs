using System.Buffers.Binary;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Channel numbers, the way a cable line-up has them: scattered (4, 13, 27),
/// not 1 to N, and fixed. Every client shows the same number and channel up/down
/// steps through them in order, so the number has to come from the server and
/// must not move when another channel is added or removed.
///
/// A pinned number (set in the dashboard) wins. Every other channel gets a
/// number derived from its id — random-looking but stable — stepping to the next
/// free number on a collision. Unpinned channels are placed in id order so the
/// collision steps are deterministic too.
/// </summary>
public static class ChannelNumbering
{
    public const int Lowest = 2;
    public const int Highest = 99;

    public static IReadOnlyDictionary<Guid, int> Assign(IEnumerable<(Guid Id, int? Pinned)> channels)
    {
        var list = channels.ToList();
        var numbers = new Dictionary<Guid, int>();
        var taken = new HashSet<int>();

        foreach (var (id, pinned) in list)
        {
            if (pinned is { } n && n >= 1 && taken.Add(n))
            {
                numbers[id] = n;
            }
        }

        var span = Highest - Lowest + 1;
        foreach (var (id, _) in list.Where(c => !numbers.ContainsKey(c.Id)).OrderBy(c => c.Id))
        {
            var candidate = Lowest + (int)(Hash(id) % (ulong)span);
            for (var step = 0; step < span && taken.Contains(candidate); step++)
            {
                candidate = candidate == Highest ? Lowest : candidate + 1;
            }

            // More than 98 channels: past the range, count on from the top.
            while (taken.Contains(candidate))
            {
                candidate++;
            }

            taken.Add(candidate);
            numbers[id] = candidate;
        }

        return numbers;
    }

    private static ulong Hash(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var z = BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
