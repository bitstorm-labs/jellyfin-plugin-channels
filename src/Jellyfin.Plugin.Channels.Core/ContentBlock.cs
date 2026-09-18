namespace Jellyfin.Plugin.Channels;

public enum ContentBlockKind
{
    /// <summary>Everything in one or more libraries.</summary>
    Library = 0,

    /// <summary>Every episode of one or more series.</summary>
    Series = 1,

    /// <summary>The members of one or more collections.</summary>
    Collection = 2,

    /// <summary>Whatever matches a filter, wherever it lives.</summary>
    Filter = 3
}

/// <summary>
/// Optional narrowing applied to a block. Every populated field must match, so
/// genres and tags intersect rather than union — "Animation released since 2010"
/// is the useful reading, and a union is expressible as two blocks.
/// </summary>
public sealed class ContentFilter
{
    public List<string> Genres { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public int? MinYear { get; set; }

    public int? MaxYear { get; set; }

    public bool IncludeMovies { get; set; } = true;

    public bool IncludeEpisodes { get; set; } = true;
}

/// <summary>
/// One source feeding a daypart. A daypart holds a list of these, which is what
/// makes a channel an arbitrary pile of sources — three sitcoms, plus a library,
/// plus everything tagged Christmas — rather than a single source with options.
/// </summary>
public sealed class ContentBlock
{
    public ContentBlockKind Kind { get; set; } = ContentBlockKind.Series;

    /// <summary>Libraries, series or collections, depending on <see cref="Kind"/>.</summary>
    public List<Guid> ItemIds { get; set; } = [];

    /// <summary>Used when <see cref="Kind"/> is <see cref="ContentBlockKind.Filter"/>.</summary>
    public ContentFilter? Filter { get; set; }
}
