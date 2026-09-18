using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Turns a daypart's blocks into the playable items behind them. This is the one
/// genuinely Jellyfin-bound part of building a channel; everything after it —
/// de-duplication, ordering, dropping the unplayable — is handled by
/// <see cref="SnapshotBuilder"/>.
/// </summary>
public sealed class BlockResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<BlockResolver> _logger;

    public BlockResolver(ILibraryManager libraryManager, ILogger<BlockResolver> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Resolve every block in order. Blocks are concatenated rather than merged:
    /// de-duplication happens once, downstream, so the "first occurrence wins"
    /// rule has a defined meaning instead of depending on which block ran first
    /// inside a set union.
    /// </summary>
    public IReadOnlyList<IChannelItem> Resolve(IEnumerable<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var items = new List<IChannelItem>();
        foreach (var block in blocks)
        {
            try
            {
                items.AddRange(ResolveBlock(block).Select(i => new JellyfinChannelItem(i)));
            }
            catch (Exception ex)
            {
                // One bad block must not cost the whole channel: a deleted
                // library or series would otherwise take the schedule off air
                // entirely rather than just losing its own contribution.
                _logger.LogError(ex, "Channel block of kind {Kind} could not be resolved; skipping it", block.Kind);
            }
        }

        return items;
    }

    private IReadOnlyList<BaseItem> ResolveBlock(ContentBlock block)
    {
        var playable = new[] { BaseItemKind.Episode, BaseItemKind.Movie };

        switch (block.Kind)
        {
            case ContentBlockKind.Library:
            case ContentBlockKind.Series:
                // Both are "everything playable underneath these ids"; a series
                // is simply a parent whose descendants are episodes.
                return block.ItemIds
                    .SelectMany(id => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        ParentId = id,
                        IncludeItemTypes = playable,
                        Recursive = true
                    }))
                    .ToArray();

            case ContentBlockKind.Collection:
                // A collection's members are *linked* children, not physical
                // ones, so the ordinary item query does not find them: asking
                // for them by ancestry returns an empty set with no error, which
                // shows up only as a channel quietly missing its movies. Walk
                // the collection itself and ask for linked children explicitly.
                return block.ItemIds
                    .Select(id => _libraryManager.GetItemById(id) as Folder)
                    .Where(folder => folder is not null)
                    .SelectMany(folder => folder!.GetRecursiveChildren(includeLinkedChildren: true))
                    .Where(item => item is Video)
                    .ToArray();

            case ContentBlockKind.Filter:
                return ResolveFilter(block.Filter);

            default:
                return [];
        }
    }

    private IReadOnlyList<BaseItem> ResolveFilter(ContentFilter? filter)
    {
        if (filter is null)
        {
            return [];
        }

        var kinds = new List<BaseItemKind>();
        if (filter.IncludeEpisodes)
        {
            kinds.Add(BaseItemKind.Episode);
        }

        if (filter.IncludeMovies)
        {
            kinds.Add(BaseItemKind.Movie);
        }

        // Asking for nothing should return nothing rather than everything, which
        // is what an empty IncludeItemTypes would mean to the query layer.
        if (kinds.Count == 0)
        {
            return [];
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds.ToArray(),
            Recursive = true
        };

        if (filter.Genres.Count > 0)
        {
            query.Genres = filter.Genres.ToArray();
        }

        if (filter.Tags.Count > 0)
        {
            query.Tags = filter.Tags.ToArray();
        }

        if (filter.MinYear is { } min)
        {
            query.MinPremiereDate = new DateTime(min, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        if (filter.MaxYear is { } max)
        {
            query.MaxPremiereDate = new DateTime(max, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        }

        return _libraryManager.GetItemList(query);
    }
}
