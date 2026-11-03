using CoaMcp.Core;
using CoaMcp.Pipeline.Bisbeard;

namespace CoaMcp.Pipeline.Tests;

public sealed class BisbeardTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coamcp-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _out;

    public BisbeardTests()
    {
        Directory.CreateDirectory(_dir);
        _data = Path.Combine(_dir, "data");
        _out = Path.Combine(_dir, "bisbeard.sqlite");
        CopyDir(Path.Combine(Fixtures, "data"), _data);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private LayerReader Import()
    {
        BisbeardImport.Run(_data, Path.Combine(Fixtures, "guides.json"), _out);
        return new LayerReader(_out);
    }

    [Fact]
    public void Item_MainList_RealmIsUnprovenCoaClassicS10()
    {
        using var r = Import();
        var e = r.Get("item:10531");

        Assert.NotNull(e);
        Assert.Equal("item", e.Type);
        Assert.Equal("Stoneclad Libram", e.Title);
        Assert.Equal(Trust.Derived, e.Trust);
        Assert.Equal(new[] { "coa", "classic", "s10" }, e.Realm.Realms);
        Assert.False(e.Realm.Proven);
        Assert.Equal(new DateOnly(2026, 8, 27), e.SourceDate);
        Assert.Contains("stamina", e.BodyMd, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9", e.BodyMd);
        Assert.Contains("Molten Core - Baron Geddon", e.BodyMd);
        Assert.Contains("BisBeard (structured from Conquest of AzerothCore project data)", e.BodyMd);
        Assert.Contains("\"id\":\"10531\"", e.DataJson);
    }

    [Fact]
    public void Item_WotlkList_RealmIsProvenWotlk()
    {
        using var r = Import();
        var e = r.Get("item:wotlk:100000");

        Assert.NotNull(e);
        Assert.Equal("Sword Master's Bulwark", e.Title);
        Assert.Equal(new[] { "wotlk" }, e.Realm.Realms);
        Assert.True(e.Realm.Proven);
        Assert.Equal(new DateOnly(2026, 7, 16), e.SourceDate);
    }

    [Fact]
    public void Item_OctoList_RealmIsProvenOcto()
    {
        using var r = Import();
        var e = r.Get("item:octo:100000");

        Assert.NotNull(e);
        Assert.Equal(new[] { "octo" }, e.Realm.Realms);
        Assert.True(e.Realm.Proven);
        Assert.Equal(new DateOnly(2026, 8, 10), e.SourceDate);
    }

    [Fact]
    public void Item_SameIdInSeveralLists_DoesNotCollide()
    {
        using var r = Import();

        Assert.Equal("Main List Blade", r.Get("item:100000")!.Title);
        Assert.Equal(4, Convert.ToInt32(r.Query("SELECT COUNT(*) AS n FROM entities WHERE type='item'")[0]["n"]));
    }

    [Fact]
    public void Marker_BodyLabelsApproximate()
    {
        using var r = Import();
        var e = r.Get("marker:worldforge-alterac-mountains-turtle-shell");

        Assert.NotNull(e);
        Assert.Equal("marker", e.Type);
        Assert.Equal("Turtle Shell", e.Title);
        Assert.Contains("approximate", e.BodyMd, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("80.8%", e.BodyMd);
        Assert.Contains("51%", e.BodyMd);
        Assert.Contains("map", e.BodyMd, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Convert.ToInt32(r.Query("SELECT COUNT(*) AS n FROM entities WHERE type='marker'")[0]["n"]));
    }

    [Fact]
    public void Guide_ExcludedInConfig_IsNotImported()
    {
        using var r = Import();

        Assert.Null(r.Get("guide:best-vpn-online-games-world-of-warcraft"));
        var kept = r.Get("guide:arcane-mage-guide");
        Assert.NotNull(kept);
        Assert.False(kept.Realm.Proven);
        Assert.Contains("phase-driven caster", kept.BodyMd);
    }

    [Fact]
    public void Guide_NotInConfig_FailsTheBuild()
    {
        File.WriteAllText(Path.Combine(_data, "guides_text", "brand-new-guide.txt"), "Fresh page.");

        var ex = Assert.Throws<InvalidOperationException>(() => BisbeardImport.Run(_data, Path.Combine(Fixtures, "guides.json"), _out));

        Assert.Contains("brand-new-guide", ex.Message);
    }

    [Fact]
    public void RealmData_DropsFunctionPlaceholders()
    {
        using var r = Import();

        var rows = r.Query("SELECT COUNT(*) AS n FROM entities WHERE body_md LIKE '%[function%' OR data_json LIKE '%[function%' OR title LIKE '%[function%'");
        Assert.Equal(0, Convert.ToInt32(rows[0]["n"]));

        var spec = r.Get("spec:coa:barbarian-headhunting");
        Assert.NotNull(spec);
        Assert.Contains("1.473", spec.BodyMd);
        Assert.Contains("defaultWeights", spec.DataJson);
    }

    [Fact]
    public void RealmData_ClassAndSpecIdsIncludeRealm()
    {
        using var r = Import();

        var cls = r.Get("class:coa:barbarian");
        Assert.NotNull(cls);
        Assert.Equal("Barbarian", cls.Title);
        Assert.True(cls.Realm.Proven);
        Assert.Equal(new[] { "coa" }, cls.Realm.Realms);
        Assert.NotNull(r.Get("class:classic:barbarian"));
        Assert.Equal(new[] { "classic" }, r.Get("spec:classic:barbarian-headhunting")!.Realm.Realms);
        Assert.Contains(cls.Links, l => l.ToId == "spec:coa:barbarian-brutality");
        Assert.Contains(r.Get("spec:coa:barbarian-brutality")!.Links, l => l.ToId == "class:coa:barbarian");
    }

    [Fact]
    public void RealmData_SpecWithoutWeights_IsStillImportedAndSaysSo()
    {
        using var r = Import();

        var spec = r.Get("spec:octo:warrior-fury");
        Assert.NotNull(spec);
        Assert.Contains("no default stat weights", spec.BodyMd, StringComparison.OrdinalIgnoreCase);
    }
}
