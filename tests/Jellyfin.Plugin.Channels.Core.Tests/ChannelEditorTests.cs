using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.Channels;

namespace Jellyfin.Plugin.Channels.Tests;

public class ChannelEditorTests
{
    private static Guid Id(int n) => new(n, 3, 5, new byte[8]);

    private static DaypartDefinition Part(int start = 0, int end = 1440, int? seasonStart = null, int? seasonEnd = null) =>
        new() { Name = "p", StartMinutes = start, EndMinutes = end, SeasonStartMonthDay = seasonStart, SeasonEndMonthDay = seasonEnd };

    private static ChannelDefinition Channel(params DaypartDefinition[] parts) =>
        new() { Id = Id(100), Name = "Test", Dayparts = parts.ToList() };

    // ---- create -----------------------------------------------------------

    [Fact]
    public void a_new_channel_is_one_all_day_shuffled_daypart_named_after_it()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var channel = ChannelEditor.CreateChannel([], "Cozy", "nebula", "America/Edmonton", now, 42);

        Assert.Equal("Cozy", channel.Name);
        Assert.Equal("nebula", channel.Logo);
        Assert.Equal("America/Edmonton", channel.TimeZoneId);
        Assert.Equal(now, channel.AnchorUtc);
        Assert.True(channel.AlignToHalfHour);
        Assert.Null(channel.ManagedCollectionId);
        var part = Assert.Single(channel.Dayparts);
        Assert.Equal("Cozy", part.Name);
        Assert.Equal(0, part.StartMinutes);
        Assert.Equal(1440, part.EndMinutes);
        Assert.Equal((int)ChannelDays.All, part.Days);
        Assert.Equal(ChannelOrdering.SeededShuffle, part.Ordering);
        Assert.Equal(42UL, part.Seed);
        Assert.Empty(part.Blocks);
        Assert.False(ChannelEditor.IsScheduled(channel));
    }

    [Fact]
    public void a_blank_logo_is_no_logo()
    {
        var channel = ChannelEditor.CreateChannel([], "Cozy", " ", "Etc/UTC", DateTime.UtcNow, 1);
        Assert.Null(channel.Logo);
    }

    [Fact]
    public void a_new_channel_is_pinned_to_a_free_number_and_moves_no_existing_channel()
    {
        var existing = Enumerable.Range(1, 6)
            .Select(n => new ChannelDefinition { Id = Id(n), Name = "c" + n, Number = n == 2 ? 7 : null })
            .ToList();
        var before = ChannelNumbering.Assign(existing.Select(c => (c.Id, c.Number)));

        // Try many ids so some land on a number already shown.
        for (var k = 0; k < 200; k++)
        {
            var channel = ChannelEditor.CreateChannel(existing, "New", null, "Etc/UTC", DateTime.UtcNow, 1, Guid.NewGuid());

            Assert.NotNull(channel.Number);
            Assert.DoesNotContain(channel.Number!.Value, before.Values);

            var after = ChannelNumbering.Assign(existing.Append(channel).Select(c => (c.Id, c.Number)));
            Assert.Equal(channel.Number.Value, after[channel.Id]);
            foreach (var c in existing)
            {
                Assert.Equal(before[c.Id], after[c.Id]);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void an_empty_name_is_refused(string? name) => Assert.Null(ChannelEditor.NormalizeName(name));

    [Fact]
    public void a_name_is_trimmed_and_capped()
    {
        Assert.Equal("Cozy", ChannelEditor.NormalizeName("  Cozy "));
        Assert.Null(ChannelEditor.NormalizeName(new string('x', ChannelEditor.MaxNameLength + 1)));
        Assert.NotNull(ChannelEditor.NormalizeName(new string('x', ChannelEditor.MaxNameLength)));
    }

    [Fact]
    public void the_managed_collection_is_named_for_the_channel() =>
        Assert.Equal("SashimiTV · Cozy", ChannelEditor.ManagedCollectionName("Cozy"));

    // ---- daypart selection -----------------------------------------------

    [Fact]
    public void the_default_daypart_is_the_first_all_day_one_that_airs_all_year()
    {
        var channel = Channel(
            Part(360, 720),
            Part(0, 1440, 1201, 1231),
            Part(0, 1440),
            Part(0, 1440));

        Assert.Equal(2, ChannelEditor.DefaultDaypartIndex(channel));
    }

    [Fact]
    public void with_no_all_day_daypart_the_default_is_the_first()
    {
        var channel = Channel(Part(360, 720), Part(720, 1440));
        Assert.Equal(0, ChannelEditor.DefaultDaypartIndex(channel));
    }

    [Fact]
    public void scheduled_means_several_dayparts_or_any_season()
    {
        Assert.False(ChannelEditor.IsScheduled(Channel(Part())));
        Assert.True(ChannelEditor.IsScheduled(Channel(Part(0, 720), Part(720, 1440))));
        Assert.True(ChannelEditor.IsScheduled(Channel(Part(0, 1440, 1201, 1231))));
    }

    [Fact]
    public void daypart_index_is_validated()
    {
        var channel = Channel(Part(), Part());
        Assert.True(ChannelEditor.IsValidDaypartIndex(channel, 1));
        Assert.False(ChannelEditor.IsValidDaypartIndex(channel, 2));
        Assert.False(ChannelEditor.IsValidDaypartIndex(channel, -1));
    }

    // ---- series ------------------------------------------------------------

    [Fact]
    public void adding_a_series_creates_a_series_block_when_there_is_none()
    {
        var channel = Channel(Part());
        channel.Dayparts[0].Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Library, ItemIds = [Id(9)] });

        Assert.True(ChannelEditor.AddSeries(channel, 0, Id(1)));

        var block = Assert.Single(channel.Dayparts[0].Blocks, b => b.Kind == ContentBlockKind.Series);
        Assert.Equal([Id(1)], block.ItemIds);
    }

    [Fact]
    public void adding_a_series_appends_to_the_first_series_block()
    {
        var channel = Channel(Part());
        var first = new ContentBlock { Kind = ContentBlockKind.Series, ItemIds = [Id(1)] };
        var second = new ContentBlock { Kind = ContentBlockKind.Series, ItemIds = [Id(2)] };
        channel.Dayparts[0].Blocks.AddRange([first, second]);

        ChannelEditor.AddSeries(channel, 0, Id(3));

        Assert.Equal([Id(1), Id(3)], first.ItemIds);
        Assert.Equal([Id(2)], second.ItemIds);
    }

    [Fact]
    public void adding_a_series_twice_is_a_no_op()
    {
        var channel = Channel(Part());
        var block = new ContentBlock { Kind = ContentBlockKind.Series, ItemIds = [Id(1)] };
        channel.Dayparts[0].Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Series, ItemIds = [] });
        channel.Dayparts[0].Blocks.Add(block);

        // Already present in the second block: still a no-op.
        Assert.False(ChannelEditor.AddSeries(channel, 0, Id(1)));
        Assert.True(ChannelEditor.AddSeries(channel, 0, Id(2)));
        Assert.False(ChannelEditor.AddSeries(channel, 0, Id(2)));

        Assert.Equal(2, channel.Dayparts[0].Blocks.SelectMany(b => b.ItemIds).Count());
    }

    [Fact]
    public void adding_goes_to_the_chosen_daypart_only()
    {
        var channel = Channel(Part(0, 720), Part(720, 1440));
        ChannelEditor.AddSeries(channel, 1, Id(1));

        Assert.Empty(channel.Dayparts[0].Blocks);
        Assert.Equal([Id(1)], ChannelEditor.AddedSeries(channel.Dayparts[1]));
    }

    [Fact]
    public void removing_a_series_leaves_its_block_empty()
    {
        var channel = Channel(Part());
        ChannelEditor.AddSeries(channel, 0, Id(1));

        Assert.True(ChannelEditor.RemoveSeries(channel, 0, Id(1)));

        var block = Assert.Single(channel.Dayparts[0].Blocks);
        Assert.Empty(block.ItemIds);
        Assert.False(ChannelEditor.RemoveSeries(channel, 0, Id(1)));
    }

    [Fact]
    public void removing_without_a_daypart_removes_it_everywhere_and_with_one_only_there()
    {
        var channel = Channel(Part(0, 720), Part(720, 1440));
        ChannelEditor.AddSeries(channel, 0, Id(1));
        ChannelEditor.AddSeries(channel, 1, Id(1));

        Assert.True(ChannelEditor.RemoveSeries(channel, 1, Id(1)));
        Assert.Equal([Id(1)], ChannelEditor.AddedSeries(channel.Dayparts[0]));
        Assert.Empty(ChannelEditor.AddedSeries(channel.Dayparts[1]));

        ChannelEditor.AddSeries(channel, 1, Id(1));
        Assert.True(ChannelEditor.RemoveSeries(channel, null, Id(1)));
        Assert.All(channel.Dayparts, p => Assert.Empty(ChannelEditor.AddedSeries(p)));
    }

    // ---- collection --------------------------------------------------------

    [Fact]
    public void the_collection_block_is_added_once()
    {
        var channel = Channel(Part());
        Assert.True(ChannelEditor.EnsureCollectionBlock(channel, 0, Id(50)));
        Assert.False(ChannelEditor.EnsureCollectionBlock(channel, 0, Id(50)));

        var block = Assert.Single(channel.Dayparts[0].Blocks);
        Assert.Equal(ContentBlockKind.Collection, block.Kind);
        Assert.Equal([Id(50)], block.ItemIds);
    }

    [Fact]
    public void a_collection_block_the_user_already_made_is_reused()
    {
        var channel = Channel(Part());
        channel.Dayparts[0].Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Collection, ItemIds = [Id(7), Id(50)] });

        Assert.False(ChannelEditor.EnsureCollectionBlock(channel, 0, Id(50)));
        Assert.Single(channel.Dayparts[0].Blocks);
    }

    // ---- summaries -------------------------------------------------------

    [Fact]
    public void the_rule_summary_names_libraries_filters_and_other_collections_but_not_the_channels_own()
    {
        var part = Part();
        part.Blocks.AddRange(
        [
            new ContentBlock { Kind = ContentBlockKind.Series, ItemIds = [Id(1)] },
            new ContentBlock { Kind = ContentBlockKind.Filter, Filter = new ContentFilter { Genres = ["Science Fiction"] } },
            new ContentBlock { Kind = ContentBlockKind.Library, ItemIds = [Id(2)] },
            new ContentBlock { Kind = ContentBlockKind.Collection, ItemIds = [Id(3), Id(50)] }
        ]);
        var names = new Dictionary<Guid, string> { [Id(2)] = "Movies", [Id(3)] = "Cozy Autumn" };

        var summary = ChannelEditor.RuleSummary(part, Id(50), id => names.GetValueOrDefault(id));

        Assert.Equal("Science Fiction, Library: Movies, Collection: Cozy Autumn", summary);
    }

    [Fact]
    public void a_daypart_fed_only_by_additions_has_no_rule_summary()
    {
        var part = Part();
        part.Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Series, ItemIds = [Id(1)] });
        part.Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Collection, ItemIds = [Id(50)] });

        Assert.Null(ChannelEditor.RuleSummary(part, Id(50), _ => "x"));
    }

    // ---- membership ------------------------------------------------------

    [Fact]
    public void a_series_in_a_series_block_is_added()
    {
        var part = Part();
        part.Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Series, ItemIds = [Id(1)] });

        var state = ChannelMembership.Classify(part, Id(1), null, new HashSet<Guid>(), [Id(11)], new HashSet<Guid> { Id(11) });

        Assert.Equal(MembershipState.Added, state);
    }

    [Fact]
    public void a_series_whose_episodes_air_through_a_rule_is_via_rule()
    {
        var part = Part();
        part.Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Filter, Filter = new ContentFilter() });

        var resolved = new HashSet<Guid> { Id(12), Id(99) };
        Assert.Equal(MembershipState.ViaRule,
            ChannelMembership.Classify(part, Id(1), null, new HashSet<Guid>(), [Id(11), Id(12)], resolved));
        Assert.Equal(MembershipState.None,
            ChannelMembership.Classify(part, Id(2), null, new HashSet<Guid>(), [Id(21)], resolved));
    }

    [Fact]
    public void a_film_in_the_managed_collection_is_added_only_where_the_collection_airs()
    {
        var members = new HashSet<Guid> { Id(5) };
        var airing = Part();
        airing.Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Collection, ItemIds = [Id(50)] });
        var other = Part();

        Assert.Equal(MembershipState.Added,
            ChannelMembership.Classify(airing, Id(5), Id(50), members, [Id(5)], new HashSet<Guid> { Id(5) }));
        Assert.Equal(MembershipState.None,
            ChannelMembership.Classify(other, Id(5), Id(50), members, [Id(5)], new HashSet<Guid>()));
    }

    [Fact]
    public void a_film_pulled_in_by_another_collection_is_via_rule()
    {
        var part = Part();
        part.Blocks.Add(new ContentBlock { Kind = ContentBlockKind.Collection, ItemIds = [Id(7)] });

        var state = ChannelMembership.Classify(part, Id(5), Id(50), new HashSet<Guid>(), [Id(5)], new HashSet<Guid> { Id(5) });

        Assert.Equal(MembershipState.ViaRule, state);
    }

    // ---- persistence ----------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void the_managed_collection_id_survives_xml_and_json(bool set)
    {
        var channel = ChannelEditor.CreateChannel([], "Cozy", null, "Etc/UTC", DateTime.UtcNow, 7);
        channel.ManagedCollectionId = set ? Id(50) : null;
        ChannelEditor.EnsureCollectionBlock(channel, 0, Id(50));

        var serializer = new XmlSerializer(typeof(ChannelDefinition));
        using var writer = new StringWriter();
        serializer.Serialize(writer, channel);
        using var reader = new StringReader(writer.ToString());
        var fromXml = (ChannelDefinition)serializer.Deserialize(reader)!;

        var fromJson = JsonSerializer.Deserialize<ChannelDefinition>(JsonSerializer.Serialize(channel))!;

        foreach (var copy in new[] { fromXml, fromJson })
        {
            Assert.Equal(channel.ManagedCollectionId, copy.ManagedCollectionId);
            Assert.Equal(channel.Number, copy.Number);
            Assert.True(ChannelEditor.HasCollectionBlock(copy.Dayparts[0], Id(50)));
        }
    }
}
