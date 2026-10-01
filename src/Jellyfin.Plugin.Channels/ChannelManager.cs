using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// A management request that cannot be carried out, with the status to answer.
/// </summary>
public sealed class ManagementException : Exception
{
    public ManagementException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }

    public static ManagementException NotFound(string message) => new(StatusCodes.Status404NotFound, message);

    public static ManagementException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);
}

/// <summary>
/// Edits channel definitions on behalf of a client — the "Add to Channel" and
/// Manage Channels screens. The configuration rules live in
/// <see cref="ChannelEditor"/>; this is the Jellyfin side: item lookups, the
/// channel's own BoxSet, and saving.
///
/// Every mutation runs under one lock and edits a copy of the current
/// configuration, then saves it through <c>UpdateConfiguration</c> — the same
/// call a dashboard save makes — so the copy replaces the live object in one
/// step (a guide request walking the channel list never sees it half-edited),
/// the XML is written the usual way, and <c>ConfigurationChanged</c> fires,
/// which is what invalidates the cached running orders. Two clients editing at
/// once queue rather than overwrite each other.
/// </summary>
public sealed class ChannelManager
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ChannelService _channels;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<ChannelManager> _logger;

    public ChannelManager(
        ChannelService channels,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<ChannelManager> logger)
    {
        _channels = channels;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    private static ChannelsPlugin Plugin =>
        ChannelsPlugin.Instance ?? throw new ManagementException(StatusCodes.Status503ServiceUnavailable, "The Channels plugin is not loaded.");

    // ---- reads ---------------------------------------------------------

    public IReadOnlyList<ManagedChannelDto> GetChannels()
    {
        var config = Plugin.Configuration;
        var numbers = ChannelNumbering.Assign(config.Channels.Select(c => (c.Id, c.Number)));
        return config.Channels
            .OrderBy(c => numbers[c.Id])
            .Select(c => ToDto(c, numbers[c.Id]))
            .ToList();
    }

    /// <summary>
    /// How an item sits on every channel and daypart. Explicit additions come
    /// from the configuration; "via rule" from the cached running orders, so
    /// this costs one episode lookup for a series and no channel rebuild.
    /// </summary>
    public IReadOnlyList<MembershipDto> GetMembership(Guid itemId)
    {
        var (subject, isSeries) = Subject(itemId);
        var playable = isSeries ? EpisodeIds(subject.Id) : [subject.Id];

        var config = Plugin.Configuration;
        var numbers = ChannelNumbering.Assign(config.Channels.Select(c => (c.Id, c.Number)));
        var result = new List<MembershipDto>();
        foreach (var channel in config.Channels.OrderBy(c => numbers[c.Id]))
        {
            var members = ManagedMembers(channel).Select(m => m.Id).ToHashSet();
            var resolved = _channels.GetResolvedItems(channel.Id);
            for (var i = 0; i < channel.Dayparts.Count; i++)
            {
                var part = channel.Dayparts[i];
                // A snapshot built before the definition changed can be a
                // daypart short; that daypart reads as airing nothing yet.
                IReadOnlySet<Guid> airing = i < resolved.Count ? resolved[i] : new HashSet<Guid>();
                var state = ChannelMembership.Classify(part, subject.Id, channel.ManagedCollectionId, members, playable, airing);
                result.Add(new MembershipDto
                {
                    ChannelId = channel.Id,
                    DaypartIndex = i,
                    State = state.ToString(),
                    RuleSummary = state == MembershipState.ViaRule
                        ? ChannelEditor.RuleSummary(part, channel.ManagedCollectionId, NameOf)
                        : null
                });
            }
        }

        return result;
    }

    // ---- mutations -------------------------------------------------------

    public Task<ManagedChannelDto> CreateChannel(string? name, string? logo, Guid? seedItemId) =>
        Mutate(async config =>
        {
            var validName = ValidName(name);
            ValidateLogo(logo);
            // Checked before anything is created: a bad seed must not leave a
            // half-made channel behind.
            if (seedItemId is { } seedItem)
            {
                Subject(seedItem);
            }

            // A seed the dashboard can hold exactly: it round-trips the
            // configuration through JavaScript numbers, and a 64-bit seed
            // rounded there would reshuffle the channel on its next save.
            var seed = (ulong)Random.Shared.Next(1, int.MaxValue);
            var channel = ChannelEditor.CreateChannel(
                config.Channels, validName, logo, TimeZoneInfo.Local.Id, DateTime.UtcNow, seed);
            config.Channels.Add(channel);

            if (seedItemId is { } seedId)
            {
                await AddItem(channel, seedId, null).ConfigureAwait(false);
            }

            return (true, channel);
        });

    public Task<ManagedChannelDto> UpdateChannel(Guid channelId, string? name, string? logo) =>
        Mutate(async config =>
        {
            var channel = Find(config, channelId);
            var changed = false;

            if (name is not null)
            {
                var validName = ValidName(name);
                if (validName != channel.Name)
                {
                    channel.Name = validName;
                    changed = true;
                    await RenameCollection(channel).ConfigureAwait(false);
                }
            }

            if (logo is not null)
            {
                // An empty string clears the logo; null (absent) leaves it.
                ValidateLogo(logo);
                var value = logo.Length == 0 ? null : logo;
                if (value != channel.Logo)
                {
                    channel.Logo = value;
                    changed = true;
                }
            }

            return (changed, channel);
        });

    public async Task DeleteChannel(Guid channelId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var config = Clone(Plugin.Configuration);
            var channel = Find(config, channelId);
            config.Channels.Remove(channel);
            Plugin.UpdateConfiguration(config);

            // After the save: if deleting the collection fails the channel is
            // still gone, and an orphaned collection is the lesser leftover.
            if (ManagedCollection(channel) is { } boxSet)
            {
                try
                {
                    _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true }, true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Deleting the collection of channel {Channel} failed", channel.Name);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<ManagedChannelDto> AddItem(Guid channelId, Guid itemId, int? daypartIndex) =>
        Mutate(async config =>
        {
            var channel = Find(config, channelId);
            var changed = await AddItem(channel, itemId, daypartIndex).ConfigureAwait(false);
            return (changed, channel);
        });

    public Task<ManagedChannelDto> RemoveItem(Guid channelId, Guid itemId, int? daypartIndex) =>
        Mutate(async config =>
        {
            var channel = Find(config, channelId);
            if (daypartIndex is { } index && !ChannelEditor.IsValidDaypartIndex(channel, index))
            {
                throw ManagementException.BadRequest($"Channel '{channel.Name}' has no daypart {index}.");
            }

            var item = _libraryManager.GetItemById(itemId);
            // An id from AddedItems may be a series that has since left the
            // library; it can still be removed from the definition.
            var subjectId = item is null ? itemId : Subject(itemId).Item.Id;

            if (ChannelEditor.RemoveSeries(channel, daypartIndex, subjectId))
            {
                return (true, channel);
            }

            if (ManagedCollection(channel) is { } boxSet && boxSet.GetLinkedChildren().Any(m => m.Id == subjectId))
            {
                // The collection belongs to the channel, not a daypart, so the
                // film leaves every daypart that airs it.
                await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, [subjectId]).ConfigureAwait(false);
                return (false, channel);
            }

            throw ManagementException.NotFound(
                "That title was not added to this channel. Titles a library, filter or other collection "
                + "brings in can only be changed in the dashboard.");
        });

    /// <summary>
    /// Load a copy of the configuration, edit it, save it when the edit says
    /// it changed anything, and answer with the channel as saved.
    /// </summary>
    private async Task<ManagedChannelDto> Mutate(Func<PluginConfiguration, Task<(bool Changed, ChannelDefinition Channel)>> edit)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var config = Clone(Plugin.Configuration);
            var (changed, channel) = await edit(config).ConfigureAwait(false);
            if (changed)
            {
                Plugin.UpdateConfiguration(config);
            }
            else
            {
                // A collection change alone still moves the schedule; the
                // BoxSet update would invalidate after the library debounce,
                // but whoever made it is looking at the guide now.
                _channels.Invalidate();
            }

            var numbers = ChannelNumbering.Assign(config.Channels.Select(c => (c.Id, c.Number)));
            return ToDto(channel, numbers[channel.Id]);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Add a title to a channel definition (and its collection). True when the definition changed.</summary>
    private async Task<bool> AddItem(ChannelDefinition channel, Guid itemId, int? daypartIndex)
    {
        var index = daypartIndex ?? ChannelEditor.DefaultDaypartIndex(channel);
        if (!ChannelEditor.IsValidDaypartIndex(channel, index))
        {
            throw ManagementException.BadRequest($"Channel '{channel.Name}' has no daypart {index}.");
        }

        var (subject, isSeries) = Subject(itemId);
        if (isSeries)
        {
            return ChannelEditor.AddSeries(channel, index, subject.Id);
        }

        var changed = false;
        var boxSet = ManagedCollection(channel);
        if (boxSet is null)
        {
            boxSet = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = ChannelEditor.ManagedCollectionName(channel.Name),
                IsLocked = true
            }).ConfigureAwait(false);
            channel.ManagedCollectionId = boxSet.Id;
            changed = true;
            _logger.LogInformation("Created collection {Collection} for channel {Channel}", boxSet.Name, channel.Name);
        }

        changed |= ChannelEditor.EnsureCollectionBlock(channel, index, boxSet.Id);

        if (!boxSet.GetLinkedChildren().Any(m => m.Id == subject.Id))
        {
            await _collectionManager.AddToCollectionAsync(boxSet.Id, [subject.Id]).ConfigureAwait(false);
        }

        return changed;
    }

    private async Task RenameCollection(ChannelDefinition channel)
    {
        if (ManagedCollection(channel) is not { } boxSet)
        {
            return;
        }

        try
        {
            boxSet.Name = ChannelEditor.ManagedCollectionName(channel.Name);
            await boxSet.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cosmetic: the channel still airs it by id.
            _logger.LogError(ex, "Renaming the collection of channel {Channel} failed", channel.Name);
        }
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// What a channel actually holds for an item: a series for a series,
    /// season or episode; the item itself for a film or other video.
    /// </summary>
    private (BaseItem Item, bool IsSeries) Subject(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId) ?? throw ManagementException.NotFound($"No item {itemId:N}.");
        switch (item)
        {
            case Series series:
                return (series, true);
            case Season season:
                return (SeriesOf(season.SeriesId), true);
            case Episode episode:
                return (SeriesOf(episode.SeriesId), true);
            case Video video:
                return (video, false);
            default:
                throw ManagementException.BadRequest(
                    $"A {item.GetBaseItemKind()} cannot be added to a channel; only series, seasons, episodes and films can.");
        }
    }

    private Series SeriesOf(Guid seriesId) =>
        _libraryManager.GetItemById(seriesId) as Series
        ?? throw ManagementException.BadRequest("That item does not belong to a series in the library.");

    private List<Guid> EpisodeIds(Guid seriesId) =>
        _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = seriesId,
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true
        }).Select(e => e.Id).ToList();

    private BoxSet? ManagedCollection(ChannelDefinition channel) =>
        channel.ManagedCollectionId is { } id ? _libraryManager.GetItemById(id) as BoxSet : null;

    /// <summary>
    /// A collection's members. They are linked children, not physical ones —
    /// an ancestry query returns nothing for them — so read the links.
    /// </summary>
    private IReadOnlyList<BaseItem> ManagedMembers(ChannelDefinition channel) =>
        ManagedCollection(channel)?.GetLinkedChildren() ?? [];

    private string? NameOf(Guid id) => _libraryManager.GetItemById(id)?.Name;

    private static ChannelDefinition Find(PluginConfiguration config, Guid channelId) =>
        config.Channels.FirstOrDefault(c => c.Id == channelId)
        ?? throw ManagementException.NotFound($"No channel {channelId:N}.");

    private static string ValidName(string? name) =>
        ChannelEditor.NormalizeName(name)
        ?? throw ManagementException.BadRequest($"A channel needs a name of 1 to {ChannelEditor.MaxNameLength} characters.");

    /// <summary>A logo key must be one the plugin ships; null or empty means none.</summary>
    private static void ValidateLogo(string? logo)
    {
        if (!string.IsNullOrEmpty(logo) && !ChannelLogos.Keys.Contains(logo))
        {
            throw ManagementException.BadRequest($"Unknown logo '{logo}'. GET /VirtualChannels/Logos lists them.");
        }
    }

    /// <summary>
    /// A deep copy to edit. JSON because the configuration is already
    /// required to survive it — that is how the dashboard posts it.
    /// </summary>
    private static PluginConfiguration Clone(PluginConfiguration config) =>
        JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(config))!;

    private ManagedChannelDto ToDto(ChannelDefinition channel, int number)
    {
        var members = ManagedMembers(channel);
        var seriesIds = channel.Dayparts.SelectMany(ChannelEditor.AddedSeries).Distinct().ToArray();
        var series = seriesIds.Length == 0
            ? new Dictionary<Guid, BaseItem>()
            : _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = seriesIds })
                .GroupBy(i => i.Id)
                .ToDictionary(g => g.Key, g => g.First());

        return new ManagedChannelDto
        {
            Id = channel.Id,
            Name = channel.Name,
            Number = number,
            Logo = string.IsNullOrWhiteSpace(channel.Logo) ? null : channel.Logo,
            IsScheduled = ChannelEditor.IsScheduled(channel),
            Dayparts = channel.Dayparts.Select((part, index) =>
            {
                var added = ChannelEditor.AddedSeries(part)
                    .Select(id => series.TryGetValue(id, out var item)
                        ? ToItemDto(item)
                        // Left in so the client can still remove it.
                        : new ManagedItemDto { Id = id, Name = "Missing from library", Type = "Series" })
                    .ToList();
                if (channel.ManagedCollectionId is { } collectionId && ChannelEditor.HasCollectionBlock(part, collectionId))
                {
                    added.AddRange(members.Select(ToItemDto));
                }

                return new ManagedDaypartDto
                {
                    Index = index,
                    Name = part.Name,
                    StartMinutes = part.StartMinutes,
                    EndMinutes = part.EndMinutes,
                    IsSeasonal = ChannelEditor.IsSeasonal(part),
                    AddedItems = added,
                    RuleSummary = ChannelEditor.RuleSummary(part, channel.ManagedCollectionId, NameOf)
                };
            }).ToList()
        };
    }

    private static ManagedItemDto ToItemDto(BaseItem item) => new()
    {
        Id = item.Id,
        Name = item.Name ?? string.Empty,
        Type = item.GetBaseItemKind().ToString(),
        ProductionYear = item.ProductionYear
    };
}

public class ManagedChannelDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The number the channel shows, pinned or assigned.</summary>
    public int? Number { get; set; }

    /// <summary>The channel's configured logo key (not a seasonal daypart's).</summary>
    public string? Logo { get; set; }

    /// <summary>More than one daypart, or any season: the client offers a daypart pick.</summary>
    public bool IsScheduled { get; set; }

    public List<ManagedDaypartDto> Dayparts { get; set; } = [];
}

public class ManagedDaypartDto
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public int StartMinutes { get; set; }

    public int EndMinutes { get; set; }

    public bool IsSeasonal { get; set; }

    /// <summary>What the client may remove: Series-block series and the channel collection's films.</summary>
    public List<ManagedItemDto> AddedItems { get; set; } = [];

    /// <summary>What else feeds the daypart — "Science Fiction, Library: Movies" — or null.</summary>
    public string? RuleSummary { get; set; }
}

public class ManagedItemDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>"Series", "Movie", …</summary>
    public string Type { get; set; } = string.Empty;

    public int? ProductionYear { get; set; }
}

public class MembershipDto
{
    public Guid ChannelId { get; set; }

    public int DaypartIndex { get; set; }

    /// <summary>"Added", "ViaRule" or "None".</summary>
    public string State { get; set; } = nameof(MembershipState.None);

    /// <summary>The daypart's rules, when <see cref="State"/> is "ViaRule".</summary>
    public string? RuleSummary { get; set; }
}

public class CreateChannelRequest
{
    public string? Name { get; set; }

    public string? Logo { get; set; }

    public Guid? SeedItemId { get; set; }
}

public class UpdateChannelRequest
{
    /// <summary>Null leaves the name as it is.</summary>
    public string? Name { get; set; }

    /// <summary>Null leaves the logo; an empty string removes it.</summary>
    public string? Logo { get; set; }
}

public class AddChannelItemRequest
{
    public Guid ItemId { get; set; }

    public int? DaypartIndex { get; set; }
}
