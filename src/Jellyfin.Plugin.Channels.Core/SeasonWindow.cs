namespace Jellyfin.Plugin.Channels;

/// <summary>
/// The part of the year a daypart airs in — the thing that makes a channel
/// seasonal rather than merely themed.
///
/// A window is a pair of month/day points and carries no year, because the
/// point is that it recurs: "October" has to mean October every year, not
/// October 2026. Dates are held as MMDD integers for the same reason the
/// daypart's times are held as minutes — plugin configuration round-trips
/// through Jellyfin's JSON API and is persisted with XmlSerializer, and
/// primitives are what survive both intact.
///
/// Both ends are inclusive, and a window whose end falls before its start wraps
/// the year end, so 1215-0105 is the fortnight around Christmas rather than an
/// error. That wrap is the whole reason this is a type and not two loose ints.
/// </summary>
public sealed class SeasonWindow
{
    /// <summary>A leap year, so 29 February validates as a real recurring date.</summary>
    private const int ValidationYear = 2024;

    public SeasonWindow(int startMonthDay, int endMonthDay)
    {
        Validate(startMonthDay, nameof(startMonthDay));
        Validate(endMonthDay, nameof(endMonthDay));

        StartMonthDay = startMonthDay;
        EndMonthDay = endMonthDay;
    }

    /// <summary>Inclusive first day of the window, as MMDD.</summary>
    public int StartMonthDay { get; }

    /// <summary>Inclusive last day of the window, as MMDD.</summary>
    public int EndMonthDay { get; }

    /// <summary>True when the window runs through the turn of the year.</summary>
    public bool WrapsYearEnd => EndMonthDay < StartMonthDay;

    /// <summary>
    /// Build a window from configuration, or <c>null</c> for "airs all year".
    /// One end set without the other is rejected rather than guessed at: a
    /// half-configured window that silently means "always" is how a Christmas
    /// channel ends up playing in July with nothing in the log to say why.
    /// </summary>
    public static SeasonWindow? From(int? startMonthDay, int? endMonthDay)
    {
        if (startMonthDay is null && endMonthDay is null)
        {
            return null;
        }

        if (startMonthDay is null || endMonthDay is null)
        {
            throw new ArgumentOutOfRangeException(
                startMonthDay is null ? nameof(startMonthDay) : nameof(endMonthDay),
                "A season needs both a start and an end, or neither.");
        }

        return new SeasonWindow(startMonthDay.Value, endMonthDay.Value);
    }

    /// <summary>Whether the window is open on the given local date.</summary>
    public bool Covers(DateTime localDate)
    {
        var today = (localDate.Month * 100) + localDate.Day;

        return WrapsYearEnd
            ? today >= StartMonthDay || today <= EndMonthDay
            : today >= StartMonthDay && today <= EndMonthDay;
    }

    private static void Validate(int monthDay, string name)
    {
        var month = monthDay / 100;
        var day = monthDay % 100;

        if (month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(ValidationYear, month))
        {
            throw new ArgumentOutOfRangeException(
                name, monthDay, "A season boundary must be a real date written as MMDD, such as 1031.");
        }
    }
}
