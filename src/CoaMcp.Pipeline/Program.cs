using CoaMcp.Pipeline.Bisbeard;

const string Usage = "Usage: coamcp-pipeline bisbeard --data <DataCOA\\data> --out <layer.sqlite> [--guides <bisbeard-guides.json>]";

if (args.Length == 0 || args[0] != "bisbeard") { Console.Error.WriteLine(Usage); return 2; }

string? data = null, output = null;
var guides = Path.Combine(AppContext.BaseDirectory, "config", "bisbeard-guides.json");
for (var i = 1; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length) { Console.Error.WriteLine(Usage); return 2; }
    switch (args[i])
    {
        case "--data": data = args[i + 1]; break;
        case "--out": output = args[i + 1]; break;
        case "--guides": guides = args[i + 1]; break;
        default: Console.Error.WriteLine(Usage); return 2;
    }
}
if (data is null || output is null) { Console.Error.WriteLine(Usage); return 2; }

try
{
    var n = BisbeardImport.Run(data, guides, output);
    Console.WriteLine($"bisbeard layer written to {output}: {n.Items} items, {n.Classes} classes, {n.Specs} specs, {n.Markers} markers, {n.Guides} guides");
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine("bisbeard import failed: " + ex.Message);
    return 1;
}
