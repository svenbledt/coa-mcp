using CoaMcp.Core;

namespace CoaMcp.Server.Tests;

/// <summary>An archive with origin ascension is Ascension's by the project's records only. No answer may read as if its copy had been checked against live.</summary>
public sealed class AscensionOriginFixture : IAsyncLifetime
{
    public const string Words = "Ascension's archive per project records; this copy may lag live";
    public static readonly ClientRelease Client = new(null, "published 2026-10-30", null, Launcher: "2026-10-30-01");
    public ServerHarness Harness { get; private set; } = null!;
    public ModelContextProtocol.Client.McpClient Mcp { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dir = TestLayers.Build(withSpells: false, withPaths: false);
        var spellSource = $"CoA client DBC (via Spellwork), {Client.Claim}, Spell.dbc from patch-T.MPQ ({Words})";
        TestLayers.BuildSpells(Path.Combine(dir, "spells.sqlite"), spellSource, Client);
        BuildDbc(Path.Combine(dir, "dbc.sqlite"));
        BuildPaths(Path.Combine(dir, "paths.sqlite"));
        Harness = new ServerHarness(dir);
        Mcp = await Harness.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await Mcp.DisposeAsync();
        Harness.Dispose();
    }

    private static void BuildDbc(string path)
    {
        using var w = new LayerWriter(path, "dbc", "9");
        var b = new DbcLayerBuilder(w, Client);
        var columns = new[] { new DbcColumn(0, "ID", DbcTypes.Int, false, true, true, null), new DbcColumn(1, "Speed", DbcTypes.Int, false, false, false, null) };
        using (var ins = b.BeginTable("Spell", columns)) ins.Add(new object?[] { 133L, 24L });
        b.AddTable(new DbcTableInfo("Spell", 1, 2, 2, "patch-T.MPQ", "ascension", null, "ok", null, "test", "ID", true, "2026-10-24"), columns, new RealmSet(new[] { "coa-client" }, false));
        b.Finish(new Dictionary<string, string> { ["tables"] = "1", ["rows"] = "1" });
    }

    private static void BuildPaths(string path)
    {
        using var w = new LayerWriter(path, "paths", "9");
        w.SetClient(Client);
        var b = new PathsLayerBuilder(w);
        b.Add(new PathRecord("DBFilesClient/Spell.dbc", 900, "patch-T.MPQ", "ascension", false, null, null, "[{\"a\":\"patch-T.MPQ\",\"o\":\"ascension\",\"s\":900}]"));
        b.Finish(new Dictionary<string, string> { ["client_date"] = "2026-10-24" }, new DateOnly(2026, 10, 24));
    }
}

public class AscensionOriginTests(AscensionOriginFixture f) : IClassFixture<AscensionOriginFixture>
{
    private const string Words = AscensionOriginFixture.Words;

    private Task<(string Text, bool IsError)> Call(string tool, params (string, object?)[] args) =>
        ServerHarness.CallAsync(f.Mcp, tool, args.ToDictionary(a => a.Item1, a => a.Item2));

    private static void NeverLiveVerified(string text)
    {
        Assert.Contains(Words, text);
        Assert.DoesNotContain("origin ascension", text);
        Assert.DoesNotContain("(ascension)", text);
    }

    [Fact] public async Task SpellPage_SaysIt() => NeverLiveVerified((await Call("get", ("id", "spell:1"))).Text);
    [Fact] public async Task SpellHelper_SaysIt() => NeverLiveVerified((await Call("spell_compare", ("idA", "1"), ("idB", "1"))).Text);
    [Fact] public async Task DbcTablePage_SaysIt() => NeverLiveVerified((await Call("get", ("id", "dbc:Spell"))).Text);
    [Fact] public async Task DbcRowPage_SaysIt() => NeverLiveVerified((await Call("get", ("id", "dbc:Spell:133"))).Text);
    [Fact] public async Task DbcRows_SaysIt() => NeverLiveVerified((await Call("dbc_rows", ("table", "Spell"))).Text);
    [Fact] public async Task DbcTables_SaysIt() => NeverLiveVerified((await Call("dbc_tables", ("filter", "Spell"))).Text);
    [Fact] public async Task ClientPathFile_SaysIt() => NeverLiveVerified((await Call("client_paths", ("prefix", "DBFilesClient/Spell.dbc"))).Text);
    [Fact] public async Task ClientPathList_SaysIt() => NeverLiveVerified((await Call("client_paths", ("prefix", "DBFilesClient/"))).Text);

    [Fact]
    public async Task SearchHit_DoesNotCutTheWording()
    {
        var (text, _) = await Call("search", ("query", "Fire Blast"), ("topic", "spells"));
        Assert.Contains($"Spell.dbc from patch-T.MPQ ({Words})", text);
    }
}
