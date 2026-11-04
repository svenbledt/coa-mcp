using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Wdb;

/// <summary>A cache folder the owner has named together with the server it came from. Only listed folders are read.</summary>
public sealed record WdbSource(string Folder, string Server, IReadOnlyList<string>? Realms = null);

public sealed class WdbSources
{
    public IReadOnlyList<WdbSource> Sources { get; }
    private WdbSources(IReadOnlyList<WdbSource> sources) => Sources = sources;

    private sealed record Raw(List<WdbSource>? Sources);

    public static WdbSources Load(string path)
    {
        Raw? raw;
        try { raw = JsonSerializer.Deserialize<Raw>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException ex) { throw new InvalidDataException($"{Path.GetFileName(path)} is not valid: {ex.Message}", ex); }
        var sources = raw?.Sources ?? new();
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s.Folder)) throw new InvalidDataException("A cache source has no folder.");
            if (string.IsNullOrWhiteSpace(s.Server)) throw new InvalidDataException($"Cache folder '{s.Folder}' doesn't name its server; say which server the cache came from.");
        }
        var slugs = sources.Select(s => WdbImporter.Slug(s.Server)).ToList();
        if (slugs.Any(string.IsNullOrEmpty) || slugs.Distinct().Count() != slugs.Count)
            throw new InvalidDataException("Each cache source needs its own server name (letters or digits); two sources can't share one.");
        return new WdbSources(sources);
    }
}

public sealed record WdbFileReport(string Folder, string Name, int Records, bool Parsed, uint Build, string Locale);

public sealed record WdbImportResult(bool Written, int Entities, IReadOnlyList<WdbFileReport> Files, IReadOnlyList<string> SkippedFolders, string Message);

/// <summary>
/// Builds the cache layer from the player caches listed in config/wdb-sources.json. Creature records are read with the
/// 3.3.5 layout. Item, quest, game object and NPC caches are recognised by header and counted, and no field is read from them:
/// a real item cache does not follow the standard item layout, and the layouts are not guessed.
/// With nothing to publish, no layer is written.
/// </summary>
public static partial class WdbImporter
{
    /// <summary>File name to (magic, parsed type). A null type means the file is recognised and counted only.</summary>
    private static readonly (string File, string Magic, string? Type)[] Known =
    {
        ("creaturecache.wdb", "WMOB", "cache-creature"),
        ("itemcache.wdb", "WIDB", null),
        ("questcache.wdb", "WQST", null),
        ("gameobjectcache.wdb", "WGOB", null),
        ("npccache.wdb", "WNPC", null),
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    public static string Slug(string server) => NonSlug().Replace(server.ToLowerInvariant(), "-").Trim('-');

    public static WdbImportResult Run(string configPath, string outPath, string? scanRoot, string? version = null)
    {
        version = LayerVersion.Resolve(version);
        var config = WdbSources.Load(configPath);
        var entities = new List<Entity>();
        var reports = new List<WdbFileReport>();

        foreach (var source in config.Sources)
        {
            if (!Directory.Exists(source.Folder)) throw new InvalidDataException($"Cache folder '{source.Folder}' (server {source.Server}) does not exist.");
            var realm = new RealmSet((source.Realms ?? Array.Empty<string>()).Select(r => r.Trim().ToLowerInvariant()).ToList(), false);
            foreach (var (file, magic, type) in Known)
            {
                var path = Path.Combine(source.Folder, file);
                if (!File.Exists(path)) continue;
                var wdb = WdbFile.Read(path, magic);
                reports.Add(new WdbFileReport(source.Folder, file, wdb.Records.Count, type is not null, wdb.Build, wdb.Locale));
                if (type is null) continue;
                var modified = DateOnly.FromDateTime(File.GetLastWriteTimeUtc(path));
                foreach (var rec in wdb.Records)
                    entities.Add(Build(type, source, realm, file, modified, wdb, rec));
            }
        }

        var skipped = scanRoot is null ? new List<string>() : UnlistedFolders(scanRoot, config);
        if (entities.Count == 0)
            return new WdbImportResult(false, 0, reports, skipped,
                config.Sources.Count == 0
                    ? "config/wdb-sources.json lists no cache folders, so there is nothing to publish and no layer was written."
                    : "The listed cache folders held no creature records, so there is nothing to publish and no layer was written.");

        using var w = new LayerWriter(outPath, "cache", version);
        foreach (var e in entities) w.Add(e);
        w.Finish();
        return new WdbImportResult(true, entities.Count, reports, skipped, $"cache layer v{version} written to {outPath}: {entities.Count} records.");
    }

    private static Entity Build(string type, WdbSource source, RealmSet realm, string file, DateOnly modified, WdbFile wdb, WdbRecord rec)
    {
        var sourceText = $"{source.Server} player cache, {file}";
        var note = $"Exactly what the server {source.Server} sent to a player's client. Source date is the file modified time ({modified:yyyy-MM-dd}), not the day the record was seen.\n";
        var key = $"{Slug(source.Server)}:{rec.Entry.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            if (type == "cache-creature")
            {
                var c = WdbRecords.Creature(rec.Entry, rec.Data);
                var body = new StringBuilder();
                if (c.SubName.Length > 0) body.Append("Subname: ").Append(c.SubName).Append('\n');
                body.Append($"Creature type: {c.Type}\nFamily: {c.Family}\nRank: {c.Rank}\nType flags: 0x{c.TypeFlags:X}\n");
                body.Append("Display ids: ").Append(c.DisplayIds.Count == 0 ? "none" : string.Join(", ", c.DisplayIds)).Append('\n');
                body.Append(note);
                var data = JsonSerializer.Serialize(new { entry = c.Entry, name = c.Name, subName = c.SubName, type = c.Type, family = c.Family, rank = c.Rank, typeFlags = c.TypeFlags, displayIds = c.DisplayIds });
                return new Entity($"cache-creature:{key}", type, Title(c.Name, c.Entry), realm, Trust.Cache, sourceText, modified, body.ToString(), data, Array.Empty<Link>());
            }
            throw new InvalidOperationException($"No reader for {type}.");
        }
        catch (WdbLayoutException ex)
        {
            throw new InvalidDataException($"{wdb.Name} (server {source.Server}): record {rec.Entry} at offset {rec.Offset} doesn't fit the 3.3.5 layout: {ex.Message}. Fields are not guessed; this cache may use a custom layout.");
        }
    }

    private static string Title(string name, uint entry) => name.Length > 0 ? name : $"(unnamed {entry})";

    private static List<string> UnlistedFolders(string scanRoot, WdbSources config)
    {
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var listed = config.Sources.Select(s => Norm(s.Folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateDirectories(scanRoot, "*", SearchOption.AllDirectories).Append(scanRoot)
            .Where(d => Directory.EnumerateFiles(d, "*.wdb").Any())
            .Where(d => !listed.Contains(Norm(d)))
            .OrderBy(d => d, StringComparer.Ordinal).ToList();
    }
}
