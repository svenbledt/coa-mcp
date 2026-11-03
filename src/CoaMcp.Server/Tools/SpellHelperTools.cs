using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoaMcp.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CoaMcp.Server.Tools;

[McpServerToolType]
public sealed partial class SpellHelperTools
{
    private const int MaxDiffs = 100;
    private const int MaxValueChars = 200;

    private readonly LayerSet _layers;

    public SpellHelperTools(LayerSet layers) => _layers = layers;

    private Layer Spells() =>
        _layers.Current("spells") ?? throw new McpException("The spells layer is not loaded on this server. See the status tool.");

    [GeneratedRegex(@"^(?:spell:)?(\d{1,10})$", RegexOptions.IgnoreCase)]
    private static partial Regex SpellIdPattern();

    private static string SpellId(string id)
    {
        var m = SpellIdPattern().Match((id ?? "").Trim());
        if (!m.Success) throw new McpException($"'{Render.OneLine(id ?? "", 40)}' is not a spell id. Use spell:133 or 133.");
        return "spell:" + m.Groups[1].Value.TrimStart('0').PadLeft(1, '0');
    }

    private static string Source(Entity e) =>
        $"realm {Render.RealmText(e.Realm)}, trust {Render.Trust(e.Trust)}, source {e.Source}, source date {e.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private Entity Spell(Layer layer, string id) =>
        layer.Get(id) ?? throw new McpException($"No spell with id '{id}' in the spells layer.");

    [McpServerTool(Name = "spell_compare", ReadOnly = true), Description("Compare two spells and list only the fields that differ.")]
    public string SpellCompare(
        [Description("First spell, e.g. spell:133 or 133.")] string idA,
        [Description("Second spell.")] string idB)
    {
        var layer = Spells();
        var a = Spell(layer, SpellId(idA));
        var b = Spell(layer, SpellId(idB));
        var fa = Flatten(a.DataJson);
        var fb = Flatten(b.DataJson);

        var diffs = new List<string>();
        foreach (var path in fa.Keys.Concat(fb.Keys.Where(k => !fa.ContainsKey(k))))
        {
            var has = fa.TryGetValue(path, out var va);
            var hasB = fb.TryGetValue(path, out var vb);
            if (has && hasB && va == vb) continue;
            diffs.Add($"- {path}: {(has ? va : "(absent)")} | {(hasB ? vb : "(absent)")}");
        }

        var sb = new StringBuilder();
        sb.Append($"A = {a.Id} {Render.OneLine(a.Title, 80)} ({Source(a)})\n");
        sb.Append($"B = {b.Id} {Render.OneLine(b.Title, 80)} ({Source(b)})\n");
        sb.Append($"{diffs.Count} differing fields (A | B):\n");
        foreach (var d in diffs.Take(MaxDiffs)) sb.Append(d).Append('\n');
        if (diffs.Count > MaxDiffs) sb.Append($"and {diffs.Count - MaxDiffs} more not shown.\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Leaf values by path, e.g. effects[0].basePoints. Order follows the document.</summary>
    private static Dictionary<string, string> Flatten(string? json)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement, "", result);
        return result;
    }

    private static void Walk(JsonElement e, string path, Dictionary<string, string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, into);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray()) Walk(item, $"{path}[{i++}]", into);
                if (i == 0) into[path] = "[]";
                break;
            default:
                var text = e.GetRawText();
                into[path] = text.Length > MaxValueChars ? text[..MaxValueChars] + "..." : text;
                break;
        }
    }

    [McpServerTool(Name = "decode_flags", ReadOnly = true), Description("Split a bitmask into named flags. kind is an enum such as SpellSchoolMask, SpellAtribute or ProcFlags.")]
    public string DecodeFlags(
        [Description("Flag enum name; an unknown name lists the valid ones.")] string kind,
        [Description("The mask, decimal or 0x hex.")] string value)
    {
        var layer = Spells();
        var kinds = layer.Query("SELECT kind, enum_name FROM flag_kinds ORDER BY kind");
        var match = kinds.FirstOrDefault(k => string.Equals((string)k["kind"]!, (kind ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new McpException($"Unknown flag kind '{Render.OneLine(kind ?? "", 60)}'. Valid kinds: {string.Join(", ", kinds.Select(k => (string)k["kind"]!))}.");
        if (!TryParseMask(value, out var mask))
            throw new McpException("value must be a whole number from 0 to 18446744073709551615, decimal or 0x hex.");

        var members = layer.Query("SELECT value, label FROM enums WHERE name = $0 ORDER BY value", (string)match["enum_name"]!)
            .Select(r => (Bits: unchecked((ulong)(long)r["value"]!), Label: (string)r["label"]!)).ToList();
        var set = members.Where(m => m.Bits != 0 && (mask & m.Bits) == m.Bits).ToList();
        var covered = set.Aggregate(0UL, (acc, m) => acc | m.Bits);
        var unmatched = mask & ~covered;

        var source = (string)layer.Query("SELECT source FROM entities LIMIT 1")[0]["source"]!;
        var sb = new StringBuilder($"{match["kind"]} 0x{mask:X} ({mask}) from the spells layer v{layer.Meta.Version} " +
            $"(trust {Render.Trust(Trust.Client)}, source {source}, source date {layer.Meta.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}):\n");
        if (mask == 0) sb.Append("no bits set\n");
        foreach (var m in set) sb.Append($"- {m.Label} (0x{m.Bits:X})\n");
        if (unmatched != 0) sb.Append($"unmatchedBits: 0x{unmatched:X}\n");
        return sb.ToString().TrimEnd();
    }

    private static bool TryParseMask(string? text, out ulong mask)
    {
        mask = 0;
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 30) return false;
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(t.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mask)
            : ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out mask);
    }

    [McpServerTool(Name = "spell_scaling", ReadOnly = true), Description("Spell effect values at a caster level, using the 3.3.5 rule. Gives base, min, max, combo-point and spell power terms per effect.")]
    public string SpellScalingTool(
        [Description("Spell id, e.g. spell:133 or 133.")] string id,
        [Description("Caster level, 1 to 100.")] long level,
        [Description("Accepted for compatibility; it does not change the result.")] long? itemLevel = null)
    {
        var layer = Spells();
        var spell = Spell(layer, SpellId(id));
        var notes = new List<string>();
        var lvl = level;
        if (lvl < 1) { lvl = 1; notes.Add($"level {level} raised to 1"); }
        else if (lvl > SpellScaling.MaxLevel) { lvl = SpellScaling.MaxLevel; notes.Add($"level {level} capped at {SpellScaling.MaxLevel}"); }
        if (itemLevel is not null) notes.Add("item level is not applied to spell values");

        var rows = layer.Query("""
            SELECT effect_index, effect, base_points, die_sides, real_points_per_level, points_per_combo,
                   spell_power_coefficient, spell_level, base_level, max_level
            FROM spell_scaling_inputs WHERE spell_id = $0 ORDER BY effect_index
            """, long.Parse(spell.Id[6..], CultureInfo.InvariantCulture));
        var names = layer.Query("SELECT value, label FROM enums WHERE name = 'SpellEffects'")
            .GroupBy(r => (long)r["value"]!) // several names can share one value
            .ToDictionary(g => g.Key, g => string.Join(" / ", g.Select(r => (string)r["label"]!)));

        var sb = new StringBuilder($"{spell.Id} {Render.OneLine(spell.Title, 80)} at level {lvl} ({Source(spell)})\n");
        if (notes.Count > 0) sb.Append("Note: ").Append(string.Join("; ", notes)).Append(".\n");
        if (rows.Count == 0) sb.Append("This spell has no effects.\n");
        foreach (var r in rows)
        {
            var rpl = (float)Convert.ToDouble(r["real_points_per_level"], CultureInfo.InvariantCulture);
            var combo = (float)Convert.ToDouble(r["points_per_combo"], CultureInfo.InvariantCulture);
            var coef = (float)Convert.ToDouble(r["spell_power_coefficient"], CultureInfo.InvariantCulture);
            var v = SpellScaling.Value((int)(long)r["base_points"]!, (int)(long)r["die_sides"]!, rpl, combo,
                (int)(long)r["spell_level"]!, (int)(long)r["base_level"]!, (int)(long)r["max_level"]!, (int)lvl);
            var effect = (long)r["effect"]!;
            var name = names.TryGetValue(effect, out var n) ? n : "unnamed effect";
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"Effect {r["effect_index"]} ({name}, effect {effect}): base={v.Base} min={v.Min} max={v.Max} perComboPoint={v.PerComboPoint} spellPowerCoefficient={(coef > 1.0E-5f ? coef : 0f)} scaled={(rpl != 0f ? "true" : "false")}\n"));
        }
        return sb.ToString().TrimEnd();
    }
}
