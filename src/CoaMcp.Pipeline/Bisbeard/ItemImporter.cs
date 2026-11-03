using System.Text;
using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>Imports the three BisBeard item databases, streaming each so the 68 MB main list is never held whole.</summary>
public static class ItemImporter
{
    private sealed record ItemList(string RelativePath, string IdPrefix, RealmSet Realm, DateOnly Date, string Label);

    private static readonly ItemList[] Lists =
    {
        new(Path.Combine("api", "items_all.json"), "item:", BisbeardImport.Unproven(BisbeardImport.MainRealms),
            new DateOnly(2026, 8, 27), "BisBeard main item API (shared by CoA, Classic and S10; not tagged per realm)"),
        new(Path.Combine("realms", "wotlk", "api", "items_all.json"), "item:wotlk:", BisbeardImport.Proven("wotlk"),
            new DateOnly(2026, 7, 16), "BisBeard WotLK item API"),
        new(Path.Combine("realms", "octo", "api", "items_all.json"), "item:octo:", BisbeardImport.Proven("octo"),
            new DateOnly(2026, 8, 10), "BisBeard Octo item API"),
    };

    private const string NotInClient = "This item's id is not in the CoA client: the client's Item table has no such row, so it is not counted as CoA.";

    /// <summary>
    /// Imports every list. With <paramref name="clientItems"/>, an item from a list that claims CoA, whose id isn't in the client's Item table, loses coa.
    /// An item that is there stays unproven: being in the client isn't proof it is CoA.
    /// </summary>
    public static (int Items, int Narrowed) Import(string dataDir, LayerWriter w, IReadOnlySet<int>? clientItems = null)
    {
        var count = 0;
        var narrowed = 0;
        foreach (var list in Lists)
        {
            using var stream = File.OpenRead(Path.Combine(dataDir, list.RelativePath));
            foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream).ToBlockingEnumerable())
            {
                var entity = ToEntity(item, list);
                if (clientItems is not null && entity.Realm.Realms.Contains("coa") && !InClient(item, clientItems))
                {
                    entity = entity with
                    {
                        Realm = entity.Realm with { Realms = entity.Realm.Realms.Where(r => r != "coa").ToList() },
                        BodyMd = entity.BodyMd + "\n" + NotInClient + "\n",
                    };
                    narrowed++;
                }
                w.Add(entity);
                count++;
            }
        }
        return (count, narrowed);
    }

    private static bool InClient(JsonElement item, IReadOnlySet<int> clientItems) =>
        int.TryParse(Text(item, "id"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && clientItems.Contains(id);

    private static Entity ToEntity(JsonElement item, ItemList list)
    {
        var id = Text(item, "id") ?? throw new InvalidDataException($"Item without id in {list.Label}.");
        var name = Text(item, "name") ?? $"Item {id}";
        return new Entity(list.IdPrefix + id, "item", name, list.Realm, Trust.Derived, list.Label, list.Date,
            Body(item), item.GetRawText(), Array.Empty<Link>());
    }

    private static string Body(JsonElement item)
    {
        var sb = new StringBuilder();
        var head = string.Join(" ", new[] { Text(item, "quality"), Text(item, "type"), Text(item, "slot") }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (head.Length > 0) sb.Line(head);
        if (Text(item, "itemLevel") is { } il) sb.Line($"Item level: {il}");
        if (Text(item, "reqLevel") is { } rl) sb.Line($"Requires level: {rl}");
        if (item.TryGetProperty("classes", out var cl) && cl.ValueKind == JsonValueKind.Array && cl.GetArrayLength() > 0)
            sb.Line("Classes: " + string.Join(", ", cl.EnumerateArray().Select(c => c.ToString())));
        if (item.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object && st.EnumerateObject().Any())
            sb.Line("Stats: " + string.Join(", ", st.EnumerateObject().Select(p => $"{p.Name} {p.Value}")));
        if (Text(item, "source") is { } src) sb.Line($"Obtained from: {src}");
        if (Text(item, "dropRate") is { } dr && dr != "Unknown") sb.Line($"Drop rate: {dr}");
        if (Text(item, "setName") is { } set) sb.Line($"Set: {set}");
        if (Text(item, "description") is { } d) sb.Line().Line(d.Trim());
        return sb.Line().Line(BisbeardImport.Credit).ToString();
    }

    private static string? Text(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        var s = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
