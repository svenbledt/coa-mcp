using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace CoaMcp.Core;

public sealed class LayerWriter : IDisposable
{
    private readonly SqliteConnection _db;
    private SqliteTransaction? _tx;
    private readonly string _name;
    private readonly string _version;

    public LayerWriter(string path, string name, string version)
    {
        if (File.Exists(path)) File.Delete(path);
        _name = name;
        _version = version;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            Init();
        }
        catch
        {
            _tx?.Dispose();
            _db.Dispose();
            throw;
        }
    }

    private void Init()
    {
        _db.Open();
        Run("""
            CREATE TABLE entities(
              id TEXT PRIMARY KEY, type TEXT NOT NULL, title TEXT NOT NULL,
              realm_json TEXT NOT NULL, realm_proven INTEGER NOT NULL, trust INTEGER NOT NULL,
              source TEXT NOT NULL, source_date TEXT NOT NULL, body_md TEXT NOT NULL, data_json TEXT);
            CREATE INDEX entities_type ON entities(type);
            CREATE TABLE links(from_id TEXT NOT NULL, to_id TEXT NOT NULL, kind TEXT NOT NULL);
            CREATE INDEX links_from ON links(from_id);
            CREATE INDEX links_to ON links(to_id);
            CREATE VIRTUAL TABLE entities_fts USING fts5(title, body_md, content='entities', content_rowid='rowid');
            CREATE TABLE layer_meta(name TEXT NOT NULL, version TEXT NOT NULL, built_at TEXT NOT NULL, source_date TEXT NOT NULL);
            """);
        _tx = _db.BeginTransaction();
    }

    public void Add(Entity e)
    {
        try
        {
            AddEntity(e);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode is 1555 or 2067)
        {
            throw new InvalidOperationException($"Duplicate entity id '{e.Id}'.", ex);
        }
    }

    private void AddEntity(Entity e)
    {
        Run("INSERT INTO entities VALUES ($0,$1,$2,$3,$4,$5,$6,$7,$8,$9)",
            e.Id, e.Type, e.Title, JsonSerializer.Serialize(e.Realm.Realms), e.Realm.Proven ? 1L : 0L,
            (long)e.Trust, e.Source, e.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            e.BodyMd, e.DataJson);
        foreach (var l in e.Links)
            Run("INSERT INTO links VALUES ($0,$1,$2)", e.Id, l.ToId, l.Kind);
    }

    /// <summary>Runs code-defined SQL (never user text). Args bind to $0, $1, ...</summary>
    public void Execute(string sql, params object?[] args) => Run(sql, args);

    public void Finish()
    {
        Run("INSERT INTO entities_fts(entities_fts) VALUES('rebuild')");
        Run("INSERT INTO layer_meta SELECT $0, $1, $2, COALESCE(MAX(source_date), $3) FROM entities",
            _name, _version, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        _tx?.Commit();
        _tx?.Dispose();
        _tx = null;
        Run("VACUUM");
    }

    private void Run(string sql, params object?[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _tx?.Dispose();
        _db.Dispose();
    }
}
