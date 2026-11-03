using System.Globalization;
using System.Text.Json;
using CoaMcp.Core;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Mpq;

public sealed record PathExportResult(long Paths, long Deleted, long Unavailable, long ProjectWinners, long Folders, long Unnamed, long InternalSkipped, long Entries);

/// <summary>
/// Builds the paths layer from a restore manifest: for each client path, which archive the client would read it from.
/// Only paths, sizes and archive names leave the manifest. File content is never opened.
/// </summary>
public static class PathExporter
{
    private const string Ok = "ok", DeleteMarker = "delete_marker";

    private sealed record Row(string Archive, string Path, long Size, string Status, ushort Locale);

    public static PathExportResult Run(string manifestPath, ArchiveConfig config, string outPath, string? version = null, DateOnly? sourceDate = null,
        Action<long>? progress = null)
    {
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"manifest not found: {manifestPath}");
        version = LayerVersion.Resolve(version);
        var date = sourceDate ?? DateOnly.FromDateTime(File.GetLastWriteTimeUtc(manifestPath));
        var byArchive = config.Archives.ToDictionary(a => a.Path, StringComparer.OrdinalIgnoreCase);

        // immutable=1: never writes, never takes a lock. A manifest with unmerged WAL content is not finished, so refuse it.
        var wal = manifestPath + "-wal";
        if (File.Exists(wal) && new FileInfo(wal).Length > 0)
            throw new InvalidDataException($"{manifestPath} has unmerged changes ({Path.GetFileName(wal)} is not empty): finish or re-run the restore first.");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(manifestPath)).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        db.Open();

        CheckStatuses(db);
        CheckArchives(db, byArchive);

        long entries = 0, ok = 0, markers = 0, unnamed = 0, internalSkipped = 0;
        var writer = new LayerWriter(outPath, "paths", version);
        try
        {
            var builder = new PathsLayerBuilder(writer);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT path_lc, path, archive, size, status, locale, name IS NULL, (COALESCE(name_source, '') = 'internal' OR path_lc IN ('(listfile)', '(attributes)', '(signature)'))
                FROM files ORDER BY path_lc, locale
                """;
            using var rd = cmd.ExecuteReader();
            var group = new List<Row>();
            string? current = null;
            long written = 0;
            while (rd.Read())
            {
                entries++;
                if (rd.GetString(4) == Ok) ok++; else markers++;
                if (rd.GetBoolean(7)) { internalSkipped++; continue; }   // (listfile), (attributes): the archive's own bookkeeping, not client files
                if (rd.GetBoolean(6)) { unnamed++; continue; }           // no real name: reported in the stats, not published

                var key = rd.GetString(0);
                if (current is not null && key != current) { Flush(); }
                current = key;
                group.Add(new Row(rd.GetString(2), Normalize(rd.GetString(1)), rd.GetInt64(3), rd.GetString(4), (ushort)rd.GetInt32(5)));
            }
            if (group.Count > 0) Flush();

            void Flush()
            {
                builder.Add(Resolve(group, byArchive));
                group.Clear();
                if (++written % 50_000 == 0) progress?.Invoke(written);
            }

            builder.Finish(new Dictionary<string, string>
            {
                ["restore_date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["entries_ok"] = ok.ToString(CultureInfo.InvariantCulture),
                ["entries_delete_marker"] = markers.ToString(CultureInfo.InvariantCulture),
                ["unnamed"] = unnamed.ToString(CultureInfo.InvariantCulture),
                ["failed"] = "0",
                ["internal_skipped"] = internalSkipped.ToString(CultureInfo.InvariantCulture),
                ["archives"] = byArchive.Count.ToString(CultureInfo.InvariantCulture),
            }, date);
        }
        catch (Exception ex)
        {
            // A half-built layer must not be mistaken for a finished one.
            writer.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(outPath); } catch (IOException) { /* the original error matters more */ }
            if (ex is ArgumentException) throw new InvalidDataException("manifest holds a path that is not clean: " + ex.Message, ex);
            throw;
        }
        writer.Dispose();
        SqliteConnection.ClearAllPools();

        using var r = new LayerReader(outPath);
        var s = r.Query("SELECT key, value FROM path_stats").ToDictionary(x => (string)x["key"]!, x => long.TryParse((string)x["value"]!, out var n) ? n : 0);
        return new PathExportResult(s["paths"], s["deleted"], s["unavailable"], s["project_winner"], s["folders"], unnamed, internalSkipped, entries);
    }

    /// <summary>The manifest keeps '/'; accept '\' too, so one path never has two spellings.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Every entry must be a real file or a delete marker. Anything else means the restore is not usable, so the export stops.</summary>
    private static void CheckStatuses(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM files WHERE status NOT IN ('ok', 'delete_marker') GROUP BY status ORDER BY status";
        using var rd = cmd.ExecuteReader();
        var bad = new List<string>();
        while (rd.Read()) bad.Add($"{rd.GetInt64(1):N0} with status '{rd.GetString(0)}'");
        if (bad.Count > 0)
            throw new InvalidDataException($"the restore manifest has entries that are neither ok nor delete markers ({string.Join(", ", bad)}); fix the restore before exporting paths.");
    }

    /// <summary>The archive order in the manifest must be the one in the config: a different restore config would give different winners.</summary>
    private static void CheckArchives(SqliteConnection db, Dictionary<string, ArchiveEntry> config)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT f.archive, a.priority, a.origin, a.realm FROM files f LEFT JOIN archives a ON a.archive = f.archive";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var name = rd.GetString(0);
            if (!config.TryGetValue(name, out var entry))
                throw new InvalidDataException($"the manifest has files from archive '{name}', which the archive config does not list.");
            if (rd.IsDBNull(1)) throw new InvalidDataException($"the manifest has files from archive '{name}' but no record of that archive.");
            var realm = rd.IsDBNull(3) ? null : rd.GetString(3);
            if (rd.GetInt32(1) != entry.Priority || rd.GetString(2) != entry.Origin || realm != entry.Realm)
                throw new InvalidDataException($"archive '{name}' in the manifest (priority {rd.GetInt32(1)}, origin {rd.GetString(2)}, realm {realm ?? "none"}) disagrees with the archive config; re-run the restore with this config.");
        }
    }

    /// <summary>
    /// Picks the copy the default client reads: the highest-priority archive that is opened and not tied to one realm.
    /// If that copy is a delete marker the file is gone, whatever older archives hold.
    /// </summary>
    private static PathRecord Resolve(List<Row> rows, Dictionary<string, ArchiveEntry> config)
    {
        var holders = rows.GroupBy(r => r.Archive).Select(g => g.OrderBy(r => r.Locale).First())
            .Select(r => (Row: r, Info: config[r.Archive]))
            .OrderByDescending(h => h.Info.Priority).ThenBy(h => h.Row.Archive, StringComparer.Ordinal).ToList();

        var loaded = holders.Where(h => h.Info.Priority >= 0 && h.Info.Realm is null).ToList();
        var json = JsonSerializer.Serialize(holders.Select(h =>
        {
            var o = new Dictionary<string, object> { ["a"] = h.Row.Archive, ["o"] = h.Info.Origin };
            if (h.Row.Status == DeleteMarker) o["d"] = 1; else o["s"] = h.Row.Size;
            if (h.Info.Realm is not null) o["r"] = h.Info.Realm;
            if (h.Info.Priority < 0) o["n"] = 1;   // the client never opens this archive
            return o;
        }));

        if (loaded.Count == 0)
            return new PathRecord(holders[0].Row.Path, null, null, null, false, null, null, json);

        var win = loaded[0];
        var deleted = win.Row.Status == DeleteMarker;
        string? baseArchive = null;
        long? baseSize = null;
        if (!deleted && win.Info.Origin == "project")
        {
            var next = loaded.Skip(1).FirstOrDefault(h => h.Info.Origin != "project" && h.Row.Status == Ok);
            if (next.Row is not null) { baseArchive = next.Row.Archive; baseSize = next.Row.Size; }
        }
        return new PathRecord(win.Row.Path, deleted ? null : win.Row.Size, win.Row.Archive, win.Info.Origin, deleted, baseArchive, baseSize, json);
    }
}
