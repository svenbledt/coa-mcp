using System.Text.Json.Nodes;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>Per-realm rule pages: races, equipment permissions and stat scaling, written only where the realm has them.</summary>
internal static class RulesImporter
{
    private static readonly (string Field, string Slug, string Title)[] Pages =
    {
        ("raceRules", "races", "Race and class rules"),
        ("equipmentRules", "equipment", "Equipment rules"),
        ("statScaling", "stat-scaling", "Stat scaling"),
    };

    public static int Import(string realm, JsonNode root, LayerWriter w)
    {
        var count = 0;
        foreach (var (field, slug, title) in Pages)
        {
            if (root[field] is not JsonObject raw || raw.Count == 0) continue;
            var data = JsonText.Clean(raw);
            var body = $"{title} ({realm} realm).\n\n{JsonText.Bullets(data)}\n{BisbeardImport.Credit}\n";
            w.Add(new Entity($"rules:{realm}:{slug}", "rules", $"{title} ({realm})", BisbeardImport.Proven(realm), Trust.Derived,
                $"BisBeard {realm} realm data ({field})", BisbeardImport.ArchiveDate, body, data.ToJsonString(), Array.Empty<Link>()));
            count++;
        }
        return count;
    }
}
