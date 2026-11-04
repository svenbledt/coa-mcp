using CoaMcp.Pipeline.Wdb;

namespace CoaMcp.Pipeline.Notes;

/// <summary>The notes and wdb commands.</summary>
public static class NotesCli
{
    private const string NotesUsage = "Usage: coamcp-pipeline notes --out <layer.sqlite> [--notes <notes folder>] [--config <notes.json>] [--version <v>]";
    private const string WdbUsage = "Usage: coamcp-pipeline wdb --out <layer.sqlite> [--config <wdb-sources.json>] [--scan <folder to check for unlisted caches>] [--version <v>]";

    public static int Run(string command, string[] args, TextWriter output, TextWriter error, IPageSource? pages = null)
    {
        var usage = command == "notes" ? NotesUsage : WdbUsage;
        var allowed = command == "notes" ? new[] { "--out", "--notes", "--config", "--version" } : new[] { "--out", "--config", "--scan", "--version" };
        var v = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !allowed.Contains(args[i])) { error.WriteLine(usage); return 2; }
            v[args[i]] = args[i + 1];
        }
        if (!v.TryGetValue("--out", out var layer)) { error.WriteLine(usage); return 2; }

        var configName = command == "notes" ? "notes.json" : "wdb-sources.json";
        var config = v.GetValueOrDefault("--config") ?? RepoFile("config", configName);
        try
        {
            if (command == "notes")
            {
                var notes = v.GetValueOrDefault("--notes") ?? RepoFile("notes");
                var own = pages is null ? new HttpPageSource() : null;
                try
                {
                    var r = NoteImporter.Run(notes, config, layer, pages ?? own!, DateOnly.FromDateTime(DateTime.UtcNow), v.GetValueOrDefault("--version"));
                    output.WriteLine($"notes layer written to {layer}: {r.Notes} notes, {r.Pages} project pages");
                    foreach (var s in r.Skipped) output.WriteLine($"  skipped (not in notes.json): {s}");
                }
                finally { own?.Dispose(); }
                return 0;
            }

            var result = WdbImporter.Run(config, layer, v.GetValueOrDefault("--scan"), v.GetValueOrDefault("--version"));
            foreach (var f in result.Files)
                output.WriteLine($"  {f.Name} in {f.Folder}: build {f.Build}, locale {f.Locale}, {f.Records} records{(f.Parsed ? "" : " (counted, not read)")}");
            foreach (var s in result.SkippedFolders) output.WriteLine($"  skipped (not listed in wdb-sources.json): {s}");
            output.WriteLine(result.Message);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            error.WriteLine($"{command} failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>A file or folder of this repository, found from where the tool runs (built output or a checkout).</summary>
    private static string RepoFile(params string[] parts)
    {
        var next = Path.Combine(parts);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, next);
            if (File.Exists(Path.Combine(d.FullName, "CoaMcp.sln")) || Directory.Exists(Path.Combine(d.FullName, ".git"))) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, next);
    }
}
