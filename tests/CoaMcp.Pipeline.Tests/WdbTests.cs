using System.Text;
using System.Text.Json;
using CoaMcp.Core;
using CoaMcp.Pipeline.Publish;
using CoaMcp.Pipeline.Wdb;

namespace CoaMcp.Pipeline.Tests;

/// <summary>Writes WDB files in the standard 3.3.5 layout (build 12340).</summary>
internal static class WdbFixture
{
    public static byte[] File(string magic, IEnumerable<(uint Entry, byte[] Data)> records, uint build = 12340, string locale = "enUS")
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes(Rev(magic)));
        w.Write(build);
        w.Write(Encoding.ASCII.GetBytes(Rev(locale)));
        w.Write(0x88u);   // record size
        w.Write(1u);      // record version
        w.Write(18u);     // cache version
        foreach (var (entry, data) in records)
        {
            w.Write(entry);
            w.Write((uint)data.Length);
            w.Write(data);
        }
        w.Write(0u);
        w.Write(0u);
        return ms.ToArray();
    }

    private static string Rev(string s) => new(s.Reverse().ToArray());

    public static byte[] Creature(string name, string subname, uint type, uint family, uint rank, uint[] display, string name2 = "")
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void S(string s) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); }
        S(name); S(name2); S(""); S(""); S(subname); S("Interact");
        w.Write(0x20u);                    // type flags
        w.Write(type); w.Write(family); w.Write(rank);
        w.Write(0u); w.Write(0u);          // kill credit
        for (var i = 0; i < 4; i++) w.Write(i < display.Length ? display[i] : 0u);
        w.Write(1.5f); w.Write(2.5f);      // health and mana modifiers
        w.Write((byte)1);                  // racial leader
        for (var i = 0; i < 6; i++) w.Write(0u);   // quest items
        w.Write(7u);                       // movement id
        return ms.ToArray();
    }
}

public class WdbTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string Folder(string name, params (string File, byte[] Bytes)[] files)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(path);
        foreach (var (f, b) in files) System.IO.File.WriteAllBytes(Path.Combine(path, f), b);
        return path;
    }

    private string Config(params (string Folder, string Server, string[]? Realms)[] sources)
    {
        var path = _dir.File($"wdb-{Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            sources = sources.Select(s => new { folder = s.Folder, server = s.Server, realms = s.Realms }).ToArray(),
        }));
        return path;
    }

    private static (uint, byte[]) Rec(uint entry, byte[] data) => (entry, data);

    private static byte[] TwoCreatures() => WdbFixture.File("WMOB", new[]
    {
        Rec(76139, WdbFixture.Creature("Book of Fireballs", "Reader", 7, 0, 1, new uint[] { 1234, 0, 0, 0 })),
        Rec(76140, WdbFixture.Creature("Thornfang", "", 1, 3, 2, new uint[] { 55, 56, 0, 0 }, name2: "Thornfangs")),
    });

    [Fact]
    public void Wdb_ParsesCreatureFixture()
    {
        var folder = Folder("a", ("creaturecache.wdb", TwoCreatures()));
        var layer = _dir.File("cache.sqlite");
        var r = WdbImporter.Run(Config((folder, "Test Realm", new[] { "coa" })), layer, null, "t1");
        Assert.Equal(2, r.Entities);
        Assert.True(r.Written);
        using var reader = new LayerReader(layer);
        var e = reader.Get("cache-creature:test-realm:76139")!;
        Assert.Equal("cache-creature", e.Type);
        Assert.Equal("Book of Fireballs", e.Title);
        Assert.Contains("Reader", e.BodyMd);
        Assert.Contains("Creature type: 7", e.BodyMd);
        Assert.Contains("Family: 0", e.BodyMd);
        Assert.Contains("Rank: 1", e.BodyMd);
        Assert.Contains("Display ids: 1234", e.BodyMd);
        using var data = JsonDocument.Parse(e.DataJson!);
        Assert.Equal(76139, data.RootElement.GetProperty("entry").GetInt32());
        Assert.Equal(7, data.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(1234, data.RootElement.GetProperty("displayIds")[0].GetInt32());
        var other = reader.Get("cache-creature:test-realm:76140")!;
        Assert.Contains("Display ids: 55, 56", other.BodyMd);
    }

    [Fact]
    public void Wdb_TrustIsCache_NamesServer()
    {
        var folder = Folder("a", ("creaturecache.wdb", TwoCreatures()));
        var modified = new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(folder, "creaturecache.wdb"), modified);
        var layer = _dir.File("cache.sqlite");
        WdbImporter.Run(Config((folder, "Pale Reach Test", new[] { "coa" })), layer, null, "t1");
        using var reader = new LayerReader(layer);
        var e = reader.Get("cache-creature:pale-reach-test:76139")!;
        Assert.Equal(Trust.Cache, e.Trust);
        Assert.Contains("Pale Reach Test", e.Source);
        Assert.Contains("creaturecache.wdb", e.Source);
        Assert.Equal(new DateOnly(2026, 9, 3), e.SourceDate);
        Assert.Contains("file modified time", e.BodyMd);
        Assert.Equal(new[] { "coa" }, e.Realm.Realms);
        Assert.False(e.Realm.Proven);
        Assert.Equal("cache", reader.Meta.Name);
    }

    [Fact]
    public void Wdb_NoRealmInConfig_StaysUnknown()
    {
        var folder = Folder("a", ("creaturecache.wdb", TwoCreatures()));
        var layer = _dir.File("cache.sqlite");
        WdbImporter.Run(Config((folder, "S", null)), layer, null, "t1");
        using var reader = new LayerReader(layer);
        var e = reader.Get("cache-creature:s:76139")!;
        Assert.Empty(e.Realm.Realms);
        Assert.False(e.Realm.Proven);
    }

    [Fact]
    public void Wdb_FolderNotListed_Skipped()
    {
        var listed = Folder("scan/listed", ("creaturecache.wdb", TwoCreatures()));
        var unlisted = Folder("scan/Pale Reach", ("creaturecache.wdb", TwoCreatures()));
        var layer = _dir.File("cache.sqlite");
        var r = WdbImporter.Run(Config((listed, "Listed", null)), layer, Path.Combine(_dir.Path, "scan"), "t1");
        Assert.Equal(new[] { Path.GetFullPath(unlisted) }, r.SkippedFolders);
        using var reader = new LayerReader(layer);
        Assert.Null(reader.Get("cache-creature:pale-reach:76139"));
        Assert.All(reader.Query("SELECT source FROM entities"), row => Assert.Contains("Listed", (string)row["source"]!));
    }

    [Fact]
    public void Wdb_EmptyConfig_WritesNothing()
    {
        var layer = _dir.File("cache.sqlite");
        var r = WdbImporter.Run(Config(), layer, null, "t1");
        Assert.False(r.Written);
        Assert.Equal(0, r.Entities);
        Assert.False(System.IO.File.Exists(layer));
        Assert.Contains("nothing", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wdb_ListedFolderMissing_FailsBuild()
    {
        var ex = Assert.Throws<InvalidDataException>(() => WdbImporter.Run(Config((Path.Combine(_dir.Path, "nope"), "S", null)), _dir.File("cache.sqlite"), null, "t1"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Wdb_UnknownVersion_FailsWithName()
    {
        var bytes = WdbFixture.File("WMOB", new[] { Rec(1, WdbFixture.Creature("A", "", 1, 0, 0, new uint[] { 1 })) }, build: 18414);
        var folder = Folder("a", ("creaturecache.wdb", bytes));
        var ex = Assert.Throws<InvalidDataException>(() => WdbImporter.Run(Config((folder, "S", null)), _dir.File("cache.sqlite"), null, "t1"));
        Assert.Contains("creaturecache.wdb", ex.Message);
        Assert.Contains("18414", ex.Message);
    }

    [Fact]
    public void Wdb_WrongMagicForFileName_FailsWithName()
    {
        var folder = Folder("a", ("creaturecache.wdb", WdbFixture.File("WIDB", Array.Empty<(uint, byte[])>())));
        var ex = Assert.Throws<InvalidDataException>(() => WdbImporter.Run(Config((folder, "S", null)), _dir.File("cache.sqlite"), null, "t1"));
        Assert.Contains("creaturecache.wdb", ex.Message);
    }

    [Fact]
    public void Wdb_RecordThatDoesNotFit_FailsWithNameAndOffset()
    {
        var good = WdbFixture.Creature("A", "", 1, 0, 0, new uint[] { 1 });
        var custom = good.Concat(new byte[] { 1, 2, 3 }).ToArray();   // three bytes the 3.3.5 layout does not explain
        var bytes = WdbFixture.File("WMOB", new[] { Rec(1, good), Rec(2, custom) });
        var folder = Folder("a", ("creaturecache.wdb", bytes));
        var ex = Assert.Throws<InvalidDataException>(() => WdbImporter.Run(Config((folder, "S", null)), _dir.File("cache.sqlite"), null, "t1"));
        Assert.Contains("creaturecache.wdb", ex.Message);
        var secondRecordOffset = 24 + 8 + good.Length;
        Assert.Contains($"offset {secondRecordOffset}", ex.Message);
    }

    [Theory]
    [InlineData("truncated header")]
    [InlineData("size past end")]
    [InlineData("unterminated string")]
    [InlineData("huge size")]
    public void Wdb_HostileFile_FailsCleanly(string kind)
    {
        var good = WdbFixture.File("WMOB", new[] { Rec(1, WdbFixture.Creature("A", "", 1, 0, 0, new uint[] { 1 })) });
        byte[] bytes = kind switch
        {
            "truncated header" => good[..10],
            "size past end" => good[..(24 + 8 + 20)],
            "unterminated string" => WdbFixture.File("WMOB", new[] { Rec(1, Enumerable.Repeat((byte)'a', 40).ToArray()) }),
            _ => WdbFixture.File("WMOB", new[] { Rec(1, new byte[8]) }).Select((b, i) => i is >= 28 and < 32 ? (byte)0xFF : b).ToArray(),
        };
        var folder = Folder("a", ("creaturecache.wdb", bytes));
        var ex = Assert.Throws<InvalidDataException>(() => WdbImporter.Run(Config((folder, "S", null)), _dir.File("cache.sqlite"), null, "t1"));
        Assert.Contains("creaturecache.wdb", ex.Message);
    }

    [Fact]
    public void Wdb_OtherCacheFiles_AreCountedNotInvented()
    {
        // Item, quest, game object and NPC caches are recognised by header and counted; no fields are read from them.
        var quest = WdbFixture.File("WQST", new[] { Rec(5, new byte[40]), Rec(6, new byte[40]) });
        var folder = Folder("a", ("creaturecache.wdb", TwoCreatures()), ("questcache.wdb", quest), ("itemnamecache.wdb", new byte[3]), ("itemcache.wdb", WdbFixture.File("WIDB", new[] { Rec(9, new byte[477]) })));
        var layer = _dir.File("cache.sqlite");
        var r = WdbImporter.Run(Config((folder, "S", null)), layer, null, "t1");
        Assert.Equal(2, r.Entities);
        Assert.Contains(r.Files, f => f.Name == "questcache.wdb" && f.Records == 2 && !f.Parsed);
        Assert.Contains(r.Files, f => f.Name == "itemcache.wdb" && f.Records == 1 && !f.Parsed);   // a record that is not the standard item layout is still only counted
        Assert.DoesNotContain(r.Files, f => f.Name == "itemnamecache.wdb");   // not on the list of files to read
    }

    [Fact]
    public void Wdb_Layer_PassesPublishGuard()
    {
        var folder = Folder("a", ("creaturecache.wdb", TwoCreatures()));
        var layer = _dir.File("cache.sqlite");
        WdbImporter.Run(Config((folder, "S", null)), layer, null, "t1");
        SqliteConnectionPool.Clear();
        var r = PublishGuard.Check(layer);
        Assert.True(r.Ok, string.Join("; ", r.Problems));
        Assert.Equal("cache", r.LayerName);
    }

    [Fact]
    public void Config_InRepository_StartsEmpty()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(Path.Combine(d.FullName, "CoaMcp.sln"))) d = d.Parent;
        var config = WdbSources.Load(Path.Combine(d!.FullName, "config", "wdb-sources.json"));
        Assert.Empty(config.Sources);
    }
}

internal static class SqliteConnectionPool
{
    public static void Clear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
