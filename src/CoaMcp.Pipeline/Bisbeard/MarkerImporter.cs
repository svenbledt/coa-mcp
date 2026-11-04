using System.Globalization;
using System.Text;
using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>Imports atlas markers. Positions are map percentages, always labeled approximate.</summary>
public static class MarkerImporter
{
    private static readonly string[] Files = { "markers_worldforge.json", "markers_mystic-enchant.json" };

    public static int Import(string dataDir, LayerWriter w)
    {
        var count = 0;
        foreach (var file in Files)
        {
            using var stream = File.OpenRead(Path.Combine(dataDir, "atlas", file));
            foreach (var m in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream).ToBlockingEnumerable())
            {
                var id = m.GetProperty("id").GetString()!;
                var name = m.GetProperty("name").GetString() ?? id;
                w.Add(new Entity("marker:" + id, "marker", name, BisbeardImport.Unproven(BisbeardImport.MainRealms), Trust.Derived,
                    "BisBeard atlas " + file, BisbeardImport.ArchiveDate, Body(m, name), m.GetRawText(), Array.Empty<Link>()));
                count++;
            }
        }
        return count;
    }

    private static string Body(JsonElement m, string name)
    {
        var sb = new StringBuilder();
        var kind = m.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var zone = m.TryGetProperty("zoneName", out var z) ? z.GetString() : null;
        var map = m.TryGetProperty("mapId", out var mi) ? mi.GetString() : null;
        sb.Line($"{name} ({kind ?? "marker"}) in {zone ?? "unknown zone"}.");
        sb.Line($"Approximate location: {Pct(m, "left")}% from the left, {Pct(m, "top")}% from the top of the {map ?? zone} map "
            + "(map percentages, approximate, not world coordinates).");
        if (m.TryGetProperty("classes", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0)
            sb.Line("Classes: " + string.Join(", ", c.EnumerateArray().Select(x => x.ToString())));
        if (m.TryGetProperty("description", out var d) && d.GetString() is { Length: > 0 } text)
            sb.Line().Line(text.Trim());
        return sb.Line().Line(BisbeardImport.Credit).ToString();
    }

    private static string Pct(JsonElement m, string prop) =>
        Math.Round(m.GetProperty(prop).GetDouble(), 1).ToString("0.#", CultureInfo.InvariantCulture);
}
