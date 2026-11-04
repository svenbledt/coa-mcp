using CoaMcp.Pipeline;
using CoaMcp.Pipeline.Bisbeard;
using CoaMcp.Pipeline.Dbc;
using CoaMcp.Pipeline.Mpq;
using CoaMcp.Pipeline.Publish;
using CoaMcp.Pipeline.Spells;

const string Usage = """
    Usage: coamcp-pipeline bisbeard --data <DataCOA\data> --client-dbc <winners folder> --out <layer.sqlite> [--guides <bisbeard-guides.json>] [--version <v>]
           coamcp-pipeline spells --dbc <winners folder> --out <layer.sqlite> [--version <v>]
           coamcp-pipeline dbc --manifest <manifest.sqlite> --client-release <CLIENT-RELEASE.json> --winners <folder outside any repository> --out <layer.sqlite> [--config <archives.json>] [--fields <dbc-fields folder>] [--version <v>]
           coamcp-pipeline publish --layer <layer.sqlite> --host <ssh host> [--dry-run]
           coamcp-pipeline restore --data <client Data folder> --out <dir> [--config <archives.json>] [--parallel <n>] [--prune-gone]
           coamcp-pipeline listfile --manifest <manifest.sqlite> --out <dir>
           coamcp-pipeline archive-check --data <client Data folder> [--config <archives.json>]
           coamcp-pipeline paths --manifest <manifest.sqlite> --client-release <CLIENT-RELEASE.json> --out <layer.sqlite> [--config <archives.json>] [--version <v>]
    """;

if (args.Length > 0 && args[0] == "publish") return await PublishCli.RunAsync(args[1..], Console.Out, Console.Error);
if (args.Length > 0 && args[0] == "dbc") return DbcCli.Run(args[1..], Console.Out, Console.Error);
if (args.Length > 0 && args[0] is "restore" or "listfile" or "archive-check" or "paths") return MpqCli.Run(args[0], args[1..], Console.Out, Console.Error);

if (args.Length == 0 || args[0] is not ("bisbeard" or "spells")) { Console.Error.WriteLine(Usage); return 2; }

string? data = null, dbc = null, output = null, version = null, clientDbc = null;
var guides = Path.Combine(AppContext.BaseDirectory, "config", "bisbeard-guides.json");
for (var i = 1; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length) { Console.Error.WriteLine(Usage); return 2; }
    switch (args[i])
    {
        case "--data" when args[0] == "bisbeard": data = args[i + 1]; break;
        case "--guides" when args[0] == "bisbeard": guides = args[i + 1]; break;
        case "--client-dbc" when args[0] == "bisbeard": clientDbc = args[i + 1]; break;
        case "--dbc" when args[0] == "spells": dbc = args[i + 1]; break;
        case "--out": output = args[i + 1]; break;
        case "--version": version = args[i + 1]; break;
        default: Console.Error.WriteLine(Usage); return 2;
    }
}

try
{
    version = LayerVersion.Resolve(version);   // fail on a bad --version before the slow part starts
    if (args[0] == "spells")
    {
        if (dbc is null || output is null) { Console.Error.WriteLine(Usage); return 2; }
        var started = DateTime.UtcNow;
        var r = SpellsImport.Run(SpellSource.OpenWinners(dbc), output, (done, total) =>
        {
            if (done % 10000 == 0 || done == total) Console.WriteLine($"  {done}/{total} spells ({DateTime.UtcNow - started:hh\\:mm\\:ss})");
        }, version);
        Console.WriteLine($"spells layer v{version} written to {output}: {r.Spells} spells, {r.Enums} enum members, {r.FlagKinds} flag kinds, {r.ScalingInputs} scaling input rows, {r.GoldenRows} golden scaling rows");
        return 0;
    }

    if (data is null || output is null || clientDbc is null) { Console.Error.WriteLine(Usage); return 2; }
    var clientItems = DbcClientItems.Read(clientDbc);
    var n = BisbeardImport.Run(data, guides, output, version, clientItems);
    Console.WriteLine($"bisbeard layer v{version} written to {output}: {n.Items} items ({n.ItemsNotInClient} say their id isn't among the client's {clientItems.Count} Item ids), {n.Classes} classes, {n.Specs} specs, {n.Markers} markers, {n.Guides} guides, {n.Talents} talents pages, {n.Rules} rules pages");
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"{args[0]} import failed: " + ex.Message);
    return 1;
}
