using CoaMcp.Pipeline.Mpq;

namespace CoaMcp.Pipeline.Dbc;

/// <summary>The ids in the client's Item table, read from a winners folder.</summary>
public static class DbcClientItems
{
    public static HashSet<int> Read(string winnersDir)
    {
        var path = Path.Combine(winnersDir, "Item.dbc");
        if (!File.Exists(path)) throw new InvalidDataException($"No Item.dbc in '{winnersDir}'. Run the dbc command first.");
        using var reader = DbcReader.Open(path);
        var ids = new HashSet<int>();
        foreach (var record in reader.Records()) ids.Add(BitConverter.ToInt32(record, 0));
        return ids;
    }
}

/// <summary>The dbc command.</summary>
public static class DbcCli
{
    private const string Usage = "Usage: coamcp-pipeline dbc --manifest <manifest.sqlite> --client-release <CLIENT-RELEASE.json> --winners <folder outside any repository> --out <layer.sqlite> [--config <archives.json>] [--fields <dbc-fields folder>] [--version <v>]";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var v = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)) { error.WriteLine(Usage); return 2; }
            v[args[i]] = args[i + 1];
        }
        if (!v.TryGetValue("--manifest", out var manifest) || !v.TryGetValue("--winners", out var winners) || !v.TryGetValue("--out", out var layer) || !v.TryGetValue("--client-release", out var release)
            || v.Keys.Except(["--manifest", "--client-release", "--winners", "--out", "--config", "--fields", "--version"]).Any()) { error.WriteLine(Usage); return 2; }

        try
        {
            var config = ArchiveConfig.Load(v.GetValueOrDefault("--config") ?? Path.Combine(AppContext.BaseDirectory, "config", "archives.json"));
            var fields = v.GetValueOrDefault("--fields") ?? Path.Combine(AppContext.BaseDirectory, "config", "dbc-fields");
            var r = DbcExporter.Run(new DbcExportOptions(manifest, config, fields, layer, winners, v.GetValueOrDefault("--version"), output.WriteLine, ClientReleaseFile.ReadForManifest(release, manifest)));
            output.WriteLine($"dbc layer written to {layer}: {r.Tables} tables, {r.Rows:N0} rows, {r.NamedTables} with proven names, {r.BaseTables} base copies, {r.Deleted.Count} removed by a delete marker, {r.Unreadable.Count} unreadable; winners in {winners}");
            foreach (var p in r.Problems) output.WriteLine("  " + p);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine("dbc failed: " + ex.Message);
            return 1;
        }
    }
}
