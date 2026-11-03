using CoaMcp.Core;

namespace CoaMcp.Core.Tests;

public sealed class LayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coamcp-" + Guid.NewGuid().ToString("N"));

    public LayerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Entity Make(string id, string type, string title, string body, params string[] realms) =>
        new(id, type, title, new RealmSet(realms, false), Trust.Derived, "unit-test",
            new DateOnly(2026, 11, 3), body, null, Array.Empty<Link>());

    private string Build(params Entity[] entities)
    {
        var path = Path.Combine(_dir, "layer.sqlite");
        using var w = new LayerWriter(path, "test", "1.0");
        foreach (var e in entities) w.Add(e);
        w.Finish();
        return path;
    }

    [Fact]
    public void Roundtrip_ReturnsEntity()
    {
        var e = new Entity("spell:1", "spell", "Fireball", new RealmSet(new[] { "coa", "s10" }, true),
            Trust.Client, "dbc", new DateOnly(2026, 1, 2), "Hurls a **fireball**.", "{\"rank\":1}",
            new[] { new Link("spell:2", "triggers") });
        var path = Build(e);

        using var r = new LayerReader(path);
        var got = r.Get("spell:1");

        Assert.NotNull(got);
        Assert.Equal(e.Title, got!.Title);
        Assert.Equal(e.Type, got.Type);
        Assert.Equal(e.Realm.Realms, got.Realm.Realms);
        Assert.True(got.Realm.Proven);
        Assert.Equal(Trust.Client, got.Trust);
        Assert.Equal(e.Source, got.Source);
        Assert.Equal(e.SourceDate, got.SourceDate);
        Assert.Equal(e.BodyMd, got.BodyMd);
        Assert.Equal(e.DataJson, got.DataJson);
        Assert.Equal(new Link("spell:2", "triggers"), Assert.Single(got.Links));
        Assert.Null(r.Get("missing"));
        Assert.Equal("test", r.Meta.Name);
        Assert.Equal("1.0", r.Meta.Version);
        Assert.Equal(new DateOnly(2026, 1, 2), r.Meta.SourceDate);
    }

    [Fact]
    public void Search_FindsByTitleAndBody()
    {
        var path = Build(
            Make("a", "spell", "Fireball", "Burns things", "coa"),
            Make("b", "npc", "Guard", "Throws a fireball at intruders", "coa"),
            Make("c", "npc", "Unrelated", "Nothing here", "coa"));

        using var r = new LayerReader(path);
        var hits = r.Search(FtsQuery.FromUser("fireball"), null, null, 10, 0);

        Assert.Equal(new[] { "a", "b" }, hits.Select(h => h.Id).OrderBy(x => x));
    }

    [Fact]
    public void Search_FiltersByRealmAndType()
    {
        var path = Build(
            Make("a", "spell", "Fireball", "x", "coa"),
            Make("b", "spell", "Fireball Classic", "x", "classic"),
            Make("c", "npc", "Fireball Imp", "x", "coa", "classic"));

        using var r = new LayerReader(path);
        var q = FtsQuery.FromUser("fireball");

        Assert.Equal(new[] { "a", "c" }, r.Search(q, null, "coa", 10, 0).Select(h => h.Id).OrderBy(x => x));
        Assert.Equal(new[] { "a" }, r.Search(q, "spell", "coa", 10, 0).Select(h => h.Id));
        Assert.Equal(new[] { "b" }, r.Search(q, "spell", "classic", 10, 0).Select(h => h.Id));
        var all = r.Search(q, null, null, 10, 0);
        Assert.Equal(3, all.Count);
        Assert.Equal(all[0].Id, Assert.Single(r.Search(q, null, null, 1, 0)).Id);
        Assert.Equal(all[1].Id, Assert.Single(r.Search(q, null, null, 1, 1)).Id);
        Assert.Equal(all[2].Id, Assert.Single(r.Search(q, null, null, 10, 2)).Id);
    }

    [Fact]
    public void Search_CoaFilterMatchesCoaClient()
    {
        var path = Build(
            Make("a", "spell", "Fireball", "x", "coa-client"),
            Make("b", "spell", "Fireball Classic", "x", "classic"));

        using var r = new LayerReader(path);
        var q = FtsQuery.FromUser("fireball");

        Assert.Equal(new[] { "a" }, r.Search(q, null, "coa", 10, 0).Select(h => h.Id));
        Assert.DoesNotContain(r.Search(q, null, "coa", 10, 0), h => h.Id == "b");
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("a*")]
    [InlineData("NEAR(a b)")]
    [InlineData("'; DROP TABLE x")]
    [InlineData("")]
    public void FromUser_NeutralizesFtsSyntax(string input)
    {
        var path = Build(Make("a", "spell", "a b", "NEAR a b", "coa"));

        using var r = new LayerReader(path);
        var hits = r.Search(FtsQuery.FromUser(input), null, null, 10, 0);

        Assert.NotNull(hits);
        Assert.NotNull(r.Get("a"));
    }

    [Fact]
    public void Open_UnfinishedBuild_ThrowsClearErrorAndReleasesFile()
    {
        var path = Path.Combine(_dir, "unfinished.sqlite");
        using (var w = new LayerWriter(path, "test", "1.0"))
            w.Add(Make("a", "spell", "Fireball", "x", "coa"));

        var ex = Assert.Throws<InvalidDataException>(() => new LayerReader(path));
        Assert.Contains("not a finished layer file", ex.Message);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Open_NonSqliteFile_ThrowsClearError()
    {
        var path = Path.Combine(_dir, "junk.sqlite");
        File.WriteAllText(path, "this is not a database, just some text padding to be long enough");
        var ex = Assert.Throws<InvalidDataException>(() => new LayerReader(path));
        Assert.Contains("not a layer file", ex.Message);
        File.Delete(path);
    }

    [Fact]
    public void Add_DuplicateId_ThrowsNamingTheId()
    {
        using var w = new LayerWriter(Path.Combine(_dir, "dup.sqlite"), "test", "1.0");
        w.Add(Make("spell:1", "spell", "A", "x", "coa"));
        var ex = Assert.Throws<InvalidOperationException>(() => w.Add(Make("spell:1", "spell", "B", "y", "coa")));
        Assert.Contains("spell:1", ex.Message);
    }

    [Fact]
    public void Label_RendersUnprovenAsOr() =>
        Assert.Equal("CoA, Classic or S10", new RealmSet(new[] { "coa", "classic", "s10" }, false).Label());

    [Fact]
    public void Label_RendersCoaClient() =>
        Assert.Equal("the CoA client", new RealmSet(new[] { "coa-client" }, true).Label());

    [Fact]
    public void Execute_And_Query_RoundtripCustomTable()
    {
        var path = Path.Combine(_dir, "custom.sqlite");
        using (var w = new LayerWriter(path, "test", "1.0"))
        {
            w.Execute("CREATE TABLE t(k TEXT, v INTEGER)");
            w.Execute("INSERT INTO t VALUES ($0, $1)", "x", 5L);
            w.Finish();
        }
        using var r = new LayerReader(path);
        var row = Assert.Single(r.Query("SELECT k, v FROM t WHERE k = $0", "x"));
        Assert.Equal("x", row["k"]);
        Assert.Equal(5L, row["v"]);
    }
}
