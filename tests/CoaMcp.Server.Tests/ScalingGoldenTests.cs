using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Server.Tests;

public class ScalingGoldenTests : IClassFixture<ReadFixture>
{
    private readonly ReadFixture _f;
    public ScalingGoldenTests(ReadFixture f) => _f = f;

    private static Dictionary<string, object?> A(params (string, object?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public async Task Scaling_MatchesGolden()
    {
        var golden = new List<(long Spell, long Level, long Index, double Base, double Min, double Max, double Combo, double Spc, bool Scaled)>();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_f.Harness.LayerDir, "spells.sqlite")};Mode=ReadOnly;Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT spell_id, level, effect_index, base, min, max, per_combo_point, spell_power_coefficient, scaled FROM golden_scaling";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                golden.Add((rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetDouble(3), rd.GetDouble(4), rd.GetDouble(5), rd.GetDouble(6), rd.GetDouble(7), rd.GetInt64(8) != 0));
        }
        Assert.True(golden.Count >= 5);

        foreach (var g in golden)
        {
            var (text, isError) = await _f.Call("spell_scaling", A(("id", $"spell:{g.Spell}"), ("level", g.Level)));
            Assert.False(isError);
            var m = Regex.Match(text, $@"Effect {g.Index} .*?base=(\S+) min=(\S+) max=(\S+) perComboPoint=(\S+) spellPowerCoefficient=(\S+) scaled=(\S+)");
            Assert.True(m.Success, $"no effect {g.Index} line for spell {g.Spell} level {g.Level}:\n{text}");
            double P(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
            Assert.Equal(g.Base, P(1));
            Assert.Equal(g.Min, P(2));
            Assert.Equal(g.Max, P(3));
            Assert.Equal(g.Combo, P(4));
            Assert.Equal(g.Spc, P(5));
            Assert.Equal(g.Scaled, m.Groups[6].Value == "true");
        }
    }

    [Fact]
    public async Task Scaling_ClampsLevelAndSaysSo_AndShowsEffectName()
    {
        var (text, _) = await _f.Call("spell_scaling", A(("id", "10"), ("level", 5000), ("itemLevel", 200)));
        Assert.Contains("level 5000 capped at 100", text);
        Assert.Contains("SPELL_EFFECT_SCHOOL_DAMAGE / SPELL_EFFECT_SCHOOL_DAMAGE_ALIAS", text);
        Assert.Contains("item level is not applied", text);
        var (low, _) = await _f.Call("spell_scaling", A(("id", "spell:10"), ("level", -4)));
        Assert.Contains("level -4 raised to 1", low);
    }

    [Fact]
    public async Task Scaling_UnknownSpell_SaysNotFound()
    {
        var (text, isError) = await _f.Call("spell_scaling", A(("id", "spell:424242"), ("level", 80)));
        Assert.True(isError);
        Assert.Contains("spell:424242", text);
    }

    [Fact]
    public void ScalingFormula_FollowsCalcValue()
    {
        // Truncation is toward zero, the MaxLevel clamp wins over the BaseLevel clamp, and a zero per-level term skips level handling.
        Assert.Equal((53f, 53f, 73f, 0f), SpellScaling.Value(99, 21, 2.5f, 0f, 20, 0, 30, 1));
        Assert.Equal((99f, 99f, 99f, 0f), SpellScaling.Value(99, 0, 0f, 0f, 20, 5, 30, 1));
        Assert.Equal((101f, 95f, 101f, 2.5f), SpellScaling.Value(100, -5, 0f, 2.5f, 0, 0, 0, 80));
    }
}
