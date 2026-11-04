using CoaMcp.Core;
using ModelContextProtocol.Client;

namespace CoaMcp.Server.Tests;

public sealed class NotesFixture : IAsyncLifetime
{
    public ServerHarness Harness { get; private set; } = null!;
    public McpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dir = TestLayers.Build();
        using (var w = new LayerWriter(Path.Combine(dir, "notes.sqlite"), "notes", "1"))
        {
            w.Add(new Entity("note:dbc-patch", "note", "Client DBC patch notes", new RealmSet(new[] { "coa-client" }, false), Trust.Derived,
                "CoA64 project notes", new DateOnly(2026, 11, 3), "How the client patches fireball tables.\n\n## Index insert\nGrowing an index drops the old maximum.\n", null, Array.Empty<Link>()));
            w.Add(new Entity("project:home-about", "project", "Project site: about", new RealmSet(new[] { "coa-development" }, true), Trust.Derived,
                "coa-development.org public page / (the emulator project, not Ascension)", new DateOnly(2026, 11, 4),
                "This page describes the Conquest of AzerothCore emulator project. It isn't evidence about Ascension.\n\nA fireball bug-test realm.\n", null, Array.Empty<Link>()));
            w.Finish();
        }
        using (var w = new LayerWriter(Path.Combine(dir, "cache.sqlite"), "cache", "1"))
        {
            w.Add(new Entity("cache-creature:test:76139", "cache-creature", "Fireball Imp", new RealmSet(Array.Empty<string>(), false), Trust.Cache,
                "Test player cache, creaturecache.wdb", new DateOnly(2026, 9, 3), "Creature type: 7\nFamily: 0\nRank: 1\nDisplay ids: 1234\n", null, Array.Empty<Link>()));
            w.Finish();
        }
        Harness = new ServerHarness(dir);
        Client = await Harness.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
        Harness.Dispose();
    }
}

public class NotesTopicTests : IClassFixture<NotesFixture>
{
    private readonly NotesFixture _f;
    public NotesTopicTests(NotesFixture f) => _f = f;

    private Task<(string Text, bool IsError)> Call(string tool, params (string, object?)[] args) =>
        ServerHarness.CallAsync(_f.Client, tool, args.ToDictionary(a => a.Item1, a => a.Item2));

    [Fact]
    public async Task Search_NotesTopic_FindsNotesAndProjectPages_NotCreatures()
    {
        var (text, isError) = await Call("search", ("query", "fireball"), ("topic", "notes"));
        Assert.False(isError);
        Assert.Contains("note:dbc-patch", text);
        Assert.Contains("project:home-about", text);
        Assert.DoesNotContain("cache-creature:", text);
        Assert.DoesNotContain("item:", text);
    }

    [Fact]
    public async Task Search_NpcsTopic_FindsCachedCreatures()
    {
        var (text, isError) = await Call("search", ("query", "fireball"), ("topic", "npcs"));
        Assert.False(isError);
        Assert.Contains("cache-creature:test:76139", text);
        Assert.Contains("unknown | cache | cache-creature:test:76139", text);   // realm unknown, trust cache
        Assert.DoesNotContain("note:dbc-patch", text);
    }

    [Fact]
    public async Task Search_ProjectHit_ShowsNotAscensionInSource()
    {
        var (text, _) = await Call("search", ("query", "bug-test realm"), ("topic", "notes"));
        Assert.Contains("not Ascension", text);
    }

    [Fact]
    public async Task Get_Note_SectionsWork_AndSaysDerived()
    {
        var (text, isError) = await Call("get", ("id", "note:dbc-patch"), ("sections", new[] { "Index insert" }));
        Assert.False(isError);
        Assert.Contains("trust: derived", text);
        Assert.Contains("source: CoA64 project notes", text);
        Assert.Contains("Growing an index drops the old maximum.", text);
    }

    [Fact]
    public async Task Get_ProjectPage_SaysNotEvidence()
    {
        var (text, _) = await Call("get", ("id", "project:home-about"));
        Assert.Contains("It isn't evidence about Ascension.", text);
    }

    [Fact]
    public async Task Get_CachedCreature_ShowsCacheTrustAndSource()
    {
        var (text, _) = await Call("get", ("id", "cache-creature:test:76139"));
        Assert.Contains("trust: cache", text);
        Assert.Contains("source: Test player cache, creaturecache.wdb", text);
        Assert.Contains("source date: 2026-09-03", text);
    }

    [Fact]
    public async Task Status_ListsNotesAndCacheLayers()
    {
        var (text, _) = await Call("status");
        Assert.Contains("- notes:", text);
        Assert.Contains("- cache:", text);
    }

    [Fact]
    public async Task Search_Reads_NeedNoLogin_AndNoWriteToolExists()
    {
        var tools = await _f.Client.ListToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name.Contains("write", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("import", StringComparison.OrdinalIgnoreCase));
    }
}
