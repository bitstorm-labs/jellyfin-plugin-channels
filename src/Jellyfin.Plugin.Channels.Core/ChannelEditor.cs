namespace Jellyfin.Plugin.Channels;

/// <summary>
/// The configuration edits the management API makes — creating a channel,
/// adding a series to a daypart, wiring in the channel's own collection. Kept
/// here, free of Jellyfin, because these are the rules a client relies on
/// (which daypart, idempotency, numbering), and they should be testable
/// without a server. The shell does the library lookups and the saving.
/// </summary>
public static class ChannelEditor
{
    /// <summary>Longest channel name accepted; a guide row has no room for more.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The title of the BoxSet holding films added to a channel from a client.</summary>
    public static string ManagedCollectionName(string channelName) => "SashimiTV · " + channelName;

    /// <summary>A trimmed name, or null when it is empty or too long.</summary>
    public static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength ? null : trimmed;
    }

    /// <summary>
    /// A new channel: one all-day, every-day daypart named after it, shuffled,
    /// aligned to the half hour, pinned to a free number so it neither moves
    /// nor displaces an existing channel's number.
    /// </summary>
    public static ChannelDefinition CreateChannel(
        IReadOnlyList<ChannelDefinition> existing,
        string name,
        string? logo,
        string timeZoneId,
        DateTime nowUtc,
        ulong seed,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var channel = new ChannelDefinition
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Logo = string.IsNullOrWhiteSpace(logo) ? null : logo,
            TimeZoneId = timeZoneId,
            AnchorUtc = nowUtc,
            AlignToHalfHour = true,
            Dayparts =
            [
                new DaypartDefinition
                {
                    Name = name,
                    StartMinutes = 0,
                    EndMinutes = 1440,
                    Days = (int)ChannelDays.All,
                    Ordering = ChannelOrdering.SeededShuffle,
                    Seed = seed
                }
            ]
        };

        channel.Number = NextFreeNumber(existing, channel.Id);
        return channel;
    }

    /// <summary>
    /// The number a new channel should be pinned to: where its id would land
    /// among the numbers already on air, stepping past any that are taken.
    /// Every existing channel is held at the number it shows today, so adding
    /// one never moves another.
    /// </summary>
    public static int NextFreeNumber(IReadOnlyList<ChannelDefinition> existing, Guid newId)
    {
        var current = ChannelNumbering.Assign(existing.Select(c => (c.Id, c.Number)));
        var held = existing.Select(c => (c.Id, (int?)current[c.Id])).ToList();
        held.Add((newId, null));
        return ChannelNumbering.Assign(held)[newId];
    }

    /// <summary>
    /// Where an added title goes when the client does not say: the first
    /// all-day daypart that airs all year, else the first daypart.
    /// </summary>
    public static int DefaultDaypartIndex(ChannelDefinition channel)
    {
        for (var i = 0; i < channel.Dayparts.Count; i++)
        {
            var part = channel.Dayparts[i];
            if (part.StartMinutes <= 0 && part.EndMinutes >= 1440 && !IsSeasonal(part))
            {
                return i;
            }
        }

        return 0;
    }

    public static bool IsValidDaypartIndex(ChannelDefinition channel, int index) =>
        index >= 0 && index < channel.Dayparts.Count;

    public static bool IsSeasonal(DaypartDefinition part) =>
        part.SeasonStartMonthDay is not null || part.SeasonEndMonthDay is not null;

    /// <summary>More than one daypart, or any season — a client offers a daypart pick.</summary>
    public static bool IsScheduled(ChannelDefinition channel) =>
        channel.Dayparts.Count > 1 || channel.Dayparts.Any(IsSeasonal);

    /// <summary>Series explicitly added to a daypart, in order, without repeats.</summary>
    public static IReadOnlyList<Guid> AddedSeries(DaypartDefinition part) =>
        part.Blocks.Where(b => b.Kind == ContentBlockKind.Series)
            .SelectMany(b => b.ItemIds)
            .Distinct()
            .ToList();

    /// <summary>Whether the daypart airs the given collection through a Collection block.</summary>
    public static bool HasCollectionBlock(DaypartDefinition part, Guid collectionId) =>
        part.Blocks.Any(b => b.Kind == ContentBlockKind.Collection && b.ItemIds.Contains(collectionId));

    /// <summary>
    /// Add a series to a daypart's first Series block, creating the block when
    /// there is none. False when the series is already in one — adding twice
    /// is a no-op, not a duplicate.
    /// </summary>
    public static bool AddSeries(ChannelDefinition channel, int daypartIndex, Guid seriesId)
    {
        var part = channel.Dayparts[daypartIndex];
        if (AddedSeries(part).Contains(seriesId))
        {
            return false;
        }

        var block = part.Blocks.FirstOrDefault(b => b.Kind == ContentBlockKind.Series);
        if (block is null)
        {
            block = new ContentBlock { Kind = ContentBlockKind.Series };
            part.Blocks.Add(block);
        }

        block.ItemIds.Add(seriesId);
        return true;
    }

    /// <summary>
    /// Remove a series from the Series blocks of one daypart, or of every
    /// daypart when <paramref name="daypartIndex"/> is null. A block left empty
    /// stays: it is the user's structure, and the next add refills it.
    /// </summary>
    public static bool RemoveSeries(ChannelDefinition channel, int? daypartIndex, Guid seriesId)
    {
        IReadOnlyList<DaypartDefinition> parts = daypartIndex is { } i ? [channel.Dayparts[i]] : channel.Dayparts;
        var removed = false;
        foreach (var block in parts.SelectMany(p => p.Blocks).Where(b => b.Kind == ContentBlockKind.Series))
        {
            removed |= block.ItemIds.RemoveAll(id => id == seriesId) > 0;
        }

        return removed;
    }

    /// <summary>Make the daypart air the collection; false when it already does.</summary>
    public static bool EnsureCollectionBlock(ChannelDefinition channel, int daypartIndex, Guid collectionId)
    {
        var part = channel.Dayparts[daypartIndex];
        if (HasCollectionBlock(part, collectionId))
        {
            return false;
        }

        part.Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Collection, ItemIds = [collectionId] });
        return true;
    }

    /// <summary>
    /// Human text for what feeds a daypart besides its explicit additions —
    /// libraries, filters, collections other than the channel's own — e.g.
    /// "Science Fiction, Library: Movies". Null when nothing does.
    /// <paramref name="nameOf"/> resolves a library or collection id to its
    /// name, null when it no longer exists.
    /// </summary>
    public static string? RuleSummary(DaypartDefinition part, Guid? managedCollectionId, Func<Guid, string?> nameOf)
    {
        var parts = new List<string>();
        foreach (var block in part.Blocks)
        {
            switch (block.Kind)
            {
                case ContentBlockKind.Library:
                    parts.AddRange(block.ItemIds.Select(id => "Library: " + (nameOf(id) ?? "unknown")));
                    break;
                case ContentBlockKind.Collection:
                    parts.AddRange(block.ItemIds
                        .Where(id => id != managedCollectionId)
                        .Select(id => "Collection: " + (nameOf(id) ?? "unknown")));
                    break;
                case ContentBlockKind.Filter:
                    parts.Add(FilterSummary(block.Filter));
                    break;
            }
        }

        var distinct = parts.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0 ? null : string.Join(", ", distinct);
    }

    private static string FilterSummary(ContentFilter? filter)
    {
        if (filter is null)
        {
            return "Filter";
        }

        var bits = new List<string>();
        if (filter.Genres.Count > 0)
        {
            bits.Add(string.Join(" / ", filter.Genres));
        }

        if (filter.Tags.Count > 0)
        {
            bits.Add("tagged " + string.Join(" / ", filter.Tags));
        }

        if (filter.AddedWithinDays is { } days)
        {
            bits.Add($"added in the last {days} days");
        }

        return bits.Count == 0 ? "Filter" : string.Join(" ", bits);
    }
}

/// <summary>How a title relates to one daypart of a channel.</summary>
public enum MembershipState
{
    /// <summary>Not on the daypart.</summary>
    None = 0,

    /// <summary>Explicitly added — a Series block, or the channel's own collection — and so removable.</summary>
    Added = 1,

    /// <summary>On air because a library, filter or other collection pulls it in.</summary>
    ViaRule = 2
}

public static class ChannelMembership
{
    /// <summary>
    /// Classify a title against one daypart.
    /// </summary>
    /// <param name="part">The daypart.</param>
    /// <param name="subjectId">The series (for an episode or season, its series) or the film.</param>
    /// <param name="managedCollectionId">The channel's own collection, if it has one.</param>
    /// <param name="managedMembers">That collection's members.</param>
    /// <param name="playableIds">
    /// What would air for the subject: a series' episodes, or the film itself.
    /// </param>
    /// <param name="resolved">Every item in the daypart's current running order.</param>
    public static MembershipState Classify(
        DaypartDefinition part,
        Guid subjectId,
        Guid? managedCollectionId,
        IReadOnlySet<Guid> managedMembers,
        IEnumerable<Guid> playableIds,
        IReadOnlySet<Guid> resolved)
    {
        if (ChannelEditor.AddedSeries(part).Contains(subjectId))
        {
            return MembershipState.Added;
        }

        if (managedCollectionId is { } collection
            && managedMembers.Contains(subjectId)
            && ChannelEditor.HasCollectionBlock(part, collection))
        {
            return MembershipState.Added;
        }

        return playableIds.Any(resolved.Contains) ? MembershipState.ViaRule : MembershipState.None;
    }
}
