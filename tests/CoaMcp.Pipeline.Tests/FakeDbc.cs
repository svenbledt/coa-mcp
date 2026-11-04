using System.Text;

namespace CoaMcp.Pipeline.Tests;

/// <summary>
/// Writes small WDBC files in the CoA 3.3.5a layouts Spellwork expects, so spell tests need no client data.
/// Everything here is invented test data.
/// </summary>
public static class FakeDbc
{
    public const int SpellFields = 234;

    private sealed class Strings
    {
        private readonly MemoryStream _block = new();
        public Strings() => _block.WriteByte(0);

        public uint Add(string s)
        {
            var offset = (uint)_block.Length;
            var bytes = Encoding.UTF8.GetBytes(s);
            _block.Write(bytes);
            _block.WriteByte(0);
            return offset;
        }

        public byte[] Bytes => _block.ToArray();
    }

    private static void Write(string path, int fields, List<uint[]> rows, Strings strings)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("WDBC"));
        w.Write(rows.Count);
        w.Write(fields);
        w.Write(fields * 4);
        w.Write(strings.Bytes.Length);
        foreach (var row in rows)
            foreach (var v in row) w.Write(v);
        w.Write(strings.Bytes);
    }

    private static uint F(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

    private static uint[] Row(int fields, params (int Col, uint Value)[] set)
    {
        var row = new uint[fields];
        foreach (var (col, value) in set) row[col] = value;
        return row;
    }

    /// <summary>Spell 133 scales per level and has a periodic aura; 1569630 triggers 133; 200 is plain.</summary>
    public static string Create(string dir)
    {
        Directory.CreateDirectory(dir);
        var s = new Strings();
        var spells = new List<uint[]>
        {
            Row(SpellFields,
                (0, 133), (4, 65536), (6, 1024), (210, 8), (225, 4), (208, 3), (35, 101), (37, 5), (38, 1), (39, 1),
                (71, 2), (72, 6), (74, 9), (75, 1), (77, F(1.5f)), (80, 14), (81, 1),
                (86, 6), (87, 6), (95 + 1, 3), (98 + 1, 2000),
                (131, 67), (136, s.Add("Fireball")), (153, s.Add("Rank 1")),
                (170, s.Add("Hurls a fiery ball that causes $s1 Fire damage.")),
                (187, s.Add("$s2 Fire damage every $t2 seconds."))),
            Row(SpellFields,
                (0, 1569630), (71, 6), (72, 6), (95, 23), (98, 1000), (116, 133), (86, 1),
                (136, s.Add("Throw"))),
            Row(SpellFields,
                (0, 200), (136, s.Add("Plain Spell"))),
        };
        Write(Path.Combine(dir, "Spell.dbc"), SpellFields, spells, s);

        var vs = new Strings();
        Write(Path.Combine(dir, "SpellVisual.dbc"), 32, new() { Row(32, (0, 67), (2, 38)) }, vs);
        Write(Path.Combine(dir, "SpellVisualKit.dbc"), 38, new() { Row(38, (0, 38), (6, 288)) }, vs);
        var en = new Strings();
        Write(Path.Combine(dir, "SpellVisualEffectName.dbc"), 7,
            new() { Row(7, (0, 288), (1, en.Add("Fire Cast Hand")), (2, en.Add(@"Spells\Fire_Cast_Hand.mdx"))) }, en);
        Write(Path.Combine(dir, "SpellVisualKitModelAttach.dbc"), 10, new() { Row(10, (0, 1), (1, 999)) }, vs);

        var none = new Strings();
        var ssv = new List<uint[]>();
        for (uint lvl = 1; lvl <= 3; lvl++) ssv.Add(Row(24, (0, lvl), (1, lvl), (2, 100 * lvl), (16, 7 * lvl)));
        Write(Path.Combine(dir, "ScalingStatValues.dbc"), 24, ssv, none);
        Write(Path.Combine(dir, "ScalingStatDistribution.dbc"), 22, new() { Row(22, (0, 1), (1, 3), (2, 5)) }, none);
        return dir;
    }
}
