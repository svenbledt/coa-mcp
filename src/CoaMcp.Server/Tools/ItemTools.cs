using System.ComponentModel;
using System.Globalization;
using System.Text;
using CoaMcp.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CoaMcp.Server.Tools;

/// <summary>
/// Finds items by realm, slot, class and stat in the bisbeard layer's structured item tables.
/// No SQL comes from the caller: filters are fixed columns, the stat name must be one the layer lists, and every value is bound.
/// </summary>
[McpServerToolType]
public sealed class ItemTools
{
    private const int DefaultItemLimit = 20;
    private const int MaxTextChars = 100;
    private const string LayerName = "bisbeard";
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(Limits.DbcQueryBudgetMs);

    private readonly LayerSet _layers;

    public ItemTools(LayerSet layers) => _layers = layers;

    [McpServerTool(Name = "items_find", ReadOnly = true), Description(
        "Find items by filter, each with its stats. All filters must hold. Sorted by the stat if given, else item level. " +
        "The shared CoA/Classic/S10 list is unproven for each of those realms. in_client=1 means the id is in the CoA client's Item table, a hint and no proof. One item: get(\"item:10531\").")]
    public string ItemsFind(
        [Description("Realm: coa, classic, s10, wotlk or octo. coa also returns the shared list, which is unproven.")] string? realm = null,
        [Description("Part of the item name, any case.")] string? name = null,
        [Description("Slot, e.g. Head, Chest, Two-Hand, Finger.")] string? slot = null,
        [Description("Quality: Common, Uncommon, Rare, Epic, Legendary.")] string? quality = null,
        [Description("Armor or weapon type, e.g. Cloth, Plate, Swords.")] string? type = null,
        [Description("Class that may use it, e.g. Druid. Items for All classes are included.")] string? item_class = null,
        [Description("Source category, e.g. raid, dungeon, worldforged, bloodforged, pvp.")] string? category = null,
        [Description("Stat name, e.g. stamina, spellPower, critRating. Only items that have it.")] string? stat = null,
        [Description("With stat: the smallest value.")] double min_value = 0,
        [Description("Items to return, 1 to 100.")] long limit = DefaultItemLimit,
        [Description("Items to skip, for paging.")] long offset = 0)
    {
        foreach (var (label, v) in new[] { ("realm", realm), ("name", name), ("slot", slot), ("quality", quality), ("type", type), ("item_class", item_class), ("category", category), ("stat", stat) })
            if (v is { Length: > MaxTextChars }) throw new McpException($"{label} is too long (max {MaxTextChars} characters).");
        if (double.IsNaN(min_value) || double.IsInfinity(min_value)) throw new McpException("min_value must be a number.");
        if (min_value != 0 && string.IsNullOrWhiteSpace(stat)) throw new McpException("min_value needs a stat.");
        var (lim, off, note) = Limits.Page(limit, offset);

        using var lease = _layers.Lease(LayerName) ?? throw new McpException("The bisbeard layer is not loaded on this server. See the status tool.");
        var layer = lease.Layer;
        if (layer.Query("SELECT 1 AS x FROM sqlite_master WHERE type='table' AND name='items'").Count == 0)
            throw new McpException("The loaded bisbeard layer has no structured item tables yet. Use search and get for items.");

        var where = new List<string>();
        var args = new List<object?>();
        string P(object? v) { args.Add(v); return "$" + (args.Count - 1); }
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        if (Clean(realm) is { } r) where.Add($"instr(',' || i.realms || ',', ',' || {P(r.ToLowerInvariant())} || ',') > 0");
        if (Clean(name) is { } n) where.Add($"instr(lower(i.name), lower({P(n)})) > 0");
        if (Clean(slot) is { } s) where.Add($"i.slot = {P(s)} COLLATE NOCASE");
        if (Clean(quality) is { } q) where.Add($"i.quality = {P(q)} COLLATE NOCASE");
        if (Clean(type) is { } t) where.Add($"i.type = {P(t)} COLLATE NOCASE");
        if (Clean(category) is { } c) where.Add($"i.source_category = {P(c)} COLLATE NOCASE");
        if (Clean(item_class) is { } k)
            where.Add($"EXISTS (SELECT 1 FROM item_classes c WHERE c.item_list = i.item_list AND c.item_id = i.id AND lower(c.class) IN (lower({P(k)}), 'all'))");

        var statName = Clean(stat);
        string from = "items i", order = "i.item_level DESC, i.name, i.id";
        if (statName is not null)
        {
            var known = layer.Query("SELECT DISTINCT stat FROM item_stats ORDER BY stat").Select(row => (string)row["stat"]!).ToList();
            var match = known.FirstOrDefault(x => string.Equals(x, statName, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException($"Unknown stat '{Render.OneLine(statName, 40)}'. Valid stats: {string.Join(", ", known)}.");
            from = $"items i JOIN item_stats s ON s.item_list = i.item_list AND s.item_id = i.id AND s.stat = {P(match)} AND s.value >= {P(min_value)}";
            order = "s.value DESC, i.item_level DESC, i.name, i.id";
        }

        var sql = $"SELECT i.* FROM {from}{(where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")} ORDER BY {order} LIMIT {P((long)lim + 1)} OFFSET {P((long)off)}";
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        try { rows = layer.Searching(() => layer.QueryWithin(Budget, sql, args.ToArray())); }
        catch (QueryBudgetExceededException) { throw new McpException("That filter took too long and was stopped. Add a realm, slot or stat filter."); }

        var sb = new StringBuilder($"Items (trust {Render.Trust(Trust.Derived)}, source BisBeard item APIs; layer {LayerName} v{layer.Meta.Version}, {Render.Dated(layer.Meta, layer.Meta.SourceDate)}).\n");
        if (note is not null) sb.Append(note).Append('\n');
        sb.Append($"{Math.Min(rows.Count, lim)} items (offset {off}, limit {lim}).\n");
        foreach (var row in rows.Take(lim)) sb.Append(Line(layer, row)).Append('\n');
        if (rows.Count > lim) sb.Append($"More results may exist: use offset={off + lim}.\n");
        return sb.ToString().TrimEnd();
    }

    private static string Line(Layer layer, IReadOnlyDictionary<string, object?> row)
    {
        string Text(string col) => row[col] is string x ? Render.OneLine(x, 80) : "";
        var realms = new RealmSet(Text("realms").Split(','), (long)row["realm_proven"]! == 1);
        var inClient = row["in_coa_client"] is long c ? $" | in_client={c}" : "";
        var stats = layer.Query("SELECT stat, value FROM item_stats WHERE item_list = $0 AND item_id = $1 ORDER BY stat", row["item_list"], row["id"])
            .Select(s => $"{s["stat"]} {Convert.ToDouble(s["value"], CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture)}");
        var head = string.Join(' ', new[] { Text("quality"), Text("type"), Text("slot") }.Where(x => x.Length > 0));
        var level = row["item_level"] is long il ? $"ilvl {il}" : "ilvl ?";
        var req = row["req_level"] is long rl ? $", req {rl}" : "";
        var source = Text("source").Length > 0 ? $" | {Text("source")} ({Text("source_category")})" : "";
        return $"{Text("entity_id")} | {Text("name")} | {head} | {level}{req} | realm {Render.RealmText(realms)}{inClient} | {string.Join(", ", stats)}{source}";
    }
}
