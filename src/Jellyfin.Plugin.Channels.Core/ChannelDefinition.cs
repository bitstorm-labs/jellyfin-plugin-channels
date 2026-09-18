namespace Jellyfin.Plugin.Channels;

/// <summary>How a channel orders the items it was built from.</summary>
public enum ChannelOrdering
{
    /// <summary>Source order: season/episode for a series, then item order.</summary>
    Sequential = 0,

    /// <summary>Shuffled once by <see cref="ChannelDefinition.Seed"/> and then stable.</summary>
    SeededShuffle = 1
}

/// <summary>
/// A channel as the user defined it. This is configuration, not schedule: it
/// says what the channel is made of, while the running order lives in a
/// <see cref="ChannelSnapshot"/> built from it.
/// </summary>
public sealed class ChannelDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Libraries, series or collections feeding the channel. A channel is a mix
    /// of sources rather than one-per-series, because "all my sitcoms" is the
    /// thing people actually want and one-per-series is a special case of it.
    /// </summary>
    public List<Guid> SourceIds { get; set; } = [];

    public ChannelOrdering Ordering { get; set; } = ChannelOrdering.SeededShuffle;

    /// <summary>
    /// Fixes the shuffle. Stored rather than derived so that rebuilding a
    /// snapshot after a library change reshuffles as little as possible.
    /// </summary>
    public ulong Seed { get; set; }

    /// <summary>
    /// The instant the channel's first program began. Everything else is
    /// computed from it, so it must not drift once a channel is in use: moving
    /// the anchor moves every airing the channel has ever had.
    /// </summary>
    public DateTime AnchorUtc { get; set; } = DateTime.UtcNow;
}
