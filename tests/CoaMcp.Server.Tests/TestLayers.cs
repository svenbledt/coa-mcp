using CoaMcp.Core;

namespace CoaMcp.Server.Tests;

/// <summary>Small real SQLite layers shaped like the pipeline's output, built with <see cref="LayerWriter"/>.</summary>
public static class TestLayers
{
    public static readonly DateOnly Date = new(2026, 8, 27);
    public static readonly string BigPageId = "talents:coa:testclass";
    public static readonly string TrickyPageId = "guide:coa:tricky";

    public static string Build(bool withSpells = true, bool withBisbeard = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "coamcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (withBisbeard) BuildBisbeard(Path.Combine(dir, "bisbeard.sqlite"));
        if (withSpells) BuildSpells(Path.Combine(dir, "spells.sqlite"));
        return dir;
    }

    private static Entity E(string id, string type, string title, string body, string source, Trust trust,
        string[] realms, bool proven, string? data = null, params Link[] links) =>
        new(id, type, title, new RealmSet(realms, proven), trust, source, Date, body, data, links);

    private static void BuildBisbeard(string path)
    {
        using var w = new LayerWriter(path, "bisbeard", "7");
        w.Add(E("item:10531", "item", "Libram of the Dawn", "Epic Libram Ranged\nItem level: 65\n", "BisBeard", Trust.Derived, new[] { "coa" }, true));
        w.Add(E("item:wotlk:10531", "item", "Libram of the Dawn", "Wotlk copy of the libram.\n", "BisBeard", Trust.Derived, new[] { "wotlk" }, true));
        w.Add(E("class:coa:barbarian", "class", "Barbarian", "Barbarian class, a melee class.\n", "BisBeard", Trust.Derived, new[] { "coa" }, true));
        w.Add(E("rules:coa:races", "rules", "Race rules", "Races of the barbarian class.\n", "BisBeard", Trust.Derived, new[] { "coa" }, false));
        for (var i = 0; i < 120; i++)
            w.Add(E($"item:900{i:000}", "item", $"Fireball Rod {i}", "A rod that casts fireball.\n", "BisBeard", Trust.Derived, new[] { "coa" }, true));
        foreach (var zone in new[] { "Ashenvale", "Azshara", "Feralas", "Silithus" })
            w.Add(E($"marker:mystic-enchant-{zone.ToLowerInvariant()}-improved-fireball", "marker", "Mystic Scroll: Improved Fireball",
                $"Mystic Scroll: Improved Fireball (mystic-enchant) in {zone}.\n", "BisBeard", Trust.Derived, new[] { "coa" }, true,
                "{\"zoneName\":\"" + zone + "\"}"));
        w.Add(E("marker:mystic-enchant-azshara-glyph-of-fireball", "marker", "Mystic Scroll: Glyph of Fireball",
            "Mystic Scroll: Glyph of Fireball (mystic-enchant) in Azshara.\n", "BisBeard", Trust.Derived, new[] { "coa" }, true,
            "{\"zoneName\":\"Azshara\"}"));
        w.Add(E(BigPageId, "talents", "Test class talents",
            "Talents of the test class.\n\n## Alpha\n" + new string('a', 5000) + "\n\n## Beta\n" + new string('b', 5000) + "\n\n## Gamma\nshort gamma text\n",
            "BisBeard", Trust.Derived, new[] { "coa" }, true));
        w.Add(E(TrickyPageId, "guide", "Tricky guide",
            "Preamble line.\n\n## Real\nreal text\n```\n## Fake fenced\nfenced text\n```\n    ## Fake indented\nindented text\n### Sub heading\nsub text\n\n## Other\nother text\n",
            "BisBeard", Trust.Derived, new[] { "coa" }, false));
        w.Finish();
    }

    private static void BuildSpells(string path)
    {
        using var w = new LayerWriter(path, "spells", "3");
        w.Add(E("spell:1", "spell", "Fire Blast",
            "## summary\nSpell 1: Fire Blast\n\n## effects\nEFFECTSMARK\n\n## triggers\nTriggers no spells.\n\n## visuals\nVISUALSMARK\n",
            "CoA client DBC (via Spellwork)", Trust.Client, new[] { "coa-client" }, false,
            """{"id":1,"name":"Fire Blast","schoolMask":4,"effects":[{"index":0,"basePoints":10}],"misc":{"speed":24}}"""));
        w.Add(E("spell:133", "spell", "Fireball", "## summary\nSpell 133: Fireball, hurls a fiery ball.\n",
            "CoA client DBC (via Spellwork)", Trust.Client, new[] { "coa-client" }, false, "{\"id\":133}"));
        w.Add(E("spell:2", "spell", "Fire Blast Improved",
            "## summary\nSpell 2: Fire Blast Improved\n\n## effects\nEFFECTS2\n\n## triggers\nnone\n\n## visuals\nnone\n",
            "CoA client DBC (via Spellwork)", Trust.Client, new[] { "coa-client" }, false,
            """{"id":2,"name":"Fire Blast Improved","schoolMask":4,"effects":[{"index":0,"basePoints":25}],"misc":{"speed":24},"extra":true}"""));
        foreach (var id in new[] { 10, 11, 12 })
            w.Add(E($"spell:{id}", "spell", $"Scaling Test {id}", $"## summary\nSpell {id}\n", "CoA client DBC (via Spellwork)",
                Trust.Client, new[] { "coa-client" }, false, $$"""{"id":{{id}}}"""));

        w.Execute("CREATE TABLE enums(name TEXT NOT NULL, value INTEGER NOT NULL, value_text TEXT NOT NULL, label TEXT NOT NULL)");
        w.Execute("CREATE TABLE flag_kinds(kind TEXT PRIMARY KEY, enum_name TEXT NOT NULL)");
        w.Execute("INSERT INTO flag_kinds VALUES ('SpellSchoolMask','SpellSchoolMask')");
        foreach (var (v, n) in new[] { (1L, "SPELL_SCHOOL_MASK_NORMAL"), (2L, "SPELL_SCHOOL_MASK_HOLY"), (4L, "SPELL_SCHOOL_MASK_FIRE") })
            w.Execute("INSERT INTO enums VALUES ('SpellSchoolMask', $0, $1, $2)", v, v.ToString(), n);
        w.Execute("INSERT INTO enums VALUES ('SpellEffects', 2, '2', 'SPELL_EFFECT_SCHOOL_DAMAGE')");
        // The real enums hold aliases: two names for one value.
        w.Execute("INSERT INTO enums VALUES ('SpellEffects', 2, '2', 'SPELL_EFFECT_SCHOOL_DAMAGE_ALIAS')");

        w.Execute("""
            CREATE TABLE spell_scaling_inputs(
              spell_id INTEGER NOT NULL, effect_index INTEGER NOT NULL, effect INTEGER NOT NULL,
              base_points INTEGER NOT NULL, die_sides INTEGER NOT NULL, real_points_per_level REAL NOT NULL,
              points_per_combo REAL NOT NULL, spell_power_coefficient REAL NOT NULL,
              spell_level INTEGER NOT NULL, base_level INTEGER NOT NULL, max_level INTEGER NOT NULL,
              PRIMARY KEY(spell_id, effect_index))
            """);
        w.Execute("""
            CREATE TABLE golden_scaling(
              spell_id INTEGER NOT NULL, level INTEGER NOT NULL, effect_index INTEGER NOT NULL, effect INTEGER NOT NULL,
              base REAL NOT NULL, min REAL NOT NULL, max REAL NOT NULL, per_combo_point REAL NOT NULL,
              spell_power_coefficient REAL NOT NULL, scaled INTEGER NOT NULL,
              PRIMARY KEY(spell_id, level, effect_index))
            """);
        // Inputs: spell 10 has a die and a per-level term capped by MaxLevel; 11 is raised to BaseLevel; 12 has a negative die and combo points.
        Input(w, 10, 0, 2, 99, 21, 2.5, 0, 0.5, 20, 0, 30);
        Input(w, 11, 0, 6, 50, 0, 3, 0, 0, 4, 10, 0);
        Input(w, 12, 0, 6, 100, -5, 0, 2.5, 0, 0, 0, 0);
        // Golden values worked out by hand from the 3.3.5 CalcValue rule, independent of the code under test.
        // Spell 10 at level 80: level capped to 30, minus 20 = 10, (int)(10*2.5)=25, bp 124, die 21 -> 125..145.
        // Spell 10 at level 1: 1-20=-19, (int)(-47.5)=-47 (toward zero), bp 52 -> 53..73.
        Golden(w, 10, 80, 0, 2, 125, 125, 145, 0, 0.5, true);
        Golden(w, 10, 1, 0, 2, 53, 53, 73, 0, 0.5, true);
        // Spell 11 at level 5: raised to BaseLevel 10, minus max(10,4) = 0 -> 50. At level 40: 40-10=30, +90 -> 140.
        Golden(w, 11, 5, 0, 6, 50, 50, 50, 0, 0, true);
        Golden(w, 11, 40, 0, 6, 140, 140, 140, 0, 0, true);
        // Spell 12: no per-level term, die -5: roll 1 gives 101, roll -5 gives 95.
        Golden(w, 12, 80, 0, 6, 101, 95, 101, 2.5, 0, false);
        w.Finish();
    }

    private static void Input(LayerWriter w, int id, int idx, int effect, int bp, int die, double rpl, double combo, double spc, int sl, int bl, int ml) =>
        w.Execute("INSERT INTO spell_scaling_inputs VALUES ($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
            (long)id, (long)idx, (long)effect, (long)bp, (long)die, rpl, combo, spc, (long)sl, (long)bl, (long)ml);

    private static void Golden(LayerWriter w, int id, int level, int idx, int effect, double b, double min, double max, double combo, double spc, bool scaled) =>
        w.Execute("INSERT INTO golden_scaling VALUES ($0,$1,$2,$3,$4,$5,$6,$7,$8,$9)",
            (long)id, (long)level, (long)idx, (long)effect, b, min, max, combo, spc, scaled ? 1L : 0L);
}
