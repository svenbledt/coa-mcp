namespace CoaMcp.Server.Tests;

public class PathToolTests : IClassFixture<ReadFixture>
{
    private readonly ReadFixture _f;
    public PathToolTests(ReadFixture f) => _f = f;

    private static Dictionary<string, object?> A(params (string, object?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private Task<(string Text, bool IsError)> Paths(params (string, object?)[] args) => _f.Call("client_paths", A(args));

    [Fact]
    public async Task Tool_IsListedReadOnly_AndNeedsNoLogin()
    {
        var tools = await _f.Client.ListToolsAsync();
        var tool = tools.Single(t => t.Name == "client_paths");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        var (text, isError) = await Paths(("query", "spell"));   // the harness sends no credentials
        Assert.False(isError);
        Assert.Contains("Spell.dbc", text);
    }

    [Fact]
    public async Task ExactPath_ShowsWinnerSizeAndSource()
    {
        var (text, isError) = await Paths(("query", "dbfilesclient\\MAP.DBC"));   // backslashes and any case

        Assert.False(isError);
        Assert.Contains("DBFilesClient/Map.dbc", text);
        Assert.Contains("900 bytes", text);
        Assert.Contains("patch-S.MPQ", text);
        Assert.Contains("common.MPQ", text);   // the older copy is listed as a holder
        Assert.Contains("trust client", text);
        Assert.Contains("realm the CoA client", text);
        Assert.Contains("source CoA client file list", text);
        Assert.Contains("2026-11-03", text);
        Assert.Contains("paths only", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Substring_FindsCaseInsensitive_WithPathsSizesAndWinners()
    {
        var (text, _) = await Paths(("query", "SPELL_fire"));

        Assert.Contains("Interface/Icons/Spell_Fire_Fireball.blp", text);
        Assert.DoesNotContain("Frost", text);
    }

    [Fact]
    public async Task Substring_AllWordsMustMatch()
    {
        var (text, _) = await Paths(("query", "spell icons"));
        Assert.Contains("Spell_Fire_Fireball.blp", text);
        Assert.Contains("Spell_Frost_FrostBolt.blp", text);
        Assert.DoesNotContain("DBFilesClient", text);
    }

    [Fact]
    public async Task Prefix_Browses_DBFilesClient()
    {
        var (text, isError) = await Paths(("prefix", "dbfilesclient/"));

        Assert.False(isError);
        Assert.Contains("DBFilesClient/", text);
        Assert.Contains("Map.dbc", text);
        Assert.Contains("Spell.dbc", text);
        Assert.Contains("SpellVisual.dbc", text);
        Assert.DoesNotContain("Interface", text);
    }

    [Fact]
    public async Task Prefix_Root_ListsFoldersWithCounts_ThenRootFiles()
    {
        var (text, _) = await Paths(("prefix", "/"));

        Assert.Contains("folder DBFilesClient/ (3 files)", text);
        Assert.Contains("folder Interface/ (3 files, 1 removed)", text);
        Assert.Contains("readme.txt", text);
        Assert.DoesNotContain("Spell.dbc", text);   // children only, not a flat dump of the tree
    }

    [Fact]
    public async Task Prefix_NestedFolder_ShowsSubfolderAndFiles()
    {
        var (text, _) = await Paths(("prefix", "Interface/Icons"));

        Assert.Contains("folder Old/ (1 file, 1 removed)", text);
        Assert.Contains("Spell_Fire_Fireball.blp", text);
        Assert.DoesNotContain("Retired.blp", text);
    }

    [Fact]
    public async Task Prefix_PartialName_ListsMatchingPaths()
    {
        var (text, _) = await Paths(("prefix", "Interface/Icons/Spell_F"));
        Assert.Contains("Spell_Fire_Fireball.blp", text);
        Assert.Contains("Spell_Frost_FrostBolt.blp", text);
        Assert.DoesNotContain("Retired", text);
    }

    [Fact]
    public async Task Prefix_Paging_ContinuesAcrossFoldersAndFiles()
    {
        var (first, _) = await Paths(("prefix", "/"), ("limit", 2));
        var (second, _) = await Paths(("prefix", "/"), ("limit", 2), ("offset", 4));

        Assert.Contains("DBFilesClient/", first);
        Assert.Contains("Interface/", first);
        Assert.DoesNotContain("readme.txt", first);
        Assert.Contains("offset=2", first);
        Assert.Contains("readme.txt", second);
    }

    [Fact]
    public async Task Ext_FiltersFiles()
    {
        var (text, _) = await Paths(("ext", ".DBC"));
        Assert.Contains("Map.dbc", text);
        Assert.DoesNotContain(".blp", text);

        var (both, _) = await Paths(("query", "spell"), ("ext", "blp"), ("prefix", "Interface"));
        Assert.Contains("Spell_Fire_Fireball.blp", both);
        Assert.DoesNotContain("dbc", both);
    }

    [Fact]
    public async Task DeletedPath_SaysRemovedByPatchAndWhereOlderCopiesAre()
    {
        var (text, _) = await Paths(("query", "Interface/Icons/Old/Retired.blp"));

        Assert.Contains("removed by patch-X.MPQ", text);
        Assert.Contains("older copies exist in common.MPQ", text);
        Assert.DoesNotContain("120 bytes", text);   // the client never sees the older copy, so it is not the file's size
    }

    [Fact]
    public async Task DeletedPath_InAList_IsMarkedRemoved()
    {
        var (text, _) = await Paths(("query", "retired"));
        Assert.Contains("Retired.blp", text);
        Assert.Contains("removed by patch-X.MPQ", text);
    }

    [Fact]
    public async Task ProjectArchivePath_SaysProjectBuiltAndShowsBaseCopy()
    {
        var (text, _) = await Paths(("query", "Interface/Icons/Spell_Fire_Fireball.blp"));

        Assert.Contains("project-built archive", text);
        Assert.Contains("patch-T.MPQ", text);
        Assert.Contains("300 bytes", text);
        Assert.Contains("patch-3.MPQ", text);
        Assert.Contains("250 bytes", text);
        Assert.Contains("not evidence about Ascension", text);
    }

    [Fact]
    public async Task ProjectArchivePath_InAList_IsFlagged()
    {
        var (text, _) = await Paths(("query", "fireball"));
        Assert.Contains("project-built archive", text);
    }

    [Fact]
    public async Task RealmOnlyPath_SaysNotInTheDefaultClient()
    {
        var (text, _) = await Paths(("query", "Realm/Only.txt"));
        Assert.Contains("not in the default client", text);
        Assert.Contains("area-52", text);
    }

    [Fact]
    public async Task RealmArchive_NeverCountsAsTheWinner()
    {
        var (text, _) = await Paths(("query", "Sound/Music/Theme.mp3"));
        Assert.Contains("5,000 bytes", text);
        Assert.DoesNotContain("6,000 bytes ", text.Split('\n').First(l => l.Contains("winner", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("area-52 realm only", text);
    }

    [Fact]
    public async Task Limit_IsCapped_AndNoted()
    {
        var (text, isError) = await Paths(("prefix", "/"), ("limit", 5000));
        Assert.False(isError);
        Assert.Contains("limit 5000 capped at 100", text);
    }

    [Fact]
    public async Task Offset_IsCapped_DeeperForBrowsingThanForSearch()
    {
        var (search, _) = await Paths(("query", "spell"), ("offset", 50_000));
        Assert.Contains("offset 50000 capped at 10000", search);
        var (browse, _) = await Paths(("prefix", "/"), ("offset", 500_000));
        Assert.Contains("offset 500000 capped at 100000", browse);
    }

    [Fact]
    public async Task NoMatch_SaysSo()
    {
        var (text, isError) = await Paths(("query", "zzzznotapath"));
        Assert.False(isError);
        Assert.Contains("0 paths", text);
    }

    [Fact]
    public async Task NoArguments_IsACleanError()
    {
        var (text, isError) = await Paths();
        Assert.True(isError);
        Assert.Contains("query, prefix or ext", text);
    }

    [Theory]
    [InlineData("ab")]          // shorter than a trigram
    [InlineData("a b")]
    public async Task ShortQuery_IsACleanError(string query)
    {
        var (text, isError) = await Paths(("query", query));
        Assert.True(isError);
        Assert.Contains("3 characters", text);
    }

    [Fact]
    public async Task Paths_HostileInput_NoFault()
    {
        var hostile = new[]
        {
            "\"", "\"\"\"", "' OR 1=1 --", "x\" OR \"y", "NEAR(a b)", "a* b*", "-x", "title : abc", "abc AND", "(((", "\\", "////", "..", "../../etc/passwd",
            "a\0b\0c", "‮\u0000evil", new string('a', 5000), new string('/', 300), "%", "_%_", "[abc]", "\ud800abc", "{\"a\":1}", "abc'; DROP TABLE paths; --",
        };
        foreach (var h in hostile)
        {
            foreach (var arg in new[] { "query", "prefix", "ext" })
            {
                var (text, _) = await Paths((arg, h));
                AssertNoFault(text);
            }
        }
        AssertNoFault((await Paths(("query", "spell"), ("limit", long.MinValue), ("offset", long.MaxValue))).Text);
        AssertNoFault((await Paths(("prefix", "/"), ("limit", -5), ("offset", -9))).Text);
        AssertNoFault((await Paths(("ext", new string('x', 500)))).Text);
        // The table is still there afterwards.
        var (after, _) = await Paths(("query", "map.dbc"));
        Assert.Contains("Map.dbc", after);
    }

    [Fact]
    public async Task Response_StaysSmall()
    {
        var (text, _) = await Paths(("prefix", "/"), ("limit", 100));
        Assert.True(text.Length < 6_000, $"response was {text.Length} characters");
    }

    private static void AssertNoFault(string text)
    {
        Assert.DoesNotMatch(@"An error occurred invoking '\w+'\.", text);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("   at ", text);
        Assert.DoesNotContain("SQLite", text);
        Assert.DoesNotContain("fts5", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_ReportsUnnamedAndFailedCounts()
    {
        var (text, _) = await _f.Call("status");

        Assert.Contains("paths: v2", text);
        Assert.Contains("9 client paths", text);
        Assert.Contains("1 removed by a later patch", text);
        Assert.Contains("1 not in the default client", text);
        Assert.Contains("0 unnamed", text);
        Assert.Contains("0 failed", text);
        Assert.Contains("assume HD archives load", text);
        Assert.Contains("reconstructed client code", text);
    }

    [Fact]
    public async Task MissingPathsLayer_IsACleanError_AndServerStillWorks()
    {
        using var h = new ServerHarness(TestLayers.Build(withPaths: false));
        await using var client = await h.ConnectAsync();

        var (text, isError) = await ServerHarness.CallAsync(client, "client_paths", A(("query", "spell")));
        Assert.True(isError);
        Assert.Contains("paths layer is not loaded", text);
        var (status, _) = await ServerHarness.CallAsync(client, "status");
        Assert.Matches(@"paths: .*missing", status);
    }
}
