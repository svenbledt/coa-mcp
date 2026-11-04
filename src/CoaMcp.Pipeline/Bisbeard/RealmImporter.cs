using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>Imports classes and specs from the per-realm data exports. Function placeholders never reach a page.</summary>
public static class RealmImporter
{
    private static readonly string[] Realms = { "coa", "classic", "s10", "wotlk", "octo" };

    public static (int Classes, int Specs, int Talents, int Rules) Import(string dataDir, LayerWriter w)
    {
        int classes = 0, specs = 0, talents = 0, rules = 0;
        foreach (var realm in Realms)
        {
            var path = Path.Combine(dataDir, "realm", realm + "RealmData.json");
            var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"Empty realm data: {path}");
            var detailByName = (root["classSpecs"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .ToDictionary(s => (string?)s["className"] + "|" + (string?)s["specName"]);
            var set = BisbeardImport.Proven(realm);
            var source = $"BisBeard {realm} realm data";

            var withTalents = TalentImporter.Import(realm, root, w);
            talents += withTalents.Count;
            rules += RulesImporter.Import(realm, root, w);

            foreach (var (classKey, cNode) in root["classData"]?.AsObject() ?? new JsonObject())
            {
                var cls = cNode!.AsObject();
                var className = (string?)cls["name"] ?? classKey;
                var classId = $"class:{realm}:{classKey}";
                var specLinks = new List<Link>();
                foreach (var (specKey, sNode) in cls["specs"]?.AsObject() ?? new JsonObject())
                {
                    var specId = $"spec:{realm}:{classKey}-{specKey}";
                    specLinks.Add(new Link(specId, "has-spec"));
                    var specName = (string?)sNode!["name"] ?? specKey;
                    detailByName.TryGetValue(className + "|" + specName, out var detail);
                    var data = new JsonObject { ["class"] = classKey, ["spec"] = specKey, ["info"] = sNode.DeepClone(), ["detail"] = detail?.DeepClone() };
                    w.Add(new Entity(specId, "spec", $"{specName} {className}", set, Trust.Derived, source,
                        BisbeardImport.ArchiveDate, SpecBody(realm, className, specName, detail), JsonText.Clean(data).ToJsonString(),
                        new[] { new Link(classId, "spec-of") }));
                    specs++;
                }
                if (withTalents.Contains(classKey)) specLinks.Add(new Link($"talents:{realm}:{classKey}", "has-talents"));
                w.Add(new Entity(classId, "class", className, set, Trust.Derived, source, BisbeardImport.ArchiveDate,
                    ClassBody(realm, className, cls), JsonText.Clean(cls).ToJsonString(), specLinks));
                classes++;
            }
        }
        return (classes, specs, talents, rules);
    }

    private static string ClassBody(string realm, string className, JsonObject cls)
    {
        var specNames = (cls["specs"]?.AsObject() ?? new JsonObject()).Select(p => (string?)p.Value!["name"] ?? p.Key);
        return $"{className} ({realm} realm). Specs: {string.Join(", ", specNames)}.\n\n{BisbeardImport.Credit}\n";
    }

    private static string SpecBody(string realm, string className, string specName, JsonObject? detail)
    {
        var sb = new StringBuilder($"{specName} {className} ({realm} realm).\n");
        if (detail?["defaultWeights"] is JsonObject dw && dw.Count > 0)
            sb.Line("Default stat weights: " + string.Join(", ", dw.Select(p => $"{p.Key} {Num(p.Value)}")));
        else
            sb.Line("No default stat weights in the source data.");
        return sb.Line().Line(BisbeardImport.Credit).ToString();
    }

    private static string Num(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString("0.###", CultureInfo.InvariantCulture) : n?.ToJsonString() ?? "";

}
