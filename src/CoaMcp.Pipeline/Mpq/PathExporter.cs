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
    public static PathExportResult Run(string manifestPath, ArchiveConfig config, string outPath, string? version = null,
        Action<long>? progress = null, ClientRelease? client = null)
    {
        version = LayerVersion.Resolve(version);
        var byArchive = config.Archives.ToDictionary(a => a.Path, StringComparer.OrdinalIgnoreCase);
        using var db = ClientWinners.OpenManifest(manifestPath);

        ClientWinners.CheckStatuses(db);
        ClientWinners.CheckArchives(db, byArchive);
        var date = ClientWinners.ClientDate(db, byArchive);

        long entries = 0, ok = 0, markers = 0, unnamed = 0, internalSkipped = 0;
        var writer = new LayerWriter(outPath, "paths", version);
        try
        {
            if (client is not null) writer.SetClient(client);
            var builder = new PathsLayerBuilder(writer);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT path_lc, path, archive, size, status, locale, name IS NULL, (COALESCE(name_source, '') = 'internal' OR path_lc IN ('(listfile)', '(attributes)', '(signature)'))
                FROM files ORDER BY path_lc, locale
                """;
            using var rd = cmd.ExecuteReader();
            var group = new List<ManifestCopy>();
            string? current = null;
            long written = 0;
            while (rd.Read())
            {
                entries++;
                if (rd.GetString(4) == ClientWinners.Ok) ok++; else markers++;
                if (rd.GetBoolean(7)) { internalSkipped++; continue; }   // (listfile), (attributes): the archive's own bookkeeping, not client files
                if (rd.GetBoolean(6)) { unnamed++; continue; }           // no real name: reported in the stats, not published

                var key = rd.GetString(0);
                if (current is not null && key != current) { Flush(); }
                current = key;
                group.Add(new ManifestCopy(rd.GetString(2), Normalize(rd.GetString(1)), rd.GetInt64(3), rd.GetString(4), (ushort)rd.GetInt32(5)));
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
                ["client_date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
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

    /// <summary>The fact the paths layer records for one path: the winner, the base copy, and every holder.</summary>
    private static PathRecord Resolve(List<ManifestCopy> rows, Dictionary<string, ArchiveEntry> config)
    {
        var copies = ClientWinners.Resolve(rows, config);
        var json = JsonSerializer.Serialize(copies.Holders.Select(h =>
        {
            var o = new Dictionary<string, object> { ["a"] = h.Copy.Archive, ["o"] = h.Archive.Origin };
            if (h.IsDeleteMarker) o["d"] = 1; else o["s"] = h.Copy.Size;
            if (h.Archive.Realm is not null) o["r"] = h.Archive.Realm;
            if (h.Archive.Priority < 0) o["n"] = 1;   // the client never opens this archive
            return o;
        }));

        if (copies.Winner is not { } win)
            return new PathRecord(copies.Holders[0].Copy.Path, null, null, null, false, null, null, json);

        var deleted = win.IsDeleteMarker;
        string? baseArchive = null;
        long? baseSize = null;
        if (!deleted && win.IsProject && copies.NextNonProject is { IsDeleteMarker: false } next)
        {
            baseArchive = next.Copy.Archive;
            baseSize = next.Copy.Size;
        }
        return new PathRecord(win.Copy.Path, deleted ? null : win.Copy.Size, win.Copy.Archive, win.Archive.Origin, deleted, baseArchive, baseSize, json);
    }
}
