using System.Text.Json;
using CoaMcp.Core;
using CoaMcp.Pipeline.Mpq;
using CoaMcp.Pipeline.Publish;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

public class PathExportTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private ArchiveConfig _config = ArchiveConfig.Load(Path.Combine(AppContext.BaseDirectory, "config", "archives.json"));

    private ArchiveConfig Config() => _config;

    /// <summary>One manifest row: which archive holds which path, how big, and in what state.</summary>
    private sealed record Entry(string Archive, string Path, long Size = 100, string Status = "ok", string NameSource = "own_listfile", bool Named = true);

    private string Manifest(params Entry[] entries)
    {
        var path = _dir.File($"manifest-{Guid.NewGuid():N}.sqlite");
        var config = Config();
        using (var m = RestoreManifest.OpenForRestore(path))
        {
            var next = new Dictionary<string, int>();
            foreach (var group in entries.GroupBy(e => e.Archive))
            {
                var a = config.Archives.Single(x => x.Path == group.Key);
                m.SaveArchive(new ArchiveInfo(a.Path, a.Priority, a.Origin, a.Realm), new ArchiveFacts(1, 1, 1, 0, 0), new ArchiveIdentity(1, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc).Ticks));
            }
            var rows = new List<FileRow>();
            foreach (var e in entries)
            {
                var i = next[e.Archive] = next.GetValueOrDefault(e.Archive) + 1;
                rows.Add(new FileRow(e.Archive, i, (uint)i, e.Path, e.Status == "delete_marker" ? 0 : e.Size, null, e.Status, null, 0, 0, (uint)i, (uint)i,
                    e.Named ? e.Path.Replace('/', '\\') : null, e.Named ? e.NameSource : null, null));
            }
            m.SaveFiles(rows);
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    private string Export(params Entry[] entries) => ExportFrom(Manifest(entries));

    private string ExportFrom(string manifest)
    {
        var layer = _dir.File($"paths-{Guid.NewGuid():N}.sqlite");
        PathExporter.Run(manifest, Config(), layer, "1");
        SqliteConnection.ClearAllPools();
        return layer;
    }

    private static IReadOnlyDictionary<string, object?>? Row(string layer, string path)
    {
        using var r = new LayerReader(layer);
        return r.Query("SELECT * FROM paths WHERE path = $0", path).SingleOrDefault();
    }

    [Fact]
    public void Winner_IsHighestPriority()
    {
        var layer = Export(
            new Entry("common.MPQ", "DBFilesClient/Spell.dbc", 10),
            new Entry("patch-2.MPQ", "DBFilesClient/Spell.dbc", 20),
            new Entry("patch-S.MPQ", "DBFilesClient/Spell.dbc", 30),
            new Entry("patch-A.MPQ", "DBFilesClient/Spell.dbc", 40));

        var row = Row(layer, "DBFilesClient/Spell.dbc")!;

        Assert.Equal("patch-S.MPQ", row["winner_archive"]);   // priority 110 beats A (72), 2 (68) and common (47)
        Assert.Equal(30L, row["size"]);
        Assert.Equal("unknown", row["winner_origin"]);
        Assert.Equal(0L, row["deleted"]);
        var holders = JsonDocument.Parse((string)row["holders_json"]!).RootElement.EnumerateArray().Select(h => h.GetProperty("a").GetString()).ToArray();
        Assert.Equal(new[] { "patch-S.MPQ", "patch-A.MPQ", "patch-2.MPQ", "common.MPQ" }, holders);
    }

    [Fact]
    public void SourceDate_DefaultsToTheNewestLoadedArchive_NotTheManifestFile()
    {
        var config = Config();
        var manifest = _dir.File($"manifest-{Guid.NewGuid():N}.sqlite");
        using (var m = RestoreManifest.OpenForRestore(manifest))
        {
            void Archive(string name, DateTime when)
            {
                var a = config.Archives.Single(x => x.Path == name);
                m.SaveArchive(new ArchiveInfo(a.Path, a.Priority, a.Origin, a.Realm), new ArchiveFacts(1, 1, 1, 0, 0), new ArchiveIdentity(1, when.Ticks));
            }
            Archive("common.MPQ", new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
            Archive("patch-B.MPQ", new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc));
            Archive("area-52/patch-D.MPQ", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));    // not read by the default client
            m.SaveFiles(new[]
            {
                new FileRow("common.MPQ", 1, 1, "a/b.dbc", 1, null, "ok", null, 0, 0, 1, 1, "a\b.dbc", "own_listfile", null),
            });
        }
        SqliteConnection.ClearAllPools();
        var layer = _dir.File($"paths-{Guid.NewGuid():N}.sqlite");

        PathExporter.Run(manifest, config, layer, "1");
        SqliteConnection.ClearAllPools();

        using var r = new LayerReader(layer);
        Assert.Equal(new DateOnly(2026, 10, 24), r.Meta.SourceDate);
        Assert.Equal("2026-10-24", Stat(r, "client_date"));
        Assert.Empty(r.Query("SELECT 1 FROM path_stats WHERE key = 'restore_date'"));
    }

    [Fact]
    public void SourceDate_AnOpenedArchiveWithNoRecordedTime_IsRefused()
    {
        var config = Config();
        var manifest = _dir.File($"manifest-{Guid.NewGuid():N}.sqlite");
        using (var m = RestoreManifest.OpenForRestore(manifest))
        {
            var dated = config.Archives.Single(x => x.Path == "common.MPQ");
            var undated = config.Archives.Single(x => x.Path == "patch-B.MPQ");
            m.SaveArchive(new ArchiveInfo(dated.Path, dated.Priority, dated.Origin, dated.Realm), new ArchiveFacts(1, 1, 1, 0, 0), new ArchiveIdentity(1, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc).Ticks));
            m.SaveArchive(new ArchiveInfo(undated.Path, undated.Priority, undated.Origin, undated.Realm), new ArchiveFacts(1, 1, 1, 0, 0), null);
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidDataException>(() => PathExporter.Run(manifest, config, _dir.File("undated.sqlite"), "1"));
        Assert.Contains("patch-B.MPQ", ex.Message);
    }

    [Fact]
    public void Winner_DeleteMarkerOnTop_IsDeleted()
    {
        var layer = Export(
            new Entry("common.MPQ", "World/Old/gone.adt", 10),
            new Entry("patch-3.MPQ", "World/Old/gone.adt", 0, "delete_marker"));

        var row = Row(layer, "World/Old/gone.adt")!;

        Assert.Equal(1L, row["deleted"]);
        Assert.Equal("patch-3.MPQ", row["winner_archive"]);   // the marker's archive, not a fallback to the older copy
        Assert.Null(row["size"]);
        var holders = JsonDocument.Parse((string)row["holders_json"]!).RootElement.EnumerateArray().ToArray();
        Assert.Equal("common.MPQ", holders[1].GetProperty("a").GetString());   // the older copy is still listed
        Assert.Equal(1, holders[0].GetProperty("d").GetInt32());
    }

    [Fact]
    public void Winner_DeleteMarkerBelowACopy_IsNotDeleted()
    {
        var layer = Export(
            new Entry("common.MPQ", "x/y.blp", 0, "delete_marker"),
            new Entry("patch-3.MPQ", "x/y.blp", 7));

        var row = Row(layer, "x/y.blp")!;

        Assert.Equal(0L, row["deleted"]);
        Assert.Equal("patch-3.MPQ", row["winner_archive"]);
    }

    [Fact]
    public void Winner_ProjectArchive_ShowsBaseCopy()
    {
        _config = DbcWorld.WithProject("patch-T.MPQ");   // the real config has one project archive; skipping a second needs two
        var layer = Export(
            new Entry("common.MPQ", "Interface/Icons/a.blp", 1),
            new Entry("patch-3.MPQ", "Interface/Icons/a.blp", 2),
            new Entry("patch-B.MPQ", "Interface/Icons/a.blp", 3),
            new Entry("patch-T.MPQ", "Interface/Icons/a.blp", 4));

        var row = Row(layer, "Interface/Icons/a.blp")!;

        Assert.Equal("patch-T.MPQ", row["winner_archive"]);
        Assert.Equal("project", row["winner_origin"]);
        Assert.Equal(4L, row["size"]);
        Assert.Equal("patch-3.MPQ", row["base_winner"]);   // next copy from a non-project archive; patch-B is skipped
        Assert.Equal(2L, row["base_size"]);
    }

    [Fact]
    public void ClientRelease_IsInTheLayerMeta_AndGuardPasses()
    {
        var manifest = Manifest(new Entry("common.MPQ", "a/b.dbc", 1));
        var layer = _dir.File($"paths-{Guid.NewGuid():N}.sqlite");

        PathExporter.Run(manifest, Config(), layer, "1", client: new ClientRelease(8, "rel-8", null));
        SqliteConnection.ClearAllPools();

        using (var r = new LayerReader(layer)) Assert.Equal(8, r.Meta.Client!.Revision);
        SqliteConnection.ClearAllPools();
        Assert.True(PublishGuard.Check(layer).Ok);
    }

    [Fact]
    public void Winner_ProjectWinner_NextCopyIsDeleteMarker_HasNoBase()
    {
        var layer = Export(
            new Entry("common.MPQ", "Interface/Icons/a.blp", 1),
            new Entry("patch-3.MPQ", "Interface/Icons/a.blp", 0, "delete_marker"),
            new Entry("patch-B.MPQ", "Interface/Icons/a.blp", 4));

        var row = Row(layer, "Interface/Icons/a.blp")!;

        Assert.Equal("patch-B.MPQ", row["winner_archive"]);
        Assert.Null(row["base_winner"]);    // common.MPQ's copy is gone below the marker
        Assert.Null(row["base_size"]);
    }

    [Fact]
    public void Winner_NonProjectWinner_HasNoBaseColumns()
    {
        var layer = Export(
            new Entry("common.MPQ", "a/b.dbc", 1),
            new Entry("patch-3.MPQ", "a/b.dbc", 2));

        var row = Row(layer, "a/b.dbc")!;

        Assert.Null(row["base_winner"]);
        Assert.Null(row["base_size"]);
    }

    [Fact]
    public void Winner_RealmArchive_NotDefault()
    {
        var layer = Export(
            new Entry("common.MPQ", "Interface/FrameXML/x.lua", 11),
            new Entry("area-52/patch-D.MPQ", "Interface/FrameXML/x.lua", 99),
            new Entry("area-52/patch-D.MPQ", "Realm/only.txt", 5));

        var shared = Row(layer, "Interface/FrameXML/x.lua")!;
        Assert.Equal("common.MPQ", shared["winner_archive"]);
        Assert.Equal(11L, shared["size"]);
        var realm = JsonDocument.Parse((string)shared["holders_json"]!).RootElement.EnumerateArray().First();
        Assert.Equal("area-52/patch-D.MPQ", realm.GetProperty("a").GetString());
        Assert.Equal("area-52", realm.GetProperty("r").GetString());

        var only = Row(layer, "Realm/only.txt")!;   // no default copy at all: kept, but with no winner
        Assert.Null(only["winner_archive"]);
        Assert.Null(only["size"]);
        Assert.Equal(0L, only["deleted"]);
    }

    [Fact]
    public void Winner_ArchiveTheClientNeverOpens_NeverWins()
    {
        var layer = Export(
            new Entry("common.MPQ", "a/b.dbc", 1),
            new Entry("enUS/base-enUS.MPQ", "a/b.dbc", 5));

        Assert.Equal("common.MPQ", Row(layer, "a/b.dbc")!["winner_archive"]);
    }

    [Fact]
    public void Export_KeepsOriginalCase_AndOneRowPerPath()
    {
        var layer = Export(
            new Entry("common.MPQ", "Interface/ICONS/Spell.BLP", 1),
            new Entry("patch-3.MPQ", "interface/icons/spell.blp", 2));

        using var r = new LayerReader(layer);
        var rows = r.Query("SELECT path, ext FROM paths");

        var row = Assert.Single(rows);
        Assert.Equal("interface/icons/spell.blp", row["path"]);   // the winning copy's spelling
        Assert.Equal("blp", row["ext"]);
    }

    [Fact]
    public void Export_SkipsMpqInternals_AndCountsThem()
    {
        var layer = Export(
            new Entry("common.MPQ", "(listfile)", 10, NameSource: "internal"),
            new Entry("common.MPQ", "(attributes)", 10, NameSource: "internal"),
            new Entry("patch-3.MPQ", "(listfile)", 10),   // named by a listfile in this archive, but still the archive's own bookkeeping
            new Entry("common.MPQ", "a/b.dbc", 10));

        using var r = new LayerReader(layer);
        Assert.Equal(1L, r.Query("SELECT COUNT(*) AS n FROM paths")[0]["n"]);
        Assert.Equal("3", Stat(r, "internal_skipped"));
    }

    [Fact]
    public void Export_RecordsCountsAndSourceDate()
    {
        var layer = Export(
            new Entry("common.MPQ", "a/b.dbc", 10),
            new Entry("common.MPQ", "a/gone.dbc", 10),
            new Entry("patch-3.MPQ", "a/gone.dbc", 0, "delete_marker"),
            new Entry("patch-B.MPQ", "a/c.dbc", 3));

        using var r = new LayerReader(layer);
        Assert.Equal("3", Stat(r, "paths"));
        Assert.Equal("1", Stat(r, "deleted"));
        Assert.Equal("1", Stat(r, "project_winner"));
        Assert.Equal("0", Stat(r, "unnamed"));
        Assert.Equal("0", Stat(r, "failed"));
        Assert.Equal("2026-11-03", r.Meta.SourceDate.ToString("yyyy-MM-dd"));
        Assert.Equal("paths", r.Meta.Name);
    }

    private static string Stat(LayerReader r, string key) =>
        (string)r.Query("SELECT value FROM path_stats WHERE key = $0", key).Single()["value"]!;

    [Fact]
    public void Export_UnnamedEntries_AreCountedNotPublished()
    {
        var layer = Export(
            new Entry("common.MPQ", "a/b.dbc", 10),
            new Entry("common.MPQ", "unknown/00000001", 10, Named: false));

        using var r = new LayerReader(layer);
        Assert.Equal(1L, r.Query("SELECT COUNT(*) AS n FROM paths")[0]["n"]);
        Assert.Equal("1", Stat(r, "unnamed"));
    }

    [Fact]
    public void Export_FailedEntry_FailsTheExport()
    {
        var manifest = Manifest(new Entry("common.MPQ", "a/b.dbc", 10), new Entry("common.MPQ", "a/c.dbc", 10, "failed"));

        var ex = Assert.Throws<InvalidDataException>(() => ExportFrom(manifest));

        Assert.Contains("failed", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void Export_ArchiveMissingFromConfig_Fails()
    {
        var manifest = Manifest(new Entry("common.MPQ", "a/b.dbc", 10));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = manifest, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE files SET archive = 'mystery.MPQ'; UPDATE archives SET archive = 'mystery.MPQ'";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.Throws<InvalidDataException>(() => ExportFrom(manifest));
    }

    [Fact]
    public void Export_HasNoFileContent()
    {
        var layer = Export(
            new Entry("common.MPQ", "DBFilesClient/Spell.dbc", 4096),
            new Entry("patch-3.MPQ", "Interface/Icons/a.blp", 77),
            new Entry("patch-3.MPQ", "World/gone.adt", 0, "delete_marker"));

        var guard = PublishGuard.Check(layer);
        Assert.True(guard.Ok, string.Join("; ", guard.Problems));
        Assert.Equal("paths", guard.LayerName);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = layer, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE '%_fts_%'";
        var tables = new List<string>();
        using (var rd = cmd.ExecuteReader()) while (rd.Read()) tables.Add(rd.GetString(0));
        Assert.Equal(new[] { "dirs", "entities", "entities_fts", "layer_meta", "links", "path_stats", "paths", "paths_fts" }, tables.Order().ToArray());
        // Nothing but text, numbers and null in the content tables: no BLOBs, and size is a number of bytes, not bytes of a file.
        foreach (var t in new[] { "paths", "dirs", "path_stats" })
        {
            using var cols = db.CreateCommand();
            cols.CommandText = $"SELECT name, type FROM pragma_table_info('{t}')";
            using var rd = cols.ExecuteReader();
            while (rd.Read())
                Assert.DoesNotContain("BLOB", rd.GetString(1), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Paths_CaseInsensitive()
    {
        var layer = Export(
            new Entry("common.MPQ", "Interface/ICONS/Spell_Fire_Fireball.BLP", 5),
            new Entry("common.MPQ", "DBFilesClient/Spell.dbc", 5));

        Assert.NotNull(Row(layer, "interface/icons/SPELL_FIRE_FIREBALL.blp"));   // exact lookup ignores case
        using var r = new LayerReader(layer);
        var hits = r.Query("SELECT p.path FROM paths_fts f JOIN paths p ON p.id = f.rowid WHERE paths_fts MATCH $0", FtsQuery.FromUser("fireBALL").Match);
        Assert.Equal("Interface/ICONS/Spell_Fire_Fireball.BLP", Assert.Single(hits)["path"]);   // substring search ignores case
    }

    [Fact]
    public void Export_BuildsFolderTreeWithCounts()
    {
        var layer = Export(
            new Entry("common.MPQ", "DBFilesClient/Spell.dbc"),
            new Entry("common.MPQ", "DBFilesClient/Map.dbc"),
            new Entry("common.MPQ", "Interface/Icons/a.blp"),
            new Entry("common.MPQ", "Interface/Icons/Old/b.blp"),
            new Entry("patch-3.MPQ", "Interface/Icons/Old/b.blp", 0, "delete_marker"),
            new Entry("common.MPQ", "readme.txt"));

        using var r = new LayerReader(layer);
        var top = r.Query("SELECT name, files, removed FROM dirs WHERE parent_id = 0 ORDER BY name_lc");
        Assert.Equal(new[] { "DBFilesClient", "Interface" }, top.Select(d => (string)d["name"]!).ToArray());
        Assert.Equal(2L, top[0]["files"]);
        Assert.Equal(2L, top[1]["files"]);      // a.blp and the removed b.blp are both under Interface
        Assert.Equal(1L, top[1]["removed"]);
        Assert.Equal(0L, r.Query("SELECT dir_id FROM paths WHERE path = 'readme.txt'")[0]["dir_id"]);
    }

    [Fact]
    public void Guard_AcceptsPathsLayer_AndRejectsPathsTablesInOtherLayers()
    {
        var path = _dir.File("other.sqlite");
        using (var w = new LayerWriter(path, "bisbeard", "7"))
        {
            w.Execute("CREATE TABLE paths(path TEXT)");
            w.Finish();
        }
        SqliteConnection.ClearAllPools();
        Assert.False(PublishGuard.Check(path).Ok);
    }

    [Fact]
    public void Guard_RejectsPathsLayerWithAnExtraTable()
    {
        var path = _dir.File("extra.sqlite");
        using (var w = new LayerWriter(path, "paths", "7"))
        {
            w.Execute("CREATE TABLE dumped(payload TEXT)");
            w.Finish();
        }
        SqliteConnection.ClearAllPools();
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("dumped"));
    }

    [Fact]
    public void Guard_RejectsPathsFtsWithADifferentDeclaration()
    {
        var layer = Export(new Entry("common.MPQ", "a/b.dbc"));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = layer, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql = 'CREATE VIRTUAL TABLE paths_fts USING fts5(path, extra, content=''paths'', content_rowid=''id'', tokenize=''trigram'')' WHERE name = 'paths_fts'";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Assert.False(PublishGuard.Check(layer).Ok);
    }

    [Fact]
    public void Guard_RejectsPathsFtsShadowTableLookalike()
    {
        var layer = Export(new Entry("common.MPQ", "a/b.dbc"));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = layer, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql = 'CREATE TABLE ''paths_fts_data''(id INTEGER PRIMARY KEY, block BLOB, extra BLOB)' WHERE name = 'paths_fts_data'";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var r = PublishGuard.Check(layer);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("paths_fts_data"));
    }
}
