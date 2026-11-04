using CoaMcp.Pipeline.Publish;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Knowledge;

/// <summary>
/// `pull-writes` copies the production store's entries into knowledge/ for review. `restore-writes` rebuilds an empty store from knowledge/.
/// Neither commits anything. Exit codes: 0 done (or nothing to do), 1 failed or refused, 2 bad usage.
/// </summary>
public static class KnowledgeCli
{
    public const string Usage = """
        Usage: coamcp-pipeline pull-writes --host <ssh host> --out <knowledge folder> [--container <name>] [--include-test]
               coamcp-pipeline restore-writes --from <knowledge folder> --store <new store.sqlite>
        """;

    public static async Task<int> RunAsync(string command, string[] args, TextWriter output, TextWriter error, Func<string, IRemote>? remoteFor = null, string? tempRoot = null)
    {
        var options = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        var allowed = command == "pull-writes" ? new[] { "--host", "--out", "--container" } : new[] { "--from", "--store" };
        for (var i = 0; i < args.Length; i++)
        {
            if (command == "pull-writes" && args[i] == "--include-test") flags.Add("include-test");
            else if (allowed.Contains(args[i]) && i + 1 < args.Length && !options.ContainsKey(args[i])) options[args[i]] = args[++i];
            else return Usage2(error);
        }

        try
        {
            if (command == "restore-writes")
            {
                if (!options.TryGetValue("--from", out var from) || !options.TryGetValue("--store", out var store) || from.Length == 0 || store.Length == 0) return Usage2(error);
                var n = KnowledgeRestorer.Restore(from, store);
                output.WriteLine($"Restored {n} entr{(n == 1 ? "y" : "ies")} into {store}.");
                output.WriteLine("To use it on production: stop the container, copy the file to writable/store.sqlite on the host, set its owner to 10001, then start the container.");
                return 0;
            }

            if (!options.TryGetValue("--host", out var host) || !options.TryGetValue("--out", out var outDir) || host.Length == 0 || outDir.Length == 0) return Usage2(error);
            options.TryGetValue("--container", out var container);
            if (container is not null && !SnapshotPuller.IsValidContainerName(container)) return Usage2(error, "The container name may only hold letters, digits, dots, dashes and underscores.");

            IRemote remote;
            try { remote = (remoteFor ?? (h => new SshRemote(h)))(host); }
            catch (ArgumentException ex) { return Usage2(error, ex.Message); }

            ExportResult? result = null;
            var puller = new SnapshotPuller(remote, tempRoot ?? Path.GetTempPath(), error);
            var found = await puller.PullAsync(container, snapshot => result = KnowledgeExporter.Export(snapshot, outDir, flags.Contains("include-test")), CancellationToken.None);
            if (!found)
            {
                output.WriteLine("The server has no writable store yet, so there is nothing to merge. Nothing was changed.");
                return 0;
            }
            result!.WriteSummary(output);
            if (result.Total > 0) output.WriteLine($"Review the diff, then commit it yourself: git add {Path.GetFileName(Path.TrimEndingDirectorySeparator(outDir))} && git commit");
            return 0;
        }
        catch (Exception ex) when (ex is KnowledgeException or IOException or UnauthorizedAccessException or SqliteException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine($"{command} failed: {ex.Message}");
            return 1;
        }
    }

    private static int Usage2(TextWriter error, string? why = null)
    {
        if (why is not null) error.WriteLine(why);
        error.WriteLine(Usage);
        return 2;
    }
}
