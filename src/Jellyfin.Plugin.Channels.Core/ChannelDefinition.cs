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

    // The three fields below are primitives on purpose, and the reason is not
    // taste. Plugin configuration round-trips through Jellyfin's JSON API and is
    // persisted with XmlSerializer, and both are hostile to richer types:
    // Jellyfin's JsonFlagEnumConverter implements Write but throws
    // NotImplementedException on Read, so a [Flags] enum cannot be posted back
    // at all, and XmlSerializer has no sensible mapping for TimeSpan. Minutes
    // and a bitmask survive both. The domain types (TimeSpan, ChannelDays) are
    // reconstructed when the runtime daypart is built.

    /// <summary>Start time as minutes past local midnight (0-1440).</summary>
    public int StartMinutes { get; set; }

    /// <summary>End time as minutes past local midnight, exclusive (0-1440).</summary>
    public int EndMinutes { get; set; } = 1440;

    /// <summary>
    /// Day bitmask matching <see cref="ChannelDays"/>: Sunday is 1, Monday 2,
    /// and so on up to Saturday 64. 127 is every day.
    /// </summary>
    public int Days { get; set; } = (int)ChannelDays.All;

    /// <summary>
    /// Inclusive first day of the season this daypart airs in, as MMDD — 1001
    /// is 1 October. Null, with <see cref="SeasonEndMonthDay"/>, means all year.
    /// An end before the start wraps the year end, so 1215/0105 is Christmas.
    /// </summary>
    public int? SeasonStartMonthDay { get; set; }

    /// <summary>Inclusive last day of the season, as MMDD.</summary>
    public int? SeasonEndMonthDay { get; set; }

    /// <summary>
    /// A regular slot: one programme per airing, in order, starting on the
    /// slot — "Survivor, Wednesdays at 7". The rest of the slot falls back to
    /// the channel's other dayparts. Always plays in series order.
    /// </summary>
    public bool EpisodePerAiring { get; set; }

    /// <summary>
    /// First local date a regular slot airs, as yyyy-MM-dd; its first episode
    /// airs then. A string for the same serialisation reasons as the MMDD
    /// fields. Empty means "count from the channel's anchor".
    /// </summary>
    public string? FirstAiringDate { get; set; }

    /// <summary>Programmes per airing of a regular slot: 1 a weekly show, more a marathon, 0 fill the slot.</summary>
    public int EpisodesPerAiring { get; set; } = 1;

    public List<ContentBlock> Blocks { get; set; } = [];

    public ChannelOrdering Ordering { get; set; } = ChannelOrdering.SeededShuffle;

    /// <summary>
    /// Fixes the shuffle. Persisted rather than derived so a rebuild after a
    /// library change disturbs the order as little as possible.
    /// </summary>
    public ulong Seed { get; set; }

    /// <summary>
    /// A logo that replaces the channel's own while this daypart's season is
    /// open — the snowflake a seasonal channel wears in December. Null keeps
    /// the channel's logo.
    /// </summary>
    public string? Logo { get; set; }
}

/// <summary>A channel as the user defined it.</summary>
public sealed class ChannelDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// What the channel is, in a line — "Comedy by day, sci-fi after dark".
    /// Describes the channel's character, which is a different thing from the
    /// synopsis of whatever happens to be airing, and stays true when the
    /// programme changes.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// A fixed channel number chosen in the dashboard. Null lets the server
    /// assign one — scattered and stable, see ChannelNumbering.
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// Key of one of the logos the plugin ships ("nebula", "the-reel"). Null
    /// is no logo; clients fall back to the number and name.
    /// </summary>
    public string? Logo { get; set; }

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
