using System.Globalization;
using CoaMcp.Pipeline.Notes;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Knowledge;

/// <summary>A suspicious pattern in an entry. Names the entry, the column and the kind; never the matched text.</summary>
public sealed record ScanWarning(long Id, string Column, string Kind)
{
    public override string ToString() => $"entry {Id} ({Column}) looks like a {Kind}";
}

public sealed record ExportResult(
    int Total, int Added, int Changed, int Unchanged, int SkippedTest, IReadOnlyList<string> SkippedSubjects, IReadOnlyList<string> Kept,
    IReadOnlyList<KeyValuePair<string, int>> Authors, IReadOnlyList<ScanWarning> Warnings, IReadOnlyList<string> Notes)
{
    private const int ListCap = 20;

    public void WriteSummary(TextWriter o)
    {
        if (Total == 0)
        {
            o.WriteLine("The store has no entries, so nothing was exported.");
            return;
        }
        o.WriteLine($"Added {Added}, changed {Changed}, unchanged {Unchanged} entr{(Added + Changed + Unchanged == 1 ? "y" : "ies")} in knowledge/entries.");
        if (Kept.Count > 0)
            o.WriteLine($"Kept {Kept.Count} file{(Kept.Count == 1 ? "" : "s")} that the store no longer has (the store only grows, so check where they came from): "
                        + string.Join(", ", Kept.Take(ListCap).Select(KnowledgeFormat.Escape)) + (Kept.Count > ListCap ? ", ..." : ""));
        if (SkippedTest > 0)
            o.WriteLine($"Skipped {SkippedTest} entr{(SkippedTest == 1 ? "y" : "ies")} about test: subjects (test data; --include-test exports them): "
                        + string.Join(", ", SkippedSubjects.Take(ListCap).Select(KnowledgeFormat.Escape)) + (SkippedSubjects.Count > ListCap ? ", ..." : ""));
        o.WriteLine("Entries by author (self-declared, so not verified):");
        foreach (var (author, count) in Authors.Take(ListCap)) o.WriteLine($"  {KnowledgeFormat.Escape(author)}: {count}");
        if (Authors.Count > ListCap) o.WriteLine($"  ... and {Authors.Count - ListCap} more authors");
        if (Warnings.Count > 0)
        {
            o.WriteLine($"Scan warnings ({Warnings.Count}). Review these before you commit; the matched text is not shown:");
            foreach (var w in Warnings.Take(ListCap * 5)) o.WriteLine("  " + w);
            if (Warnings.Count > ListCap * 5) o.WriteLine($"  ... and {Warnings.Count - ListCap * 5} more");
        }
        foreach (var n in Notes) o.WriteLine(n);
    }
}

/// <summary>
/// Writes a store snapshot to knowledge/entries/&lt;id&gt;.md, one file per entry. The files are built in a temp folder beside entries, read back and
/// compared with the store, and only then swapped in, so a failure leaves the folder as it was. Files the snapshot doesn't have are kept.
/// </summary>
public static class KnowledgeExporter
{
    private const string Columns = "id, subject_id, kind, requested_kind, trust, realm_json, content_md, evidence_json, field, value, position_json, author, created_at, supersedes_id, undone_at, undone_by";

    public static ExportResult Export(string snapshotPath, string outDir, bool includeTest = false)
    {
        var all = Read(snapshotPath);
        var entriesDir = Path.Combine(outDir, "entries");
        var existing = Directory.Exists(entriesDir) ? Directory.GetFileSystemEntries(entriesDir) : Array.Empty<string>();

        if (all.Count == 0)
        {
            if (existing.Length > 0)
                throw new KnowledgeException("The snapshot is empty, but knowledge/entries already has files. An empty snapshot never replaces them, so nothing was changed. Check that you are looking at the right server.");
            return new ExportResult(0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<KeyValuePair<string, int>>(), Array.Empty<ScanWarning>(), Array.Empty<string>());
        }

        var status = Statuses(all);
        var isTest = (KnowledgeEntry e) => e.SubjectId.StartsWith("test:", StringComparison.Ordinal);
        var exported = all.Where(e => includeTest || !isTest(e)).ToList();
        var skipped = all.Where(e => !includeTest && isTest(e)).ToList();

        var authors = exported.GroupBy(e => e.Author).Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderByDescending(a => a.Value).ThenBy(a => a.Key, StringComparer.Ordinal).ToList();
        var warnings = Scan(exported);
        var skippedSubjects = skipped.Select(e => e.SubjectId).Distinct().Order(StringComparer.Ordinal).ToList();

        if (exported.Count == 0)   // only test entries: nothing to write, so nothing is touched
            return new ExportResult(all.Count, 0, 0, 0, skipped.Count, skippedSubjects, Array.Empty<string>(), authors, warnings, Array.Empty<string>());

        if (existing.Any(Directory.Exists)) throw new KnowledgeException("knowledge/entries contains a folder. It should hold only entry files, so nothing was changed.");

        Directory.CreateDirectory(outDir);
        var tag = Guid.NewGuid().ToString("N")[..12];
        var temp = Path.Combine(outDir, ".entries-new-" + tag);
        var old = Path.Combine(outDir, ".entries-old-" + tag);
        var notes = new List<string>();
        try
        {
            Directory.CreateDirectory(temp);
            var bytesById = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var e in exported)
            {
                var name = KnowledgeFormat.FileName(e.Id);
                var bytes = KnowledgeFormat.Render(e, status[e.Id]);
                File.WriteAllBytes(Path.Combine(temp, name), bytes);
                bytesById[name] = bytes;
            }

            // Read every file back: what is on disk must parse to exactly the stored row.
            foreach (var e in exported)
            {
                var name = KnowledgeFormat.FileName(e.Id);
                var back = File.ReadAllBytes(Path.Combine(temp, name));
                if (!back.AsSpan().SequenceEqual(bytesById[name]) || KnowledgeFormat.Parse(back, name) != e)
                    throw new KnowledgeException($"{name} did not read back the way it was written, so nothing was changed.");
            }

            int added = 0, changed = 0, unchanged = 0;
            foreach (var (name, bytes) in bytesById)
            {
                var current = Path.Combine(entriesDir, name);
                if (!File.Exists(current)) added++;
                else if (File.ReadAllBytes(current).AsSpan().SequenceEqual(bytes)) unchanged++;
                else changed++;
            }

            var kept = existing.Select(Path.GetFileName).Where(n => !bytesById.ContainsKey(n!)).Order(StringComparer.Ordinal).ToList();
            foreach (var name in kept) File.Copy(Path.Combine(entriesDir, name!), Path.Combine(temp, name!));

            if (Directory.Exists(entriesDir)) MoveFolder(entriesDir, old);
            try { MoveFolder(temp, entriesDir); }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(entriesDir)) MoveFolder(old, entriesDir);
                throw;
            }
            try { if (Directory.Exists(old)) Directory.Delete(old, true); }
            catch (IOException ex) { notes.Add($"Could not delete the old folder {old} ({KnowledgeFormat.Escape(ex.Message)}). Delete it by hand; it is not part of the export."); }

            return new ExportResult(all.Count, added, changed, unchanged, skipped.Count, skippedSubjects, kept!, authors, warnings, notes);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { /* the export has already succeeded or failed on its own terms */ }
        }
    }

    /// <summary>Renames a folder, trying again for a few seconds, because on Windows something like a virus scanner can hold a just-written file for a moment. A lasting failure is thrown.</summary>
    private static void MoveFolder(string from, string to)
    {
        const int Attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts && Directory.Exists(from) && !Directory.Exists(to))
            {
                Thread.Sleep(100 << attempt);   // 200 ms up to 3.2 s, about 6 s in all
            }
        }
    }

    private static List<ScanWarning> Scan(IEnumerable<KnowledgeEntry> entries)
    {
        var warnings = new List<ScanWarning>();
        foreach (var e in entries)
        {
            var columns = new (string Name, string? Text)[]
            {
                ("content_md", e.ContentMd), ("subject_id", e.SubjectId), ("field", e.Field), ("value", e.Value),
                ("evidence_json", e.EvidenceJson), ("position_json", e.PositionJson), ("author", e.Author),
            };
            foreach (var (name, text) in columns)
                if (text is not null)
                    warnings.AddRange(SecretScan.Find(text).Select(h => h.Kind).Distinct().Select(kind => new ScanWarning(e.Id, name, kind)));
        }
        return warnings;
    }

    /// <summary>Undone, or a page with a newer live page of the same or higher trust (a lower number), or live. The same rule as the server's.</summary>
    private static Dictionary<long, string> Statuses(List<KnowledgeEntry> all)
    {
        var status = new Dictionary<long, string>();
        foreach (var e in all.Where(e => e.UndoneAt is not null)) status[e.Id] = KnowledgeFormat.Undone;
        foreach (var e in all.Where(e => e.UndoneAt is null && e.Kind != "page")) status[e.Id] = KnowledgeFormat.Live;
        foreach (var subject in all.Where(e => e.UndoneAt is null && e.Kind == "page").GroupBy(e => e.SubjectId))
        {
            var bestNewerTrust = long.MaxValue;   // the highest trust (lowest number) among newer live pages
            foreach (var e in subject.OrderByDescending(e => e.Id))
            {
                status[e.Id] = bestNewerTrust <= e.Trust ? KnowledgeFormat.Superseded : KnowledgeFormat.Live;
                bestNewerTrust = Math.Min(bestNewerTrust, e.Trust);
            }
        }
        return status;
    }

    private static List<KnowledgeEntry> Read(string path)
    {
        if (!File.Exists(path)) throw new KnowledgeException($"There is no snapshot file at {path}.");
        var rows = new List<KnowledgeEntry>();
        var seen = new HashSet<long>();
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM entries ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            var n = 0;
            while (r.Read())
            {
                n++;
                // The id becomes part of a file name, so it must be a positive whole number and nothing else.
                if (r.GetValue(0) is not long id || id <= 0)
                    throw new KnowledgeException($"Row {n} of the snapshot has an id that is not a positive whole number, so nothing was exported.");
                if (!seen.Add(id)) throw new KnowledgeException($"The snapshot has entry {id} twice, so nothing was exported.");
                string Req(int i, string col) => r.GetValue(i) as string ?? throw new KnowledgeException($"Entry {id}: {col} is missing or not text, so nothing was exported.");
                string? Opt(int i, string col) => r.IsDBNull(i) ? null : Req(i, col);
                long? OptNum(int i, string col) => r.IsDBNull(i) ? null : r.GetValue(i) as long? ?? throw new KnowledgeException($"Entry {id}: {col} is not a whole number, so nothing was exported.");
                rows.Add(new KnowledgeEntry(id, Req(1, "subject_id"), Req(2, "kind"), Req(3, "requested_kind"), OptNum(4, "trust") ?? throw new KnowledgeException($"Entry {id}: trust is missing."),
                    Req(5, "realm_json"), Opt(8, "field"), Opt(9, "value"), Opt(10, "position_json"), Req(7, "evidence_json"), Req(11, "author"), Req(12, "created_at"),
                    OptNum(13, "supersedes_id"), Opt(14, "undone_at"), Opt(15, "undone_by"), Req(6, "content_md")));
            }
        }
        catch (SqliteException ex)
        {
            throw new KnowledgeException($"The snapshot is not a readable store: {KnowledgeFormat.Escape(ex.Message)}", ex);
        }
        return rows.OrderBy(e => e.Id).ToList();
    }
}
