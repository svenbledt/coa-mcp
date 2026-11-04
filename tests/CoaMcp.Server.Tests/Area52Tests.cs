using CoaMcp.Core;

namespace CoaMcp.Server.Tests;

public sealed class Area52Fixture : IAsyncLifetime
{
    public ServerHarness Harness { get; private set; } = null!;
    public ModelContextProtocol.Client.McpClient Mcp { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dir = TestLayers.Build();
        Build(Path.Combine(dir, "dbc.sqlite"));
        Harness = new ServerHarness(dir);
        Mcp = await Harness.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await Mcp.DisposeAsync();
        Harness.Dispose();
    }

    private static void Build(string path)
    {
        using var w = new LayerWriter(path, "dbc", "9");
        var client = new ClientRelease(null, "published 2026-10-30", null, new[] { new ClientArchiveMismatch("area-52/patch-D.MPQ", 1000000, 2000000, null) }, "2026-10-30-01");
        var b = new DbcLayerBuilder(w, client);
        var columns = new[] { new DbcColumn(0, "ID", DbcTypes.Int, false, true, true, null), new DbcColumn(1, "Speed", DbcTypes.Int, false, false, false, null) };
        var coa = new RealmSet(new[] { "coa-client" }, false);

        using (var ins = b.BeginTable("Spell", columns)) ins.Add(new object?[] { 133L, 24L });
        b.AddTable(new DbcTableInfo("Spell", 1, 2, 2, "patch-T.MPQ", "ascension", null, "ok", null, "test", "ID", true, "2026-10-24"), columns, coa, "Spell__area52");
        using (var ins = b.BeginTable("Spell__area52", columns)) ins.Add(new object?[] { 133L, 999L });
        b.AddTable(new DbcTableInfo("Spell__area52", 1, 2, 2, "area-52/patch-D.MPQ", "unknown", null, "ok", null, "test", "ID", true, "2026-10-24"), columns, DbcLayerBuilder.Area52Realms);
        using (var ins = b.BeginTable("Map", columns)) ins.Add(new object?[] { 1L, 2L });
        b.AddTable(new DbcTableInfo("Map", 1, 2, 2, "patch-M.MPQ", "unknown", null, "ok", null, "test", "ID", true, "2026-10-24"), columns, coa);
        b.Finish(new Dictionary<string, string> { ["tables"] = "2", ["rows"] = "2", ["area52_tables"] = "1", ["area52_rows"] = "1" });
    }
}

public class Area52Tests(Area52Fixture f) : IClassFixture<Area52Fixture>
{
    private const string Separate = "Area 52 is a separate realm from CoA (realm ids 11 and 13); the client only loads this archive there.";

    private Task<(string Text, bool IsError)> Call(string tool, params (string, object?)[] args) =>
        ServerHarness.CallAsync(f.Mcp, tool, args.ToDictionary(a => a.Item1, a => a.Item2));

    [Fact]
    public async Task Row_ThroughGet_IsLabeledAndHasArea52Values()
    {
        var (text, isError) = await Call("get", ("id", "dbc:Spell__area52:133"));
        Assert.False(isError);
        Assert.Contains("Speed: 999", text);
        Assert.Contains(Separate, text);
        Assert.Contains("realm: area-52 (unproven)", text);
        Assert.Contains("trust: client", text);
        Assert.Contains("area-52/patch-D.MPQ", text);
        Assert.DoesNotContain("the CoA client", text);
        Assert.DoesNotContain("Winning copy", text);
        Assert.Contains("doesn't match the launcher's entry", text);
    }

    [Fact]
    public async Task Rows_ThroughDbcRows_AreLabeledAndHaveArea52Values()
    {
        var (text, isError) = await Call("dbc_rows", ("table", "Spell__area52"));
        Assert.False(isError);
        Assert.Contains("Speed=999", text);
        Assert.Contains(Separate, text);
        Assert.Contains("realm area-52 (unproven)", text);
        Assert.Contains("area-52/patch-D.MPQ", text);
    }

    [Fact]
    public async Task TablePage_IsLabeled()
    {
        var (text, _) = await Call("get", ("id", "dbc:Spell__area52"));
        Assert.Contains(Separate, text);
        Assert.Contains("realm: area-52 (unproven)", text);
    }

    [Fact]
    public async Task DefaultRow_NeverUsesArea52Data_AndMentionsTheVersionInOneLine()
    {
        var (row, _) = await Call("get", ("id", "dbc:Spell:133"));
        Assert.Contains("Speed: 24", row);
        Assert.DoesNotContain("999", row);
        Assert.DoesNotContain("doesn't match the launcher's entry", row);
        Assert.Contains("Area 52 has its own version of this table: Spell__area52.", row);
        Assert.DoesNotContain(Separate, row);

        var (rows, _) = await Call("dbc_rows", ("table", "Spell"));
        Assert.Contains("Speed=24", rows);
        Assert.DoesNotContain("999", rows);
        Assert.Contains("Area 52 has its own version of this table: Spell__area52.", rows);
    }

    [Fact]
    public async Task TableWithoutAnArea52Copy_SaysNothingAboutIt()
    {
        var (row, _) = await Call("get", ("id", "dbc:Map:1"));
        Assert.DoesNotContain("Area 52", row);
        var (rows, _) = await Call("dbc_rows", ("table", "Map"));
        Assert.DoesNotContain("Area 52", rows);
    }

    [Fact]
    public async Task DbcTables_ListsThemLastAndLabeled()
    {
        var (text, _) = await Call("dbc_tables");
        var lines = text.Split('\n').Where(l => l.Contains(" rows | ")).ToList();
        Assert.Equal(new[] { "Map", "Spell", "Spell__area52" }, lines.Select(l => l.Split(' ')[0]));
        Assert.Contains("realm area-52", lines[2]);
        Assert.DoesNotContain("area-52 realm", lines[1]);
        Assert.Contains(Separate, text);

        var (none, _) = await Call("dbc_tables", ("filter", "Map"));
        Assert.DoesNotContain("Area 52", none);
    }

    [Fact]
    public async Task Search_FiltersByRealm()
    {
        var (coa, _) = await Call("search", ("query", "Spell area52"), ("topic", "dbc"), ("realm", "coa"));
        Assert.DoesNotContain("dbc:Spell__area52", coa);
        var (area52, _) = await Call("search", ("query", "Spell area52"), ("topic", "dbc"), ("realm", "area-52"));
        Assert.Contains("[area-52 | client | dbc:Spell__area52]", area52);
    }

    [Fact]
    public async Task Status_SaysThereAreNoArea52SpellPages()
    {
        var (text, _) = await Call("status");
        Assert.Contains("plus 1 Area 52 tables", text);
        Assert.Contains("Known gap (Area 52): no spell pages in the spells layer; read its DBC rows as <table>__area52.", text);
    }

    [Fact]
    public async Task HostileNames_GetACleanUnknownTable()
    {
        foreach (var name in new[] { "Spell__area52\"; DROP TABLE x;--", "__area52", "Ghost__area52" })
        {
            var (text, isError) = await Call("dbc_rows", ("table", name));
            Assert.True(isError);
            Assert.Contains("Unknown DBC table", text);
        }
    }
}
