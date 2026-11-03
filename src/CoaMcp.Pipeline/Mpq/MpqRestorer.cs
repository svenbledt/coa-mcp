using System.Diagnostics;
using System.Text;

namespace CoaMcp.Pipeline.Mpq;

public sealed record RestoreOptions(string DataDir, string OutDir, ArchiveConfig Config, int Parallelism = 1,
    Func<string, long>? FreeBytes = null, Action<string>? Progress = null);

public sealed record RestoreResult(int Archives, long Entries, long Ok, long Extracted, long Skipped, long Failed, long Unnamed,
    long PatchFiles, long DeleteMarkers, long Recovered, IReadOnlyList<string> BrokenArchives, TimeSpan Elapsed);

/// <summary>Restores every archive to &lt;out&gt;/&lt;archive&gt;/&lt;path&gt; and records every entry in manifest.sqlite. Archives are only read.</summary>
public static class MpqRestorer
{
    public const string StatusOk = "ok", StatusFailed = "failed", StatusUnnamed = "unnamed", StatusPatchFile = "patch_file", StatusDeleteMarker = "delete_marker";
    public const string SourceOwnListfile = "own_listfile", SourceInternal = "internal";

    private const long ReserveBytes = 2L << 30;   // stop before the disk fills, not when it has
    private const long PerFileOverhead = 4096;    // cluster rounding
    private const int BatchSize = 2000;

    private static readonly string[] InternalNames = ["(listfile)", "(attributes)", "(signature)", "(user data)", "(patch_metadata)"];

    public static string StatusFromFlags(uint flags)
    {
        if ((flags & MpqArchive.FlagDeleteMarker) != 0) return StatusDeleteMarker;
        if ((flags & MpqArchive.FlagExists) == 0) return StatusFailed;
        return (flags & MpqArchive.FlagPatchFile) != 0 ? StatusPatchFile : StatusOk;
    }

    public static RestoreResult Run(RestoreOptions o)
    {
        var clock = Stopwatch.StartNew();
        var data = Path.GetFullPath(o.DataDir);
        var output = Path.GetFullPath(o.OutDir);
        RefuseOutput(data, output);
        var archives = ArchiveOrder.Resolve(data, o.Config);
        var free = o.FreeBytes ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);

        var manifestPath = Path.Combine(output, "manifest.sqlite");
        CheckSpaceBeforeStart(data, output, manifestPath, archives, free);

        Directory.CreateDirectory(output);
        using var manifest = RestoreManifest.OpenForRestore(manifestPath);
        manifest.SetMeta(RestoreManifest.DataDirKey, data);
        manifest.SetMeta(RestoreManifest.OutDirKey, output);

        var progress = new Counters();
        var broken = new List<string>();
        using var stop = new CancellationTokenSource();
        Exception? fatal = null;
        var nextReport = Stopwatch.StartNew();
        Parallel.ForEach(archives, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Parallelism) }, archive =>
        {
            if (stop.IsCancellationRequested) return;
            try
            {
                RestoreArchive(data, output, archive, manifest, free, progress, stop.Token, () =>
                {
                    if (o.Progress is null || nextReport.Elapsed.TotalSeconds < 30) return;
                    lock (nextReport)
                    {
                        if (nextReport.Elapsed.TotalSeconds < 30) return;
                        nextReport.Restart();
                        o.Progress($"  {progress.Entries:N0} entries, {progress.Extracted:N0} extracted, {progress.Skipped:N0} already correct, {progress.Bytes / (1L << 30):N1} GiB ({clock.Elapsed:hh\\:mm\\:ss})");
                    }
                });
                o.Progress?.Invoke($"  done {archive.RelPath} ({clock.Elapsed:hh\\:mm\\:ss})");
            }
            catch (OutOfSpaceException ex)
            {
                fatal = ex;
                stop.Cancel();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (broken) broken.Add($"{archive.RelPath}: {ex.Message}");
                manifest.SaveArchive(archive, null, null, null, null, null, ex.Message);
            }
        });
        if (fatal is not null) throw new IOException(fatal.Message, fatal);

        var byStatus = manifest.CountBy("SELECT status, count(*) FROM files GROUP BY status");
        long Of(string s) => byStatus.GetValueOrDefault(s);
        return new RestoreResult(archives.Count, byStatus.Values.Sum(), Of(StatusOk), progress.Extracted, progress.Skipped, Of(StatusFailed), Of(StatusUnnamed),
            Of(StatusPatchFile), Of(StatusDeleteMarker), manifest.Count("SELECT count(*) FROM files WHERE status='ok' AND reason LIKE 'recovered:%'"), broken, clock.Elapsed);
    }

    private static void RefuseOutput(string data, string output)
    {
        var client = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(data)) ?? data;
        if (SafePath.IsInside(output, client) || SafePath.IsInside(client, output))
            throw new InvalidOperationException($"--out must be outside the client folder {client}: {output}");
        SafePath.RefuseRepo(output);
    }

    private static void CheckSpaceBeforeStart(string data, string output, string manifestPath, IReadOnlyList<ArchiveInfo> archives, Func<string, long> free)
    {
        long needed = 0;
        foreach (var archive in archives)
        {
            try
            {
                using var mpq = MpqArchive.Open(Path.Combine(data, archive.RelPath));
                foreach (var e in mpq.ReadEntries().Entries)
                    if (StatusFromFlags(e.Flags) == StatusOk) needed += e.Size + PerFileOverhead;
            }
            catch (IOException) { /* reported per archive once the run reaches it */ }
        }
        if (File.Exists(manifestPath))
        {
            using var old = RestoreManifest.OpenExisting(manifestPath);
            needed -= old.Count("SELECT coalesce(sum(size), 0) + count(*) * " + PerFileOverhead + " FROM files WHERE status='ok'");
        }
        var available = free(output);
        if (needed + ReserveBytes > available)
            throw new IOException($"not enough free space on {Path.GetPathRoot(output)}: the restore needs {(needed + ReserveBytes) / (1L << 30):N1} GiB including reserve, {available / (1L << 30):N1} GiB is free. Nothing was written. Pick another --out.");
    }

    private static void RestoreArchive(string data, string output, ArchiveInfo archive, RestoreManifest manifest, Func<string, long> free,
        Counters progress, CancellationToken stop, Action tick)
    {
        using var mpq = MpqArchive.Open(Path.Combine(data, archive.RelPath));
        var (entries, hashSlots, blockSlots) = mpq.ReadEntries();
        var byName = entries.ToLookup(e => (e.Name1, e.Name2));

        var lines = ReadListfile(mpq, byName);
        var names = new Dictionary<int, (string Name, string Source)>();
        var matchedLines = new List<(string, bool)>(lines.Count);
        foreach (var line in lines)
        {
            var hits = byName[(MpqHash.Name1(line), MpqHash.Name2(line))].ToList();
            matchedLines.Add((line, hits.Count > 0));
            foreach (var hit in hits) names.TryAdd(hit.HashIndex, (line, SourceOwnListfile));
        }
        foreach (var name in InternalNames)
            foreach (var hit in byName[(MpqHash.Name1(name), MpqHash.Name2(name))])
                names.TryAdd(hit.HashIndex, (name, SourceInternal));

        var prior = manifest.FilesOf(archive.RelPath);
        foreach (var e in entries)   // names a later listfile run recovered survive a restore re-run
            if (!names.ContainsKey(e.HashIndex) && prior.TryGetValue(e.HashIndex, out var old) && old.Name is not null && old.NameSource is not null
                && MpqHash.Name1(old.Name) == e.Name1 && MpqHash.Name2(old.Name) == e.Name2)
                names[e.HashIndex] = (old.Name, old.NameSource);

        manifest.SaveListfile(archive.RelPath, matchedLines);
        manifest.SaveArchive(archive, hashSlots, blockSlots, entries.Count, lines.Count, matchedLines.Count(l => !l.Item2), null);

        var root = Path.Combine(output, archive.RelPath);
        var lowest = entries.GroupBy(e => (e.Name1, e.Name2)).ToDictionary(g => g.Key, g => g.Min(e => e.Locale));
        var batch = new List<FileRow>(BatchSize);
        var count = 0;
        foreach (var e in entries)
        {
            if (stop.IsCancellationRequested) throw new OutOfSpaceException("stopped: another archive hit the free-space limit");
            var named = names.TryGetValue(e.HashIndex, out var n) ? n : default;
            prior.TryGetValue(e.HashIndex, out var previous);
            var row = EntryWriter.Write(mpq, archive.RelPath, root, e, named.Name, named.Source, e.Locale == lowest[(e.Name1, e.Name2)], previous, progress);
            batch.Add(row);
            progress.CountEntry();
            if (batch.Count >= BatchSize) { manifest.SaveFiles(batch); batch.Clear(); }
            if (++count % 256 == 0)
            {
                var left = free(output);
                if (left < ReserveBytes / 2) { manifest.SaveFiles(batch); throw new OutOfSpaceException($"stopped: only {left / (1L << 20):N0} MiB left on {Path.GetPathRoot(output)}. Free space and run the same command again to resume."); }
                tick();
            }
        }
        manifest.SaveFiles(batch);
    }

    /// <summary>The lines of the archive's own (listfile), with each name's bytes kept as a Latin-1 string.</summary>
    private static List<string> ReadListfile(MpqArchive mpq, ILookup<(uint, uint), MpqEntry> byName)
    {
        var entry = byName[(MpqHash.Name1("(listfile)"), MpqHash.Name2("(listfile)"))].Cast<MpqEntry?>().FirstOrDefault();
        if (entry is null) return [];
        var bytes = mpq.TryReadAll("(listfile)", entry.Value.Locale);
        if (bytes is null) return [];
        return Encoding.Latin1.GetString(bytes).Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public sealed class OutOfSpaceException(string message) : Exception(message);
}

/// <summary>Running totals shared across archive workers.</summary>
public sealed class Counters
{
    private long _entries, _extracted, _skipped, _bytes;
    public long Entries => Interlocked.Read(ref _entries);
    public long Extracted => Interlocked.Read(ref _extracted);
    public long Skipped => Interlocked.Read(ref _skipped);
    public long Bytes => Interlocked.Read(ref _bytes);
    public void CountEntry() => Interlocked.Increment(ref _entries);
    public void CountExtracted(long bytes) { Interlocked.Increment(ref _extracted); Interlocked.Add(ref _bytes, bytes); }
    public void CountSkipped() => Interlocked.Increment(ref _skipped);
}
