using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>One hash table entry as recorded in manifest.sqlite.</summary>
public sealed record FileRow(
    string Archive, int HashIndex, uint BlockIndex, string Path, long Size, uint? Crc, string Status, string? Reason,
    ushort Locale, uint Flags, uint Name1, uint Name2, string? Name, string? NameSource, uint? AttrCrc)
{
    public MpqEntry ToEntry() => new(HashIndex, Name1, Name2, Locale, BlockIndex, (uint)Size, Flags, true);
}

/// <summary>What tells us an archive file was replaced: its size and last-modified time.</summary>
public readonly record struct ArchiveIdentity(long Size, long ModifiedTicks)
{
    public static ArchiveIdentity Of(string path)
    {
        var file = new FileInfo(path);
        return new ArchiveIdentity(file.Length, file.LastWriteTimeUtc.Ticks);
    }
}

/// <summary>manifest.sqlite: every entry of every archive, plus each listfile line and per-archive facts. Calls are serialized.</summary>
public sealed class RestoreManifest : IDisposable
{
    public const string DataDirKey = "data_dir", OutDirKey = "out_dir";

    private const string RowColumns = "archive, hash_index, block_index, path, size, crc, status, reason, locale, flags, name1, name2, name, name_source, attr_crc";

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS files(
            archive TEXT NOT NULL, path TEXT NOT NULL, path_lc TEXT NOT NULL, size INTEGER NOT NULL, crc INTEGER, status TEXT NOT NULL,
            reason TEXT, hash_index INTEGER NOT NULL, block_index INTEGER NOT NULL, locale INTEGER NOT NULL, flags INTEGER NOT NULL,
            name1 INTEGER NOT NULL, name2 INTEGER NOT NULL, name TEXT, name_source TEXT, attr_crc INTEGER,
            PRIMARY KEY(archive, hash_index));
        CREATE INDEX IF NOT EXISTS idx_files_path ON files(path_lc);
        CREATE INDEX IF NOT EXISTS idx_files_name ON files(archive, name1, name2);
        CREATE TABLE IF NOT EXISTS listfile_names(archive TEXT NOT NULL, name TEXT NOT NULL, matched INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_listfile_archive ON listfile_names(archive);
        CREATE TABLE IF NOT EXISTS archives(
            archive TEXT PRIMARY KEY, priority INTEGER NOT NULL, origin TEXT NOT NULL, realm TEXT, hash_slots INTEGER, block_slots INTEGER,
            hash_entries INTEGER, listfile_lines INTEGER, stale_lines INTEGER, error TEXT, file_size INTEGER, file_mtime INTEGER);
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """;

    private readonly SqliteConnection _db;
    private readonly string _connectionString;
    private readonly Lock _gate = new();

    private RestoreManifest(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        _db = new SqliteConnection(_connectionString);
        _db.Open();
    }

    /// <summary>Opens or creates the manifest of a restore.</summary>
    public static RestoreManifest OpenForRestore(string path)
    {
        var m = new RestoreManifest(path);
        m.Run("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        m.Run(Schema);
        // A manifest from before archive identity existed: its archives count as unchanged and get verified by CRC.
        var columns = m.Strings("SELECT name FROM pragma_table_info('archives')");
        foreach (var column in new[] { "file_size", "file_mtime" })   // constants, never user text
            if (!columns.Contains(column)) m.Run($"ALTER TABLE archives ADD COLUMN {column} INTEGER");
        return m;
    }

    /// <summary>Opens a manifest that must already be one.</summary>
    public static RestoreManifest OpenExisting(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"manifest not found: {path}");
        RestoreManifest? m = null;
        try
        {
            m = new RestoreManifest(path);
            m.Run("PRAGMA synchronous=NORMAL;");
            using var cmd = m._db.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('files','listfile_names','archives','meta')";
            if ((long)cmd.ExecuteScalar()! != 4) throw new InvalidDataException($"not a restore manifest: {path}");
            return m;
        }
        catch (SqliteException ex)
        {
            m?.Dispose();
            throw new InvalidDataException($"not a restore manifest: {path}: {ex.Message}", ex);
        }
        catch (InvalidDataException)
        {
            m?.Dispose();
            throw;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate) Run("INSERT OR REPLACE INTO meta(key, value) VALUES($a, $b)", key, value);
    }

    public string Meta(string key)
    {
        lock (_gate) return Scalar("SELECT value FROM meta WHERE key=$a", key) as string
            ?? throw new InvalidDataException($"manifest has no {key}");
    }

    public void SaveArchive(ArchiveInfo a, int? hashSlots, int? blockSlots, int? hashEntries, int? listLines, int? staleLines, string? error, ArchiveIdentity? identity)
    {
        lock (_gate) Run(
            "INSERT OR REPLACE INTO archives(archive, priority, origin, realm, hash_slots, block_slots, hash_entries, listfile_lines, stale_lines, error, file_size, file_mtime) VALUES($a,$b,$c,$d,$e,$f,$g,$h,$i,$j,$k,$l)",
            a.RelPath, a.Priority, a.Origin, a.Realm, hashSlots, blockSlots, hashEntries, listLines, staleLines, error, identity?.Size, identity?.ModifiedTicks);
    }

    /// <summary>Records that an archive couldn't be restored, leaving what's known about it as it was, including its identity.</summary>
    public void MarkBroken(ArchiveInfo a, string error)
    {
        lock (_gate) Run(
            "INSERT INTO archives(archive, priority, origin, realm, error) VALUES($a,$b,$c,$d,$e) ON CONFLICT(archive) DO UPDATE SET error=excluded.error",
            a.RelPath, a.Priority, a.Origin, a.Realm, error);
    }

    /// <summary>Records the identity of an archive that finished restoring.</summary>
    public void SetIdentity(string archive, ArchiveIdentity identity)
    {
        lock (_gate) Run("UPDATE archives SET file_size=$a, file_mtime=$b WHERE archive=$c", identity.Size, identity.ModifiedTicks, archive);
    }

    /// <summary>The identity recorded for an archive, or null if none is: a new archive, or a manifest from before identity existed.</summary>
    public ArchiveIdentity? IdentityOf(string archive)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT file_size, file_mtime FROM archives WHERE archive=$a", [archive]);
            using var r = cmd.ExecuteReader();
            return r.Read() && !r.IsDBNull(0) && !r.IsDBNull(1) ? new ArchiveIdentity(r.GetInt64(0), r.GetInt64(1)) : null;
        }
    }

    public void DropFiles(string archive, IEnumerable<int> hashIndexes)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var index in hashIndexes) Run("DELETE FROM files WHERE archive=$a AND hash_index=$b", archive, index);
            tx.Commit();
        }
    }

    /// <summary>Forgets an archive the caller chose to prune.</summary>
    public void DropArchive(string archive)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var table in new[] { "files", "listfile_names", "archives" }) Run($"DELETE FROM {table} WHERE archive=$a", archive);   // constants, never user text
            tx.Commit();
        }
    }

    public void SaveListfile(string archive, IEnumerable<(string Name, bool Matched)> lines)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Run("DELETE FROM listfile_names WHERE archive=$a", archive);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO listfile_names(archive, name, matched) VALUES($a,$n,$m)";
            var a = cmd.Parameters.Add("$a", SqliteType.Text);
            var n = cmd.Parameters.Add("$n", SqliteType.Text);
            var m = cmd.Parameters.Add("$m", SqliteType.Integer);
            a.Value = archive;
            foreach (var (name, matched) in lines)
            {
                n.Value = name;
                m.Value = matched ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void SaveFiles(IReadOnlyCollection<FileRow> rows)
    {
        if (rows.Count == 0) return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO files(archive, hash_index, block_index, path, path_lc, size, crc, status, reason, locale, flags, name1, name2, name, name_source, attr_crc)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15)
                """;
            var p = Enumerable.Range(0, 16).Select(i => cmd.Parameters.Add("$p" + i, SqliteType.Text)).ToArray();
            foreach (var r in rows)
            {
                object?[] values = [r.Archive, r.HashIndex, r.BlockIndex, r.Path, r.Path.ToLowerInvariant(), r.Size, r.Crc, r.Status, r.Reason,
                    (int)r.Locale, r.Flags, r.Name1, r.Name2, r.Name, r.NameSource, r.AttrCrc];
                for (var i = 0; i < values.Length; i++) p[i].Value = values[i] ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public Dictionary<int, FileRow> FilesOf(string archive)
    {
        lock (_gate) return ReadRows("WHERE archive=$a", archive).ToDictionary(r => r.HashIndex);
    }

    public List<FileRow> UnnamedFiles()
    {
        lock (_gate) return ReadRows("WHERE name IS NULL").ToList();
    }

    /// <summary>Lowest locale among the archive's entries sharing these hashes: the one that keeps the plain path.</summary>
    public int LowestLocale(string archive, uint name1, uint name2)
    {
        lock (_gate) return Convert.ToInt32(Scalar("SELECT min(locale) FROM files WHERE archive=$a AND name1=$b AND name2=$c", archive, name1, name2));
    }

    /// <summary>Restored files of these extensions, for string scanning.</summary>
    public List<(string Archive, string Path)> RestoredFiles(IEnumerable<string> extensions)
    {
        var like = string.Join(" OR ", extensions.Select(e => $"path_lc LIKE '%.{e}'"));   // constants from code, never user text
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT archive, path FROM files WHERE status='ok' AND ({like})";
            using var r = cmd.ExecuteReader();
            var rows = new List<(string, string)>();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
            return rows;
        }
    }

    public List<string> ListfileLines()
    {
        lock (_gate) return Strings("SELECT DISTINCT name FROM listfile_names");
    }

    public List<string> ArchiveNames()
    {
        lock (_gate) return Strings("SELECT archive FROM archives ORDER BY priority DESC");
    }

    public List<string> NamesOf(string archive)
    {
        lock (_gate) return Strings("SELECT DISTINCT name FROM files WHERE name IS NOT NULL AND archive=$a", archive);
    }

    public Dictionary<string, long> CountBy(string sql)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var counts = new Dictionary<string, long>();
            while (r.Read()) counts[r.GetString(0)] = r.GetInt64(1);
            return counts;
        }
    }

    public long Count(string sql)
    {
        lock (_gate) return Convert.ToInt64(Scalar(sql));
    }

    public void Dispose()
    {
        _db.Dispose();
        using var clear = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(clear);
    }

    private IEnumerable<FileRow> ReadRows(string where, params object?[] args)
    {
        using var cmd = Command($"SELECT {RowColumns} FROM files {where}", args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return new FileRow(r.GetString(0), r.GetInt32(1), (uint)r.GetInt64(2), r.GetString(3), r.GetInt64(4),
                r.IsDBNull(5) ? null : (uint)r.GetInt64(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                (ushort)r.GetInt32(8), (uint)r.GetInt64(9), (uint)r.GetInt64(10), (uint)r.GetInt64(11),
                r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : (uint)r.GetInt64(14));
    }

    private List<string> Strings(string sql, params object?[] args)
    {
        using var cmd = Command(sql, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    private object? Scalar(string sql, params object?[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteScalar();
    }

    private void Run(string sql, params object?[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, object?[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$" + (char)('a' + i), args[i] ?? DBNull.Value);
        return cmd;
    }
}
