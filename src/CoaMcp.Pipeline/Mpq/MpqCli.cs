namespace CoaMcp.Pipeline.Mpq;

/// <summary>The restore and listfile commands.</summary>
public static class MpqCli
{
    public const string Usage = """
        Usage: coamcp-pipeline restore --data <client Data folder> --out <dir> [--config <archives.json>] [--parallel <n>]
               coamcp-pipeline listfile --manifest <manifest.sqlite> --out <dir>
        """;

    public static int Run(string command, string[] args, TextWriter output, TextWriter error)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)) { error.WriteLine(Usage); return 2; }
            values[args[i]] = args[i + 1];
        }
        try
        {
            return command == "restore" ? Restore(values, output, error) : Listfile(values, output, error);
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

    private static int Restore(Dictionary<string, string> v, TextWriter output, TextWriter error)
    {
        if (!v.TryGetValue("--data", out var data) || !v.TryGetValue("--out", out var dir)
            || v.Keys.Except(["--data", "--out", "--config", "--parallel"]).Any()) { error.WriteLine(Usage); return 2; }
        var parallel = 4;
        if (v.TryGetValue("--parallel", out var p) && (!int.TryParse(p, out parallel) || parallel < 1)) { error.WriteLine(Usage); return 2; }
        var config = ArchiveConfig.Load(v.GetValueOrDefault("--config") ?? Path.Combine(AppContext.BaseDirectory, "config", "archives.json"));

        var r = MpqRestorer.Run(new RestoreOptions(data, dir, config, parallel, Progress: output.WriteLine));
        output.WriteLine($"restored {r.Archives} archives in {r.Elapsed:hh\\:mm\\:ss}: {r.Entries:N0} entries, {r.Ok:N0} ok ({r.Extracted:N0} extracted, {r.Skipped:N0} already correct, {r.Recovered:N0} recovered with another sector size), {r.Unnamed:N0} unnamed, {r.Failed:N0} failed, {r.DeleteMarkers:N0} delete markers, {r.PatchFiles:N0} patch files");
        foreach (var changed in r.ChangedArchives) output.WriteLine("archive changed since last run, restored again: " + changed);
        foreach (var broken in r.BrokenArchives) output.WriteLine("broken archive: " + broken);
        return r.BrokenArchives.Count == 0 ? 0 : 1;
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
