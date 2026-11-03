using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;

namespace CoaMcp.Server.Tests;

public sealed class ReadFixture : IAsyncLifetime
{
    public ServerHarness Harness { get; } = new();
    public McpClient Client { get; private set; } = null!;

    public async Task InitializeAsync() => Client = await Harness.ConnectAsync();

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
        Harness.Dispose();
    }

    public Task<(string Text, bool IsError)> Call(string tool, Dictionary<string, object?>? args = null) =>
        ServerHarness.CallAsync(Client, tool, args);
}

public class ReadToolTests : IClassFixture<ReadFixture>
{
    private readonly ReadFixture _f;
    public ReadToolTests(ReadFixture f) => _f = f;

    private static Dictionary<string, object?> A(params (string, object?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public async Task ListsOnlyTheSixReadTools()
    {
        var tools = await _f.Client.ListToolsAsync();
        Assert.Equal(new[] { "decode_flags", "get", "search", "spell_compare", "spell_scaling", "status" },
            tools.Select(t => t.Name).Order().ToArray());
    }

    [Fact]
    public async Task Get_RendersHeader()
    {
        var (text, isError) = await _f.Call("get", A(("id", "item:10531")));
        Assert.False(isError);
        Assert.Contains("item:10531", text);
        Assert.Contains("type: item", text);
        Assert.Contains("realm: CoA", text);
        Assert.Contains("proven", text);
        Assert.Contains("trust: derived", text);
        Assert.Contains("source: BisBeard", text);
        Assert.Contains("2026-08-27", text);
        Assert.Contains("bisbeard v7", text);
        Assert.Contains("Epic Libram Ranged", text);
    }

    [Fact]
    public async Task Get_SectionsLimitOutput()
    {
        var (text, _) = await _f.Call("get", A(("id", "spell:1"), ("sections", new[] { "effects" })));
        Assert.Contains("EFFECTSMARK", text);
        Assert.DoesNotContain("VISUALSMARK", text);
        Assert.DoesNotContain("Triggers no spells", text);
        Assert.Contains("spell:1", text);
    }

    [Fact]
    public async Task Get_UnknownSection_ListsAvailable()
    {
        var (text, _) = await _f.Call("get", A(("id", "spell:1"), ("sections", new[] { "nope" })));
        Assert.Contains("No section named 'nope'", text);
        Assert.Contains("summary", text);
        Assert.Contains("visuals", text);
    }

    [Fact]
    public async Task Get_LargePage_ReturnsSectionList()
    {
        var (text, _) = await _f.Call("get", A(("id", TestLayers.BigPageId)));
        Assert.True(text.Length < Limits.LargePageBytes, $"answer was {text.Length} chars");
        Assert.Contains("talents:coa:testclass", text);
        Assert.Contains("Alpha", text);
        Assert.Contains("Beta", text);
        Assert.Contains("Gamma", text);
        Assert.Contains("sections", text);
        Assert.DoesNotContain(new string('a', 100), text);

        var (one, _) = await _f.Call("get", A(("id", TestLayers.BigPageId), ("sections", new[] { "Gamma" })));
        Assert.Contains("short gamma text", one);
        Assert.DoesNotContain("bbbbbbbbbb", one);
    }

    [Fact]
    public async Task Get_HeadingInsideContent_DoesNotSplitWrongly()
    {
        var (real, _) = await _f.Call("get", A(("id", TestLayers.TrickyPageId), ("sections", new[] { "Real" })));
        // A fenced "## Fake" line, an indented one and a "###" line are content of "Real", not new sections.
        Assert.Contains("real text", real);
        Assert.Contains("## Fake fenced", real);
        Assert.Contains("fenced text", real);
        Assert.Contains("    ## Fake indented", real);
        Assert.Contains("### Sub heading", real);
        Assert.DoesNotContain("other text", real);

        var (fake, _) = await _f.Call("get", A(("id", TestLayers.TrickyPageId), ("sections", new[] { "Fake fenced" })));
        Assert.Contains("No section named 'Fake fenced'", fake);
        Assert.Contains("Real", fake);
        Assert.Contains("Other", fake);
        Assert.DoesNotContain("Fake indented'", fake.Replace("No section named", ""));
    }

    [Fact]
    public void PageSections_SplitsOnExactHeadingsOnly()
    {
        var (pre, sections) = PageSections.Split("intro\n## One\na\n## Two\nb\n```\n## Three\n```\n  ## Four\n");
        Assert.Equal("intro", pre);
        Assert.Equal(new[] { "One", "Two" }, sections.Select(s => s.Name).ToArray());
        Assert.Contains("## Three", sections[1].Text);
        Assert.Contains("  ## Four", sections[1].Text);
    }

    [Fact]
    public async Task Get_Unknown_SaysNotFoundWithSuggestions()
    {
        var (text, _) = await _f.Call("get", A(("id", "item:99999")));
        Assert.Contains("No entity with id 'item:99999'", text);

        var (near, _) = await _f.Call("get", A(("id", "Libram")));
        Assert.Contains("No entity with id 'Libram'", near);
        Assert.Contains("item:10531", near);
    }

    [Fact]
    public async Task Search_ShowsRealmTrustAndIdOnEveryHit()
    {
        var (text, isError) = await _f.Call("search", A(("query", "libram")));
        Assert.False(isError);
        var hits = text.Split('\n').Where(l => Regex.IsMatch(l, @"^\d+\. ")).ToList();
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Matches(@"\[(CoA|wotlk)[^|\]]*\| derived \| (item:[^\]]+)\]", h));
        Assert.Contains(hits, h => h.Contains("wotlk") && h.Contains("item:wotlk:10531"));
        // Every hit also names its source and the source's age.
        Assert.All(hits, h => Assert.Contains("src: BisBeard, 2026-08-27", h));

        var (spells, _) = await _f.Call("search", A(("query", "blast")));
        Assert.Matches(@"\[the CoA client[^|\]]*\| client \| spell:1\]", spells);
        Assert.Contains("src: CoA client DBC (via Spellwork), 2026-08-27", spells);
    }

    [Fact]
    public async Task Search_ExactTitleSpellFirst()
    {
        var (text, _) = await _f.Call("search", A(("query", "FIREBALL")));
        var hits = text.Split('\n').Where(l => Regex.IsMatch(l, @"^\d+\. ")).ToList();
        Assert.Contains("spell:133", hits[0]);
        // Next come items (type priority), not the markers that happen to rank well on their own.
        Assert.Contains("item:", hits[1]);

        var (all, _) = await _f.Call("search", A(("query", "fireball"), ("limit", 100)));
        Assert.DoesNotContain("marker:", all);
    }

    [Fact]
    public async Task Search_CollapsesIdenticalMarkers()
    {
        var (text, _) = await _f.Call("search", A(("query", "mystic scroll")));
        var hits = text.Split('\n').Where(l => Regex.IsMatch(l, @"^\d+\. ")).ToList();
        Assert.Equal(2, hits.Count);
        var improved = Assert.Single(hits, h => h.Contains("Improved Fireball"));
        Assert.Contains("4 markers share this title", improved);
        foreach (var zone in new[] { "Ashenvale", "Azshara", "Feralas", "Silithus" }) Assert.Contains(zone, improved);
        Assert.Single(hits, h => h.Contains("Glyph of Fireball") && !h.Contains("share this title"));
    }

    [Fact]
    public async Task Search_CapsAt100()
    {
        var (text, _) = await _f.Call("search", A(("query", "fireball"), ("limit", 5000)));
        Assert.Equal(100, text.Split('\n').Count(l => Regex.IsMatch(l, @"^\d+\. ")));
        Assert.Contains("limit 5000 capped at 100", text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Search_NegativeOrZeroLimit_IsRaisedNotUnlimited(int limit)
    {
        var (text, _) = await _f.Call("search", A(("query", "fireball"), ("limit", limit), ("offset", -7)));
        Assert.Equal(1, text.Split('\n').Count(l => Regex.IsMatch(l, @"^\d+\. ")));
        Assert.Contains($"limit {limit} raised to 1", text);
        Assert.Contains("offset -7 raised to 0", text);
    }

    [Fact]
    public async Task Search_Paging_OffsetSkipsHits()
    {
        var (first, _) = await _f.Call("search", A(("query", "rod"), ("limit", 3)));
        var (second, _) = await _f.Call("search", A(("query", "rod"), ("limit", 3), ("offset", 3)));
        var ids1 = Regex.Matches(first, @"item:\d+").Select(m => m.Value).ToList();
        var ids2 = Regex.Matches(second, @"item:\d+").Select(m => m.Value).ToList();
        Assert.Equal(3, ids1.Count);
        Assert.Equal(3, ids2.Count);
        Assert.Empty(ids1.Intersect(ids2));
    }

    [Theory]
    [InlineData("classes", "class:coa:barbarian")]
    [InlineData("spells", "spell:1")]
    [InlineData("items", "item:10531")]
    public async Task Search_TopicFiltersByEntityType(string topic, string expectedId)
    {
        var query = topic switch { "classes" => "barbarian", "spells" => "blast", _ => "libram" };
        var (text, _) = await _f.Call("search", A(("query", query), ("topic", topic)));
        Assert.Contains(expectedId, text);
        if (topic == "classes") Assert.Contains("rules:coa:races", text);
        if (topic != "classes") Assert.DoesNotContain("class:coa:barbarian", text);
    }

    [Theory]
    [InlineData("dungeons")]
    [InlineData("npcs")]
    public async Task Search_TopicWithoutDataYet_IsEmptyNotAnError(string topic)
    {
        var (text, isError) = await _f.Call("search", A(("query", "libram"), ("topic", topic)));
        Assert.False(isError);
        Assert.Contains("0 hits", text);
        Assert.Contains("no data", text);
    }

    [Fact]
    public async Task Search_UnknownTopic_ListsValidOnes()
    {
        var (text, isError) = await _f.Call("search", A(("query", "x"), ("topic", "bogus")));
        Assert.True(isError);
        Assert.Contains("Unknown topic 'bogus'", text);
        foreach (var t in new[] { "spells", "items", "classes", "dungeons", "npcs" }) Assert.Contains(t, text);
    }

    [Fact]
    public async Task Search_RealmFilter_CoaAlsoMatchesClientRealm()
    {
        var (text, _) = await _f.Call("search", A(("query", "blast"), ("realm", "coa")));
        Assert.Contains("spell:1", text);
        var (wotlk, _) = await _f.Call("search", A(("query", "libram"), ("realm", "wotlk")));
        Assert.Contains("item:wotlk:10531", wotlk);
        Assert.DoesNotContain("item:10531 ", wotlk + " ");
    }

    public static IEnumerable<object[]> Hostile() => new[]
    {
        "", "   ", "\"", "\"\"\"", "\" OR 1=1 --", "'; DROP TABLE entities; --", "NEAR(", "fire*", "a AND", "AND", "OR NOT",
        "{title}: x", "title:fire", "-fire", "(", ")", "^", "\0", "fire\0ball", "🔥", "‮", "%", "_", "*", "\\", "../../etc/passwd",
        "fire ball NOT", "\n\r\t", new string('a', 100_000), string.Join(" ", Enumerable.Repeat("fireball", 5000)),
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task Search_HostileInput_NoFault(string query)
    {
        var (text, _) = await _f.Call("search", A(("query", query)));
        AssertNoFault(text);
        foreach (var topic in new[] { "spells", "classes" })
            AssertNoFault((await _f.Call("search", A(("query", query), ("topic", topic), ("realm", query)))).Text);
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task Get_HostileInput_NoFault(string id)
    {
        AssertNoFault((await _f.Call("get", A(("id", id)))).Text);
        AssertNoFault((await _f.Call("get", A(("id", "spell:1"), ("sections", new[] { id })))).Text);
    }

    [Fact]
    public async Task HostileNumbers_NoFault()
    {
        foreach (var n in new long[] { long.MinValue, int.MinValue, -1, 0, int.MaxValue, long.MaxValue })
        {
            AssertNoFault((await _f.Call("search", A(("query", "fireball"), ("limit", n), ("offset", n)))).Text);
            AssertNoFault((await _f.Call("spell_scaling", A(("id", "spell:10"), ("level", n), ("itemLevel", n)))).Text);
        }
        AssertNoFault((await _f.Call("get", A(("id", "spell:1"), ("sections", Enumerable.Repeat("x", 500).ToArray())))).Text);
        AssertNoFault((await _f.Call("decode_flags", A(("kind", "SpellSchoolMask"), ("value", "99999999999999999999999")))).Text);
        AssertNoFault((await _f.Call("spell_compare", A(("idA", "spell:1"), ("idB", new string('9', 5000))))).Text);
    }

    private static void AssertNoFault(string text)
    {
        // A tool fault shows as the SDK's generic message with no reason; a refused input names its reason after a colon.
        Assert.DoesNotMatch(@"An error occurred invoking '\w+'\.", text);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("   at ", text);
        Assert.DoesNotContain("SQLite", text);
        Assert.DoesNotContain("fts5", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_ListsEachLayerVersionAndKnownGaps()
    {
        var (text, _) = await _f.Call("status");
        Assert.Contains("bisbeard", text);
        Assert.Contains("v7", text);
        Assert.Contains("spells", text);
        Assert.Contains("v3", text);
        Assert.Contains("2026-08-27", text);
        Assert.Contains("Known gaps: dungeon behavior and NPC positions have no data until observations arrive; WotLK, Octo, Classic and S10 have BisBeard data only.", text);
    }

    [Fact]
    public async Task Status_MissingLayer_IsReportedAndServerStillWorks()
    {
        using var h = new ServerHarness(TestLayers.Build(withSpells: false));
        await using var client = await h.ConnectAsync();
        var (text, _) = await ServerHarness.CallAsync(client, "status");
        Assert.Matches(@"spells: .*missing", text);
        Assert.Contains("bisbeard", text);
        var (hit, _) = await ServerHarness.CallAsync(client, "get", A(("id", "item:10531")));
        Assert.Contains("Epic Libram", hit);
        var (spell, _) = await ServerHarness.CallAsync(client, "get", A(("id", "spell:1")));
        Assert.Contains("No entity with id 'spell:1'", spell);
        Assert.DoesNotContain(h.LayerDir, text);
    }

    [Fact]
    public async Task Status_CorruptLayer_IsReportedAndOthersStillWork()
    {
        var dir = TestLayers.Build();
        SqliteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(dir, "spells.sqlite"), "this is not a database");
        using var h = new ServerHarness(dir);
        await using var client = await h.ConnectAsync();
        var (text, _) = await ServerHarness.CallAsync(client, "status");
        Assert.Matches(@"spells: .*could not be opened", text);
        Assert.DoesNotContain(dir, text);
    }

    [Fact]
    public async Task DecodeFlags_KnownKind_ReturnsNames()
    {
        var (text, isError) = await _f.Call("decode_flags", A(("kind", "SpellSchoolMask"), ("value", "6")));
        Assert.False(isError);
        Assert.Contains("SPELL_SCHOOL_MASK_HOLY", text);
        Assert.Contains("SPELL_SCHOOL_MASK_FIRE", text);
        Assert.DoesNotContain("SPELL_SCHOOL_MASK_NORMAL", text);
        Assert.Contains("trust client", text);
        Assert.Contains("source CoA client DBC (via Spellwork)", text);
        Assert.Contains("source date 2026-08-27", text);

        var (hex, _) = await _f.Call("decode_flags", A(("kind", "spellschoolmask"), ("value", "0x13")));
        Assert.Contains("SPELL_SCHOOL_MASK_NORMAL", hex);
        Assert.Contains("unmatched", hex);
        Assert.Contains("0x10", hex);
    }

    [Fact]
    public async Task DecodeFlags_UnknownKind_ListsKinds()
    {
        var (text, isError) = await _f.Call("decode_flags", A(("kind", "Nope"), ("value", "1")));
        Assert.True(isError);
        Assert.Contains("Unknown flag kind 'Nope'", text);
        Assert.Contains("SpellSchoolMask", text);

        var (bad, badError) = await _f.Call("decode_flags", A(("kind", "SpellSchoolMask"), ("value", "-3")));
        Assert.True(badError);
        Assert.Contains("value", bad);
    }

    [Fact]
    public async Task SpellCompare_ReturnsOnlyDifferingFields()
    {
        var (text, isError) = await _f.Call("spell_compare", A(("idA", "spell:1"), ("idB", "2")));
        Assert.False(isError);
        Assert.Contains("effects[0].basePoints", text);
        Assert.Contains("10", text);
        Assert.Contains("25", text);
        Assert.Contains("name", text);
        Assert.Contains("extra", text);
        Assert.Contains("(absent)", text);
        Assert.DoesNotContain("schoolMask", text);
        Assert.DoesNotContain("speed", text);
    }

    [Fact]
    public async Task SpellCompare_UnknownSpell_SaysNotFound()
    {
        var (text, isError) = await _f.Call("spell_compare", A(("idA", "spell:1"), ("idB", "spell:424242")));
        Assert.True(isError);
        Assert.Contains("spell:424242", text);
    }
}
