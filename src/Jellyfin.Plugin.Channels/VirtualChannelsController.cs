using System.Net.Mime;
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

    public VirtualChannelsController(ChannelService channels)
    {
        _channels = channels;
    }

    /// <summary>Every configured channel.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<ChannelSummary>> GetChannels() =>
        Ok(_channels.GetChannels().Select(c => new ChannelSummary
        {
            Id = c.Id,
            Name = c.Name,
            Description = c.Description,
            TimeZoneId = c.TimeZoneId,
            DaypartCount = c.Dayparts.Count
        }));

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
}

public class ChannelSummary
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string TimeZoneId { get; set; } = string.Empty;

    public int DaypartCount { get; set; }
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
