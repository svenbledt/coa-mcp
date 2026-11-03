using System.Text.Json;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>An archive on disk with its load priority. Higher priority wins; below zero means the client never opens it.</summary>
public record ArchiveInfo(string RelPath, int Priority, string Origin, string? Realm);

/// <summary>One reviewed archive line in config/archives.json.</summary>
public record ArchiveEntry(string Path, int Priority, string Origin, string? Realm, string Rule);

/// <summary>config/archives.json: each archive's priority and origin, plus the evidence for each order rule.</summary>
public record ArchiveConfig(IReadOnlyDictionary<string, string> Rules, IReadOnlyList<ArchiveEntry> Archives)
{
    private static readonly string[] Origins = ["ascension", "project", "unknown"];

    public static ArchiveConfig Load(string path)
    {
        ArchiveConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ArchiveConfig>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
        if (config?.Archives is null || config.Rules is null) throw new InvalidDataException($"{path}: needs rules and archives");

        var priorities = new HashSet<int>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in config.Archives)
        {
            if (!Origins.Contains(a.Origin)) throw new InvalidDataException($"{path}: {a.Path} has unknown origin '{a.Origin}'");
            if (!priorities.Add(a.Priority)) throw new InvalidDataException($"{path}: {a.Path} repeats priority {a.Priority}");
            if (!paths.Add(a.Path)) throw new InvalidDataException($"{path}: {a.Path} is listed twice");
            if (SafePath.Normalize(a.Path, out _) != a.Path) throw new InvalidDataException($"{path}: {a.Path} is not a clean relative path");
        }
        return config;
    }
}

public static class ArchiveOrder
{
    /// <summary>The archives under <paramref name="dataDir"/>, highest priority first. An archive the config doesn't list is an error.</summary>
    public static IReadOnlyList<ArchiveInfo> Resolve(string dataDir, ArchiveConfig config)
    {
        var known = config.Archives.ToDictionary(a => a.Path, StringComparer.OrdinalIgnoreCase);
        var found = new List<ArchiveInfo>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 };
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.mpq", options))
        {
            var rel = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
            if (!known.TryGetValue(rel, out var entry))
                throw new InvalidDataException($"archive on disk is not listed in the archive config: {rel}");
            found.Add(new ArchiveInfo(entry.Path, entry.Priority, entry.Origin, entry.Realm));
        }
        return found.OrderByDescending(a => a.Priority).ToList();
    }
}
