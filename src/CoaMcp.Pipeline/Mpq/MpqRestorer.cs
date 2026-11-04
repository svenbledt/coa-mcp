using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace CoaMcp.Pipeline.Mpq;

/// <param name="AllowEmpty">Lets a changed archive that now holds only internal files replace a restore that had real entries.</param>
/// <param name="Checkpoint">For tests: called at each step of a changed archive's swap, as "&lt;archive&gt;: staged|committed|old moved|new moved", so a crash there can be simulated.</param>
public sealed record RestoreOptions(string DataDir, string OutDir, ArchiveConfig Config, int Parallelism = 1,
    Func<string, long>? FreeBytes = null, Action<string>? Progress = null, bool PruneGone = false, bool AllowEmpty = false, Action<string>? Checkpoint = null);

public sealed record RestoreResult(int Archives, long Entries, long Ok, long Extracted, long Skipped, long Failed, long Unnamed,
    long PatchFiles, long DeleteMarkers, long Recovered, IReadOnlyList<string> BrokenArchives, IReadOnlyList<string> ChangedArchives, IReadOnlyList<string> GoneArchives,
    IReadOnlyDictionary<string, long> FailedByArchive, TimeSpan Elapsed);

/// <summary>
/// Restores every archive to &lt;out&gt;/&lt;archive&gt;/&lt;path&gt; and records every entry in manifest.sqlite. Archives are only read.
/// An archive seen for the first time, or unchanged since its last run, is written in place, skipping files that are already
/// right. An archive whose size or modified time changed is extracted whole into &lt;out&gt;/.staging and swapped in only if
/// all of it passed; otherwise the older restore of it stays exactly as it was.
/// </summary>
public static class MpqRestorer
{
    public const string StatusOk = "ok", StatusFailed = "failed", StatusUnnamed = "unnamed", StatusPatchFile = "patch_file", StatusDeleteMarker = "delete_marker";
    public const string SourceOwnListfile = "own_listfile", SourceInternal = "internal";
    public const string StagingFolder = ".staging";

    private const long ReserveBytes = 2L << 30;   // stop before the disk fills, not when it has
    private const long PerFileOverhead = 4096;    // cluster rounding
    private const int BatchSize = 2000;

    private static readonly HashSet<string> InternalNames = ["(listfile)", "(attributes)", "(signature)", "(user data)", "(patch_metadata)"];

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
        var archives = ArchiveOrder.Resolve(data, o.Config);   // finding none is an error, before anything is touched
        var free = o.FreeBytes ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);

        var manifestPath = Path.Combine(output, "manifest.sqlite");
        RefuseOtherClient(data, output, manifestPath);
        CheckSpaceBeforeStart(data, output, manifestPath, archives, free);

        Directory.CreateDirectory(output);
        using var manifest = RestoreManifest.OpenForRestore(manifestPath);
        manifest.SetMeta(RestoreManifest.DataDirKey, data);
        manifest.SetMeta(RestoreManifest.OutDirKey, output);
        FinishPendingSwaps(output, manifest, archives.Select(a => a.RelPath).Concat(manifest.ArchiveNames()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), o.Progress);

        var onDisk = archives.Select(a => a.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var goneArchives = manifest.ArchiveNames().Where(a => !onDisk.Contains(a)).ToList();
        if (o.PruneGone)
            foreach (var gone in goneArchives)
            {
                if (SafePath.Normalize(gone, out _) == gone)   // an unsafe name never named a folder of ours: its rows go, no file does
                {
                    var goneRoot = SafePath.Under(output, gone);
                    RestoredFiles.Remove(goneRoot, manifest.FilesOf(gone).Values.Select(r => r.Path));
                    RestoredFiles.PruneEmptyFolders(goneRoot, output);
                }
                manifest.DropArchive(gone);
                o.Progress?.Invoke($"  pruned {gone}: no longer on disk");
            }

        var progress = new Counters();
        var broken = new List<string>();
        var changed = new ConcurrentBag<string>();
        using var stop = new CancellationTokenSource();
        Exception? fatal = null;
        var nextReport = Stopwatch.StartNew();
        Parallel.ForEach(archives, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Parallelism) }, archive =>
        {
            if (stop.IsCancellationRequested) return;
            try
            {
                var run = new ArchiveRun(data, output, archive, manifest, free, progress, stop.Token, o.AllowEmpty, o.Checkpoint, () =>
                {
                    if (o.Progress is null || nextReport.Elapsed.TotalSeconds < 30) return;
                    lock (nextReport)
                    {
                        if (nextReport.Elapsed.TotalSeconds < 30) return;
                        nextReport.Restart();
                        o.Progress($"  {progress.Entries:N0} entries, {progress.Extracted:N0} extracted, {progress.Skipped:N0} already correct, {progress.Bytes / (1L << 30):N1} GiB ({clock.Elapsed:hh\\:mm\\:ss})");
                    }
                });
                if (run.Restore()) changed.Add(archive.RelPath);
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
                manifest.MarkBroken(archive, ex.Message);
            }
        });
        if (fatal is not null) throw new IOException(fatal.Message, fatal);
        RemoveEmptyStaging(output);

        var byStatus = manifest.CountBy("SELECT status, count(*) FROM files GROUP BY status");
        long Of(string s) => byStatus.GetValueOrDefault(s);
        return new RestoreResult(archives.Count, byStatus.Values.Sum(), Of(StatusOk), progress.Extracted, progress.Skipped, Of(StatusFailed), Of(StatusUnnamed),
            Of(StatusPatchFile), Of(StatusDeleteMarker), manifest.Count("SELECT count(*) FROM files WHERE status='ok' AND reason LIKE 'recovered:%'"), broken, changed.OrderBy(a => a, StringComparer.Ordinal).ToList(), goneArchives,
            manifest.CountBy($"SELECT archive, count(*) FROM files WHERE status='{StatusFailed}' GROUP BY archive"), clock.Elapsed);
    }

    private static void RefuseOutput(string data, string output)
    {
        var client = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(data)) ?? data;
        if (SafePath.IsInside(output, client) || SafePath.IsInside(client, output))
            throw new InvalidOperationException($"--out must be outside the client folder {client}: {output}");
        SafePath.RefuseRepo(output);
    }

    /// <summary>A manifest belongs to one client folder. Another folder's archives would look like changes and replace this restore.</summary>
    private static void RefuseOtherClient(string data, string output, string manifestPath)
    {
        if (!File.Exists(manifestPath)) return;
        using var old = RestoreManifest.OpenExisting(manifestPath);
        var recorded = old.Meta(RestoreManifest.DataDirKey);
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(recorded)).Equals(Path.TrimEndingDirectorySeparator(data), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"--data is {data}, but the restore in {output} came from {recorded}. Nothing was changed. Use that --data, or another --out for a different client.");
    }

    private static void CheckSpaceBeforeStart(string data, string output, string manifestPath, IReadOnlyList<ArchiveInfo> archives, Func<string, long> free)
    {
        long needed = 0;
        foreach (var archive in archives)
        {
            try
            {
                using var mpq = MpqArchive.Open(Path.Combine(data, archive.RelPath));
                needed += BytesToRestore(mpq.ReadEntries().Entries);
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

    private static long BytesToRestore(IEnumerable<MpqEntry> entries) =>
        entries.Where(e => e.BlockValid && StatusFromFlags(e.Flags) == StatusOk).Sum(e => e.Size + PerFileOverhead);

    private static string StagingDir(string output, string archive) =>
        SafePath.Under(Path.Combine(output, StagingFolder), archive.Replace('/', '_').Replace('\\', '_'));

    /// <summary>
    /// Every swap the manifest says is pending has new rows committed and a complete staged folder, so it is finished here.
    /// A set-aside folder with no note whose live folder is missing holds the only copies, so it goes back where it was.
    /// Whatever else is under .staging never reached the manifest and is removed.
    /// </summary>
    private static void FinishPendingSwaps(string output, RestoreManifest manifest, IReadOnlyList<string> archiveNames, Action<string>? progress)
    {
        foreach (var archive in manifest.PendingSwaps())
        {
            if (SafePath.Normalize(archive, out _) == archive) SwapIntoPlace(output, archive, manifest, null);
            else manifest.ClearPendingSwap(archive);   // never one of ours: nothing on disk belongs to it
            progress?.Invoke($"  finished the interrupted swap of {archive}");
        }
        foreach (var archive in archiveNames.Where(a => SafePath.Normalize(a, out _) == a))
        {
            var root = SafePath.Under(output, archive);
            var old = StagingDir(output, archive) + ".old";
            if (!Directory.Exists(old) || Directory.Exists(root)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Directory.Move(old, root);
            progress?.Invoke($"  put {archive}'s set-aside folder back: its swap never reached the manifest");
        }
        var staging = Path.Combine(output, StagingFolder);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
    }

    /// <summary>
    /// Puts an archive's staged folder where its live one is: old aside, new in, old removed, pending note cleared. Each step
    /// is skipped if a previous run already did it, so this can run again after a crash at any point.
    /// </summary>
    private static void SwapIntoPlace(string output, string archive, RestoreManifest manifest, Action<string>? checkpoint)
    {
        var root = SafePath.Under(output, archive);
        var staging = StagingDir(output, archive);
        var old = staging + ".old";
        if (Directory.Exists(staging))
        {
            if (Directory.Exists(root))
            {
                if (Directory.Exists(old)) Directory.Delete(old, true);
                Directory.Move(root, old);
                checkpoint?.Invoke($"{archive}: old moved");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Directory.Move(staging, root);
            checkpoint?.Invoke($"{archive}: new moved");
        }
        if (Directory.Exists(old)) Directory.Delete(old, true);
        manifest.ClearPendingSwap(archive);
    }

    /// <summary>Removes .staging once no archive is using it. Anything still inside belongs to a swap that didn't finish, and stays for the next run.</summary>
    private static void RemoveEmptyStaging(string output)
    {
        var staging = Path.Combine(output, StagingFolder);
        if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
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

    /// <summary>One archive's restore: reads its tables and names, then writes it in place or stages and swaps it.</summary>
    private sealed class ArchiveRun(string data, string output, ArchiveInfo archive, RestoreManifest manifest, Func<string, long> free,
        Counters progress, CancellationToken stop, bool allowEmpty, Action<string>? checkpoint, Action tick)
    {
        /// <summary>Returns true if the archive had changed and its new version was swapped in.</summary>
        public bool Restore()
        {
            var path = Path.Combine(data, archive.RelPath);
            var identity = ArchiveIdentity.Of(path);
            using var mpq = MpqArchive.Open(path);
            var (entries, hashSlots, blockSlots) = mpq.ReadEntries();
            if (ArchiveIntegrity.Problem(mpq, entries) is { } problem)
                throw new IOException($"archive looks damaged or half-written, so nothing was changed for it: {problem}");

            var byName = entries.ToLookup(e => (e.Name1, e.Name2));
            var lines = ReadListfile(mpq, byName);
            var names = new Dictionary<int, (string Name, string Source)>();
            var matchedLines = new List<(string Name, bool Matched)>(lines.Count);
            foreach (var line in lines)
            {
                var hits = byName[(MpqHash.Name1(line), MpqHash.Name2(line))].ToList();
                matchedLines.Add((line, hits.Count > 0));
                foreach (var hit in hits) names.TryAdd(hit.HashIndex, (line, SourceOwnListfile));
            }
            foreach (var name in InternalNames)
                foreach (var hit in byName[(MpqHash.Name1(name), MpqHash.Name2(name))])
                    names.TryAdd(hit.HashIndex, (name, SourceInternal));

            var oldRows = manifest.FilesOf(archive.RelPath);
            // A name a listfile run recovered is still right if it still matches both hashes, whatever slot the entry sits in now.
            var oldNames = oldRows.Values.Where(r => r.Name is not null && r.NameSource is not null).ToLookup(r => (r.Name1, r.Name2));
            foreach (var e in entries)
                if (!names.ContainsKey(e.HashIndex) && oldNames[(e.Name1, e.Name2)].FirstOrDefault(r => MpqHash.Name1(r.Name!) == e.Name1 && MpqHash.Name2(r.Name!) == e.Name2) is { } old)
                    names[e.HashIndex] = (old.Name!, old.NameSource!);

            var facts = new ArchiveFacts(hashSlots, blockSlots, entries.Count, lines.Count, matchedLines.Count(l => !l.Matched));
            var lowest = entries.GroupBy(e => (e.Name1, e.Name2)).ToDictionary(g => g.Key, g => g.Min(e => e.Locale));
            FileRow Write(MpqEntry e, string root, FileRow? previous)
            {
                var named = names.TryGetValue(e.HashIndex, out var found) ? found : default;
                return EntryWriter.Write(mpq, archive.RelPath, root, e, named.Name, named.Source, e.Locale == lowest[(e.Name1, e.Name2)], previous, progress);
            }

            var recorded = manifest.IdentityOf(archive.RelPath);
            if (recorded is not null && recorded != identity)
            {
                RestoreWhole(entries, identity, facts, matchedLines, oldRows, Write);
                return true;
            }
            RestoreInPlace(entries, identity, facts, matchedLines, oldRows, recorded, Write);
            return false;
        }

        /// <summary>A first restore, or an unchanged archive: files that are already right are skipped, the rest written straight to the archive's folder.</summary>
        private void RestoreInPlace(List<MpqEntry> entries, ArchiveIdentity identity, ArchiveFacts facts, List<(string Name, bool Matched)> lines,
            Dictionary<int, FileRow> oldRows, ArchiveIdentity? recorded, Func<MpqEntry, string, FileRow?, FileRow> write)
        {
            var root = SafePath.Under(output, archive.RelPath);
            manifest.SaveListfile(archive.RelPath, lines);
            manifest.SaveArchive(archive, facts, recorded);   // the new identity waits until the archive has finished

            var batch = new List<FileRow>(BatchSize);
            var failed = 0;
            var count = 0;
            foreach (var e in entries)
            {
                Guard(count++);
                // A row from an earlier run counts only if it still describes this entry.
                var previous = oldRows.TryGetValue(e.HashIndex, out var p) && p.Name1 == e.Name1 && p.Name2 == e.Name2 && p.Locale == e.Locale && p.Size == e.Size ? p : null;
                var row = write(e, root, previous);
                if (row.Status == StatusFailed) failed++;
                batch.Add(row);
                progress.CountEntry();
                if (batch.Count >= BatchSize) { manifest.SaveFiles(batch); batch.Clear(); }
            }
            manifest.SaveFiles(batch);
            var live = entries.Select(e => e.HashIndex).ToHashSet();
            manifest.DropFiles(archive.RelPath, oldRows.Keys.Where(k => !live.Contains(k)));   // a manifest from before identity existed can list entries the archive no longer has
            if (failed == 0) manifest.SetIdentity(archive.RelPath, identity);
        }

        /// <summary>
        /// A changed archive: everything goes to a staging folder first. Only if all of it passed are the folder, rows and
        /// identity replaced, in that order and with the manifest noting the pending swap, so a crash leaves a state the next run finishes.
        /// If anything is wrong, the staging copy is removed and the older restore stays untouched.
        /// </summary>
        private void RestoreWhole(List<MpqEntry> entries, ArchiveIdentity identity, ArchiveFacts facts, List<(string Name, bool Matched)> lines,
            Dictionary<int, FileRow> oldRows, Func<MpqEntry, string, FileRow?, FileRow> write)
        {
            var needed = BytesToRestore(entries) + ReserveBytes;
            var available = free(output);
            if (needed > available)
                throw Rejected($"not enough free space for a staging copy: {needed / (1L << 20):N0} MiB needed including reserve, {available / (1L << 20):N0} MiB free");

            var staging = StagingDir(output, archive.RelPath);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            var rows = new List<FileRow>(entries.Count);
            string? why;
            try
            {
                foreach (var e in entries)
                {
                    Guard(rows.Count);
                    rows.Add(write(e, staging, null));
                    progress.CountEntry();
                }
                why = WhyNotWhole(oldRows, rows);
                if (why is not null) throw Rejected(why);
                checkpoint?.Invoke($"{archive.RelPath}: staged");
            }
            catch
            {
                try { Directory.Delete(staging, true); }
                catch (IOException) { /* the next run removes what's left under .staging; the reason being thrown matters more */ }
                throw;
            }

            manifest.ReplaceArchive(archive, facts, identity, rows, lines);   // from here on the swap is finished even after a crash
            checkpoint?.Invoke($"{archive.RelPath}: committed");
            SwapIntoPlace(output, archive.RelPath, manifest, checkpoint);
        }

        private static IOException Rejected(string why) => new(
            $"changed since the last run, but its new version can't be restored whole: {why}. The restored copy and its rows are from the older version of the archive and were kept.");

        /// <summary>Why a changed archive's staging copy can't replace the restore, or null if it can.</summary>
        private string? WhyNotWhole(Dictionary<int, FileRow> oldRows, List<FileRow> rows)
        {
            // A shell with only internal files is what a failed rebuild leaves behind; every rule below would pass on it.
            static bool Real(FileRow r) => r.Name is null || !InternalNames.Contains(r.Name);
            var oldReal = oldRows.Values.Count(Real);
            if (!allowEmpty && oldReal > 0 && rows.Count(Real) == 0)
                return $"it holds no entries besides internal files, where the previous version had {oldReal}; pass --allow-empty if that is intended";
            var failed = rows.Count(r => r.Status == StatusFailed);
            if (failed > 0) return $"{failed} of {rows.Count} entries failed to read";
            var unnamed = rows.Count(r => r.Name is null);
            var oldUnnamed = oldRows.Values.Count(r => r.Name is null);
            if (unnamed > 0 && unnamed > oldUnnamed) return $"{unnamed} entries have no name, where the previous version had {oldUnnamed}";
            if (oldRows.Values.Any(r => r.AttrCrc is not null))
            {
                var withoutCrc = rows.Count(r => r.Status is StatusOk or StatusUnnamed && r.Size > 0 && r.AttrCrc is null && !(r.Name is not null && InternalNames.Contains(r.Name)));
                if (withoutCrc > 0) return $"(attributes) gives no CRC for {withoutCrc} entries, though the previous version had CRCs";
            }
            return null;
        }

        /// <summary>Before each entry: stops if another archive ran out of space, and every 256 entries checks free space and reports progress.</summary>
        private void Guard(int count)
        {
            if (stop.IsCancellationRequested) throw new OutOfSpaceException("stopped: another archive hit the free-space limit");
            if (count == 0 || count % 256 != 0) return;
            var left = free(output);
            if (left < ReserveBytes / 2) throw new OutOfSpaceException($"stopped: only {left / (1L << 20):N0} MiB left on {Path.GetPathRoot(output)}. Free space and run the same command again to resume.");
            tick();
        }
    }
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
