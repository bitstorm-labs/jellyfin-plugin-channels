using System.Net.Mime;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// What a client needs to show a channel list and tune in. Deliberately two
/// calls: the list is cheap and cacheable, while what is airing is only true for
/// as long as the current program lasts.
/// </summary>
[ApiController]
[Authorize]
// NOT "Channels": Jellyfin's own ChannelsController already owns that route,
// and registering a second one makes every request to it fail with
// AmbiguousMatchException rather than quietly preferring either.
[Route("VirtualChannels")]
[Produces(MediaTypeNames.Application.Json)]
public class VirtualChannelsController : ControllerBase
{
    private readonly ChannelService _channels;

    public VirtualChannelsController(ChannelService channels, ILibraryManager libraryManager)
    {
        _channels = channels;
        _libraryManager = libraryManager;
    }

    private readonly ILibraryManager _libraryManager;

    /// <summary>Every configured channel.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<ChannelSummary>> GetChannels()
    {
        var numbers = _channels.Numbers();
        return Ok(_channels.GetChannelsInNumberOrder().Select(c => new ChannelSummary
        {
            Id = c.Id,
            Number = numbers[c.Id],
            Name = c.Name,
            Description = c.Description,
            TimeZoneId = c.TimeZoneId,
            DaypartCount = c.Dayparts.Count,
            Logo = LogoFor(c)
        }));
    }

    /// <summary>
    /// A channel's logo as a PNG — <c>style=mono</c> for the white version a
    /// player lays over the picture. Anonymous like Jellyfin's own images, so
    /// clients can hand the URL straight to an image view.
    /// </summary>
    /// <response code="404">No such channel, or it has no logo.</response>
    [HttpGet("{channelId}/Logo")]
    [AllowAnonymous]
    [Produces("image/png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetLogo([FromRoute] Guid channelId, [FromQuery] string? style = null)
    {
        var channel = _channels.GetChannel(channelId);
        var key = channel is null ? null : LogoFor(channel);
        var stream = key is null ? null : ChannelLogos.Open(key, string.Equals(style, "mono", StringComparison.OrdinalIgnoreCase));
        if (stream is null)
        {
            return NotFound();
        }

        // The URL clients build carries the key, so a day's cache is safe:
        // a seasonal swap changes the key and therefore the URL.
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, "image/png");
    }

    /// <summary>Every logo the plugin ships, for the dashboard's picker.</summary>
    [HttpGet("Logos")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<string>> GetLogoKeys() => Ok(ChannelLogos.Keys);

    private static string? LogoFor(ChannelDefinition channel)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(channel.TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // The schedule logs a bad zone when it builds; the logo only needs a date.
            zone = TimeZoneInfo.Utc;
        }

        var key = ChannelLogo.Resolve(channel, TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        return key is not null && ChannelLogos.Keys.Contains(key) ? key : null;
    }

    /// <summary>
    /// What the channel is airing right now, and where in it to start.
    /// </summary>
    /// <response code="200">The airing program.</response>
    /// <response code="204">Off air — a gap in the schedule, or nothing playable.</response>
    /// <response code="404">No such channel.</response>
    [HttpGet("{channelId}/NowPlaying")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<NowPlayingDto> GetNowPlaying([FromRoute] Guid channelId)
    {
        if (_channels.GetChannel(channelId) is null)
        {
            return NotFound();
        }

        var slot = _channels.GetNowPlaying(channelId, DateTime.UtcNow);
        if (slot is null)
        {
            // Off air is a normal state, not a failure: a channel may simply
            // have a gap overnight. 204 lets a client show "off air" without
            // treating it as an error.
            return NoContent();
        }

        var value = slot.Value;
        return Ok(new NowPlayingDto
        {
            ItemId = value.ItemId,
            StartPositionSeconds = value.OffsetSeconds,
            StartUtc = value.StartUtc,
            EndUtc = value.EndUtc,
            NextItemId = value.NextItemId == Guid.Empty ? null : value.NextItemId
        });
    }

    /// <summary>
    /// What every channel will air over the next <paramref name="hours"/> hours.
    ///
    /// One call for all channels rather than one per channel: a guide draws
    /// every row at once, and a request per channel would make the grid appear
    /// a row at a time.
    /// </summary>
    [HttpGet("Guide")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<ChannelGuideDto>> GetGuide([FromQuery] double hours = 3)
    {
        // A week, the horizon a printed guide had. Clamped so a stray query
        // cannot ask the server to walk a year of schedule for every channel.
        var window = Math.Clamp(hours, 0.5, 168);
        var from = DateTime.UtcNow;
        var to = from.AddHours(window);
        // The per-channel program cap scales with the window: 40 an hour is
        // more than the shortest episodes can fill, and a fixed 200 was
        // reached by a channel of ten-minute videos inside three days.
        var maxPrograms = Math.Max(200, (int)(window * 40));

        var numbers = _channels.Numbers();
        var guides = _channels.GetChannelsInNumberOrder().Select(channel => new ChannelGuideDto
        {
            Id = channel.Id,
            Number = numbers[channel.Id],
            Name = channel.Name,
            Description = channel.Description,
            Logo = LogoFor(channel),
            Programs = _channels.GetGuide(channel.Id, from, to, maxPrograms).Select(slot => new GuideEntryDto
            {
                ItemId = slot.ItemId,
                StartUtc = slot.StartUtc,
                EndUtc = slot.EndUtc,
                StartPositionSeconds = slot.OffsetSeconds
            }).ToList()
        }).ToList();

        // Names travel with the schedule. A day of guide is ~275 distinct
        // items and a week is thousands; a client fetching each one by id —
        // which is what the first tvOS guide did — cannot afford that, and a
        // guide that shows ids while it catches up is not a guide.
        // One query for all of them. A lookup per id was ~1,500 round trips on a
        // cold cache — 15 s of the first week of guide after a restart.
        var ids = guides.SelectMany(g => g.Programs.Select(p => p.ItemId)).Distinct().ToArray();
        var details = ids.Length == 0
            ? new Dictionary<Guid, BaseItem>()
            : _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = ids })
                .GroupBy(item => item.Id)
                .ToDictionary(g => g.Key, g => g.First());

        foreach (var entry in guides.SelectMany(g => g.Programs))
        {
            if (!details.TryGetValue(entry.ItemId, out var item))
            {
                continue;
            }

            entry.Name = item.Name;
            entry.IsNew = item.DateCreated >= DateTime.UtcNow.AddDays(-7);
            entry.Type = item.GetBaseItemKind().ToString();
            entry.RuntimeTicks = item.RunTimeTicks;
            if (item is Episode episode)
            {
                entry.SeriesName = episode.SeriesName;
                entry.SeasonNumber = episode.ParentIndexNumber;
                entry.EpisodeNumber = episode.IndexNumber;
            }
            else
            {
                entry.ProductionYear = item.ProductionYear;
            }
        }

        return Ok(guides);
    }
}

public class ChannelGuideDto
{
    public Guid Id { get; set; }

    public int Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>The logo key in effect today; fetch it from /VirtualChannels/{Id}/Logo.</summary>
    public string? Logo { get; set; }

    public List<GuideEntryDto> Programs { get; set; } = [];
}

public class GuideEntryDto
{
    public Guid ItemId { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    /// <summary>
    /// How far into the item this airing already is. Non-zero only for the
    /// entry in progress when the guide was requested.
    /// </summary>
    public double StartPositionSeconds { get; set; }

    // Everything below is filled from the library so a client can draw the
    // guide without a request per item. Nullable, and absent for an item the
    // library no longer has — the id is still there to fall back on.

    public string? Name { get; set; }

    /// <summary>"Episode" or "Movie".</summary>
    public string? Type { get; set; }

    public string? SeriesName { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public int? ProductionYear { get; set; }

    public long? RuntimeTicks { get; set; }

    /// <summary>Added to the library within the last week — clients badge it "New".</summary>
    public bool IsNew { get; set; }
}

public class ChannelSummary
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string TimeZoneId { get; set; } = string.Empty;

    public int DaypartCount { get; set; }

    public int Number { get; set; }

    /// <summary>The logo key in effect today; fetch it from /VirtualChannels/{Id}/Logo.</summary>
    public string? Logo { get; set; }
}

public class NowPlayingDto
{
    /// <summary>The item to play.</summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// How far into the item to start. This is a join-in-progress offset, not a
    /// resume position: it must not be written back as playback progress.
    /// </summary>
    public double StartPositionSeconds { get; set; }

    public DateTime StartUtc { get; set; }

    /// <summary>When to re-query, because this program ends.</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>What follows, so a client can prepare it before the boundary.</summary>
    public Guid? NextItemId { get; set; }
}
