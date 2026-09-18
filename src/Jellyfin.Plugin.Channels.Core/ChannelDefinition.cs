namespace Jellyfin.Plugin.Channels;

/// <summary>How a daypart orders the items its blocks resolved to.</summary>
public enum ChannelOrdering
{
    /// <summary>Grouped by series, then season and episode.</summary>
    Sequential = 0,

    /// <summary>Shuffled once by the daypart's seed, and stable thereafter.</summary>
    SeededShuffle = 1
}

/// <summary>
/// A daypart as configured: when it airs and what feeds it. The running order
/// built from it lives in a <see cref="ChannelSnapshot"/>, kept separate because
/// configuration is what the user owns and a snapshot is derived state that gets
/// rebuilt whenever the library moves underneath it.
/// </summary>
public sealed class DaypartDefinition
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Start time of day in the channel's timezone.</summary>
    public TimeSpan StartLocal { get; set; } = TimeSpan.Zero;

    /// <summary>End time of day, exclusive. Must be after the start.</summary>
    public TimeSpan EndLocal { get; set; } = TimeSpan.FromDays(1);

    public ChannelDays Days { get; set; } = ChannelDays.All;

    public List<ContentBlock> Blocks { get; set; } = [];

    public ChannelOrdering Ordering { get; set; } = ChannelOrdering.SeededShuffle;

    /// <summary>
    /// Fixes the shuffle. Persisted rather than derived so a rebuild after a
    /// library change disturbs the order as little as possible.
    /// </summary>
    public ulong Seed { get; set; }
}

/// <summary>A channel as the user defined it.</summary>
public sealed class ChannelDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// IANA zone deciding what "6:00 AM" means for this channel's dayparts.
    /// Stored per channel and never read from the host: a server's own zone is
    /// easy to get wrong, and a schedule quietly an hour out is worse than one
    /// that refuses to guess.
    /// </summary>
    public string TimeZoneId { get; set; } = "Etc/UTC";

    /// <summary>
    /// When the channel's timelines began. Everything is computed from it, so it
    /// must not drift once a channel is in use — moving it moves every airing
    /// the channel has ever had.
    /// </summary>
    public DateTime AnchorUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The broadcast day. A single daypart covering 00:00-24:00 is the ordinary
    /// undayparted channel; gaps between dayparts are legal and mean off air.
    /// </summary>
    public List<DaypartDefinition> Dayparts { get; set; } = [];
}
