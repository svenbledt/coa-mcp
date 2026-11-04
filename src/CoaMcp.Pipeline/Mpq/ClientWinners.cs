using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>One archive's entry for a path, as the manifest has it.</summary>
public sealed record ManifestCopy(string Archive, string Path, long Size, string Status, ushort Locale);

/// <summary>A copy of a path, with the reviewed facts about the archive holding it.</summary>
public readonly record struct HeldCopy(ManifestCopy Copy, ArchiveEntry Archive)
{
    public bool IsDeleteMarker => Copy.Status == ClientWinners.DeleteMarker;
    public bool IsProject => Archive.Origin == "project";
}

/// <summary>All copies of one path. <see cref="Loaded"/> is what the default client can read, highest priority first; its first is the winner.</summary>
public sealed record ClientCopies(IReadOnlyList<HeldCopy> Holders, IReadOnlyList<HeldCopy> Loaded)
{
    public HeldCopy? Winner => Loaded.Count > 0 ? Loaded[0] : null;

    /// <summary>The next copy below the winner that isn't from a project-built archive, which may be a delete marker.</summary>
    public HeldCopy? NextNonProject
    {
        get
        {
            foreach (var h in Loaded.Skip(1)) if (!h.IsProject) return h;
            return null;
        }
    }
}

/// <summary>
/// The default client's rules for which copy of a file it reads, and the checks a manifest must pass before those rules mean anything.
/// Everything that needs a winner goes through here.
/// </summary>
public static class ClientWinners
{
    public const string Ok = "ok", DeleteMarker = "delete_marker";

    /// <summary>Opens a finished manifest read-only. immutable=1 never writes and never takes a lock; a manifest with unmerged WAL content is not finished, so it is refused.</summary>
    public static SqliteConnection OpenManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"manifest not found: {manifestPath}");
        var wal = manifestPath + "-wal";
        if (File.Exists(wal) && new FileInfo(wal).Length > 0)
            throw new InvalidDataException($"{manifestPath} has unmerged changes ({Path.GetFileName(wal)} is not empty): finish or re-run the restore first.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(manifestPath)).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        db.Open();
        return db;
    }

    /// <summary>Every entry must be a real file or a delete marker. Anything else means the restore is not usable, so the export stops.</summary>
    public static void CheckStatuses(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM files WHERE status NOT IN ('ok', 'delete_marker') GROUP BY status ORDER BY status";
        using var rd = cmd.ExecuteReader();
        var bad = new List<string>();
        while (rd.Read()) bad.Add($"{rd.GetInt64(1):N0} with status '{rd.GetString(0)}'");
        if (bad.Count > 0)
            throw new InvalidDataException($"the restore manifest has entries that are neither ok nor delete markers ({string.Join(", ", bad)}); fix the restore before exporting.");
    }

    /// <summary>The archive order in the manifest must be the one in the config: a different restore config would give different winners.</summary>
    public static void CheckArchives(SqliteConnection db, Dictionary<string, ArchiveEntry> config)
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
    /// Sorts the copies of one path. The default client reads the highest-priority archive it opens that isn't tied to one realm.
    /// If that copy is a delete marker the file is gone, whatever older archives hold.
    /// </summary>
    public static ClientCopies Resolve(IEnumerable<ManifestCopy> copies, IReadOnlyDictionary<string, ArchiveEntry> config)
    {
        var holders = copies.GroupBy(c => c.Archive).Select(g => g.OrderBy(c => c.Locale).First())
            .Select(c => new HeldCopy(c, config[c.Archive]))
            .OrderByDescending(h => h.Archive.Priority).ThenBy(h => h.Copy.Archive, StringComparer.Ordinal).ToList();
        return new ClientCopies(holders, holders.Where(h => h.Archive.Priority >= 0 && h.Archive.Realm is null).ToList());
    }

    /// <summary>
    /// The one date every client layer carries: the newest modified time, in UTC, among the archives the default client reads.
    /// An archive it reads with no recorded time would make that date a guess, so it is refused.
    /// </summary>
    public static DateOnly ClientDate(SqliteConnection db, IReadOnlyDictionary<string, ArchiveEntry> config)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT archive, file_mtime FROM archives";
        using var rd = cmd.ExecuteReader();
        DateOnly? newest = null;
        while (rd.Read())
        {
            var archive = rd.GetString(0);
            if (!config.TryGetValue(archive, out var entry) || entry.Priority < 0 || entry.Realm is not null) continue;
            if (rd.IsDBNull(1)) throw new InvalidDataException($"the manifest records no modified time for archive '{archive}', so the client has no date; re-run the restore.");
            var date = DateOnly.FromDateTime(new DateTime(rd.GetInt64(1), DateTimeKind.Utc));
            if (newest is null || date > newest) newest = date;
        }
        return newest ?? throw new InvalidDataException("the manifest records no archive the default client reads, so the client has no date; re-run the restore.");
    }

    /// <summary>Each archive's size on the client's disk, as the restore recorded it.</summary>
    public static Dictionary<string, long> ArchiveSizes(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT archive, file_size FROM archives WHERE file_size IS NOT NULL";
        using var rd = cmd.ExecuteReader();
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (rd.Read()) sizes[rd.GetString(0)] = rd.GetInt64(1);
        return sizes;
    }

    public static string Text(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
