using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Acceptance;

/// <summary>
/// Random spells: the server's page, parsed here, against the local Spellwork tool's structured output and against Spell.dbc read directly.
/// Expected values come from those two; the page is only ever what's being checked.
/// </summary>
[Collection("04 Accuracy spells")]
public class AccuracySpellTests
{
    private const string Area = "Accuracy: spells";
    private const int Sample = 100;

    /// <summary>A spell's page in full: a large one lists its sections, so they're fetched by name.</summary>
    private static async Task<string> FullPage(LiveClient client, string id)
    {
        var r = await client.Call("get", new { id });
        if (!r.Text.Contains("This page is large", StringComparison.Ordinal)) return r.Text;
        var parts = await client.Call("get", new { id, sections = new[] { "summary", "effects", "triggers", "visuals" } });
        return parts.Text;
    }

    private static string? LineStarting(string page, string start) =>
        page.Split('\n').FirstOrDefault(l => l.StartsWith(start, StringComparison.Ordinal));

    private static List<JsonElement> List(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : new List<JsonElement>();

    /// <summary>Every non-zero value under a JSON value must be on the server's line, each under its own name where the page writes one.</summary>
    private static void Leaves(Checks c, string who, string path, JsonElement e, string line, string? key = null)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("raw", out var raw))
                {
                    var rawText = raw.GetRawText();
                    if (e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() != "0")
                        c.Has(line, $"{n.GetString()} ({rawText})", $"{who} {path}");
                    else if (rawText != "0")
                        c.Has(line, rawText, $"{who} {path}");
                    if (e.TryGetProperty("flags", out var flags))
                        foreach (var f in flags.EnumerateArray())
                            if (f.TryGetProperty("name", out var fn)) c.Has(line, fn.GetString()!, $"{who} {path} flag");
                    return;
                }
                foreach (var p in e.EnumerateObject()) Leaves(c, who, path + "." + p.Name, p.Value, line, p.Name);
                return;
            case JsonValueKind.Array:
                foreach (var (x, i) in e.EnumerateArray().Select((x, i) => (x, i))) Leaves(c, who, $"{path}[{i}]", x, line, x.ValueKind == JsonValueKind.Number ? null : key);
                return;
            case JsonValueKind.Number when e.GetRawText() != "0":
                c.Has(line, key is null ? e.GetRawText() : $"{key}={e.GetRawText()}", $"{who} {path}");
                return;
            case JsonValueKind.String when e.GetString() is { Length: > 0 } s:
                c.Has(line, s, $"{who} {path}");
                return;
            case JsonValueKind.True:
                c.Has(line, key is null ? "yes" : $"{key}=yes", $"{who} {path}");
                return;
        }
    }

    private static void CheckSpell(Checks c, int id, JsonElement s, string page, string? dbcName)
    {
        var who = $"spell:{id}:";
        var name = s.GetProperty("name").GetString() ?? "";
        var rank = s.GetProperty("rank").GetString() ?? "";
        var title = name.Trim().Length == 0 ? $"Spell {id}" : name;

        c.Equal("# " + AccuracyItemTests.OneLine(title, 200), page.Split('\n')[0], who + " title");
        if (dbcName is not null) c.Equal(dbcName, name, who + " Spell.dbc's name against Spellwork's");
        c.Has(page, $"\nSpell {id}: {title}" + (rank.Length > 0 ? $" ({rank})" : "") + "\n", who + " name and rank");
        c.Has(page, "realm: the CoA client (unproven)", who + " realm");
        c.Has(page, "trust: client", who + " trust");
        c.Has(page, $"{Settings.ClientClaim}, Spell.dbc from patch-T.MPQ ({Settings.AscensionWords})", who + " names the client and says what its copy's origin means");

        foreach (var key in new[] { "description", "nameSubtext", "auraDescription" })
            if (s.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } text) c.Has(page, text, $"{who} {key}");

        foreach (var key in new[] { "misc", "spellFamilyName", "spellClassMask", "schoolMask", "powers", "targets", "reagents", "aura", "procs", "context", "cooldowns", "levels", "interrupts", "shapeshift", "equippedItems", "totems" })
        {
            if (!s.TryGetProperty(key, out var section)) continue;
            var line = LineStarting(page, $"- {key}: ") ?? "";
            Leaves(c, who, key, section, line);
        }

        foreach (var word in List(s, "attributes"))
        {
            var value = word.GetProperty("value").GetUInt64();
            if (value == 0) continue;
            c.Has(LineStarting(page, "- attributes: ") ?? "", $"word {word.GetProperty("word").GetInt32()}: {value}", who + " attribute word");
        }

        // Spellwork leaves out what a spell doesn't have, such as a spell with no effects.
        if (List(s, "effects").Count == 0) c.Has(page, "## effects\nValues are computed at level 100. Use the scaling data for other levels.\nNone.", who + " says it has no effects");
        foreach (var e in List(s, "effects"))
        {
            var i = e.GetProperty("index").GetInt32();
            var line = LineStarting(page, $"- Effect {i}: ") ?? "";
            c.That(line.Length > 0, $"{who} effect {i} is on the page");
            var points = e.TryGetProperty("basePointsMin", out var min) && e.TryGetProperty("basePointsMax", out var max)
                ? $"basePoints={min.GetRawText()} to {max.GetRawText()}"
                : $"basePoints={e.GetProperty("basePoints").GetRawText()}";
            c.Has(line, points, $"{who} effect {i} base points");
            foreach (var p in e.EnumerateObject().Where(p => p.Name is not ("index" or "basePoints" or "basePointsMin" or "basePointsMax")))
                Leaves(c, who, $"effects[{i}].{p.Name}", p.Value, line, p.Name);
        }

        var triggers = s.TryGetProperty("triggers", out var tr) ? tr : default;
        var by = (triggers.ValueKind == JsonValueKind.Object ? List(triggers, "triggeredBy") : new List<JsonElement>()).Select(x => x.GetInt32()).ToList();
        if (by.Count == 0) c.Has(page, "Not triggered by any spell.", who + " triggered by nothing");
        else
        {
            c.Has(page, $"Triggered by {by.Count} spell(s):", who + " triggered-by count");
            foreach (var t in by.Take(100)) c.Has(page, $"\n- {t} (", who + " a spell that triggers it");
        }
        foreach (var t in triggers.ValueKind == JsonValueKind.Object ? List(triggers, "triggers") : new List<JsonElement>()) c.Has(page, $"spell {t.GetInt32()} (", who + " a spell it triggers");

        if (s.TryGetProperty("visual", out var visual) && visual.TryGetProperty("variants", out var variants))
        {
            foreach (var variant in variants.EnumerateArray()) c.Has(page, $"spellVisualId={variant.GetProperty("spellVisualId").GetRawText()}", who + " visual id");
            if (visual.TryGetProperty("kits", out var kits))
                c.Equal(kits.GetArrayLength(), page.Split('\n').Count(l => l.StartsWith("- Kit: ", StringComparison.Ordinal)), who + " number of visual kits");
        }
    }

    /// <summary>Which column of Spell.dbc holds the name, from the reviewed field config, so a name is checked against the file itself.</summary>
    private static int NameOffset()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Settings.RepoRoot, "config", "dbc-fields", "Spell.json")));
        var offset = 0;
        foreach (var f in doc.RootElement.GetProperty("fields").EnumerateArray())
        {
            var fname = f.GetProperty("name").GetString()!;
            if (fname.Equals("NameLang_0", StringComparison.Ordinal)) return offset;
            offset += f.GetProperty("type").GetString() == "byte" ? 1 : 4;
        }
        throw new InvalidDataException("no spell name field in the config");
    }

    [SkippableFact]
    public Task RandomSpells_AgainstSpellwork() => Suite.Run(Area, $"Spell 133 and {Sample} random spells against Spellwork CoA and Spell.dbc", async c =>
    {
        var client = await Suite.Anonymous();
        await using var spellwork = await Spellwork.Start();

        var manifest = ClientManifest.Instance;
        var table = manifest.Dbc("Spell")!;
        var dbc = new Wdbc(manifest.Restored(table.Winner!, table.Path));
        c.Equal("patch-T.MPQ", table.Winner!.Archive, "the manifest's winner for Spell.dbc");
        var nameOffset = NameOffset();
        var records = Enumerable.Range(0, dbc.Records).ToDictionary(r => dbc.Int(r, 0));

        var rng = Suite.Rng("spells");
        var ids = new[] { 133 }.Concat(records.Keys.OrderBy(_ => rng.Next()).Take(Sample)).Distinct().ToList();
        foreach (var id in ids)
        {
            var s = await spellwork.GetSpell(id);
            var page = await FullPage(client, "spell:" + id);
            var dbcName = dbc.Text(dbc.UInt(records[id], nameOffset));
            CheckSpell(c, id, s, page, dbcName);
        }
    });

    [SkippableFact]
    public Task SpotCheck_Spell133_ExactValues() => Suite.Run(Area, "Spell 133, exact values read from Spell.dbc by hand-checked fields", async c =>
    {
        var client = await Suite.Anonymous();
        var page = await client.Call("get", new { id = "spell:133" });
        await using var spellwork = await Spellwork.Start();
        var s = await spellwork.GetSpell(133);
        c.Equal("Fireball", s.GetProperty("name").GetString(), "Spellwork's name");
        c.Equal("Rank 1", s.GetProperty("rank").GetString(), "Spellwork's rank");
        c.Has(page.Text, "# Fireball", "title");
        c.Has(page.Text, "Spell 133: Fireball (Rank 1)", "name and rank");
        c.Has(page.Text, "effect=SPELL_EFFECT_SCHOOL_DAMAGE (2)", "effect 0 is school damage");
        c.Has(page.Text, "basePoints=14 to 22", "effect 0 base points");
        c.Has(page.Text, "spellFamilyName: SPELLFAMILY_MAGE (3)", "spell family");
    });
}

[Collection("05 Accuracy scaling")]
public class AccuracyScalingTests
{
    private const string Area = "Accuracy: scaling";

    private static double N(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Match Line(string text, long index) =>
        Regex.Match(text, $@"^Effect {index} \(.*?\): base=(\S+) min=(\S+) max=(\S+) perComboPoint=(\S+) spellPowerCoefficient=(\S+) scaled=(\S+)", RegexOptions.Multiline);

    /// <summary>The layer on this machine must be the version production serves, or its golden rows prove nothing about production.</summary>
    [SkippableFact]
    public Task GoldenRows() => Suite.Run(Area, "the 20 golden scaling spells, every row", async c =>
    {
        var client = await Suite.Anonymous();
        var file = Path.Combine(Settings.LocalLayers, "spells.sqlite");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();

        var status = await client.Call("status");
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM layer_meta";
            c.Equal(Suite.LayerVersions(status.Text)["spells"], cmd.ExecuteScalar()?.ToString(), "the local spells layer is the version production serves");
        }

        var rows = new List<(long Spell, long Level, long Index, double Base, double Min, double Max, double Combo, double Spc, bool Scaled)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT spell_id, level, effect_index, base, min, max, per_combo_point, spell_power_coefficient, scaled FROM golden_scaling ORDER BY 1, 2, 3";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) rows.Add((rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetDouble(3), rd.GetDouble(4), rd.GetDouble(5), rd.GetDouble(6), rd.GetDouble(7), rd.GetInt64(8) != 0));
        }
        c.That(rows.Select(r => r.Spell).Distinct().Count() >= 20, "at least 20 golden spells", rows.Select(r => r.Spell).Distinct().Count().ToString());

        foreach (var group in rows.GroupBy(r => (r.Spell, r.Level)))
        {
            var r = await client.Call("spell_scaling", new { id = $"spell:{group.Key.Spell}", level = group.Key.Level });
            foreach (var g in group)
            {
                var who = $"spell:{g.Spell} level {g.Level} effect {g.Index}";
                var m = Line(r.Text, g.Index);
                if (!c.That(m.Success, who + " is in the answer", r.All)) continue;
                c.Equal(g.Base, N(m.Groups[1].Value), who + " base");
                c.Equal(g.Min, N(m.Groups[2].Value), who + " min");
                c.Equal(g.Max, N(m.Groups[3].Value), who + " max");
                c.Equal(g.Combo, N(m.Groups[4].Value), who + " per combo point");
                c.That(Math.Abs(g.Spc - N(m.Groups[5].Value)) < 1e-6, who + " spell power coefficient", $"expected {g.Spc}, server {m.Groups[5].Value}");
                c.Equal(g.Scaled, m.Groups[6].Value == "true", who + " scaled");
            }
        }
    });

    [SkippableFact]
    public Task RandomSpells_AgainstSpellwork() => Suite.Run(Area, "20 random spells at random levels against Spellwork's calculator", async c =>
    {
        var client = await Suite.Anonymous();
        await using var spellwork = await Spellwork.Start();
        var manifest = ClientManifest.Instance;
        var table = manifest.Dbc("Spell")!;
        var dbc = new Wdbc(manifest.Restored(table.Winner!, table.Path));
        var rng = Suite.Rng("scaling");
        for (var n = 0; n < 20; n++)
        {
            var id = dbc.Int(rng.Next(dbc.Records), 0);
            var level = rng.Next(1, 101);
            var local = await spellwork.Scaling(id, level);
            var r = await client.Call("spell_scaling", new { id = id.ToString(CultureInfo.InvariantCulture), level });
            foreach (var e in local.GetProperty("effects").EnumerateArray())
            {
                var index = e.GetProperty("index").GetInt64();
                var who = $"spell:{id} level {level} effect {index}";
                var m = Line(r.Text, index);
                if (!c.That(m.Success, who + " is in the answer", r.All)) continue;
                c.Equal(e.GetProperty("base").GetDouble(), N(m.Groups[1].Value), who + " base");
                c.Equal(e.GetProperty("min").GetDouble(), N(m.Groups[2].Value), who + " min");
                c.Equal(e.GetProperty("max").GetDouble(), N(m.Groups[3].Value), who + " max");
                c.Equal(e.GetProperty("perComboPoint").GetDouble(), N(m.Groups[4].Value), who + " per combo point");
                c.That(Math.Abs(e.GetProperty("spellPowerCoefficient").GetDouble() - N(m.Groups[5].Value)) < 1e-6, who + " spell power coefficient", $"expected {e.GetProperty("spellPowerCoefficient")}, server {m.Groups[5].Value}");
                c.Equal(e.GetProperty("scaled").GetBoolean(), m.Groups[6].Value == "true", who + " scaled");
            }
        }
    });
}
