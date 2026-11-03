using CoaMcp.Pipeline;
using CoaMcp.Pipeline.Bisbeard;
using CoaMcp.Pipeline.Publish;
using CoaMcp.Pipeline.Spells;

const string Usage = """
    Usage: coamcp-pipeline bisbeard --data <DataCOA\data> --out <layer.sqlite> [--guides <bisbeard-guides.json>] [--version <v>]
           coamcp-pipeline spells --dbc <dbc folder> --out <layer.sqlite> [--version <v>]
           coamcp-pipeline publish --layer <layer.sqlite> --host <ssh host> [--dry-run]
    """;

if (args.Length > 0 && args[0] == "publish") return await PublishCli.RunAsync(args[1..], Console.Out, Console.Error);

if (args.Length == 0 || args[0] is not ("bisbeard" or "spells")) { Console.Error.WriteLine(Usage); return 2; }

string? data = null, dbc = null, output = null, version = null;
var guides = Path.Combine(AppContext.BaseDirectory, "config", "bisbeard-guides.json");
for (var i = 1; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length) { Console.Error.WriteLine(Usage); return 2; }
    switch (args[i])
    {
        case "--data" when args[0] == "bisbeard": data = args[i + 1]; break;
        case "--guides" when args[0] == "bisbeard": guides = args[i + 1]; break;
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
        var r = SpellsImport.Run(dbc, output, (done, total) =>
        {
            if (done % 10000 == 0 || done == total) Console.WriteLine($"  {done}/{total} spells ({DateTime.UtcNow - started:hh\\:mm\\:ss})");
        }, version);
        Console.WriteLine($"spells layer v{version} written to {output}: {r.Spells} spells, {r.Enums} enum members, {r.FlagKinds} flag kinds, {r.ScalingInputs} scaling input rows, {r.GoldenRows} golden scaling rows");
        return 0;
    }

    if (data is null || output is null) { Console.Error.WriteLine(Usage); return 2; }
    var n = BisbeardImport.Run(data, guides, output, version);
    Console.WriteLine($"bisbeard layer v{version} written to {output}: {n.Items} items, {n.Classes} classes, {n.Specs} specs, {n.Markers} markers, {n.Guides} guides, {n.Talents} talents pages, {n.Rules} rules pages");
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"{args[0]} import failed: " + ex.Message);
    return 1;
}
