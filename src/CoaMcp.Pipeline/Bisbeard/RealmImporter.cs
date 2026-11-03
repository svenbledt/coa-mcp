using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>Imports classes and specs from the per-realm data exports. Function placeholders never reach a page.</summary>
public static class RealmImporter
{
    private static readonly string[] Realms = { "coa", "classic", "s10", "wotlk", "octo" };

    public static (int Classes, int Specs) Import(string dataDir, LayerWriter w)
    {
        int classes = 0, specs = 0;
        foreach (var realm in Realms)
        {
            var path = Path.Combine(dataDir, "realm", realm + "RealmData.json");
            var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"Empty realm data: {path}");
            var detailByName = (root["classSpecs"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .ToDictionary(s => (string?)s["className"] + "|" + (string?)s["specName"]);
            var set = BisbeardImport.Proven(realm);
            var source = $"BisBeard {realm} realm data";

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
                        BisbeardImport.ArchiveDate, SpecBody(realm, className, specName, detail), Clean(data),
                        new[] { new Link(classId, "spec-of") }));
                    specs++;
                }
                w.Add(new Entity(classId, "class", className, set, Trust.Derived, source, BisbeardImport.ArchiveDate,
                    ClassBody(realm, className, cls), Clean(cls), specLinks));
                classes++;
            }
        }
        return (classes, specs);
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

    /// <summary>Serializes a copy with every "[function ...]" placeholder removed.</summary>
    private static string Clean(JsonNode node)
    {
        var copy = node.DeepClone();
        Strip(copy);
        return copy.ToJsonString();
    }

    private static bool IsPlaceholder(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("[function", StringComparison.Ordinal);

    private static void Strip(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            foreach (var key in o.Where(p => IsPlaceholder(p.Value)).Select(p => p.Key).ToList()) o.Remove(key);
            foreach (var p in o) Strip(p.Value);
        }
        else if (n is JsonArray a)
        {
            for (var i = a.Count - 1; i >= 0; i--)
                if (IsPlaceholder(a[i])) a.RemoveAt(i);
            foreach (var x in a) Strip(x);
        }
    }
}
