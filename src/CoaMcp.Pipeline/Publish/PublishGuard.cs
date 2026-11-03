using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Publish;

public sealed record GuardResult(string LayerName, string Version, IReadOnlyList<string> Problems, long RowsScanned, TimeSpan Elapsed)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// The last check before a layer leaves this machine. Client files (MPQ, DBC, BLP, M2) must never reach the server, so a layer is refused if it has
/// a table that is not on its allow-list, a BLOB column or value, or any text that starts with the magic bytes of those formats.
/// The scan streams every row of every content table; nothing is sampled. It opens the file read-only.
/// </summary>
public static class PublishGuard
{
    /// <summary>File signatures. Only a field that starts with one is flagged, so prose that mentions "WDBC" passes.</summary>
    private static readonly (string Name, byte[] Bytes)[] Magic =
    {
        ("WDBC", "WDBC"u8.ToArray()),
        ("MPQ\\x1a", new byte[] { (byte)'M', (byte)'P', (byte)'Q', 0x1A }),
        ("BLP2", "BLP2"u8.ToArray()),
        ("BLP1", "BLP1"u8.ToArray()),
        ("MD20", "MD20"u8.ToArray()),
        ("MD21", "MD21"u8.ToArray()),
    };

    public static GuardResult Check(string path)
    {
        var sw = Stopwatch.StartNew();
        var problems = new List<string>();
        string name = "?", version = "?";
        long rows = 0;
        try
        {
            if (!File.Exists(path)) { problems.Add($"'{path}' does not exist."); return Done(); }
            using var db = Open(path);
            (name, version) = ReadMeta(db, problems);
            if (name != "?" && !LayerAllowList.IsKnownLayer(name))
                problems.Add($"Layer name '{name}' is not on the publish allow-list; add it to LayerAllowList first.");
            if (problems.Count == 0)
                rows = CheckTables(db, name, problems);
        }
        catch (SqliteException ex)
        {
            problems.Add($"'{Path.GetFileName(path)}' is not a readable SQLite layer: {ex.Message}");
        }
        return Done();

        GuardResult Done() => new(name, version, problems, rows, sw.Elapsed);
    }

    private static SqliteConnection Open(string path)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static (string Name, string Version) ReadMeta(SqliteConnection db, List<string> problems)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name, version FROM layer_meta";
        try
        {
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) { problems.Add("layer_meta is empty; the layer was not finished."); return ("?", "?"); }
            var result = (rd.GetString(0), rd.GetString(1));
            if (rd.Read()) problems.Add("layer_meta has more than one row.");
            return result;
        }
        catch (SqliteException)
        {
            problems.Add("The file has no readable layer_meta table, so it is not a layer.");
            return ("?", "?");
        }
    }

    private static long CheckTables(SqliteConnection db, string layer, List<string> problems)
    {
        var objects = new List<(string Type, string Name, string Sql)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, COALESCE(sql, '') FROM sqlite_master";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) objects.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2)));
        }

        var content = new List<string>();
        foreach (var (type, name, sql) in objects)
        {
            switch (type)
            {
                case "index":
                    break;
                case "table":
                    if (!LayerAllowList.IsAllowedTable(layer, name))
                        problems.Add($"Table '{name}' is not allowed in the {layer} layer.");
                    else if (sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                    {
                        // The one full-text table indexes entities; its own rows are an index, not content.
                        if (!LayerAllowList.IsFtsTable(name) || !sql.Contains("fts5", StringComparison.OrdinalIgnoreCase))
                            problems.Add($"Virtual table '{name}' is not allowed.");
                    }
                    else if (!LayerAllowList.IsFtsShadow(name) && !LayerAllowList.IsSqliteInternal(name))
                        content.Add(name);
                    break;
                default:
                    problems.Add($"{type} '{name}' is not allowed in a layer.");
                    break;
            }
        }
        if (problems.Count > 0) return 0;   // do not scan tables of a layer that is already refused

        long rows = 0;
        foreach (var table in content) rows += CheckTable(db, table, problems);
        return rows;
    }

    private static long CheckTable(SqliteConnection db, string table, List<string> problems)
    {
        var columns = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT name, type FROM pragma_table_xinfo({Literal(table)})";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var col = rd.GetString(0);
                columns.Add(col);
                if (rd.GetString(1).Contains("BLOB", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{table}.{col} is declared BLOB; layers hold text and numbers only.");
            }
        }
        if (columns.Count == 0) return 0;

        // One pass per table. For each column SQLite hands back its storage class and the first four bytes only,
        // so a large text or blob field is never copied into this process.
        var select = string.Join(", ", columns.Select(c => $"typeof({Ident(c)}), substr(CAST({Ident(c)} AS BLOB), 1, 4)"));
        var flagged = new HashSet<int>();
        long n = 0;
        using var scan = db.CreateCommand();
        scan.CommandText = $"SELECT {select} FROM {Ident(table)}";
        using var reader = scan.ExecuteReader();
        while (reader.Read())
        {
            n++;
            for (var i = 0; i < columns.Count; i++)
            {
                var kind = reader.GetString(2 * i);
                if (kind is "blob")
                {
                    if (flagged.Add(i)) problems.Add($"{table}.{columns[i]} holds a blob value (first at row {n}); layers hold text and numbers only.");
                    continue;
                }
                if (kind is not "text" || reader.IsDBNull(2 * i + 1)) continue;
                var head = (byte[])reader.GetValue(2 * i + 1);
                foreach (var (magicName, bytes) in Magic)
                {
                    if (head.AsSpan().SequenceEqual(bytes) && flagged.Add(i))
                        problems.Add($"{table}.{columns[i]} has text starting with the {magicName} file signature (first at row {n}); client files must not be published.");
                }
            }
        }
        return n;
    }

    private static string Ident(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Literal(string name) => "'" + name.Replace("'", "''") + "'";
}
