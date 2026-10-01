using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Channel management from a client: create, rename, re-logo and delete
/// channels, and add or remove titles. Dayparts, seasons and filter rules stay
/// in the dashboard. Administrators only — reads included, since they expose
/// how every channel is built.
/// </summary>
[ApiController]
// Jellyfin's own policy name for administrators (Jellyfin.Api.Constants.Policies.RequiresElevation);
// the constant lives in Jellyfin.Api, which a plugin does not reference.
[Authorize(Policy = "RequiresElevation")]
[Route("VirtualChannels/Manage")]
[Produces(MediaTypeNames.Application.Json)]
public class ChannelManagementController : ControllerBase
{
    private readonly ChannelManager _manager;

    public ChannelManagementController(ChannelManager manager)
    {
        _manager = manager;
    }

    /// <summary>Every channel with its dayparts, what was added to each, and the rules that feed it.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<ManagedChannelDto>> GetChannels() => Run(() => Ok(_manager.GetChannels()));

    /// <summary>Where an item is on every channel and daypart: "Added", "ViaRule" or "None".</summary>
    [HttpGet("Membership")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<MembershipDto>> GetMembership([FromQuery] Guid itemId) =>
        Run(() => Ok(_manager.GetMembership(itemId)));

    /// <summary>A new all-day channel, optionally seeded with a title.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ManagedChannelDto>> CreateChannel([FromBody] CreateChannelRequest request) =>
        RunAsync(async () =>
        {
            var channel = await _manager.CreateChannel(request.Name, request.Logo, request.SeedItemId).ConfigureAwait(false);
            return StatusCode(StatusCodes.Status201Created, channel);
        });

    /// <summary>Rename a channel or change its logo. Absent fields are left alone; Logo "" removes it.</summary>
    [HttpPatch("{channelId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ManagedChannelDto>> UpdateChannel([FromRoute] Guid channelId, [FromBody] UpdateChannelRequest request) =>
        RunAsync(async () => Ok(await _manager.UpdateChannel(channelId, request.Name, request.Logo).ConfigureAwait(false)));

    /// <summary>Delete a channel, and the collection the plugin made for it.</summary>
    [HttpDelete("{channelId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ManagedChannelDto>> DeleteChannel([FromRoute] Guid channelId) =>
        RunAsync(async () =>
        {
            await _manager.DeleteChannel(channelId).ConfigureAwait(false);
            return NoContent();
        });

    /// <summary>
    /// Add a title. A series (or an episode's or season's series) joins the
    /// daypart's first Series block; a film joins the channel's own collection.
    /// Idempotent.
    /// </summary>
    [HttpPost("{channelId}/Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ManagedChannelDto>> AddItem([FromRoute] Guid channelId, [FromBody] AddChannelItemRequest request) =>
        RunAsync(async () => Ok(await _manager.AddItem(channelId, request.ItemId, request.DaypartIndex).ConfigureAwait(false)));

    /// <summary>Remove an added title. Titles a rule brings in are not removable here (404).</summary>
    [HttpDelete("{channelId}/Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ManagedChannelDto>> RemoveItem(
        [FromRoute] Guid channelId,
        [FromRoute] Guid itemId,
        [FromQuery] int? daypartIndex = null) =>
        RunAsync(async () => Ok(await _manager.RemoveItem(channelId, itemId, daypartIndex).ConfigureAwait(false)));

    private ActionResult Run(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (ManagementException ex)
        {
            return Problem(detail: ex.Message, statusCode: ex.StatusCode);
        }
    }

    private async Task<ActionResult<ManagedChannelDto>> RunAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ManagementException ex)
        {
            return Problem(detail: ex.Message, statusCode: ex.StatusCode);
        }
    }
}
