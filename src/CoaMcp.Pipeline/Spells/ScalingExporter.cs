using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Spells;

/// <summary>Exports what the server needs to compute spell values without Spellwork, plus golden values to check it against.</summary>
public static class ScalingExporter
{
    public static readonly uint[] GoldenLevels = { 1, 40, 80 };
    private const int GoldenSpellCount = 20;
    private const int PreferredSpell = 133;

    /// <returns>Rows in spell_scaling_inputs and in golden_scaling.</returns>
    public static (int Inputs, int Golden) Export(SpellSource source, string dbcDir, LayerWriter w)
    {
        var inputs = ExportInputs(source, w);
        ExportStatTable(dbcDir, "ScalingStatValues", w);
        ExportStatTable(dbcDir, "ScalingStatDistribution", w);
        return (inputs, ExportGolden(source, w));
    }

    private static int ExportInputs(SpellSource source, LayerWriter w)
    {
        w.Execute("""
            CREATE TABLE spell_scaling_inputs(
              spell_id INTEGER NOT NULL, effect_index INTEGER NOT NULL, effect INTEGER NOT NULL,
              base_points INTEGER NOT NULL, die_sides INTEGER NOT NULL, real_points_per_level REAL NOT NULL,
              points_per_combo REAL NOT NULL, spell_power_coefficient REAL NOT NULL,
              spell_level INTEGER NOT NULL, base_level INTEGER NOT NULL, max_level INTEGER NOT NULL,
              PRIMARY KEY(spell_id, effect_index))
            """);
        var rows = 0;
        foreach (var id in source.SpellIds)
        {
            var info = source.Info(id)!;
            var s = info.Spell;
            foreach (var e in info.Effects)
            {
                var i = e.Entry.EffectIndex;
                w.Execute("INSERT INTO spell_scaling_inputs VALUES ($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
                    (long)id, (long)i, (long)s.Effect[i], (long)s.EffectBasePoints[i], (long)s.EffectDieSides[i],
                    (double)s.EffectRealPointsPerLevel[i], (double)s.EffectPointsPerCombo[i], (double)s.EffectBonusCoefficient[i],
                    (long)s.SpellLevel, (long)s.BaseLevel, (long)s.MaxLevel);
                rows++;
            }
        }
        return rows;
    }

    // Raw copy of a WDBC table as integer columns c0..cN (unsigned values kept exact). Column meaning follows the CoA server's
    // ScalingStatValuesEntry / ScalingStatDistributionEntry (DBCStructure.h).
    private static void ExportStatTable(string dbcDir, string table, LayerWriter w)
    {
        var path = Path.Combine(dbcDir, table + ".dbc");
        if (!File.Exists(path)) throw new InvalidOperationException($"{table}.dbc is missing from '{dbcDir}'; the spell scaling port needs it.");
        using var r = new BinaryReader(File.OpenRead(path));
        if (new string(r.ReadChars(4)) != "WDBC") throw new InvalidDataException($"{table}.dbc is not a WDBC file.");
        var records = r.ReadInt32();
        var fields = r.ReadInt32();
        r.ReadInt32(); // record size
        r.ReadInt32(); // string block size

        var cols = string.Join(",", Enumerable.Range(0, fields).Select(c => $"c{c} INTEGER NOT NULL"));
        w.Execute($"CREATE TABLE {table}({cols})");
        var marks = string.Join(",", Enumerable.Range(0, fields).Select(c => "$" + c));
        for (var n = 0; n < records; n++)
        {
            var values = new object?[fields];
            for (var c = 0; c < fields; c++) values[c] = (long)r.ReadUInt32();
            w.Execute($"INSERT INTO {table} VALUES ({marks})", values);
        }
    }

    // Spells with a per-level term first, since those are the ones the formula can get wrong; then dice; then plain values.
    private static IReadOnlyList<int> GoldenSpells(SpellSource source)
    {
        var leveled = new List<int>();
        var dice = new List<int>();
        var plain = new List<int>();
        foreach (var id in source.SpellIds)
        {
            var info = source.Info(id)!;
            if (info.Effects.Count == 0) continue;
            if (info.Effects.Any(e => info.Spell.EffectRealPointsPerLevel[e.Entry.EffectIndex] != 0)) leveled.Add(id);
            else if (info.Effects.Any(e => info.Spell.EffectDieSides[e.Entry.EffectIndex] != 0)) dice.Add(id);
            else plain.Add(id);
        }

        var picked = new List<int>();
        if (source.Info(PreferredSpell) is { Effects.Count: > 0 }) picked.Add(PreferredSpell);
        var want = GoldenSpellCount - picked.Count;
        var nLeveled = want * 3 / 5;
        var nDice = want / 5;
        foreach (var (list, n) in new[] { (leveled, nLeveled), (dice, nDice), (plain, want - nLeveled - nDice) })
            picked.AddRange(Spread(list, n).Where(i => !picked.Contains(i)));
        // Top up if a group was too small, so the set stays at 20 where the data allows.
        foreach (var id in leveled.Concat(dice).Concat(plain))
        {
            if (picked.Count >= GoldenSpellCount) break;
            if (!picked.Contains(id)) picked.Add(id);
        }
        return picked.Take(GoldenSpellCount).Order().ToList();
    }

    private static IEnumerable<int> Spread(List<int> list, int n)
    {
        if (list.Count <= n) return list;
        return Enumerable.Range(0, n).Select(k => list[(int)((long)k * list.Count / n)]);
    }

    private static int ExportGolden(SpellSource source, LayerWriter w)
    {
        w.Execute("""
            CREATE TABLE golden_scaling(
              spell_id INTEGER NOT NULL, level INTEGER NOT NULL, effect_index INTEGER NOT NULL, effect INTEGER NOT NULL,
              base REAL NOT NULL, min REAL NOT NULL, max REAL NOT NULL, per_combo_point REAL NOT NULL,
              spell_power_coefficient REAL NOT NULL, scaled INTEGER NOT NULL,
              PRIMARY KEY(spell_id, level, effect_index))
            """);
        var rows = 0;
        foreach (var id in GoldenSpells(source))
            foreach (var level in GoldenLevels)
            {
                var root = JsonDocument.Parse(source.Decoder.CalculateSpellScaling(id, level, 0).GetAwaiter().GetResult()).RootElement;
                if (!root.GetProperty("ok").GetBoolean())
                    throw new InvalidOperationException($"calculate_spell_scaling failed for spell {id}: {root.GetProperty("error").GetProperty("message").GetString()}");
                foreach (var e in root.GetProperty("result").GetProperty("effects").EnumerateArray())
                {
                    w.Execute("INSERT INTO golden_scaling VALUES ($0,$1,$2,$3,$4,$5,$6,$7,$8,$9)",
                        (long)id, (long)level, (long)e.GetProperty("index").GetInt32(), (long)e.GetProperty("effect").GetProperty("raw").GetInt32(),
                        e.GetProperty("base").GetDouble(), e.GetProperty("min").GetDouble(), e.GetProperty("max").GetDouble(),
                        e.GetProperty("perComboPoint").GetDouble(), e.GetProperty("spellPowerCoefficient").GetDouble(),
                        e.GetProperty("scaled").GetBoolean() ? 1L : 0L);
                    rows++;
                }
            }
        return rows;
    }
}
