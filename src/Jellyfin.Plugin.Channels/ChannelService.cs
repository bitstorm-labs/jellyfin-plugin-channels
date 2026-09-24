using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Builds channels from their definitions and answers what is airing.
///
/// Snapshots are cached because building one walks the library, and a channel is
/// queried every time anyone tunes in or a client refreshes its guide. They are
/// dropped wholesale when the library changes rather than surgically patched:
/// working out which channels a deleted episode belonged to costs more than
/// rebuilding, and a stale running order silently shifts every airing after the
/// missing item.
/// </summary>
public sealed class ChannelService
{
    private readonly BlockResolver _blocks;
    private readonly ILogger<ChannelService> _logger;
    private readonly ConcurrentDictionary<Guid, CachedChannel> _cache = new();
    private int _generation;

    public ChannelService(BlockResolver blocks, ILogger<ChannelService> logger)
    {
        _blocks = blocks;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        ChannelsPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public IReadOnlyList<ChannelDefinition> GetChannels() => Config.Channels;

    /// <summary>Every channel's number, pinned or assigned.</summary>
    public IReadOnlyDictionary<Guid, int> Numbers() =>
        ChannelNumbering.Assign(Config.Channels.Select(c => (c.Id, c.Number)));

    /// <summary>Channels in number order — the order a guide lists and channel up/down steps.</summary>
    public IReadOnlyList<ChannelDefinition> GetChannelsInNumberOrder()
    {
        var numbers = Numbers();
        return Config.Channels.OrderBy(c => numbers[c.Id]).ToList();
    }

    public ChannelDefinition? GetChannel(Guid id) =>
        Config.Channels.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// What <paramref name="channelId"/> is airing at <paramref name="nowUtc"/>,
    /// or <c>null</c> when the channel is off air or has nothing playable.
    /// </summary>
    public ProgramSlot? GetNowPlaying(Guid channelId, DateTime nowUtc)
    {
        var definition = GetChannel(channelId);
        if (definition is null)
        {
            return null;
        }

        var built = Built(channelId, definition);
        return ChannelScheduleResolver.Resolve(built.Dayparts, built.TimeZone, definition.AnchorUtc, nowUtc);
    }

    /// <summary>
    /// Everything <paramref name="channelId"/> will air in a window — the guide.
    /// </summary>
    public IReadOnlyList<ProgramSlot> GetGuide(Guid channelId, DateTime fromUtc, DateTime toUtc, int maxPrograms = 200)
    {
        var definition = GetChannel(channelId);
        if (definition is null)
        {
            return [];
        }

        var built = Built(channelId, definition);
        return ChannelScheduleResolver.ResolveRange(
            built.Dayparts, built.TimeZone, definition.AnchorUtc, fromUtc, toUtc, maxPrograms);
    }

    /// <summary>
    /// Drop every cached running order. Called when the library changes or a
    /// definition is edited.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
        _logger.LogDebug("Channel snapshots invalidated (generation {Generation})", _generation);
    }

    /// <summary>
    /// The cached running order, rebuilt if the library moved under it. A
    /// definition edited in the config page must not keep serving the old
    /// order, and the config object carries no version of its own.
    /// </summary>
    private CachedChannel Built(Guid channelId, ChannelDefinition definition)
    {
        var built = _cache.GetOrAdd(channelId, _ => Build(definition));
        // Also rebuilt once a day: "added within N days" ages items out with
        // the calendar, and nothing in the library changes to say so.
        if (built.Generation == _generation && built.BuiltOn == DateTime.UtcNow.Date)
        {
            return built;
        }

        built = Build(definition);
        _cache[channelId] = built;
        return built;
    }

    private CachedChannel Build(ChannelDefinition definition)
    {
        var now = DateTime.UtcNow;
        var dayparts = new List<Daypart>();

        foreach (var part in definition.Dayparts)
        {
            var items = _blocks.Resolve(part.Blocks);
            // A regular slot is "the next episode", which only means something
            // in series order.
            var ordering = part.EpisodePerAiring ? ChannelOrdering.Sequential : part.Ordering;
            var snapshot = SnapshotBuilder.Build(items, ordering, part.Seed, _generation, now);

            try
            {
                dayparts.Add(new Daypart(
                    part.Name,
                    TimeSpan.FromMinutes(part.StartMinutes),
                    TimeSpan.FromMinutes(part.EndMinutes),
                    snapshot,
                    (ChannelDays)part.Days,
                    SeasonWindow.From(part.SeasonStartMonthDay, part.SeasonEndMonthDay),
                    part.EpisodePerAiring,
                    ParseDate(part.FirstAiringDate, part.Name, definition.Name),
                    part.EpisodesPerAiring));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // A daypart with impossible times is dropped rather than taking
                // the whole channel down; the rest of the broadcast day still airs.
                _logger.LogError(ex, "Daypart {Name} on channel {Channel} has invalid times; skipping it",
                    part.Name, definition.Name);
            }
        }

        _logger.LogInformation(
            "Built channel {Channel}: {Dayparts} daypart(s), {Programs} program(s)",
            definition.Name, dayparts.Count, dayparts.Sum(d => d.Snapshot.Programs.Count));

        return new CachedChannel(dayparts, ResolveTimeZone(definition.TimeZoneId), _generation, now.Date);
    }

    /// <summary>
    /// An unknown zone falls back to UTC loudly rather than to the host's zone.
    /// Silently adopting the server's clock is how a schedule ends up an hour
    /// out with nothing in the logs to say why.
    /// </summary>
    private TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogError("Unknown channel time zone '{TimeZone}'; falling back to UTC", id);
            return TimeZoneInfo.Utc;
        }
    }

    private sealed record CachedChannel(IReadOnlyList<Daypart> Dayparts, TimeZoneInfo TimeZone, int Generation, DateTime BuiltOn);

    /// <summary>An unparseable start date is logged and ignored rather than taking the daypart down.</summary>
    private DateTime? ParseDate(string? value, string daypart, string channel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
        {
            return date;
        }

        _logger.LogError("Daypart {Daypart} on channel {Channel} has an unreadable first airing date '{Value}'; ignoring it",
            daypart, channel, value);
        return null;
    }
}
