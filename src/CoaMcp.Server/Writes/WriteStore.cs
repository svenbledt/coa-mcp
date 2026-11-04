using System.Globalization;
using System.Text.Json;
using CoaMcp.Core;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;

namespace CoaMcp.Server;

/// <summary>
/// The one writable database, append-only: written entries, separate from the read-only layers. It is created by the first write and never by a read.
/// A broken store never stops reads of generated data: reads get null and report it, writes get a clean error.
/// </summary>
public sealed class WriteStore : IDisposable
{
    public const long DefaultMaxBytes = 1024L * 1024 * 1024;
    private const int SchemaVersion = 1;
    private const int BusyTimeoutMs = 5_000;
    private const long PageBytes = 4_096;
    private const int MaxHiddenIds = 10;
    public const int MaxFieldRows = 600;

    /// <summary>Distinct values read per structured field: one more than a conflict note shows, to tell when there are more.</summary>
    public const int MaxValuesPerField = 6;

    private const int SqliteBusy = 5, SqliteLocked = 6, SqliteFull = 13;

    /// <summary>
    /// A live entry: not undone, and not a page with a newer live version of the same or higher trust (a lower number is higher trust).
    /// A memory page never replaces an observed one, so then both stay live.
    /// </summary>
    private const string Live = """
        (e.undone_at IS NULL AND NOT (e.kind = 'page' AND EXISTS (
            SELECT 1 FROM entries n WHERE n.subject_id = e.subject_id AND n.kind = 'page' AND n.undone_at IS NULL AND n.id > e.id AND n.trust <= e.trust)))
        """;

    /// <summary>Written for test runs: readable by id, but not in anyone's search.</summary>
    private const string NotTest = "substr(e.subject_id, 1, 5) <> 'test:'";

    private const string Schema = """
        CREATE TABLE schema_version(version INTEGER NOT NULL);
        CREATE TABLE entries(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          subject_id TEXT NOT NULL,
          kind TEXT NOT NULL CHECK (kind IN ('observation','note','correction','page')),
          requested_kind TEXT NOT NULL CHECK (requested_kind IN ('observation','note','correction','page')),
          realm_json TEXT NOT NULL,
          trust INTEGER NOT NULL CHECK (trust IN (4, 5)),
          content_md TEXT NOT NULL,
          evidence_json TEXT NOT NULL,
          field TEXT,
          value TEXT,
          position_json TEXT,
          author TEXT NOT NULL,
          created_at TEXT NOT NULL,
          supersedes_id INTEGER REFERENCES entries(id),
          undone_at TEXT,
          undone_by TEXT);
        CREATE INDEX entries_subject ON entries(subject_id, kind, id);
        CREATE VIRTUAL TABLE entries_fts USING fts5(subject_id, content_md, field, value);
        """;

    private readonly string _path;
    private readonly ILogger? _log;
    private readonly long _maxPages;
    private readonly object _writeLock = new();
    private readonly object _prepareLock = new();
    private volatile bool _ready;
    private volatile string? _problem;
    private volatile string? _writeProblem;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _damaged = new();

    public WriteStore(string path, ILogger? log = null, long maxBytes = DefaultMaxBytes)
    {
        _path = path;
        _log = log;
        _maxPages = Math.Max(1, maxBytes / PageBytes);
    }

    // ---- connections ----

    private SqliteConnection Open(bool create)
    {
        Secure(create, sidecarsToo: false);   // before SQLite first creates the file, and on every open: an older, wider file is tightened
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            DefaultTimeout = BusyTimeoutMs / 1000,
        }.ToString());
        try
        {
            c.Open();
            Exec(c, null, $"PRAGMA busy_timeout = {BusyTimeoutMs}");
            Exec(c, null, "PRAGMA synchronous = FULL");
            Exec(c, null, $"PRAGMA max_page_count = {_maxPages}");
            if (!_ready) Prepare(c);
            Secure(create: false, sidecarsToo: true);   // -wal and -shm exist now
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Keeps the store, and its -wal and -shm files, at mode 0640 or tighter on Unix. SQLite gives the -wal and -shm files the main file's mode.
    /// A failure is logged and never stops the open: a store that is too open is better than reads that are down.
    /// </summary>
    private volatile bool _modeFailureLogged;

    private void Secure(bool create, bool sidecarsToo)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            if (create) PrivateFile.CreateIfMissing(_path);
            PrivateFile.Tighten(_path);
            if (sidecarsToo)
            {
                PrivateFile.Tighten(_path + "-wal");
                PrivateFile.Tighten(_path + "-shm");
            }
            _modeFailureLogged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This runs on every open, so a lasting failure is logged once, not once per request.
            if (!_modeFailureLogged) _log?.LogError(ex, "The writable store's file mode could not be set to 0640, so it may be readable by other users.");
            _modeFailureLogged = true;
        }
    }

    private void Prepare(SqliteConnection c)
    {
        lock (_prepareLock)
        {
            if (_ready) return;
            var mode = Scalar(c, null, "PRAGMA journal_mode = WAL") as string;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The store could not switch to WAL (it is in {mode} mode).");

            using var tx = c.BeginTransaction();
            if (Scalar(c, tx, "SELECT 1 FROM sqlite_master WHERE name = 'schema_version'") is null)
            {
                Exec(c, tx, Schema);
                Exec(c, tx, "INSERT INTO schema_version VALUES ($0)", (long)SchemaVersion);
            }
            var version = Scalar(c, tx, "SELECT MAX(version) FROM schema_version") as long?;
            if (version != SchemaVersion) throw new InvalidDataException($"The store has schema version {version}, and this server knows version {SchemaVersion}.");
            tx.Commit();
            _ready = true;
        }
    }

    private static void Bind(SqliteCommand cmd, object?[] args)
    {
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, args);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static int Count(SqliteConnection c, string sql, params object?[] args) => (int)(long)Scalar(c, null, sql, args)!;

    private static bool IsStoreFailure(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException;

    private void Fail(Exception ex)
    {
        var problem = ex.Message;
        if (_problem != problem) _log?.LogError(ex, "The writable store is unavailable.");
        _problem = problem;
    }

    /// <summary>Runs a read. Null means the store exists but failed. A store nobody has written to yet reads as <paramref name="none"/>.</summary>
    private T? Reading<T>(T none, Func<SqliteConnection, T> work) where T : class
    {
        if (!File.Exists(_path)) return none;
        try
        {
            using var c = Open(create: false);
            var result = work(c);
            _problem = null;
            return result;
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            Fail(ex);
            return null;
        }
    }

    private T Writing<T>(Func<SqliteConnection, T> work)
    {
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                using var c = Open(create: true);
                var result = work(c);
                _problem = null;
                _writeProblem = null;
                return result;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteFull)
            {
                _log?.LogError(ex, "The writable store is full.");
                _writeProblem = "the store is full, so new writes are refused";
                throw new McpException("The writable store is full, so nothing was written. Reads are unaffected.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
            {
                _log?.LogWarning(ex, "The writable store stayed busy.");
                throw new McpException("The writable store is busy, so nothing was written. Try again shortly.");
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                Fail(ex);
                throw new McpException("The writable store is unavailable, so nothing was written. See the status tool.");
            }
        }
    }

    // ---- writes ----

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public EntryReceipt Add(NewEntry e) => Writing(c =>
    {
        using var tx = c.BeginTransaction();   // BEGIN IMMEDIATE: the writer lock is taken up front, and a busy database waits out the timeout
        var supersedes = e.Kind == "page"
            ? Scalar(c, tx, "SELECT MAX(id) FROM entries WHERE subject_id = $0 AND kind = 'page' AND undone_at IS NULL AND trust >= $1", e.SubjectId, (long)e.Trust) as long?
            : null;
        Exec(c, tx, """
            INSERT INTO entries(subject_id, kind, realm_json, trust, content_md, evidence_json, field, value, position_json, author, created_at, supersedes_id, requested_kind)
            VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            """, e.SubjectId, e.Kind, JsonSerializer.Serialize(e.Realms), (long)e.Trust, e.ContentMd, e.EvidenceJson, e.Field, e.Value, e.PositionJson, e.Author, Now(), supersedes, e.RequestedKind ?? e.Kind);
        var id = (long)Scalar(c, tx, "SELECT last_insert_rowid()")!;
        Exec(c, tx, "INSERT INTO entries_fts(rowid, subject_id, content_md, field, value) VALUES ($0, $1, $2, $3, $4)", id, e.SubjectId, e.ContentMd, e.Field, e.Value);
        tx.Commit();
        return new EntryReceipt(id, supersedes);
    });

    /// <summary>Marks an entry undone and deletes nothing. A second undo changes nothing, and the first one stays on record.</summary>
    public UndoReceipt Undo(long id, string author) => Writing(c =>
    {
        using var tx = c.BeginTransaction();
        var e = Query(c, tx, "SELECT e.*, NULL AS superseded_by FROM entries e WHERE e.id = $0", id).FirstOrDefault()
            ?? throw new McpException($"No entry with id {id.ToString(CultureInfo.InvariantCulture)}.");
        if (e.UndoneAt is not null) return new UndoReceipt(id, e.UndoneAt, e.UndoneBy ?? "", true);
        var at = Now();
        Exec(c, tx, "UPDATE entries SET undone_at = $1, undone_by = $2 WHERE id = $0", id, at, author);
        tx.Commit();
        return new UndoReceipt(id, at, author, false);
    });

    // ---- reads ----

    private List<WriteEntry> Query(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] args) =>
        Rows(c, tx, sql, args, ToEntry);

    private static List<T> Rows<T>(SqliteConnection c, SqliteTransaction? tx, string sql, object?[] args, Func<SqliteDataReader, T> map)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<T>();
        while (r.Read()) rows.Add(map(r));
        return rows;
    }

    private static string? Text(SqliteDataReader r, string column) => r[column] as string;

    /// <summary>Reads one row. A row whose stored JSON is damaged comes back marked unreadable, so it costs only itself.</summary>
    private WriteEntry ToEntry(SqliteDataReader r)
    {
        var id = (long)r["id"];
        try
        {
            var evidence = (string)r["evidence_json"];
            JsonSerializer.Deserialize<EntryEvidence>(evidence);
            var position = Text(r, "position_json");
            if (position is not null && JsonSerializer.Deserialize<EntryPosition>(position) is null) throw new JsonException("position_json is null.");
            return new WriteEntry(id, (string)r["subject_id"], (string)r["kind"], ParseRealms((string)r["realm_json"]), (Trust)(long)r["trust"],
                (string)r["content_md"], evidence, Text(r, "field"), Text(r, "value"), position, (string)r["author"], (string)r["created_at"],
                r["supersedes_id"] as long?, Text(r, "undone_at"), Text(r, "undone_by"), r["superseded_by"] as long?, Text(r, "requested_kind"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidCastException)
        {
            NoteDamaged(id, ex);
            return new WriteEntry(id, "", "note", Array.Empty<string>(), Trust.Memory, "", "{}", null, null, null, "", "",
                null, Text(r, "undone_at"), Text(r, "undone_by"), Unreadable: true);
        }
    }

    private static string[] ParseRealms(string json) => JsonSerializer.Deserialize<string[]>(json) ?? throw new JsonException("realm_json is null.");

    /// <summary>Logged once per entry, because anyone reading the page would otherwise repeat it. The message never holds the entry's text.</summary>
    private void NoteDamaged(long id, Exception ex)
    {
        if (_damaged.TryAdd(id, 0))
            _log?.LogError("Written entry:{Id} is damaged and can't be read ({Problem}). It is shown as unreadable.", id, ex.GetType().Name);
    }

    /// <returns>The entry, with what replaced it if it is an old page version. Null with <paramref name="unavailable"/> false means no such entry.</returns>
    public WriteEntry? Get(long id, out bool unavailable)
    {
        var rows = Reading(new List<WriteEntry>(), c => Query(c, null, """
            SELECT e.*, (SELECT MIN(n.id) FROM entries n WHERE e.kind = 'page' AND n.subject_id = e.subject_id AND n.kind = 'page' AND n.undone_at IS NULL AND n.id > e.id AND n.trust <= e.trust) AS superseded_by
            FROM entries e WHERE e.id = $0
            """, id));
        unavailable = rows is null;
        return rows?.FirstOrDefault();
    }

    /// <summary>One page of a subject's live entries, higher trust first, newest first within a level. Null if the store is unavailable.</summary>
    public SubjectEntries? ForSubject(string subject, int limit, int offset) =>
        Reading(new SubjectEntries(Array.Empty<WriteEntry>(), 0, 0, 0, Array.Empty<long>(), Array.Empty<FieldClaim>(), false), c =>
        {
            var page = Query(c, null, $"SELECT e.*, NULL AS superseded_by FROM entries e WHERE e.subject_id = $0 AND {Live} ORDER BY e.trust ASC, e.id DESC LIMIT $1 OFFSET $2", subject, (long)limit, (long)offset);
            var live = Count(c, $"SELECT COUNT(*) FROM entries e WHERE e.subject_id = $0 AND {Live}", subject);
            var undone = Count(c, "SELECT COUNT(*) FROM entries e WHERE e.subject_id = $0 AND e.undone_at IS NOT NULL", subject);
            var superseded = Count(c, $"SELECT COUNT(*) FROM entries e WHERE e.subject_id = $0 AND e.undone_at IS NULL AND NOT {Live}", subject);
            var hidden = Rows(c, null, $"SELECT e.id FROM entries e WHERE e.subject_id = $0 AND NOT {Live} ORDER BY e.id DESC LIMIT $1", new object?[] { subject, (long)MaxHiddenIds }, r => (long)r["id"]);
            var (claims, truncated) = Claims(c, subject);
            return new SubjectEntries(page, live, undone, superseded, hidden, claims, truncated);
        });

    /// <summary>
    /// The distinct values of each structured field, best entry first. One field can't crowd out another: values are capped per field, and fields with
    /// more than one value come before the rest. If the total cap is still reached, the caller is told, so a conflict is never dropped silently.
    /// </summary>
    private (List<FieldClaim> Claims, bool Truncated) Claims(SqliteConnection c, string subject)
    {
        var total = 0L;
        var claims = Rows(c, null, $"""
            WITH live AS (
                SELECT e.id, e.field, e.value, e.trust, e.author, e.realm_json,
                       COUNT(*) OVER (PARTITION BY e.field, e.value) AS same_value,
                       ROW_NUMBER() OVER (PARTITION BY e.field, e.value ORDER BY e.trust, e.id DESC) AS best
                FROM entries e WHERE e.subject_id = $0 AND e.field IS NOT NULL AND {Live}),
            vals AS (
                SELECT *, COUNT(*) OVER (PARTITION BY field) AS distinct_values,
                       ROW_NUMBER() OVER (PARTITION BY field ORDER BY trust, id DESC) AS place
                FROM live WHERE best = 1),
            kept AS (SELECT *, COUNT(*) OVER () AS total FROM vals WHERE place <= $1)
            SELECT * FROM kept ORDER BY distinct_values > 1 DESC, field, place LIMIT $2
            """, new object?[] { subject, (long)MaxValuesPerField, (long)MaxFieldRows }, r =>
        {
            total = (long)r["total"];
            var id = (long)r["id"];
            string[] realms;
            try { realms = ParseRealms((string)r["realm_json"]); }
            catch (JsonException ex) { NoteDamaged(id, ex); realms = Array.Empty<string>(); }   // the value itself is still real, so the conflict still shows
            return new FieldClaim(id, (string)r["field"], (string)r["value"], (Trust)(long)r["trust"], (string)r["author"],
                realms, (int)(long)r["same_value"], (int)(long)r["distinct_values"]);
        });
        return (claims, total > MaxFieldRows);
    }

    private static (string Where, List<object?> Args) Matching(FtsQuery q, string? realm, IReadOnlyList<string>? subjectTypes)
    {
        var args = new List<object?> { q.Match, realm };
        var types = "";
        if (subjectTypes is not null)
        {
            var marks = subjectTypes.Select((_, i) => "$" + (2 + i));
            types = $" AND substr(e.subject_id, 1, instr(e.subject_id, ':') - 1) IN ({string.Join(",", marks)})";
            args.AddRange(subjectTypes);
        }
        return ($"""
            f.entries_fts MATCH $0 AND {Live} AND {NotTest}
            AND ($1 IS NULL OR EXISTS (SELECT 1 FROM json_each(e.realm_json) j WHERE j.value = $1)){types}
            """, args);
    }

    /// <summary>How many live entries match, counting no further than <paramref name="atMost"/>. Zero if the store is unavailable.</summary>
    public int SearchCount(FtsQuery q, string? realm, IReadOnlyList<string>? subjectTypes, int atMost)
    {
        if (q.IsEmpty) return 0;
        var (where, args) = Matching(q, realm, subjectTypes);
        var limit = "$" + args.Count;
        args.Add((long)atMost);
        return Reading(new[] { 0 }, c => new[]
        {
            Count(c, $"SELECT COUNT(*) FROM (SELECT 1 FROM entries_fts f JOIN entries e ON e.id = f.rowid WHERE {where} LIMIT {limit})", args.ToArray()),
        })?[0] ?? 0;
    }

    public IReadOnlyList<EntryHit> Search(FtsQuery q, string? realm, IReadOnlyList<string>? subjectTypes, int limit, int offset)
    {
        if (q.IsEmpty || limit <= 0) return Array.Empty<EntryHit>();
        var (where, args) = Matching(q, realm, subjectTypes);
        var marks = $"LIMIT ${args.Count} OFFSET ${args.Count + 1}";
        args.Add((long)limit);
        args.Add((long)offset);
        return Reading(new List<EntryHit>(), c => Rows(c, null, $"""
            SELECT e.*, NULL AS superseded_by, snippet(entries_fts, -1, '[', ']', '...', 12) AS snippet_text
            FROM entries_fts f JOIN entries e ON e.id = f.rowid
            WHERE {where} ORDER BY f.rank, e.id DESC {marks}
            """, args.ToArray(), r => new EntryHit(ToEntry(r), (string)r["snippet_text"]))) ?? new List<EntryHit>();
    }

    /// <summary>Why the store can't be used right now, or null. Costs nothing, so a search can ask every time.</summary>
    public string? Problem => _problem;

    public StoreCounts Status()
    {
        var exists = File.Exists(_path);
        var counts = Reading(new StoreCounts(0, 0, 0, 0, 0, exists, null, null), c =>
        {
            var observed = Count(c, $"SELECT COUNT(*) FROM entries e WHERE {Live} AND e.trust = $0", (long)Trust.Observed);
            var memory = Count(c, $"SELECT COUNT(*) FROM entries e WHERE {Live} AND e.trust = $0", (long)Trust.Memory);
            var undone = Count(c, "SELECT COUNT(*) FROM entries e WHERE e.undone_at IS NOT NULL");
            var superseded = Count(c, $"SELECT COUNT(*) FROM entries e WHERE e.undone_at IS NULL AND NOT {Live}");
            return new StoreCounts(observed + memory, observed, memory, undone, superseded, exists, null, null);
        });
        return (counts ?? new StoreCounts(0, 0, 0, 0, 0, exists, null, null)) with { Problem = _problem, WriteProblem = _writeProblem, Damaged = _damaged.Count };
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}
