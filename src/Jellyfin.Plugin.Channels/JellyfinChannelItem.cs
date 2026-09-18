using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Adapts a Jellyfin item to the little the running order needs, so the
/// assembly rules in Core never have to know about BaseItem.
/// </summary>
internal sealed class JellyfinChannelItem : IChannelItem
{
    private readonly BaseItem _item;

    public JellyfinChannelItem(BaseItem item)
    {
        _item = item;
        var episode = item as Episode;
        SeriesName = episode?.SeriesName ?? item.Name ?? string.Empty;
        SeasonNumber = episode?.ParentIndexNumber;
        EpisodeNumber = episode?.IndexNumber;
    }

    public Guid Id => _item.Id;

    public long RuntimeTicks => _item.RunTimeTicks ?? 0;

    public string SeriesName { get; }

    public int? SeasonNumber { get; }

    public int? EpisodeNumber { get; }

    public string SortName => _item.SortName ?? _item.Name ?? string.Empty;
}
