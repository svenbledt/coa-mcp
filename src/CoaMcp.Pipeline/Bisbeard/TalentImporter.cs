using System.Text;
using System.Text.Json.Nodes;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>One talents page per class: CoA from coaTalentData, other realms from the talentData grids where populated.</summary>
internal static class TalentImporter
{
    /// <summary>Writes the pages and returns the keys of the classes that got one.</summary>
    public static HashSet<string> Import(string realm, JsonNode root, LayerWriter w)
    {
        var written = new HashSet<string>();
        if (root["coaTalentData"] is JsonObject coa) ImportCoa(realm, coa, w, written);
        if (root["talentData"] is JsonObject grids) ImportGrids(realm, grids, root["classData"] as JsonObject, w, written);
        return written;
    }

    private static Entity Page(string realm, string classKey, string className, string body, JsonNode data, string source) =>
        new($"talents:{realm}:{classKey}", "talents", $"{className} talents", BisbeardImport.Proven(realm), Trust.Derived, source,
            BisbeardImport.ArchiveDate, body, data.ToJsonString(), new[] { new Link($"class:{realm}:{classKey}", "talents-of") });

    private static void ImportCoa(string realm, JsonObject coaRaw, LayerWriter w, HashSet<string> written)
    {
        var coa = JsonText.Clean(coaRaw);
        var roleMeta = coa["specRoleMeta"] as JsonObject;
        foreach (var cls in (coa["classes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var key = JsonText.Str(cls, "key") ?? throw new InvalidDataException("CoA talent class without key.");
            var name = JsonText.Str(cls, "name") ?? key;
            var id = JsonText.Str(cls, "id");
            var essence = id is null ? null : coa["essenceByClass"]?[id];
            var roles = coa["specRolesByClassSpec"]?[key] as JsonObject;

            var sb = new StringBuilder();
            sb.Line($"{name} talent trees ({realm} realm).");
            if (essence is not null)
                sb.Line($"Essence budget: {JsonText.Str(essence, "talentEssence")} talent essence, {JsonText.Str(essence, "abilityEssence")} ability essence.");

            var nodes = new JsonObject();
            var passives = new JsonObject();
            foreach (var tab in (cls["tabs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var tabKey = JsonText.Str(tab, "tabKey") ?? "";
                var roleKey = JsonText.Str(tab, "specKey");
                var role = roleKey is not null && roles?[roleKey] is { } rk ? JsonText.Str(roleMeta?[JsonText.Scalar(rk)], "label") ?? JsonText.Scalar(rk) : null;
                sb.Line().Line($"## {JsonText.Str(tab, "name")}" + (role is null ? "" : $" ({role})"));
                if (coa["nodesByTab"]?[tabKey] is JsonArray n)
                {
                    nodes[tabKey] = n.DeepClone();
                    sb.Line("Talents:");
                    foreach (var node in n.OfType<JsonObject>()) sb.Line(NodeLine(node));
                }
                if (coa["passivesByTab"]?[tabKey] is JsonArray p)
                {
                    passives[tabKey] = p.DeepClone();
                    sb.Line("Passives:");
                    foreach (var pas in p.OfType<JsonObject>()) sb.Line(PassiveLine(pas));
                }
            }
            sb.Line().Line(BisbeardImport.Credit);

            var data = new JsonObject
            {
                ["class"] = cls.DeepClone(), ["essence"] = essence?.DeepClone(), ["specRoles"] = roles?.DeepClone(),
                ["nodesByTab"] = nodes, ["passivesByTab"] = passives,
            };
            w.Add(Page(realm, key, name, sb.ToString(), data, "BisBeard coa realm data (coaTalentData)"));
            written.Add(key);
        }
    }

    private static string NodeLine(JsonObject n)
    {
        var facts = new List<string>();
        if (JsonText.Str(n, "spellId") is { } s) facts.Add($"spell {s}");
        if (JsonText.Str(n, "nodeType") is { } t) facts.Add(t);
        if (JsonText.Str(n, "maxPoints") is { } m) facts.Add($"max {m} points");
        if (JsonText.Str(n, "requiredLevel") is { } l) facts.Add($"level {l}");
        if (n["cost"] is JsonObject c) facts.Add($"cost {JsonText.Str(c, "talentEssence") ?? "0"} talent essence, {JsonText.Str(c, "abilityEssence") ?? "0"} ability essence");
        return $"- {JsonText.Str(n, "name")} ({string.Join(", ", facts)}): {JsonText.Str(n, "description")}";
    }

    private static string PassiveLine(JsonObject p)
    {
        var facts = new List<string>();
        if (JsonText.Str(p, "spellId") is { } s) facts.Add($"spell {s}");
        if (JsonText.Str(p, "level") is { } l) facts.Add($"level {l}");
        return $"- {JsonText.Str(p, "name")} ({string.Join(", ", facts)}): {JsonText.Str(p, "description")}";
    }

    private static void ImportGrids(string realm, JsonObject gridsRaw, JsonObject? classData, LayerWriter w, HashSet<string> written)
    {
        var classNames = (JsonText.Clean(gridsRaw) as JsonObject)!;
        foreach (var (classKey, specs) in classNames)
        {
            if (specs is not JsonObject specGrids) continue;
            var sb = new StringBuilder();
            var any = false;
            sb.Line($"{JsonText.Str(classData?[classKey], "name") ?? classKey} talent trees ({realm} realm). Cells are row, column in the tree.");
            foreach (var (specKey, grid) in specGrids)
            {
                if (grid is not JsonArray rows) continue;
                var lines = new List<string>();
                for (var r = 0; r < rows.Count; r++)
                {
                    if (rows[r] is not JsonArray cols) continue;
                    for (var c = 0; c < cols.Count; c++)
                        if (cols[c] is JsonObject cell) lines.Add(CellLine(r, c, cell));
                }
                if (lines.Count == 0) continue;
                any = true;
                sb.Line().Line($"## {specKey}");
                foreach (var l in lines) sb.Line(l);
            }
            if (!any) continue;
            sb.Line().Line(BisbeardImport.Credit);
            var name = JsonText.Str(classData?[classKey], "name") ?? classKey;
            w.Add(Page(realm, classKey, name, sb.ToString(), classNames[classKey]!, $"BisBeard {realm} realm data (talentData)"));
            written.Add(classKey);
        }
    }

    private static string CellLine(int row, int col, JsonObject cell)
    {
        var ranks = (cell["ranks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(r => JsonText.Str(r, "text")).ToList();
        var last = ranks.Count > 0 ? ranks[^1] : null;
        return $"- r{row + 1}c{col + 1} {JsonText.Str(cell, "name")} (spell {JsonText.Str(cell, "spellId")}, {ranks.Count} rank{(ranks.Count == 1 ? "" : "s")})"
            + (last is null ? "" : $": {last}");
    }
}
