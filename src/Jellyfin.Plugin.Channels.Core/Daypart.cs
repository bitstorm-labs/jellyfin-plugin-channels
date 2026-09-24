namespace Jellyfin.Plugin.Channels;

/// <summary>Days a daypart airs on. Combine with <c>|</c>.</summary>
[Flags]
public enum ChannelDays
{
    None = 0,
    Sunday = 1 << 0,
    Monday = 1 << 1,
    Tuesday = 1 << 2,
    Wednesday = 1 << 3,
    Thursday = 1 << 4,
    Friday = 1 << 5,
    Saturday = 1 << 6,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend
}

/// <summary>
/// A block of the broadcast day with its own content — cartoons in the morning,
/// drama at night. A channel is a list of these; one daypart covering the whole
/// day is the ordinary single-schedule channel.
///
/// Each daypart owns an independent running order and runs on its own continuous
/// timeline, exactly like a channel does. That is what makes dayparting cheap:
/// selecting a daypart picks which timeline you are watching, and the existing
/// resolver does the rest untouched. It also matches the behaviour the feature
/// already has — a timeline advances whether or not anyone is tuned in, and you
/// join whatever is airing rather than resuming what you missed.
/// </summary>
public sealed class Daypart
{
    /// <param name="startLocal">Start time of day, in the channel's timezone.</param>
    /// <param name="endLocal">
    /// End time of day, exclusive. Must be after <paramref name="startLocal"/>:
    /// a block spanning midnight is expressed as two dayparts (22:00-24:00 and
    /// 00:00-02:00). Allowing a single wrapping window makes "which day does
    /// this airing belong to" ambiguous at exactly the boundaries the schedule
    /// has to get right, so it is rejected rather than guessed at.
    /// </param>
    public Daypart(
        string name,
        TimeSpan startLocal,
        TimeSpan endLocal,
        ChannelSnapshot snapshot,
        ChannelDays days = ChannelDays.All,
        SeasonWindow? season = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (startLocal < TimeSpan.Zero || startLocal >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(startLocal), startLocal, "Start must fall within a day.");
        }

        if (endLocal <= startLocal || endLocal > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(endLocal), endLocal, "End must be after start and no later than midnight.");
        }

        Name = name;
        StartLocal = startLocal;
        EndLocal = endLocal;
        Snapshot = snapshot;
        Days = days;
        Season = season;
    }

    public string Name { get; }

    public TimeSpan StartLocal { get; }

    public TimeSpan EndLocal { get; }

    public ChannelDays Days { get; }

    /// <summary>
    /// The part of the year this daypart airs in, or <c>null</c> for all year.
    /// Outside its window the daypart is simply not on air, which is the same
    /// state as a gap in the broadcast day — so a channel whose every daypart is
    /// out of season is off air, and needs no separate notion of a dormant
    /// channel.
    /// </summary>
    public SeasonWindow? Season { get; }

    public ChannelSnapshot Snapshot { get; }

    /// <summary>Whether this daypart is on air at the given local instant.</summary>
    public bool Covers(DateTime localTime)
    {
        if (Season is not null && !Season.Covers(localTime))
        {
            return false;
        }

        if (!Airs(localTime.DayOfWeek))
        {
            return false;
        }

        var timeOfDay = localTime.TimeOfDay;
        return timeOfDay >= StartLocal && timeOfDay < EndLocal;
    }

    public bool Airs(DayOfWeek day) => (Days & ToFlag(day)) != 0;

    internal static ChannelDays ToFlag(DayOfWeek day) => (ChannelDays)(1 << (int)day);
}
