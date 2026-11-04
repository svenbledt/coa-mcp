using System.Globalization;
using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>
/// Reads which client this is, read-only, and whether the archives its source lists are the ones on disk. The launcher's state file beside the release file is the source when it exists,
/// because the launcher is what last updated the client. Otherwise CLIENT-RELEASE.json is: which revision the client says it is.
/// The note states only sizes the two sources give, and doesn't guess why they differ.
/// </summary>
public static class ClientReleaseFile
{
    private const string DataPrefix = "Data/";

    /// <summary>The launcher's state file, relative to the client folder that holds the release file.</summary>
    public const string LauncherState = ".coa-launcher/state.json";

    /// <summary>Reads the launcher state or the release file, and compares it with the archive sizes in a finished restore's manifest.</summary>
    public static ClientRelease ReadForManifest(string path, string manifestPath)
    {
        using var db = ClientWinners.OpenManifest(manifestPath);
        return ReadForArchives(path, ClientWinners.ArchiveSizes(db));
    }

    public static ClientRelease ReadForArchives(string path, IReadOnlyDictionary<string, long> archiveSizes)
    {
        var state = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, LauncherState);
        return File.Exists(state) ? ReadLauncher(state, archiveSizes) : Read(path, archiveSizes);
    }

    /// <summary>
    /// The launcher's version and its size for each archive. A mismatch is reported only for an archive that differs from its entry. One the launcher doesn't list is noted, not treated as a mismatch.
    /// A state file that can't be read is an error: falling back would hide it.
    /// </summary>
    public static ClientRelease ReadLauncher(string path, IReadOnlyDictionary<string, long> archiveSizes)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.GetString()))
                throw new InvalidDataException($"{path}: needs a text version.");
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{path}: needs a files list.");

            var listed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String || Bytes(f) is not { } size)
                    throw new InvalidDataException($"{path}: every file needs a path and a size.");
                listed[p.GetString()!] = size;
            }

            var mismatches = new List<ClientArchiveMismatch>();
            var unlisted = new List<string>();
            foreach (var (archive, actual) in archiveSizes.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                if (!listed.TryGetValue(DataPrefix + archive, out var size)) unlisted.Add(archive);
                else if (size != actual) mismatches.Add(new ClientArchiveMismatch(archive, size, actual, null));
            }

            var notes = mismatches.Select(m => $"The launcher's entry for {m.Archive} says {N(m.ListedBytes)} bytes, but the client's file is {N(m.ActualBytes)} bytes.").ToList();
            if (unlisted.Count > 0) notes.Add($"The launcher doesn't list {string.Join(", ", unlisted)}.");
            var published = root.TryGetProperty("publishedAt", out var at) && at.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? "published " + when.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "publish date not given";
            return new ClientRelease(null, published, notes.Count > 0 ? string.Join(" ", notes) : null, mismatches.Count > 0 ? mismatches : null, version.GetString());
        }
        catch (JsonException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
    }

    public static ClientRelease Read(string path, IReadOnlyDictionary<string, long> archiveSizes)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"client release file not found: {path}");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path}: expected an object.");
            if (!root.TryGetProperty("clientRevision", out var rev) || rev.ValueKind != JsonValueKind.Number || !rev.TryGetInt32(out var revision))
                throw new InvalidDataException($"{path}: needs a whole-number clientRevision.");
            if (!root.TryGetProperty("releaseId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidDataException($"{path}: needs a text releaseId.");

            var notes = new List<string>();
            var mismatches = new List<ClientArchiveMismatch>();
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in files.EnumerateObject().Where(f => f.Name.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    var archive = f.Name[DataPrefix.Length..];
                    var listed = Bytes(f.Value);
                    if (listed is null || !archiveSizes.TryGetValue(archive, out var actual) || actual == listed) continue;
                    var before = PreviousBytes(root, f.Name);
                    notes.Add(Mismatch(root, archive, revision, listed.Value, actual, before));
                    mismatches.Add(new ClientArchiveMismatch(archive, listed.Value, actual, before));
                }
            }
            return new ClientRelease(revision, id.GetString()!, notes.Count > 0 ? string.Join(" ", notes) : null, mismatches.Count > 0 ? mismatches : null);
        }
        catch (JsonException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
    }

    private static long? PreviousBytes(JsonElement root, string key) =>
        root.TryGetProperty("previousPackage", out var previous) && previous.ValueKind == JsonValueKind.Object && previous.TryGetProperty(key, out var entry) ? Bytes(entry) : null;

    private static string Mismatch(JsonElement root, string archive, int revision, long listed, long actual, long? previousBytes)
    {
        var text = $"its revision {revision} entry for {archive} says {N(listed)} bytes, but the client's file is {N(actual)} bytes";
        if (previousBytes is not { } before) return $"CLIENT-RELEASE.json: {text}.";

        var previous = root.GetProperty("previousPackage");
        var label = previous.TryGetProperty("clientRevision", out var r) && r.ValueKind == JsonValueKind.Number ? $"previous package (revision {r.GetRawText()})" : "previous package";
        return before == actual
            ? $"CLIENT-RELEASE.json: {text}, which is the size it gives for the {label}."
            : $"CLIENT-RELEASE.json: {text}; its {label} entry says {N(before)} bytes, so neither entry matches.";
    }

    private static long? Bytes(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object && (entry.TryGetProperty("bytes", out var b) || entry.TryGetProperty("size", out b)) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var n) ? n : null;

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
