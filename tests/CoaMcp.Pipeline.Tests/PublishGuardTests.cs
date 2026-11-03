using CoaMcp.Core;
using CoaMcp.Pipeline.Publish;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coamcp-pub-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch (IOException) { /* temp data */ }
    }
}

public class PublishGuardTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static Entity E(string id, string body, string title = "Thing") =>
        new(id, "item", title, new RealmSet(new[] { "coa" }, true), Trust.Derived, "BisBeard",
            new DateOnly(2026, 8, 27), body, null, Array.Empty<Link>());

    /// <summary>A finished layer file. <paramref name="extra"/> adds tables or rows before the layer is finished.</summary>
    private string Layer(string name = "bisbeard", string version = "7", Action<LayerWriter>? extra = null, string? body = null)
    {
        var path = _dir.File($"{name}-{Guid.NewGuid():N}.sqlite");
        using (var w = new LayerWriter(path, name, version))
        {
            w.Add(E("item:1", body ?? "A plain item.\n"));
            extra?.Invoke(w);
            w.Finish();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public void Guard_AcceptsCleanBisbeardLayer()
    {
        var r = PublishGuard.Check(Layer());
        Assert.True(r.Ok, string.Join("; ", r.Problems));
        Assert.Equal("bisbeard", r.LayerName);
        Assert.Equal("7", r.Version);
    }

    [Fact]
    public void Guard_AcceptsSpellsLayerWithItsOwnTables()
    {
        var path = Layer("spells", "3", w =>
        {
            w.Execute("CREATE TABLE enums(name TEXT NOT NULL, value INTEGER NOT NULL, value_text TEXT NOT NULL, label TEXT NOT NULL)");
            w.Execute("CREATE TABLE flag_kinds(kind TEXT PRIMARY KEY, enum_name TEXT NOT NULL)");
            w.Execute("CREATE TABLE ScalingStatValues(c0 INTEGER NOT NULL)");
        });
        var r = PublishGuard.Check(path);
        Assert.True(r.Ok, string.Join("; ", r.Problems));
    }

    [Fact]
    public void Guard_RejectsBlobColumn()
    {
        var path = Layer("spells", "3", w => w.Execute("CREATE TABLE enums(name TEXT, payload BLOB)"));
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("BLOB", StringComparison.OrdinalIgnoreCase) && p.Contains("enums"));
    }

    [Fact]
    public void Guard_RejectsBlobValueInUntypedColumn()
    {
        var path = Layer("spells", "3", w =>
        {
            w.Execute("CREATE TABLE enums(name, label)");
            w.Execute("INSERT INTO enums VALUES ('x', $0)", new byte[] { 1, 2, 3, 4, 5 });
        });
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("blob", StringComparison.OrdinalIgnoreCase) && p.Contains("enums"));
    }

    [Theory]
    [InlineData("entities_fts_content")]   // external-content FTS5 has no such table
    public void Guard_RejectsFakeShadowTable(string name)
    {
        // A layer with its own plain table of an FTS shadow name, holding a raw "client file" blob.
        var path = _dir.File($"fake-{Guid.NewGuid():N}.sqlite");
        using (var w = new LayerWriter(path, "bisbeard", "7"))
        {
            w.Add(E("item:1", "A plain item.\n"));
            w.Execute($"CREATE TABLE {name}(payload BLOB)");
            w.Execute($"INSERT INTO {name} VALUES ($0)", new byte[] { (byte)'M', (byte)'P', (byte)'Q', 0x1A, 1, 2, 3 });
            w.Finish();
        }
        SqliteConnection.ClearAllPools();
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains(name));
    }

    [Fact]
    public void Guard_RejectsFakeShadowTable_WhenNoFtsVirtualTableExists()
    {
        // The real shadow definition, but copied into a layer whose entities_fts is gone.
        var path = Layer();
        RewriteSchema(path, "DROP TABLE entities_fts; CREATE TABLE 'entities_fts_data'(id INTEGER PRIMARY KEY, block BLOB)");
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Guard_RejectsShadowTableWithFts5NameButDifferentDefinition()
    {
        var path = Layer();
        RewriteSchema(path, "PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql = 'CREATE TABLE ''entities_fts_data''(id INTEGER PRIMARY KEY, block BLOB, extra BLOB)' WHERE name = 'entities_fts_data'");
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("entities_fts_data"));
    }

    [Fact]
    public void Guard_RejectsFts5TableWithADifferentDeclaration()
    {
        var path = Layer();
        RewriteSchema(path, "PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql = 'CREATE VIRTUAL TABLE entities_fts USING fts5(title, body_md, extra, content=''entities'', content_rowid=''rowid'')' WHERE name = 'entities_fts'");
        Assert.False(PublishGuard.Check(path).Ok);
    }

    private static void RewriteSchema(string path, string sql)
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Guard_RejectsUnknownTable()
    {
        var path = Layer("bisbeard", "7", w => w.Execute("CREATE TABLE dbc_dump(x TEXT)"));
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("dbc_dump"));
    }

    [Fact]
    public void Guard_RejectsTableThatBelongsToAnotherLayer()
    {
        // enums is allowed in spells, not in bisbeard.
        var path = Layer("bisbeard", "7", w => w.Execute("CREATE TABLE enums(name TEXT)"));
        Assert.False(PublishGuard.Check(path).Ok);
    }

    [Fact]
    public void Guard_RejectsUnknownLayerName()
    {
        var r = PublishGuard.Check(Layer("mystery", "1"));
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("mystery"));
    }

    [Fact]
    public void Guard_RejectsViewsAndTriggers()
    {
        var path = Layer("bisbeard", "7", w => w.Execute("CREATE VIEW v AS SELECT id FROM entities"));
        Assert.False(PublishGuard.Check(path).Ok);
    }

    [Theory]
    [InlineData("WDBC")]
    [InlineData("MPQ\u001a")]
    [InlineData("BLP2")]
    [InlineData("MD20")]
    public void Guard_RejectsMpqOrDbcMagicInText(string magic)
    {
        var r = PublishGuard.Check(Layer(body: magic + " and then some ordinary-looking text"));
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("entities") && p.Contains("body_md"));
    }

    [Fact]
    public void Guard_RejectsMagicInAnyTableNotOnlyEntities()
    {
        var path = Layer("spells", "3", w =>
        {
            w.Execute("CREATE TABLE enums(name TEXT NOT NULL, value INTEGER NOT NULL, value_text TEXT NOT NULL, label TEXT NOT NULL)");
            w.Execute("INSERT INTO enums VALUES ('n', 1, '1', 'BLP2xxxx')");
        });
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("enums") && p.Contains("label"));
    }

    [Theory]
    [InlineData("Documents mention WDBC files and MPQ archives, BLP2 textures and MD20 models in prose.")]
    [InlineData("MPQ without the control byte")]
    [InlineData("wdbc in lower case")]
    [InlineData(" WDBC after a space")]
    [InlineData("MD2")]
    public void Guard_DoesNotFlagMagicThatIsNotAtTheStart(string body)
    {
        var r = PublishGuard.Check(Layer(body: body));
        Assert.True(r.Ok, string.Join("; ", r.Problems));
    }

    [Fact]
    public void Guard_RejectsFileThatIsNotSqlite_WithoutThrowing()
    {
        var path = _dir.File("junk.sqlite");
        File.WriteAllText(path, "this is not a database");
        var r = PublishGuard.Check(path);
        Assert.False(r.Ok);
        Assert.NotEmpty(r.Problems);
    }

    [Fact]
    public void Guard_RejectsMissingFile_WithoutThrowing()
    {
        var r = PublishGuard.Check(_dir.File("nope.sqlite"));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Guard_ChecksReadOnly_AndLeavesTheFileUntouched()
    {
        var path = Layer();
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.True(PublishGuard.Check(path).Ok);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void Guard_StreamsManyRows()
    {
        var path = Layer(extra: w =>
        {
            for (var i = 2; i < 20_000; i++) w.Add(E($"item:{i}", $"Body number {i} " + new string('x', 200)));
        });
        Assert.True(PublishGuard.Check(path).Ok);
    }
}
