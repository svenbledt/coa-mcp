using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace CoaMcp.Core;

public sealed class LayerReader : IDisposable
{
    private readonly SqliteConnection _db;

    public LayerMeta Meta { get; }

    public LayerReader(string path)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            _db.Open();
            Meta = ReadMeta(path);
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    private LayerMeta ReadMeta(string path)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = Query("SELECT name, version, built_at, source_date FROM layer_meta");
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException($"'{path}' is not a layer file (no readable layer_meta).", ex);
        }
        if (rows.Count != 1)
            throw new InvalidDataException($"'{path}' is not a finished layer file (layer_meta has {rows.Count} rows).");
        var m = rows[0];
        return new LayerMeta((string)m["name"]!, (string)m["version"]!,
            DateTimeOffset.Parse((string)m["built_at"]!, CultureInfo.InvariantCulture),
            DateOnly.ParseExact((string)m["source_date"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public Entity? Get(string id)
    {
        var rows = Query("SELECT * FROM entities WHERE id = $0", id);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var links = Query("SELECT to_id, kind FROM links WHERE from_id = $0 ORDER BY rowid", id)
            .Select(l => new Link((string)l["to_id"]!, (string)l["kind"]!)).ToList();
        return new Entity(id, (string)r["type"]!, (string)r["title"]!,
            new RealmSet(JsonSerializer.Deserialize<string[]>((string)r["realm_json"]!)!, (long)r["realm_proven"]! != 0),
            (Trust)(long)r["trust"]!, (string)r["source"]!,
            DateOnly.ParseExact((string)r["source_date"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            (string)r["body_md"]!, (string?)r["data_json"], links);
    }

    /// <summary>Full-text search. A realm of "coa" also matches entities tagged "coa-client".</summary>
    public IReadOnlyList<SearchHit> Search(FtsQuery query, string? type, string? realm, int limit, int offset)
    {
        if (query.IsEmpty) return Array.Empty<SearchHit>();
        var rows = Query("""
            SELECT e.id, e.type, e.title, snippet(entities_fts, 1, '[', ']', '...', 12) AS snip, bm25(entities_fts) AS rank
            FROM entities_fts JOIN entities e ON e.rowid = entities_fts.rowid
            WHERE entities_fts MATCH $0
              AND ($1 IS NULL OR e.type = $1)
              AND ($2 IS NULL OR EXISTS (SELECT 1 FROM json_each(e.realm_json) j
                                         WHERE j.value = $2 OR ($2 = 'coa' AND j.value = 'coa-client')))
            ORDER BY rank, e.id
            LIMIT $3 OFFSET $4
            """, query.Match, type, realm, (long)limit, (long)offset);
        return rows.Select(r => new SearchHit((string)r["id"]!, (string)r["type"]!, (string)r["title"]!,
            (string)r["snip"]!, Convert.ToDouble(r["rank"], CultureInfo.InvariantCulture))).ToList();
    }

    /// <summary>Runs code-defined SQL (never user text). Args bind to $0, $1, ...</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql, params object?[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        using var rd = cmd.ExecuteReader();
        var result = new List<IReadOnlyDictionary<string, object?>>();
        while (rd.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < rd.FieldCount; i++)
                row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    public void Dispose() => _db.Dispose();
}
