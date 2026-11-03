using System.Collections.Concurrent;
using System.Text;

namespace CoaMcp.Pipeline.Mpq;

public sealed record UnnamedEntry(string Archive, int HashIndex, uint Name1, uint Name2);

public sealed record ListfileResult(int Archives, long UniqueNames, long Entries, long Named, long StillUnnamed, long Failed,
    IReadOnlyDictionary<string, long> RecoveredBySource, IReadOnlyDictionary<string, long> UnnamedByArchive);

/// <summary>Finds entries by name hashes. A candidate counts only if both hashes match.</summary>
public sealed class CandidateMatcher(IEnumerable<UnnamedEntry> entries)
{
    private readonly ILookup<(uint, uint), UnnamedEntry> _byHash = entries.ToLookup(e => (e.Name1, e.Name2));

    public IEnumerable<UnnamedEntry> Match(string name) => _byHash[(MpqHash.Name1(name), MpqHash.Name2(name))];
}

/// <summary>
/// Names what the archives' own listfiles left unnamed, from other archives' listfiles and strings in restored
/// files, then writes one list per archive and the union. Archives are only read.
/// </summary>
public static class ListfileBuilder
{
    public const string SourceOtherListfile = "other_listfile";

    public static ListfileResult Run(string manifestPath, string outDir, Action<string>? progress = null)
    {
        using var manifest = RestoreManifest.OpenExisting(manifestPath);
        var data = manifest.Meta(RestoreManifest.DataDirKey);
        var restored = manifest.Meta(RestoreManifest.OutDirKey);
        var output = Path.GetFullPath(outDir);
        if (SafePath.IsInside(output, data) || SafePath.IsInside(data, output))
            throw new InvalidOperationException($"--out must not overlap the client data folder {data}: {output}");
        SafePath.RefuseRepo(output);

        var unnamed = manifest.UnnamedFiles().ToDictionary(r => (r.Archive, r.HashIndex));
        var found = new Dictionary<(string, int), (string Name, string Source)>();
        void Offer(CandidateMatcher matcher, string name, string source)
        {
            foreach (var hit in matcher.Match(name)) found.TryAdd((hit.Archive, hit.HashIndex), (name, source));
        }

        if (unnamed.Count > 0)
        {
            progress?.Invoke($"  {unnamed.Count:N0} unnamed entries; trying other archives' listfiles");
            var matcher = new CandidateMatcher(unnamed.Values.Select(r => new UnnamedEntry(r.Archive, r.HashIndex, r.Name1, r.Name2)));
            foreach (var name in manifest.ListfileLines()) Offer(matcher, name, SourceOtherListfile);

            if (found.Count < unnamed.Count)
            {
                progress?.Invoke($"  {unnamed.Count - found.Count:N0} left; scanning restored files for path strings");
                var (absolute, relative) = ScanRestoredFiles(manifest, restored);
                progress?.Invoke($"  {absolute.Count:N0} distinct path strings, {relative.Count:N0} relative candidates");
                foreach (var name in absolute) Offer(matcher, name, PathStrings.SourceStrings);
                foreach (var name in absolute)
                    foreach (var (variant, source) in PathStrings.Variants(name))
                        Offer(matcher, variant, source);
                foreach (var name in relative) Offer(matcher, name, PathStrings.SourceTocRelative);
            }
            ApplyNames(manifest, data, restored, unnamed, found);
        }

        WriteLists(manifest, output, out var uniqueNames);

        var bySource = manifest.CountBy("SELECT name_source, count(*) FROM files WHERE name_source IS NOT NULL GROUP BY name_source");
        var stillUnnamed = manifest.CountBy("SELECT archive, count(*) FROM files WHERE name IS NULL GROUP BY archive");
        return new ListfileResult(manifest.ArchiveNames().Count, uniqueNames, manifest.Count("SELECT count(*) FROM files"),
            manifest.Count("SELECT count(*) FROM files WHERE name IS NOT NULL"), stillUnnamed.Values.Sum(),
            manifest.Count("SELECT count(*) FROM files WHERE status='failed'"), bySource, stillUnnamed);
    }

    /// <summary>Path strings from restored files, and those joined to the folder of the file that names them (.toc and .xml paths are relative).</summary>
    private static (HashSet<string> Absolute, HashSet<string> Relative) ScanRestoredFiles(RestoreManifest manifest, string restored)
    {
        var absolute = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var relative = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var files = manifest.RestoredFiles(PathStrings.ScanExtensions).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
        {
            var (archive, path) = file;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(SafePath.Under(Path.Combine(restored, archive), path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // gone since the restore: its row says so on re-run
            var relativeKind = path.EndsWith(".toc", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            var folder = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)].Replace('/', '\\') : "";
            foreach (var token in PathStrings.Extract(bytes))
            {
                absolute.TryAdd(token, 0);
                if (relativeKind) relative.TryAdd(folder + token, 0);
            }
        });
        return (absolute.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), relative.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Gives matched entries their names and moves their bytes to the real path, re-reading from the archive (encrypted bytes need the name).</summary>
    private static void ApplyNames(RestoreManifest manifest, string data, string restored,
        Dictionary<(string, int), FileRow> unnamed, Dictionary<(string, int), (string Name, string Source)> found)
    {
        foreach (var group in found.GroupBy(f => f.Key.Item1))
        {
            using var mpq = MpqArchive.Open(Path.Combine(data, group.Key));
            var root = Path.Combine(restored, group.Key);
            var rows = new List<FileRow>();
            foreach (var hit in group)
            {
                var old = unnamed[hit.Key];
                var lowest = manifest.LowestLocale(group.Key, old.Name1, old.Name2) == old.Locale;
                rows.Add(EntryWriter.Write(mpq, group.Key, root, old.ToEntry(), hit.Value.Name, hit.Value.Source, lowest, null, null));
                var pseudo = SafePath.Under(root, old.Path);
                if (File.Exists(pseudo)) File.Delete(pseudo);
            }
            manifest.SaveFiles(rows);
        }
    }

    private static void WriteLists(RestoreManifest manifest, string output, out long uniqueNames)
    {
        Directory.CreateDirectory(output);
        var union = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in manifest.ArchiveNames())
        {
            var names = manifest.NamesOf(archive);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            WriteLines(Path.Combine(output, SafePath.ListfileName(archive)), names);
            foreach (var name in names) union.TryAdd(name, name);
        }
        WriteLines(Path.Combine(output, "client-listfile.txt"), union.Values);
        uniqueNames = union.Count;
    }

    /// <summary>Latin-1, so a name's bytes stay exactly the bytes its hashes were computed over.</summary>
    private static void WriteLines(string path, IEnumerable<string> lines)
    {
        var temp = path + ".tmp";
        using (var w = new StreamWriter(temp, false, Encoding.Latin1) { NewLine = "\r\n" })
            foreach (var line in lines) w.WriteLine(line);
        File.Move(temp, path, overwrite: true);
    }
}
