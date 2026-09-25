using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Builds channels from their definitions and answers what is airing.
///
/// Snapshots are cached because building one walks the library, and a channel is
/// queried every time anyone tunes in or a client refreshes its guide. They are
/// rebuilt wholesale when the library changes rather than surgically patched:
/// working out which channels a deleted episode belonged to costs more than
/// rebuilding, and a stale running order silently shifts every airing after the
/// missing item.
///
/// A change marks the snapshots stale; it does not throw them away. The next
/// request is answered from the stale snapshot while ONE rebuild per channel
/// runs in the background, and the fresh one is swapped in when it is done.
/// Dropping them made every request after a change rebuild all seven channels
/// inline — and a download importing a series fires a change every couple of
/// minutes, so the guide spent most of an evening blocked on rebuilds (7 s
/// for an hour of guide), and concurrent requests each rebuilt the same channel
/// (19 builds in a minute that needed 7). Serving the old order for the few
/// seconds a rebuild takes costs at most a programme the viewer could have
/// seen a moment earlier.
/// </summary>
public sealed class ChannelService
{
    private readonly BlockResolver _blocks;
    private readonly ILogger<ChannelService> _logger;
    private readonly ConcurrentDictionary<Guid, CachedChannel> _cache = new();
    // One build per channel at a time, whoever asks: a first build is awaited
    // by every caller; a rebuild runs in the background.
    private readonly ConcurrentDictionary<Guid, Lazy<CachedChannel>> _building = new();
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
        return ChannelScheduleResolver.Resolve(built.Dayparts, built.TimeZone, AnchorOf(definition), nowUtc);
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
            built.Dayparts, built.TimeZone, AnchorOf(definition), fromUtc, toUtc, maxPrograms);
    }

    /// <summary>
    /// The instant the channel's timelines count from. Slots are whole half
    /// hours, so a timeline anchored on the half hour starts every slot on :00
    /// or :30; the stored anchor is whenever the channel was created, so it is
    /// floored to the half hour. (Zone offsets are whole half hours almost
    /// everywhere; Nepal's +5:45 would land slots on :15/:45.)
    /// </summary>
    private static DateTime AnchorOf(ChannelDefinition definition)
    {
        if (!definition.AlignToHalfHour)
        {
            return definition.AnchorUtc;
        }

        var ticks = definition.AnchorUtc.Ticks;
        return new DateTime(ticks - (ticks % SlotPacker.HalfHourTicks), DateTimeKind.Utc);
    }

    /// <summary>Build every channel ahead of the first request. Called once at startup.</summary>
    public void Warm()
    {
        foreach (var channel in Config.Channels)
        {
            try
            {
                Built(channel.Id, channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Warming channel {Channel} failed", channel.Name);
            }
        }
    }

    /// <summary>
    /// Mark every cached running order stale. Called when the library changes
    /// or a definition is edited; the rebuild happens on the next request.
    /// </summary>
    public void Invalidate()
    {
        var generation = Interlocked.Increment(ref _generation);
        _logger.LogDebug("Channel snapshots invalidated (generation {Generation})", generation);
    }

    /// <summary>
    /// The running order to answer from: the cached one, with a background
    /// rebuild started if the library or definition moved under it (or a day
    /// has turned — "added within N days" ages items out with the calendar and
    /// nothing in the library changes to say so). Only a channel never built
    /// makes the caller wait.
    /// </summary>
    private CachedChannel Built(Guid channelId, ChannelDefinition definition)
    {
        if (_cache.TryGetValue(channelId, out var cached))
        {
            if (!IsCurrent(cached))
            {
                RebuildInBackground(channelId, definition);
            }

            return cached;
        }

        return BuildOnce(channelId, definition);
    }

    private bool IsCurrent(CachedChannel built) =>
        built.Generation == Volatile.Read(ref _generation) && built.BuiltOn == DateTime.UtcNow.Date;

    /// <summary>Build, sharing one build between everyone who asks at the same time.</summary>
    private CachedChannel BuildOnce(Guid channelId, ChannelDefinition definition)
    {
        var build = _building.GetOrAdd(
            channelId,
            _ => new Lazy<CachedChannel>(() => Build(definition), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var built = build.Value;
            _cache[channelId] = built;
            return built;
        }
        finally
        {
            _building.TryRemove(new KeyValuePair<Guid, Lazy<CachedChannel>>(channelId, build));
        }
    }

    private void RebuildInBackground(Guid channelId, ChannelDefinition definition)
    {
        if (_building.ContainsKey(channelId))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                BuildOnce(channelId, definition);
            }
            catch (Exception ex)
            {
                // The stale order keeps airing; the next request tries again.
                _logger.LogError(ex, "Rebuilding channel {Channel} failed", definition.Name);
            }
        });
    }

    private CachedChannel Build(ChannelDefinition definition)
    {
        var now = DateTime.UtcNow;
        // Read before walking the library: a change that lands mid-build must
        // leave this snapshot stale, not be stamped as already included.
        var generation = Volatile.Read(ref _generation);
        var dayparts = new List<Daypart>();

        foreach (var part in definition.Dayparts)
        {
            var items = _blocks.Resolve(part.Blocks);
            // A regular slot is "the next episode", which only means something
            // in series order.
            var ordering = part.EpisodePerAiring ? ChannelOrdering.Sequential : part.Ordering;
            var snapshot = SnapshotBuilder.Build(
                items, ordering, part.Seed, generation, now,
                alignToHalfHour: definition.AlignToHalfHour && !part.EpisodePerAiring);

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

        return new CachedChannel(dayparts, ResolveTimeZone(definition.TimeZoneId), generation, now.Date);
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
