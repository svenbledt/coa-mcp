using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoaMcp.Acceptance;

/// <summary>Random items against the BisBeard files, read directly here. Every difference names the item id and both values.</summary>
[Collection("03 Accuracy items")]
public class AccuracyItemTests
{
    private const string Area = "Accuracy: items";
    private const int Sample = 100;

    /// <summary>Whitespace and control runs become one space, as the server does for text that must fit one line.</summary>
    internal static string OneLine(string s, int max)
    {
        var flat = Regex.Replace(s, @"[\p{C}\s]+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }

    private static string? Text(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        var s = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static List<JsonElement> Load(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static void CheckPage(Checks c, JsonElement item, string id, string page, string realm, string date, string source)
    {
        var who = $"{id}: ";
        c.Equal("# " + OneLine(Text(item, "name") ?? "", 200), page.Split('\n')[0], who + "title");
        c.Has(page, $"realm: {realm}", who + "realm label");
        c.Has(page, "trust: derived", who + "trust");
        c.Has(page, $"source: {source}", who + "source");
        c.Has(page, $"source date: {date}", who + "source date");

        var head = string.Join(" ", new[] { Text(item, "quality"), Text(item, "type"), Text(item, "slot") }.Where(s => s is not null));
        if (head.Length > 0) c.Has(page, "\n" + head + "\n", who + "quality, type and slot");
        if (Text(item, "itemLevel") is { } il) c.Has(page, $"Item level: {il}\n", who + "item level");
        if (Text(item, "reqLevel") is { } rl) c.Has(page, $"Requires level: {rl}\n", who + "required level");
        if (item.TryGetProperty("classes", out var cl) && cl.ValueKind == JsonValueKind.Array && cl.GetArrayLength() > 0)
            c.Has(page, "Classes: " + string.Join(", ", cl.EnumerateArray().Select(x => x.ToString())) + "\n", who + "classes");
        if (item.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object)
            foreach (var p in st.EnumerateObject()) c.Has(page, $"{p.Name} {p.Value}", who + $"stat {p.Name}");
        if (Text(item, "source") is { } src) c.Has(page, $"Obtained from: {src}\n", who + "source of the item");
        if (Text(item, "dropRate") is { } dr && dr != "Unknown") c.Has(page, $"Drop rate: {dr}\n", who + "drop rate");
        if (Text(item, "setName") is { } set) c.Has(page, $"Set: {set}\n", who + "set");
        if (Text(item, "description") is { } d) c.Has(page, d.Trim(), who + "tooltip text");
        c.Has(page, "BisBeard", who + "credits BisBeard");
    }

    [SkippableFact]
    public Task MainList_RandomItems() => Suite.Run(Area, $"{Sample} random items against items_all.json", async c =>
    {
        var client = await Suite.Anonymous();
        var items = Load(Settings.ItemsJson);
        var rng = Suite.Rng("items");

        // Whether the id is in the client's Item table, read directly from the winning Item.dbc.
        var manifest = ClientManifest.Instance;
        var itemTable = manifest.Dbc("Item")!;
        var dbc = new Wdbc(manifest.Restored(itemTable.Winner!, itemTable.Path));
        var clientIds = new HashSet<int>(Enumerable.Range(0, dbc.Records).Select(r => dbc.Int(r, 0)));

        var sample = items.OrderBy(_ => rng.Next()).Take(Sample).ToList();
        var checkedPages = 0;
        foreach (var item in sample)
        {
            var id = Text(item, "id")!;
            var r = await client.Call("get", new { id = "item:" + id });
            if (!c.That(!r.IsError, $"item:{id} answers", r.All)) continue;
            checkedPages++;
            CheckPage(c, item, "item:" + id, r.Text, "CoA, Classic or S10 (unproven)", "2026-08-27", "BisBeard main item API (shared by CoA, Classic and S10; not tagged per realm)");
            const string note = "This id isn't in the client's Item table";
            var inClient = int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && clientIds.Contains(n);
            c.That(r.Text.Contains(note, StringComparison.Ordinal) == !inClient, $"item:{id}: the 'not in the client' note matches Item.dbc", $"in Item.dbc: {inClient}\npage says: {(r.Text.Contains(note, StringComparison.Ordinal) ? "not in the client" : "nothing")}");
        }
        c.Equal(Sample, checkedPages, "pages checked");
    });

    [SkippableFact]
    public Task RealmLists_RandomItems() => Suite.Run(Area, "random WotLK and Octo items, labeled as proven for their realm", async c =>
    {
        var client = await Suite.Anonymous();
        var rng = Suite.Rng("realm-items");
        foreach (var (realm, date, source) in new[] { ("wotlk", "2026-07-16", "BisBeard WotLK item API"), ("octo", "2026-08-10", "BisBeard Octo item API") })
        {
            var items = Load(Settings.RealmItems(realm));
            foreach (var item in items.OrderBy(_ => rng.Next()).Take(10))
            {
                var id = $"item:{realm}:{Text(item, "id")}";
                var r = await client.Call("get", new { id });
                if (c.That(!r.IsError, id + " answers", r.All)) CheckPage(c, item, id, r.Text, $"{realm} (proven)", date, source);
            }
        }
    });

    [SkippableFact]
    public Task Search_FindsItemsByTheirNames() => Suite.Run(Area, "20 random items are found by name", async c =>
    {
        var client = await Suite.Anonymous();
        var rng = Suite.Rng("item-search");
        var items = Load(Settings.ItemsJson);
        var byName = items.GroupBy(i => Text(i, "name")!).ToDictionary(g => g.Key, g => g.Select(i => "item:" + Text(i, "id")).ToHashSet());
        foreach (var item in items.OrderBy(_ => rng.Next()).Take(20))
        {
            var name = Text(item, "name")!;
            var r = await client.Call("search", new { query = name, topic = "items", limit = 5 });
            var first = r.Text.Split('\n').FirstOrDefault(l => Regex.IsMatch(l, @"^1\. \[")) ?? "(no hits)";
            var m = Regex.Match(first, @"^1\. \[[^|]+\| \w+ \| ([^\]]+)\] (.+?) \(");
            c.That(m.Success && (byName[name].Contains(m.Groups[1].Value) || string.Equals(m.Groups[2].Value, OneLine(name, 120), StringComparison.OrdinalIgnoreCase)),
                $"search for '{name}' puts an item of that exact name first", $"expected one of: {string.Join(", ", byName[name].Take(5))}\nserver's first hit: {first}");
        }
    });
}
