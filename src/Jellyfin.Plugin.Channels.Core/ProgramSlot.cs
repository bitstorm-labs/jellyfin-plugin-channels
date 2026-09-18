namespace Jellyfin.Plugin.Channels;

/// <summary>
/// What a channel is airing at a given instant, and where inside it to start.
/// </summary>
/// <param name="ItemId">The item to play.</param>
/// <param name="OffsetTicks">How far into that item the viewer joins.</param>
/// <param name="StartUtc">When this program began airing.</param>
/// <param name="EndUtc">When it ends and <paramref name="NextItemId"/> begins.</param>
/// <param name="NextItemId">
/// The following program, so a client can prepare it before the boundary rather
/// than discovering it late and showing a gap.
/// </param>
public readonly record struct ProgramSlot(
    Guid ItemId,
    long OffsetTicks,
    DateTime StartUtc,
    DateTime EndUtc,
    Guid NextItemId)
{
    /// <summary>Offset in seconds, which is what player APIs actually take.</summary>
    public double OffsetSeconds => (double)OffsetTicks / TimeSpan.TicksPerSecond;
}
