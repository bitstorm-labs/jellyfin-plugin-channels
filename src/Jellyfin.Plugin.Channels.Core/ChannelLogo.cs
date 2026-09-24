namespace Jellyfin.Plugin.Channels;

/// <summary>Which logo a channel wears on a given day.</summary>
public static class ChannelLogo
{
    /// <summary>
    /// The first daypart with its own logo whose season is open on the local
    /// date wins, else the channel's logo. Keyed on the date, not the time of
    /// day: a seasonal logo belongs to the season, so the December snowflake
    /// shows at breakfast too, not only while the Christmas films air.
    /// Dayparts without a season never override — a logo on one would
    /// otherwise replace the channel's all year.
    /// </summary>
    public static string? Resolve(ChannelDefinition channel, DateTime localDate)
    {
        foreach (var part in channel.Dayparts)
        {
            if (string.IsNullOrWhiteSpace(part.Logo))
            {
                continue;
            }

            SeasonWindow? season;
            try
            {
                season = SeasonWindow.From(part.SeasonStartMonthDay, part.SeasonEndMonthDay);
            }
            catch (ArgumentOutOfRangeException)
            {
                // A half-set season is dropped when the channel is built; it
                // cannot choose a logo either.
                continue;
            }

            if (season is not null && season.Covers(localDate))
            {
                return part.Logo;
            }
        }

        return string.IsNullOrWhiteSpace(channel.Logo) ? null : channel.Logo;
    }
}
