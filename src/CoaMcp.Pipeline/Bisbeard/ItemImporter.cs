using System.Text;
using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>Imports the three BisBeard item databases, streaming each so the 68 MB main list is never held whole.</summary>
public static class ItemImporter
{
    private sealed record ItemList(string Name, string RelativePath, string IdPrefix, RealmSet Realm, DateOnly Date, string Label);

    private static readonly ItemList[] Lists =
    {
        new("main", Path.Combine("api", "items_all.json"), "item:", BisbeardImport.Unproven(BisbeardImport.MainRealms),
            new DateOnly(2026, 8, 27), "BisBeard main item API (shared by CoA, Classic and S10; not tagged per realm)"),
        new("wotlk", Path.Combine("realms", "wotlk", "api", "items_all.json"), "item:wotlk:", BisbeardImport.Proven("wotlk"),
            new DateOnly(2026, 7, 16), "BisBeard WotLK item API"),
        new("octo", Path.Combine("realms", "octo", "api", "items_all.json"), "item:octo:", BisbeardImport.Proven("octo"),
            new DateOnly(2026, 8, 10), "BisBeard Octo item API"),
    };

    private const string NotInClient = "This id isn't in the client's Item table; that alone doesn't prove which realm has it.";

    /// <summary>
    /// Imports every list. With <paramref name="clientItems"/>, a page from a list that may be CoA says so when its id isn't in the client's Item table.
    /// The realm stays as it is either way: such ids look server-generated, so absence proves nothing, and being in the client isn't proof it is CoA.
    /// </summary>
    public static (int Items, int NotInClient) Import(string dataDir, LayerWriter w, IReadOnlySet<int>? clientItems = null)
    {
        var count = 0;
        var notInClient = 0;
        ItemTables.Create(w);
        using var rows = w.BeginInsert(ItemTables.Items, ItemTables.ItemColumns);
        using var stats = w.BeginInsert(ItemTables.Stats, ItemTables.StatColumns);
        using var classes = w.BeginInsert(ItemTables.Classes, ItemTables.ClassColumns);
        foreach (var list in Lists)
        {
            using var stream = File.OpenRead(Path.Combine(dataDir, list.RelativePath));
            foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream).ToBlockingEnumerable())
            {
                var entity = ToEntity(item, list);
                if (clientItems is not null && entity.Realm.Realms.Contains("coa") && !InClient(item, clientItems))
                {
                    entity = entity with { BodyMd = entity.BodyMd + "\n" + NotInClient + "\n" };
                    notInClient++;
                }
                w.Add(entity);
                AddRows(item, list, entity, clientItems, rows, stats, classes);
                count++;
            }
        }
        return (count, notInClient);
    }

    /// <summary>One structured row per item, plus its stats and classes. in_coa_client is only set for a list that may be CoA, and only when the client ids are known.</summary>
    private static void AddRows(JsonElement item, ItemList list, Entity entity, IReadOnlySet<int>? clientItems,
        BulkInsert rows, BulkInsert stats, BulkInsert classes)
    {
        var id = long.Parse(Text(item, "id")!, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        object? inClient = clientItems is not null && list.Realm.Realms.Contains("coa") ? (InClient(item, clientItems) ? 1L : 0L) : null;
        JsonElement dmg = default;
        if (item.TryGetProperty("damage", out var d)) dmg = d;
        double? Dmg(string n) => dmg.ValueKind == JsonValueKind.Object && dmg.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        rows.Add(new object?[]
        {
            list.Name, id, entity.Id, entity.Title, string.Join(",", list.Realm.Realms), list.Realm.Proven ? 1L : 0L, inClient,
            Text(item, "slot"), Text(item, "type"), Text(item, "quality"), Num(item, "itemLevel"), Num(item, "reqLevel"),
            Text(item, "sourceCategory"), Text(item, "source"), Text(item, "version"), Num(item, "phase"),
            Text(item, "dropRate"), Text(item, "setName"), Text(item, "requiredFaction"), Dmg("min"), Dmg("max"), Dmg("speed"),
        });
        if (item.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object)
            foreach (var p in st.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number))
                stats.Add(new object?[] { list.Name, id, p.Name, p.Value.GetDouble() });
        if (item.TryGetProperty("classes", out var cl) && cl.ValueKind == JsonValueKind.Array)
            foreach (var c in cl.EnumerateArray().Select(c => c.GetString()).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct())
                classes.Add(new object?[] { list.Name, id, c });
    }

    private static object? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

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
