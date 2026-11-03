namespace CoaMcp.Pipeline.Mpq;

/// <summary>The restore and listfile commands.</summary>
public static class MpqCli
{
    public const string Usage = """
        Usage: coamcp-pipeline restore --data <client Data folder> --out <dir> [--config <archives.json>] [--parallel <n>] [--prune-gone] [--allow-empty]
               coamcp-pipeline listfile --manifest <manifest.sqlite> --out <dir>
               coamcp-pipeline archive-check --data <client Data folder> [--config <archives.json>]
               coamcp-pipeline paths --manifest <manifest.sqlite> --out <layer.sqlite> [--config <archives.json>] [--version <v>]
        """;

    public static int Run(string command, string[] args, TextWriter output, TextWriter error)
    {
        var values = new Dictionary<string, string>();
        var pruneGone = false;
        var allowEmpty = false;
        for (var i = 0; i < args.Length; i += 2)
        {
            if (command == "restore" && args[i] == "--prune-gone") { pruneGone = true; i--; continue; }
            if (command == "restore" && args[i] == "--allow-empty") { allowEmpty = true; i--; continue; }
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)) { error.WriteLine(Usage); return 2; }
            values[args[i]] = args[i + 1];
        }
        try
        {
            return command switch
            {
                "restore" => Restore(values, pruneGone, allowEmpty, output, error),
                "archive-check" => ArchiveCheck(values, output, error),
                "paths" => Paths(values, output, error),
                _ => Listfile(values, output, error),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"{command} failed: {ex.Message}");
            return 1;
        }
        catch (DllNotFoundException)
        {
            error.WriteLine($"{command} failed: StormLib.dll not found. Build it with tools/build-stormlib.ps1, then rebuild.");
            return 1;
        }
    }

    /// <summary>Checks every archive without writing anything.</summary>
    private static int ArchiveCheck(Dictionary<string, string> v, TextWriter output, TextWriter error)
    {
        if (!v.TryGetValue("--data", out var data) || v.Keys.Except(["--data", "--config"]).Any()) { error.WriteLine(Usage); return 2; }
        var config = ArchiveConfig.Load(v.GetValueOrDefault("--config") ?? Path.Combine(AppContext.BaseDirectory, "config", "archives.json"));

        var archives = ArchiveOrder.Resolve(data, config);
        var bad = 0;
        foreach (var archive in archives)
            if (ArchiveIntegrity.Check(Path.Combine(data, archive.RelPath)) is { } problem)
            {
                bad++;
                output.WriteLine($"{archive.RelPath}: {problem}");
            }
        output.WriteLine($"{archives.Count - bad} of {archives.Count} archives pass");
        return bad == 0 ? 0 : 1;
    }

    private static int Restore(Dictionary<string, string> v, bool pruneGone, bool allowEmpty, TextWriter output, TextWriter error)
    {
        if (!v.TryGetValue("--data", out var data) || !v.TryGetValue("--out", out var dir)
            || v.Keys.Except(["--data", "--out", "--config", "--parallel"]).Any()) { error.WriteLine(Usage); return 2; }
        var parallel = 4;
        if (v.TryGetValue("--parallel", out var p) && (!int.TryParse(p, out parallel) || parallel < 1)) { error.WriteLine(Usage); return 2; }
        var config = ArchiveConfig.Load(v.GetValueOrDefault("--config") ?? Path.Combine(AppContext.BaseDirectory, "config", "archives.json"));

        var r = MpqRestorer.Run(new RestoreOptions(data, dir, config, parallel, Progress: output.WriteLine, PruneGone: pruneGone, AllowEmpty: allowEmpty));
        output.WriteLine($"restored {r.Archives} archives in {r.Elapsed:hh\\:mm\\:ss}: {r.Entries:N0} entries, {r.Ok:N0} ok ({r.Extracted:N0} extracted, {r.Skipped:N0} already correct, {r.Recovered:N0} recovered with another sector size), {r.Unnamed:N0} unnamed, {r.Failed:N0} failed, {r.DeleteMarkers:N0} delete markers, {r.PatchFiles:N0} patch files");
        foreach (var changed in r.ChangedArchives) output.WriteLine("archive changed since last run, restored again as a whole: " + changed);
        foreach (var broken in r.BrokenArchives) output.WriteLine("broken archive: " + broken);
        foreach (var (archive, count) in r.FailedByArchive.OrderBy(f => f.Key, StringComparer.Ordinal))
            output.WriteLine($"needs attention: {count:N0} failed entries in {archive}");
        foreach (var gone in r.GoneArchives)
            output.WriteLine(pruneGone ? "pruned, no longer on disk: " + gone : $"no longer on disk, kept (remove with --prune-gone): {gone}");
        return r.Failed == 0 && r.BrokenArchives.Count == 0 && (pruneGone || r.GoneArchives.Count == 0) ? 0 : 1;
    }

    /// <summary>Builds the paths layer from a finished restore's manifest. Reads the manifest only.</summary>
    private static int Paths(Dictionary<string, string> v, TextWriter output, TextWriter error)
    {
        if (!v.TryGetValue("--manifest", out var manifest) || !v.TryGetValue("--out", out var layer)
            || v.Keys.Except(["--manifest", "--out", "--config", "--version"]).Any()) { error.WriteLine(Usage); return 2; }
        var config = ArchiveConfig.Load(v.GetValueOrDefault("--config") ?? Path.Combine(AppContext.BaseDirectory, "config", "archives.json"));
        var version = LayerVersion.Resolve(v.GetValueOrDefault("--version"));

        var started = DateTime.UtcNow;
        var r = PathExporter.Run(manifest, config, layer, version, progress: n => output.WriteLine($"  {n:N0} paths ({DateTime.UtcNow - started:hh\\:mm\\:ss})"));
        output.WriteLine($"paths layer v{version} written to {layer}: {r.Paths:N0} paths in {r.Folders:N0} folders, {r.Deleted:N0} removed by a later patch, {r.Unavailable:N0} not in the default client, {r.ProjectWinners:N0} won by a project archive; {r.Unnamed:N0} unnamed entries left out, {r.InternalSkipped:N0} archive internals left out, from {r.Entries:N0} manifest entries");
        return 0;
    }

    private static int Listfile(Dictionary<string, string> v, TextWriter output, TextWriter error)
    {
        if (!v.TryGetValue("--manifest", out var manifest) || !v.TryGetValue("--out", out var dir) || v.Count != 2) { error.WriteLine(Usage); return 2; }

        var r = ListfileBuilder.Run(manifest, dir, output.WriteLine);
        output.WriteLine($"listfile for {r.Archives} archives: {r.Entries:N0} entries, {r.Named:N0} named, {r.StillUnnamed:N0} unnamed, {r.Failed:N0} failed, {r.UniqueNames:N0} unique names, written to {dir}");
        foreach (var (source, count) in r.RecoveredBySource.OrderByDescending(s => s.Value)) output.WriteLine($"  {source}: {count:N0}");
        foreach (var (archive, count) in r.UnnamedByArchive.OrderBy(a => a.Key)) output.WriteLine($"  still unnamed in {archive}: {count:N0}");
        return 0;
    }
}
