namespace Jellyfin.Plugin.Channels;

/// <summary>
/// The little a running order needs to know about a playable item.
///
/// Deliberately an interface rather than a Jellyfin type: assembling, ordering
/// and de-duplicating a running order is the part with the interesting rules,
/// and tying it to BaseItem would make it untestable without a server. The shell
/// is left with only the library query, which is the uninteresting part.
/// </summary>
public interface IChannelItem
{
    Guid Id { get; }

    long RuntimeTicks { get; }

    /// <summary>Series name for an episode, otherwise the item's own name.</summary>
    string SeriesName { get; }

    int? SeasonNumber { get; }

    int? EpisodeNumber { get; }

    /// <summary>Used to order movies, which have no season/episode.</summary>
    string SortName { get; }
}
