using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Knowledge;

/// <summary>
/// Rebuilds an empty writable store from knowledge/entries, so the repository doubles as a backup. Ids, dates and undo state come back as they were,
/// and the search index is rebuilt. It never touches a store that already has entries. The file is built under a temporary name and renamed into place.
/// </summary>
public static partial class KnowledgeRestorer
{
    private const int SchemaVersion = 1;

    /// <summary>
    /// An exact copy of the server's schema (WriteStore.Schema), because the pipeline doesn't reference the server.
    /// A test creates a store with the real WriteStore and compares every table, index and the version, so a drift fails the build.
    /// </summary>
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

    [GeneratedRegex(@"^\d{8}\.md\z", RegexOptions.CultureInvariant)]
    private static partial Regex EntryName();

    /// <returns>How many entries were restored.</returns>
    public static int Restore(string from, string storePath)
    {
        var entriesDir = Path.Combine(from, "entries");
        if (!Directory.Exists(entriesDir)) throw new KnowledgeException($"There is no entries folder at {entriesDir}, so there is nothing to restore.");
        CheckTarget(storePath);

        // Every file is read and checked before anything is created.
        var entries = new List<KnowledgeEntry>();
        foreach (var file in Directory.GetFiles(entriesDir).Where(f => EntryName().IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var e = KnowledgeFormat.Parse(File.ReadAllBytes(file), name);
            if (KnowledgeFormat.FileName(e.Id) != name) throw new KnowledgeException($"{name}: the file name doesn't match the id inside it.");
            entries.Add(e);
        }
        if (entries.Count == 0) throw new KnowledgeException($"There are no entry files in {entriesDir}, so there is nothing to restore.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(storePath))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(storePath) + ".restore-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                c.Open();
                using var tx = c.BeginTransaction();
                Exec(c, tx, Schema);
                Exec(c, tx, "INSERT INTO schema_version VALUES ($0)", (long)SchemaVersion);
                foreach (var e in entries)
                {
                    Exec(c, tx, """
                        INSERT INTO entries(id, subject_id, kind, requested_kind, realm_json, trust, content_md, evidence_json, field, value, position_json, author, created_at, supersedes_id, undone_at, undone_by)
                        VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
                        """, e.Id, e.SubjectId, e.Kind, e.RequestedKind, e.RealmJson, e.Trust, e.ContentMd, e.EvidenceJson, e.Field, e.Value, e.PositionJson, e.Author, e.CreatedAt, e.SupersedesId, e.UndoneAt, e.UndoneBy);
                    Exec(c, tx, "INSERT INTO entries_fts(rowid, subject_id, content_md, field, value) VALUES ($0, $1, $2, $3, $4)", e.Id, e.SubjectId, e.ContentMd, e.Field, e.Value);
                }
                tx.Commit();
                using var check = c.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                if (check.ExecuteScalar() as string != "ok") throw new KnowledgeException("The restored store failed its integrity check, so it was discarded.");
            }
            foreach (var sidecar in new[] { storePath + "-wal", storePath + "-shm" }) File.Delete(sidecar);   // they belong to the empty store being replaced
            File.Move(temp, storePath, overwrite: true);
        }
        catch (SqliteException ex)
        {
            throw new KnowledgeException($"Could not build the store: {KnowledgeFormat.Escape(ex.Message)}", ex);
        }
        finally
        {
            File.Delete(temp);
        }
        return entries.Count;
    }

    /// <summary>A missing file is fine, and so is a store with no entries. Anything else is refused untouched.</summary>
    private static void CheckTarget(string storePath)
    {
        if (!File.Exists(storePath)) return;
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = storePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var has = c.CreateCommand();
            has.CommandText = "SELECT 1 FROM sqlite_master WHERE name = 'entries'";
            if (has.ExecuteScalar() is null) throw new KnowledgeException($"{storePath} exists and is not a writable store, so it was left alone.");
            using var count = c.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM entries";
            var n = (long)count.ExecuteScalar()!;
            if (n > 0) throw new KnowledgeException($"The store at {storePath} already has {n} entr{(n == 1 ? "y" : "ies")}. Restore only fills an empty store, so nothing was changed.");
        }
        catch (SqliteException ex)
        {
            throw new KnowledgeException($"{storePath} exists and can't be read as a store ({KnowledgeFormat.Escape(ex.Message)}), so it was left alone.", ex);
        }
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params object?[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
