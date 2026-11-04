using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Spells;

/// <summary>Writes one spell:&lt;id&gt; entity per spell in the loaded client data, rendered from the get_spell tool output.</summary>
public static class SpellExporter
{
    public const string Source = "CoA client DBC (via Spellwork)";
    public static readonly RealmSet ClientRealm = new(new[] { "coa-client" }, false);

    public static int Export(SpellSource source, LayerWriter w, Action<int, int>? progress = null)
    {
        var ids = source.SpellIds;
        var done = 0;
        foreach (var id in ids)
        {
            var json = source.Spells.GetSpell(id, "json", SpellPage.RequestedSections).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException($"get_spell failed for spell {id}: {root.GetProperty("error").GetProperty("message").GetString()}");
            w.Add(SpellPage.Build(root.GetProperty("result"), source.SpellName, source.SourceText, ClientRealm, source.SourceDate));
            done++;
            progress?.Invoke(done, ids.Count);
        }
        return done;
    }
}
