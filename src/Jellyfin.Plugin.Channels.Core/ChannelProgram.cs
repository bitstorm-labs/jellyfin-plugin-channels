namespace Jellyfin.Plugin.Channels;

/// <summary>
/// One playable item as it appears in a channel's running order.
/// </summary>
/// <param name="ItemId">Jellyfin item id.</param>
/// <param name="RuntimeTicks">
/// Item runtime. Ticks (100 ns) rather than seconds so the schedule stays exact:
/// a channel resolves positions years past its anchor, and rounding each program
/// to whole seconds would let the error accumulate without bound.
/// </param>
public readonly record struct ChannelProgram(Guid ItemId, long RuntimeTicks);
