using System.Text;
using System.Text.Json;
using CoaMcp.Core;
using CoaMcp.Pipeline.Bisbeard;

namespace CoaMcp.Pipeline.Spells;

/// <summary>Turns one get_spell JSON document into the spell page: Markdown sections plus a compact comparison record.</summary>
internal static class SpellPage
{
    public static readonly string[] SectionNames = { "summary", "effects", "triggers", "visuals" };

    /// <summary>get_spell sections to request. "items" needs the emulator's world database, so it is never asked for.</summary>
    public static readonly string[] RequestedSections =
    {
        "misc", "attributes", "effects", "powers", "targets", "reagents", "aura", "procs", "context",
        "cooldowns", "levels", "interrupts", "shapeshift", "equippedItems", "triggers", "totems", "visual",
    };

    private const int MaxListed = 100;

    private static readonly HashSet<string> NotInSummary = new()
    {
        "id", "requestedId", "name", "rank", "difficulty", "sections", "level", "itemLevel", "effects",
        "triggers", "visual", "items", "itemsNote", "worldDatabaseError",
    };

    private static readonly HashSet<string> NotInData = new()
    {
        "requestedId", "difficulty", "sections", "level", "itemLevel", "items", "itemsNote", "worldDatabaseError",
        "description", "auraDescription", "nameSubtext", "visual",
    };

    public static Entity Build(JsonElement spell, Func<int, string?> nameOf, string source, RealmSet realm, DateOnly date, string? sourceWarning = null)
    {
        var id = spell.GetProperty("id").GetInt32();
        var name = Str(spell, "name");
        var rank = Str(spell, "rank");
        var title = string.IsNullOrWhiteSpace(name) ? $"Spell {id}" : name;

        var triggered = new List<int>();
        var body = new StringBuilder();
        Summary(body, spell, id, title, rank, sourceWarning);
        Effects(body, spell);
        Triggers(body, spell, id, nameOf, triggered);
        Visuals(body, spell);

        var links = triggered.Where(t => t != id && nameOf(t) is not null).Distinct()
            .Select(t => new Link($"spell:{t}", "triggers")).ToList();
        return new Entity($"spell:{id}", "spell", title, realm, Trust.Client, source, date, body.ToString().TrimEnd('\n') + "\n", DataJson(spell), links);
    }

    private static void Summary(StringBuilder b, JsonElement spell, int id, string title, string rank, string? sourceWarning)
    {
        b.Line("## summary");
        b.Line($"Spell {id}: {title}" + (rank.Length > 0 ? $" ({rank})" : ""));
        // The source and realm are in the page header, unindexed. Here their words would make every spell match a search for them.
        b.Line("Raw client data, not proven for any server.");
        if (sourceWarning is not null) b.Line(sourceWarning);
        foreach (var p in spell.EnumerateObject())
        {
            if (NotInSummary.Contains(p.Name)) continue;
            var text = p.Name == "attributes" ? Attributes(p.Value) : Fmt(p.Value);
            if (text.Length > 0) b.Line($"- {p.Name}: {text}");
        }
        b.Line();
    }

    // Only words with bits set, each with its word number so the reader knows which attribute word the names belong to.
    private static string Attributes(JsonElement words) =>
        string.Join("; ", words.EnumerateArray()
            .Where(w => w.GetProperty("value").GetUInt64() != 0)
            .Select(w => $"word {w.GetProperty("word").GetInt32()}: {w.GetProperty("value").GetRawText()}"
                + (w.TryGetProperty("flags", out var fl) && fl.GetArrayLength() > 0 ? " = " + string.Join(" | ", fl.EnumerateArray().Select(f => f.TryGetProperty("name", out var n) ? n.GetString() : f.GetRawText())) : "")));

    private static void Effects(StringBuilder b, JsonElement spell)
    {
        b.Line("## effects");
        b.Line("Values are computed at level 100. Use the scaling data for other levels.");
        var any = false;
        if (spell.TryGetProperty("effects", out var effects))
            foreach (var e in effects.EnumerateArray())
            {
                any = true;
                var parts = new List<string>();
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "index") continue;
                    if (p.Name is "basePointsMin" or "basePointsMax") continue;
                    var text = p.Name == "basePoints" ? BasePoints(e) : Fmt(p.Value);
                    if (text.Length > 0) parts.Add($"{p.Name}={text}");
                }
                b.Line($"- Effect {e.GetProperty("index").GetInt32()}: " + string.Join(", ", parts));
            }
        if (!any) b.Line("None.");
        b.Line();
    }

    private static string BasePoints(JsonElement e) =>
        e.TryGetProperty("basePointsMin", out var min) && e.TryGetProperty("basePointsMax", out var max)
            ? $"{min.GetRawText()} to {max.GetRawText()}"
            : e.GetProperty("basePoints").GetRawText();

    private static void Triggers(StringBuilder b, JsonElement spell, int id, Func<int, string?> nameOf, List<int> triggered)
    {
        b.Line("## triggers");
        var lines = new List<string>();
        if (spell.TryGetProperty("effects", out var effects))
            foreach (var e in effects.EnumerateArray())
                if (e.TryGetProperty("triggerSpell", out var t) && t.TryGetProperty("id", out var tid) && tid.GetInt32() != 0)
                {
                    triggered.Add(tid.GetInt32());
                    lines.Add($"- Effect {e.GetProperty("index").GetInt32()} triggers spell {tid.GetInt32()} ({nameOf(tid.GetInt32()) ?? "no row in the client data"})");
                }
        if (spell.TryGetProperty("triggers", out var tr) && tr.TryGetProperty("triggers", out var list))
            foreach (var t in list.EnumerateArray())
                if (!triggered.Contains(t.GetInt32()))
                {
                    triggered.Add(t.GetInt32());
                    lines.Add($"- Triggers spell {t.GetInt32()} ({nameOf(t.GetInt32()) ?? "no row in the client data"})");
                }
        b.Line(lines.Count == 0 ? "Triggers no spells." : string.Join("\n", lines));

        var by = new List<int>();
        if (spell.TryGetProperty("triggers", out var tr2) && tr2.TryGetProperty("triggeredBy", out var byList))
            by.AddRange(byList.EnumerateArray().Select(x => x.GetInt32()));
        if (by.Count == 0) b.Line("Not triggered by any spell.");
        else
        {
            b.Line($"Triggered by {by.Count} spell(s):");
            foreach (var t in by.Take(MaxListed)) b.Line($"- {t} ({nameOf(t) ?? "no row in the client data"})");
            if (by.Count > MaxListed) b.Line($"- and {by.Count - MaxListed} more");
        }
        b.Line();
    }

    private static void Visuals(StringBuilder b, JsonElement spell)
    {
        b.Line("## visuals");
        if (!spell.TryGetProperty("visual", out var v)) { b.Line("Visual data was not exported."); return; }
        if (v.TryGetProperty("storeUnloaded", out var un) && un.ValueKind == JsonValueKind.True)
        {
            var missing = v.TryGetProperty("missingStores", out var m) ? Fmt(m) : "";
            b.Line($"Visual data unavailable: these client tables did not load: {missing}.");
            return;
        }
        var variants = v.TryGetProperty("variants", out var vs) ? vs.EnumerateArray().ToList() : new List<JsonElement>();
        if (variants.Count == 0) { b.Line("None. The spell has no visual."); return; }
        foreach (var variant in variants) b.Line("- Visual: " + Fmt(variant));
        if (v.TryGetProperty("kits", out var kits))
            foreach (var kit in kits.EnumerateArray()) b.Line("- Kit: " + Fmt(kit));
        if (v.TryGetProperty("unresolvedReferences", out var un2) && un2.GetArrayLength() > 0)
            b.Line("- Unresolved references: " + Fmt(un2));
        if (v.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True)
            b.Line("- The kit list was cut off at the tool's limit.");
    }

    // Short readable text for any JSON value. Zero, false, empty and null values are dropped: they are the client's defaults.
    internal static string Fmt(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("raw", out var raw))
                {
                    if (e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() != "0")
                        return $"{n.GetString()} ({raw.GetRawText()})";
                    if (e.TryGetProperty("flags", out var flags))
                    {
                        var names = flags.EnumerateArray().Select(f => f.TryGetProperty("name", out var fn) ? fn.GetString() : f.GetRawText());
                        var joined = string.Join(" | ", names);
                        return raw.GetRawText() == "0" && joined.Length == 0 ? "" : $"{raw.GetRawText()}" + (joined.Length > 0 ? $" = {joined}" : "");
                    }
                    return raw.GetRawText() == "0" ? "" : raw.GetRawText();
                }
                var parts = new List<string>();
                foreach (var p in e.EnumerateObject())
                {
                    var t = Fmt(p.Value);
                    if (t.Length > 0) parts.Add($"{p.Name}={t}");
                }
                return string.Join(", ", parts);
            case JsonValueKind.Array:
                // A row of plain numbers (a mask) keeps its positions; other lists drop empty items.
                if (e.GetArrayLength() > 0 && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number))
                    return e.EnumerateArray().All(x => x.GetRawText() == "0") ? "" : "[" + string.Join(", ", e.EnumerateArray().Select(x => x.GetRawText())) + "]";
                return string.Join("; ", e.EnumerateArray().Select(Fmt).Where(s => s.Length > 0));
            case JsonValueKind.String: return e.GetString() ?? "";
            case JsonValueKind.Number: return e.GetRawText() == "0" ? "" : e.GetRawText();
            case JsonValueKind.True: return "yes";
            default: return "";
        }
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // The record kept for comparing spells: numbers only, names dropped where the raw value says the same thing.
    private static string DataJson(JsonElement spell)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in spell.EnumerateObject())
            {
                if (NotInData.Contains(p.Name)) continue;
                w.WritePropertyName(p.Name);
                Reduce(w, p.Value);
            }
            w.WritePropertyName("visualIds");
            w.WriteStartArray();
            if (spell.TryGetProperty("visual", out var v) && v.TryGetProperty("variants", out var vs))
                foreach (var variant in vs.EnumerateArray())
                    if (variant.TryGetProperty("spellVisualId", out var vid)) vid.WriteTo(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Reduce(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("raw", out var raw)) { raw.WriteTo(w); return; }
                if (e.EnumerateObject().Count() == 2 && e.TryGetProperty("id", out var id) && e.TryGetProperty("name", out _)) { id.WriteTo(w); return; }
                var hasValue = e.TryGetProperty("value", out _);
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "flags" && hasValue) continue;
                    w.WritePropertyName(p.Name);
                    Reduce(w, p.Value);
                }
                w.WriteEndObject();
                return;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var x in e.EnumerateArray()) Reduce(w, x);
                w.WriteEndArray();
                return;
            default:
                e.WriteTo(w);
                return;
        }
    }
}
