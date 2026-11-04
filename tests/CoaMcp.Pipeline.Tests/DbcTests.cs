using System.Text;
using System.Text.Json;
using CoaMcp.Core;
using CoaMcp.Pipeline.Dbc;
using CoaMcp.Pipeline.Mpq;
using CoaMcp.Pipeline.Publish;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

/// <summary>Builds invented WDBC files in any shape, including broken ones.</summary>
public sealed class DbcBytes
{
    private readonly MemoryStream _strings = new();
    private readonly List<uint[]> _rows = new();
    private readonly int _fields;

    public DbcBytes(int fields)
    {
        _fields = fields;
        _strings.WriteByte(0);
    }

    public uint Str(string s)
    {
        var offset = (uint)_strings.Length;
        _strings.Write(Encoding.UTF8.GetBytes(s));
        _strings.WriteByte(0);
        return offset;
    }

    public static uint F(float f) => BitConverter.SingleToUInt32Bits(f);

    public DbcBytes Row(params uint[] cells)
    {
        Assert.Equal(_fields, cells.Length);
        _rows.Add(cells);
        return this;
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("WDBC"));
        w.Write(_rows.Count);
        w.Write(_fields);
        w.Write(_fields * 4);
        w.Write((int)_strings.Length);
        foreach (var row in _rows) foreach (var c in row) w.Write(c);
        w.Write(_strings.ToArray());
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A file whose records are not four bytes per field: <paramref name="records"/> holds them as raw bytes.</summary>
    public static byte[] Packed(int fields, int recordSize, int rows, byte[] records, byte[]? strings = null)
    {
        strings ??= new byte[] { 0 };
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("WDBC"));
        w.Write(rows);
        w.Write(fields);
        w.Write(recordSize);
        w.Write(strings.Length);
        w.Write(records);
        w.Write(strings);
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>A pretend restore: real manifest, real files under &lt;root&gt;/&lt;archive&gt;/&lt;path&gt;, and a field config folder.</summary>
public sealed class DbcWorld : IDisposable
{
    public static readonly long Oct3 = new DateTime(2026, 10, 3, 17, 30, 0, DateTimeKind.Utc).Ticks;
    public static readonly long Oct22 = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc).Ticks;

    private readonly TempDir _dir = new();
    private readonly List<(string Archive, string Path, byte[]? Bytes, long Mtime)> _entries = new();
    public string Root => Path.Combine(_dir.Path, "restored");
    public string Fields => Path.Combine(_dir.Path, "fields");
    public string Winners => Path.Combine(_dir.Path, "winners");
    public string Layer => _dir.File("dbc.sqlite");

    public DbcWorld()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Fields);
    }

    public void Dispose() => _dir.Dispose();

    public DbcWorld Add(string archive, string table, byte[] bytes, long? mtime = null)
    {
        _entries.Add((archive, $"DBFilesClient/{table}.dbc", bytes, mtime ?? Oct3));
        return this;
    }

    public DbcWorld Marker(string archive, string table)
    {
        _entries.Add((archive, $"DBFilesClient/{table}.dbc", null, Oct3));
        return this;
    }

    public DbcWorld Config(string table, params (string Name, string Type)[] fields)
    {
        File.WriteAllText(Path.Combine(Fields, table + ".json"), JsonSerializer.Serialize(new
        {
            table, source = "test source", fields = fields.Select(f => new { name = f.Name, type = f.Type }),
        }));
        return this;
    }

    public string BuildManifest()
    {
        var config = ArchiveConfig.Load(Path.Combine(AppContext.BaseDirectory, "config", "archives.json"));
        var path = Path.Combine(Root, "manifest.sqlite");
        if (File.Exists(path)) File.Delete(path);
        using (var m = RestoreManifest.OpenForRestore(path))
        {
            foreach (var group in _entries.GroupBy(e => e.Archive))
            {
                var a = config.Archives.Single(x => x.Path == group.Key);
                m.SaveArchive(new ArchiveInfo(a.Path, a.Priority, a.Origin, a.Realm), new ArchiveFacts(1, 1, 1, 0, 0), new ArchiveIdentity(1, group.First().Mtime));
            }
            var rows = new List<FileRow>();
            var next = new Dictionary<string, int>();
            foreach (var (archive, rel, bytes, _) in _entries)
            {
                var i = next[archive] = next.GetValueOrDefault(archive) + 1;
                rows.Add(new FileRow(archive, i, (uint)i, rel, bytes?.Length ?? 0, null, bytes is null ? "delete_marker" : "ok", null, 0, 0, (uint)i, (uint)i,
                    rel.Replace('/', '\\'), "own_listfile", null));
                if (bytes is null) continue;
                var file = Path.Combine(Root, archive, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
            }
            m.SaveFiles(rows);
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    public DbcExportResult Export(ClientRelease? client = null)
    {
        var result = DbcExporter.Run(new DbcExportOptions(BuildManifest(), ArchiveConfig.Load(Path.Combine(AppContext.BaseDirectory, "config", "archives.json")),
            Fields, Layer, Winners, "1", Client: client));
        SqliteConnection.ClearAllPools();
        return result;
    }

    public LayerReader Open() => new(Layer);
}

public class DbcReaderTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string Write(byte[] bytes)
    {
        var path = _dir.File(Guid.NewGuid().ToString("N") + ".dbc");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Reader_ParsesHeaderAndStrings()
    {
        var d = new DbcBytes(3);
        var fire = d.Str("Fireball");
        var path = Write(d.Row(133, fire, DbcBytes.F(1.5f)).Build());

        using var r = DbcReader.Open(path);

        Assert.Equal(1, r.RecordCount);
        Assert.Equal(3, r.FieldCount);
        Assert.Equal(12, r.RecordSize);
        Assert.Equal("Fireball", r.GetString(fire));
        Assert.Equal("", r.GetString(0));
        Assert.Null(r.GetString(100_000));
        Assert.True(r.IsStringStart(fire));
        Assert.False(r.IsStringStart(fire + 1));
    }

    [Fact]
    public void Reader_StringBlockWithoutLeadingNul_OffsetZeroIsARealString()
    {
        var strings = Encoding.UTF8.GetBytes("Map Trigger\0Other\0");
        var rec = BitConverter.GetBytes(1u).Concat(BitConverter.GetBytes(0u)).ToArray();

        using var r = DbcReader.Open(Write(DbcBytes.Packed(2, 8, 1, rec, strings)));

        Assert.Equal("Map Trigger", r.GetString(0));
        Assert.Equal("Other", r.GetString(12));
    }

    [Fact]
    public void Reader_CountMatchesHeader()
    {
        var d = new DbcBytes(2);
        for (uint i = 0; i < 500; i++) d.Row(i, i * 2);
        using var r = DbcReader.Open(Write(d.Build()));

        uint seen = 0;
        foreach (var rec in r.Records())
        {
            Assert.Equal(seen, BitConverter.ToUInt32(rec, 0));
            Assert.Equal(seen * 2, BitConverter.ToUInt32(rec, 4));
            seen++;
        }
        Assert.Equal(500u, seen);
    }

    [Fact]
    public void Reader_NotWdbc_Refused()
    {
        var ex = Assert.Throws<DbcFormatException>(() => DbcReader.Open(Write(Encoding.ASCII.GetBytes("WDB2 is not a thing, nor is this a header"))));
        Assert.Contains("WDBC", ex.Message);
    }

    [Fact]
    public void Reader_TooShortForAHeader_Refused() => Assert.Throws<DbcFormatException>(() => DbcReader.Open(Write(new byte[] { 87, 68 })));

    [Fact]
    public void Reader_LengthDisagreesWithHeader_Refused()
    {
        var bytes = new DbcBytes(2).Row(1, 2).Build();
        var ex = Assert.Throws<DbcFormatException>(() => DbcReader.Open(Write(bytes.Concat(new byte[] { 1, 2, 3 }).ToArray())));
        Assert.Contains("length", ex.Message);
    }

    [Fact]
    public void Reader_HugeHeaderValues_DoNotOverflow()
    {
        var bytes = new byte[20];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), uint.MaxValue);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), uint.MaxValue);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), uint.MaxValue);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), uint.MaxValue);

        Assert.Throws<DbcFormatException>(() => DbcReader.Open(Write(bytes)));
    }
}

public class FieldGuessTests
{
    private static byte[] Block(params string[] strings) =>
        new byte[] { 0 }.Concat(strings.SelectMany(s => Encoding.UTF8.GetBytes(s).Append((byte)0))).ToArray();

    private static GuessKind Guess(byte[] strings, params uint[] values) => FieldGuess.Classify(values, strings).Kind;

    [Fact]
    public void Guess_StringVsIntVsFloat()
    {
        var strings = Block("Fireball", "Frostbolt");
        Assert.Equal(GuessKind.MaybeString, Guess(strings, 1, 10, 1));        // both are starts of strings
        Assert.Equal(GuessKind.Int, Guess(strings, 7, 100, 3));
        Assert.Equal(GuessKind.Float, Guess(strings, DbcBytes.F(1.5f), DbcBytes.F(-20.25f), 0));
    }

    [Fact]
    public void Guess_AllZeros_IsIntAndSaysItCouldBeAnything()
    {
        var g = FieldGuess.Classify(new uint[] { 0, 0, 0 }, Block("Fireball"));
        Assert.Equal(GuessKind.Int, g.Kind);
        Assert.Contains("zero", g.Note);
    }

    [Fact]
    public void Guess_NoRows_IsIntAndSaysSo()
    {
        var g = FieldGuess.Classify(Array.Empty<uint>(), Block("Fireball"));
        Assert.Equal(GuessKind.Int, g.Kind);
        Assert.Contains("no rows", g.Note);
    }

    [Fact]
    public void Guess_SmallIntsOnStringStarts_AreOnlyMaybeStrings()
    {
        // 1 and 3 happen to start strings: a flag column could look exactly like this.
        var strings = Block("a", "b");
        var g = FieldGuess.Classify(new uint[] { 1, 3, 1, 0 }, strings);
        Assert.Equal(GuessKind.MaybeString, g.Kind);
        Assert.Contains("maybe", g.Note);
    }

    [Fact]
    public void Guess_SmallIntsNotOnStringStarts_AreInts()
    {
        Assert.Equal(GuessKind.Int, Guess(Block("abcdef"), 1, 2, 3));   // 2 and 3 fall inside a string
    }

    [Fact]
    public void Guess_NoStringTable_IsNeverAString()
    {
        Assert.Equal(GuessKind.Int, Guess(new byte[] { 0 }, 0, 0));
        Assert.Equal(GuessKind.Int, Guess(Array.Empty<byte>(), 1));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]        // -1 is a NaN as a float
    [InlineData(0x80000000u)]        // only the sign bit
    [InlineData(0x00800000u)]        // a large id: as a float it is absurdly small
    [InlineData(5u)]
    public void Guess_ImplausibleFloats_AreInts(uint odd)
    {
        Assert.Equal(GuessKind.Int, Guess(Array.Empty<byte>(), DbcBytes.F(1.5f), odd));
    }
}

public class DbcExportTests : IDisposable
{
    private readonly DbcWorld _w = new();
    public void Dispose() => _w.Dispose();

    private static IReadOnlyDictionary<string, object?> Table(LayerReader r, string name) =>
        r.Query("SELECT * FROM dbc_tables WHERE name = $0", name).Single();

    private static List<IReadOnlyDictionary<string, object?>> Fields(LayerReader r, string table) =>
        r.Query("SELECT * FROM dbc_fields WHERE \"table\" = $0 ORDER BY idx, name", table).ToList();

    private static IReadOnlyDictionary<string, object?> Field(LayerReader r, string table, string name) =>
        Fields(r, table).Single(f => (string)f["name"]! == name);

    private static byte[] Simple(uint id = 1, uint value = 2) => new DbcBytes(2).Row(id, value).Build();

    [Fact]
    public void Export_UnnamedTable_UsesPositionsAndSaysGuessed()
    {
        var d = new DbcBytes(4);
        var s = d.Str("Sword");
        _w.Add("patch-M.MPQ", "Thing", d.Row(7, s, DbcBytes.F(2.5f), 0).Row(8, s, DbcBytes.F(0.5f), 0).Build());

        _w.Export();

        using var r = _w.Open();
        var t = Table(r, "Thing");
        Assert.Equal(2L, t["rows"]);
        Assert.Equal(4L, t["fields"]);
        Assert.Equal(0L, t["named"]);
        Assert.Equal("patch-M.MPQ", t["winner_archive"]);
        Assert.Equal("unknown", t["origin"]);
        Assert.Equal("ok", t["status"]);
        Assert.Equal("f0", t["id_column"]);
        Assert.All(Fields(r, "Thing"), f => Assert.Equal(1L, f["guessed"]));
        Assert.Equal("int", Field(r, "Thing", "f0")["type"]);
        Assert.Equal("int_or_string", Field(r, "Thing", "f1")["type"]);
        Assert.Equal("string", Field(r, "Thing", "f1__str")["type"]);
        Assert.Equal("float", Field(r, "Thing", "f2")["type"]);

        var row = r.Query("SELECT * FROM t_Thing WHERE f0 = 7").Single();
        Assert.Equal((long)s, row["f1"]);          // the original integer stays
        Assert.Equal("Sword", row["f1__str"]);
        Assert.Equal(2.5, row["f2"]);
    }

    [Fact]
    public void Export_OffsetZeroInABlockWithoutLeadingNul_KeepsItsText()
    {
        var strings = Encoding.UTF8.GetBytes("Map Trigger\0Other\0");
        var recs = new[] { 1u, 0u, 2u, 12u }.SelectMany(BitConverter.GetBytes).ToArray();
        _w.Add("patch-M.MPQ", "Creature", DbcBytes.Packed(2, 8, 2, recs, strings));

        _w.Export();

        using var r = _w.Open();
        var row = r.Query("SELECT * FROM t_Creature WHERE f0 = 1").Single();
        Assert.Equal(0L, row["f1"]);
        Assert.Equal("Map Trigger", row["f1__str"]);
    }

    [Fact]
    public void Export_Config_NamesFieldsAndTheyAreNotGuessed()
    {
        var d = new DbcBytes(3);
        var s = d.Str("Fireball");
        _w.Add("patch-M.MPQ", "Spell", d.Row(133, s, DbcBytes.F(1.5f)).Build());
        _w.Config("Spell", ("ID", "int"), ("Name", "string"), ("Speed", "float"));

        _w.Export();

        using var r = _w.Open();
        var t = Table(r, "Spell");
        Assert.Equal(3L, t["named"]);
        Assert.Equal("test source", t["names_source"]);
        Assert.All(Fields(r, "Spell"), f => Assert.Equal(0L, f["guessed"]));
        Assert.Equal("Fireball", r.Query("SELECT Name FROM t_Spell WHERE ID = 133").Single()["Name"]);
    }

    [Fact]
    public void Export_ConfigFieldCountMismatch_FallsBackToPositionsAndReports()
    {
        _w.Add("patch-M.MPQ", "Spell", new DbcBytes(3).Row(1, 2, 3).Build());
        _w.Config("Spell", ("ID", "int"), ("Name", "int"));   // two names for three fields

        var result = _w.Export();

        using var r = _w.Open();
        var t = Table(r, "Spell");
        Assert.Equal(0L, t["named"]);
        Assert.Contains("config", (string)t["problem"]!);
        Assert.Equal(new[] { "f0", "f1", "f2" }, Fields(r, "Spell").Select(f => (string)f["name"]!));
        Assert.Contains(result.Problems, p => p.Contains("Spell"));
    }

    [Fact]
    public void Export_ConfigStringColumnHoldsBadOffsets_FallsBackAndReports()
    {
        _w.Add("patch-M.MPQ", "Spell", new DbcBytes(2).Row(1, 99_999).Build());
        _w.Config("Spell", ("ID", "int"), ("Name", "string"));

        _w.Export();

        using var r = _w.Open();
        var t = Table(r, "Spell");
        Assert.Equal(0L, t["named"]);
        Assert.Contains("Name", (string)t["problem"]!);
    }

    [Fact]
    public void Export_ConfigPartial_MarksUnknownColumnsGuessed()
    {
        _w.Add("patch-M.MPQ", "Map", new DbcBytes(3).Row(1, 5, 9).Build());
        _w.Config("Map", ("ID", "int"), ("f1", "unknown"), ("Type", "int"));

        _w.Export();

        using var r = _w.Open();
        Assert.Equal(2L, Table(r, "Map")["named"]);
        Assert.Equal(1L, Field(r, "Map", "f1")["guessed"]);
        Assert.Equal(0L, Field(r, "Map", "Type")["guessed"]);
    }

    [Fact]
    public void Export_Config_ByteColumns_AreDecodedAtTheirOwnWidth()
    {
        var rec = new byte[] { 9, 0, 0, 0, 1, 2, 3, 7, 0, 0, 0 };
        _w.Add("patch-M.MPQ", "Packed", DbcBytes.Packed(5, 11, 1, rec));
        _w.Config("Packed", ("ID", "int"), ("A", "byte"), ("B", "byte"), ("C", "byte"), ("Last", "int"));

        _w.Export();

        using var r = _w.Open();
        var row = r.Query("SELECT * FROM t_Packed").Single();
        Assert.Equal(9L, row["ID"]);
        Assert.Equal(3L, row["C"]);
        Assert.Equal(7L, row["Last"]);
    }

    [Fact]
    public void Export_AllByteRecords_AreBytesAndGuessed()
    {
        _w.Add("patch-M.MPQ", "CharBaseInfo", DbcBytes.Packed(2, 2, 2, new byte[] { 1, 2, 3, 4 }));

        _w.Export();

        using var r = _w.Open();
        Assert.Equal("byte", Field(r, "CharBaseInfo", "f1")["type"]);
        Assert.Equal(4L, r.Query("SELECT f1 FROM t_CharBaseInfo WHERE f0 = 3").Single()["f1"]);
    }

    [Fact]
    public void Export_RecordSizeNotFieldsTimesFour_ShownAsWordsAndReported()
    {
        // 6 fields in 15 bytes: where the fields start is unknown, so nothing is called a field.
        var rec = new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 10, 20, 30 };
        _w.Add("patch-M.MPQ", "PowerDisplay", DbcBytes.Packed(6, 15, 1, rec));

        var result = _w.Export();

        using var r = _w.Open();
        var t = Table(r, "PowerDisplay");
        Assert.Equal("ok", t["status"]);
        Assert.Contains("15 bytes", (string)t["problem"]!);
        Assert.Equal(new[] { "w0", "w1", "w2", "b12", "b13", "b14" }, Fields(r, "PowerDisplay").Select(f => (string)f["name"]!));
        var row = r.Query("SELECT * FROM t_PowerDisplay").Single();
        Assert.Equal(3L, row["w2"]);
        Assert.Equal(30L, row["b14"]);
        Assert.Contains(result.Problems, p => p.Contains("PowerDisplay"));
    }

    [Theory]
    [InlineData("NotWdbc")]
    [InlineData("BadLength")]
    public void Export_UnreadableFile_IsReportedAndOthersContinue(string kind)
    {
        var bad = kind == "NotWdbc"
            ? Encoding.ASCII.GetBytes("this is not a dbc file at all, just text")
            : new DbcBytes(2).Row(1, 2).Build().Concat(new byte[] { 0, 0 }).ToArray();
        _w.Add("patch-M.MPQ", "Broken", bad).Add("patch-M.MPQ", "Good", Simple());

        var result = _w.Export();

        using var r = _w.Open();
        var t = Table(r, "Broken");
        Assert.Equal("unreadable", t["status"]);
        Assert.False(string.IsNullOrEmpty((string?)t["problem"]));
        Assert.Empty(r.Query("SELECT name FROM sqlite_master WHERE name = 't_Broken'"));
        Assert.Equal("ok", Table(r, "Good")["status"]);
        Assert.Contains(result.Problems, p => p.Contains("Broken"));
    }

    [Fact]
    public void Export_FileNameNotUsableAsATable_IsUnreadable()
    {
        _w.Add("patch-M.MPQ", "Bad-Name", Simple()).Add("patch-M.MPQ", "Good", Simple());

        _w.Export();

        using var r = _w.Open();
        Assert.Equal("unreadable", Table(r, "Bad-Name")["status"]);
        Assert.True(PublishGuard.Check(_w.Layer).Ok);
    }

    [Fact]
    public void Export_NoTablesInTheManifest_FailsLoudly()
    {
        _w.Add("patch-M.MPQ", "Thing", Simple());
        var manifest = _w.BuildManifest();
        using (var db = new SqliteConnection($"Data Source={manifest}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM files";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.Throws<InvalidDataException>(() => DbcExporter.Run(new DbcExportOptions(manifest,
            ArchiveConfig.Load(Path.Combine(AppContext.BaseDirectory, "config", "archives.json")), _w.Fields, _w.Layer, _w.Winners, "1")));
    }

    [Fact]
    public void Export_TooManyFields_IsUnreadableNotAServerFault()
    {
        var fields = DbcExporter.MaxFields + 1;
        _w.Add("patch-M.MPQ", "Wide", DbcBytes.Packed(fields, fields * 4, 0, Array.Empty<byte>()));

        _w.Export();

        using var r = _w.Open();
        Assert.Equal("unreadable", Table(r, "Wide")["status"]);
    }

    [Fact]
    public void Export_WinnerIsHighestPriorityLoadedArchive_RealmArchiveIgnored()
    {
        _w.Add("common.MPQ", "Thing", Simple(1, 100))
          .Add("patch-M.MPQ", "Thing", Simple(1, 200))
          .Add("area-52/patch-D.MPQ", "Thing", Simple(1, 300));

        _w.Export();

        using var r = _w.Open();
        Assert.Equal("patch-M.MPQ", Table(r, "Thing")["winner_archive"]);
        Assert.Equal(200L, r.Query("SELECT f1 FROM t_Thing").Single()["f1"]);
    }

    [Fact]
    public void Export_RealmOnlyTable_IsListedNotExported()
    {
        _w.Add("area-52/patch-D.MPQ", "Realmy", Simple()).Add("patch-M.MPQ", "Good", Simple());

        _w.Export();

        using var r = _w.Open();
        Assert.Equal("not_in_default_client", Table(r, "Realmy")["status"]);
        Assert.Empty(r.Query("SELECT name FROM sqlite_master WHERE name = 't_Realmy'"));
    }

    [Fact]
    public void Export_DeleteMarkerOnTop_IsNotExportedAndIsReported()
    {
        _w.Add("common.MPQ", "Gone", Simple()).Marker("patch-M.MPQ", "Gone").Add("patch-M.MPQ", "Good", Simple());

        var result = _w.Export();

        using var r = _w.Open();
        var t = Table(r, "Gone");
        Assert.Equal("deleted", t["status"]);
        Assert.Equal("patch-M.MPQ", t["winner_archive"]);
        Assert.Empty(r.Query("SELECT name FROM sqlite_master WHERE name = 't_Gone'"));
        Assert.Contains("Gone", result.Deleted);
        Assert.False(File.Exists(Path.Combine(_w.Winners, "Gone.dbc")));
    }

    [Fact]
    public void Export_ProjectWinner_AlsoExportsNextCopyAsBase_NotClaimedAsAscension()
    {
        _w.Add("common.MPQ", "Spell", Simple(1, 100))
          .Add("patch-M.MPQ", "Spell", Simple(1, 200))
          .Add("patch-B.MPQ", "Spell", Simple(1, 250))
          .Add("patch-T.MPQ", "Spell", Simple(1, 300));

        var result = _w.Export();

        using var r = _w.Open();
        var t = Table(r, "Spell");
        Assert.Equal("patch-T.MPQ", t["winner_archive"]);
        Assert.Equal("project", t["origin"]);
        Assert.Equal(300L, r.Query("SELECT f1 FROM t_Spell").Single()["f1"]);
        var b = Table(r, "Spell__base");   // patch-T (111), then patch-M (104): the next copy below the winner that is not project-built
        Assert.Equal("patch-M.MPQ", b["winner_archive"]);
        Assert.Equal("unknown", b["origin"]);
        Assert.Equal("Spell", b["base_of"]);
        Assert.Equal(200L, r.Query("SELECT f1 FROM t_Spell__base").Single()["f1"]);
        Assert.Empty(r.Query("SELECT name FROM dbc_tables WHERE name LIKE '%ascension%'"));
        Assert.Equal(1, result.BaseTables);
    }

    [Fact]
    public void Export_ProjectWinner_SkipsAnotherProjectCopyToFindTheBase()
    {
        _w.Add("common.MPQ", "Spell", Simple(1, 100))
          .Add("patch-B.MPQ", "Spell", Simple(1, 250))
          .Add("patch-T.MPQ", "Spell", Simple(1, 300));

        _w.Export();

        using var r = _w.Open();
        Assert.Equal("common.MPQ", Table(r, "Spell__base")["winner_archive"]);
        Assert.Equal(100L, r.Query("SELECT f1 FROM t_Spell__base").Single()["f1"]);
    }

    [Fact]
    public void Export_BaseCopyUnreadable_IsReportedAndWinnerStays()
    {
        _w.Add("patch-M.MPQ", "Spell", Encoding.ASCII.GetBytes("nope nope nope nope nope nope nope nope"))
          .Add("patch-T.MPQ", "Spell", Simple(1, 300));

        var result = _w.Export();

        using var r = _w.Open();
        Assert.Equal("ok", Table(r, "Spell")["status"]);
        Assert.Equal("unreadable", Table(r, "Spell__base")["status"]);
        Assert.Contains(result.Problems, p => p.Contains("Spell__base"));
    }

    [Fact]
    public void Export_ProjectWinner_NextCopyIsDeleteMarker_NoBaseAndSaysSo()
    {
        _w.Add("common.MPQ", "Spell", Simple(1, 100))
          .Marker("patch-M.MPQ", "Spell")
          .Add("patch-T.MPQ", "Spell", Simple(1, 300));

        var result = _w.Export();

        using var r = _w.Open();
        Assert.Empty(r.Query("SELECT name FROM dbc_tables WHERE name = 'Spell__base'"));
        Assert.Contains("delete marker", (string)Table(r, "Spell")["problem"]!);
        Assert.Contains(result.Problems, p => p.Contains("Spell") && p.Contains("delete marker"));
    }

    [Fact]
    public void Export_ProjectWinner_NoOtherCopy_SaysSo()
    {
        _w.Add("patch-T.MPQ", "Spell", Simple());

        _w.Export();

        using var r = _w.Open();
        Assert.Empty(r.Query("SELECT name FROM dbc_tables WHERE name = 'Spell__base'"));
        Assert.Contains("no copy", (string)Table(r, "Spell")["problem"]!);
    }

    [Fact]
    public void Export_MaterializesWinnersOnly_AndRemovesStaleOnes()
    {
        Directory.CreateDirectory(_w.Winners);
        File.WriteAllBytes(Path.Combine(_w.Winners, "Stale.dbc"), new byte[] { 1 });
        _w.Add("common.MPQ", "Thing", Simple(1, 100)).Add("patch-M.MPQ", "Thing", Simple(1, 200));

        _w.Export();

        Assert.False(File.Exists(Path.Combine(_w.Winners, "Stale.dbc")));
        using var reader = DbcReader.Open(Path.Combine(_w.Winners, "Thing.dbc"));
        Assert.Equal(200u, BitConverter.ToUInt32(reader.Records().First(), 4));
        var list = DbcWinnerList.Read(_w.Winners);
        Assert.Equal("patch-M.MPQ", list.Files["Thing.dbc"].Archive);
    }

    [Fact]
    public void Export_WinnersFolderInsideGitRepo_Refused()
    {
        var repo = Path.Combine(_w.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        _w.Add("patch-M.MPQ", "Thing", Simple());

        var ex = Assert.Throws<InvalidDataException>(() => DbcExporter.Run(new DbcExportOptions(_w.BuildManifest(),
            ArchiveConfig.Load(Path.Combine(AppContext.BaseDirectory, "config", "archives.json")), _w.Fields, _w.Layer, Path.Combine(repo, "winners"), "1")));
        Assert.Contains("repository", ex.Message);
    }

    [Fact]
    public void Export_SourceDate_IsTheWinningArchivesModifiedTime()
    {
        _w.Add("patch-M.MPQ", "Older", Simple(), DbcWorld.Oct3).Add("patch-ZG.MPQ", "Newer", Simple(), DbcWorld.Oct22);

        _w.Export();

        using var r = _w.Open();
        Assert.Equal("2026-10-03", Table(r, "Older")["source_date"]);
        Assert.Equal("2026-10-22", Table(r, "Newer")["source_date"]);
        Assert.Equal(new DateOnly(2026, 10, 22), r.Meta.SourceDate);   // the newest winning archive
        Assert.Equal("2026-10-22", DbcWinnerList.Read(_w.Winners).Files["Newer.dbc"].Date);
    }

    [Fact]
    public void Export_IdColumn_IsPrimaryKeyWhenUnique_IndexedWhenNot()
    {
        _w.Add("patch-M.MPQ", "Uniq", new DbcBytes(2).Row(5, 1).Row(9, 2).Build());
        _w.Add("patch-M.MPQ", "Dupes", new DbcBytes(2).Row(5, 1).Row(5, 2).Build());

        _w.Export();

        using var r = _w.Open();
        Assert.Equal(1L, Table(r, "Uniq")["id_unique"]);
        Assert.Equal(0L, Table(r, "Dupes")["id_unique"]);
        Assert.Equal(2, r.Query("SELECT * FROM t_Dupes WHERE f0 = 5").Count);
        Assert.Equal(1L, Field(r, "Uniq", "f0")["indexed"]);
        Assert.Equal(1L, Field(r, "Dupes", "f0")["indexed"]);
        Assert.Equal(0L, Field(r, "Dupes", "f1")["indexed"]);
    }

    [Fact]
    public void Export_NameFields_AreIndexed_FirstLocaleSlotOnly()
    {
        var d = new DbcBytes(3);
        var s = d.Str("Fireball");
        _w.Add("patch-M.MPQ", "Spell", d.Row(1, s, 0).Build());
        _w.Config("Spell", ("ID", "int"), ("Name_0", "string"), ("Name_1", "string"));

        _w.Export();

        using var r = _w.Open();
        Assert.Equal(1L, Field(r, "Spell", "Name_0")["indexed"]);
        Assert.Equal(0L, Field(r, "Spell", "Name_1")["indexed"]);
        Assert.NotEmpty(r.Query("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 't_Spell'"));
    }

    [Fact]
    public void Export_TableEntity_CarriesSourceTrustAndRealm()
    {
        _w.Add("patch-T.MPQ", "Spell", Simple());

        _w.Export();

        using var r = _w.Open();
        var e = r.Get("dbc:Spell")!;
        Assert.Equal("dbc_table", e.Type);
        Assert.Equal("Spell", e.Title);
        Assert.Equal(Trust.Client, e.Trust);
        Assert.False(e.Realm.Proven);
        Assert.Contains("coa-client", e.Realm.Realms);
        Assert.Contains("patch-T.MPQ", e.BodyMd);
        Assert.Contains("project", e.BodyMd);
    }

    [Fact]
    public void Export_ClientRelease_IsInMetaSourceAndWinnersList()
    {
        _w.Add("patch-T.MPQ", "Spell", Simple());

        _w.Export(new ClientRelease(8, "rel-8", "a note"));

        using var r = _w.Open();
        Assert.Equal(8, r.Meta.Client!.Revision);
        Assert.Equal("rel-8", r.Meta.Client.ReleaseId);
        Assert.Equal("a note", r.Meta.Client.Note);
        Assert.Equal("CoA client DBC, client revision 8", r.Get("dbc:Spell")!.Source);
        Assert.Equal(8, DbcWinnerList.Read(_w.Winners).Client!.Revision);
        Assert.True(PublishGuard.Check(_w.Layer).Ok);
    }

    [Fact]
    public void Tables_All368Listed()
    {
        for (var i = 0; i < 368; i++) _w.Add("patch-M.MPQ", $"Table{i}", Simple((uint)i));

        var result = _w.Export();

        Assert.Equal(368, result.Tables);
        using var r = _w.Open();
        Assert.Equal(368L, r.Query("SELECT COUNT(*) AS n FROM dbc_tables WHERE base_of IS NULL").Single()["n"]);
        Assert.Equal(368, Directory.GetFiles(_w.Winners, "*.dbc").Length);
    }

    [Fact]
    public void Export_Guard_PassesOnARealLayer_AndNoBlobs()
    {
        var d = new DbcBytes(3);
        var s = d.Str("Sword");
        _w.Add("patch-M.MPQ", "Thing", d.Row(7, s, DbcBytes.F(2.5f)).Build());
        _w.Add("patch-T.MPQ", "Spell", Simple()).Add("patch-M.MPQ", "Spell", Simple(1, 5));
        _w.Add("patch-M.MPQ", "Broken", Encoding.ASCII.GetBytes("nope nope nope nope nope nope nope nope"));

        _w.Export();

        var g = PublishGuard.Check(_w.Layer);
        Assert.True(g.Ok, string.Join("; ", g.Problems));
        Assert.Equal("dbc", g.LayerName);
    }
}

public class ClientReleaseFileTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string File_(string json)
    {
        var path = _dir.File("CLIENT-RELEASE.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string Release = """
        {"releaseId":"rel-8","clientRevision":8,
         "previousPackage":{"name":"old","clientRevision":7,"Data/patch-B.MPQ":{"bytes":8982122}},
         "files":{"README.txt":{"bytes":5},"Data/patch-B.MPQ":{"bytes":8987639},"Data/patch-T.MPQ":{"bytes":19507059}}}
        """;

    [Fact]
    public void Read_SizesMatch_NoNote()
    {
        var c = ClientReleaseFile.Read(File_(Release), new Dictionary<string, long> { ["patch-B.MPQ"] = 8987639, ["patch-T.MPQ"] = 19507059 });

        Assert.Equal(8, c.Revision);
        Assert.Equal("rel-8", c.ReleaseId);
        Assert.Null(c.Note);
    }

    [Fact]
    public void Read_ArchiveMatchesNeitherEntry_SaysExactlyThat()
    {
        var c = ClientReleaseFile.Read(File_(Release), new Dictionary<string, long> { ["patch-B.MPQ"] = 228738210, ["patch-T.MPQ"] = 19507059 });

        Assert.Contains("patch-B.MPQ", c.Note);
        Assert.Contains("8,987,639", c.Note);
        Assert.Contains("8,982,122", c.Note);
        Assert.Contains("228,738,210", c.Note);
        Assert.DoesNotContain("patch-T.MPQ", c.Note);
    }

    [Fact]
    public void Read_ArchiveMatchesThePreviousPackage_SaysThat()
    {
        var c = ClientReleaseFile.Read(File_(Release), new Dictionary<string, long> { ["patch-B.MPQ"] = 8982122, ["patch-T.MPQ"] = 19507059 });

        Assert.Contains("previous package", c.Note);
        Assert.Contains("revision 7", c.Note);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"releaseId":"x"}""")]
    [InlineData("""{"clientRevision":8}""")]
    [InlineData("""{"releaseId":5,"clientRevision":"eight"}""")]
    public void Read_Malformed_FailsLoudly(string json) =>
        Assert.Throws<InvalidDataException>(() => ClientReleaseFile.Read(File_(json), new Dictionary<string, long>()));

    [Fact]
    public void Read_Missing_FailsLoudly() =>
        Assert.Throws<InvalidDataException>(() => ClientReleaseFile.Read(_dir.File("nope.json"), new Dictionary<string, long>()));
}

public class DbcFieldConfigTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("""{"table":"Spell","source":"s","fields":[{"name":"bad name","type":"int"}]}""", "bad name")]
    [InlineData("""{"table":"Spell","source":"s","fields":[{"name":"a\"; DROP TABLE x","type":"int"}]}""", "DROP")]
    [InlineData("""{"table":"Spell","source":"s","fields":[{"name":"ID","type":"nonsense"}]}""", "nonsense")]
    [InlineData("""{"table":"Spell","source":"s","fields":[{"name":"ID","type":"int"},{"name":"id","type":"int"}]}""", "twice")]
    [InlineData("""{"table":"Other","source":"s","fields":[{"name":"ID","type":"int"}]}""", "Other")]
    [InlineData("""{"table":"Spell","fields":[{"name":"ID","type":"int"}]}""", "source")]
    public void Load_BadConfig_FailsLoudly(string json, string expected)
    {
        File.WriteAllText(_dir.File("Spell.json"), json);
        var ex = Assert.Throws<InvalidDataException>(() => DbcFieldConfig.LoadAll(_dir.Path));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void RealConfig_LoadsAndCoversTheThirtySpellworkTables()
    {
        var all = DbcFieldConfig.LoadAll(Path.Combine(AppContext.BaseDirectory, "config", "dbc-fields"));
        Assert.True(all.Count >= 30);
        Assert.Equal(234, all["Spell"].Fields.Count);
        Assert.Contains(all.Values, t => t.Source.Contains("SpellWork"));
        Assert.All(all.Values, t => Assert.False(string.IsNullOrWhiteSpace(t.Source)));
    }
}

public class DbcGuardTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string Layer(Action<LayerWriter> extra)
    {
        var path = _dir.File($"dbc-{Guid.NewGuid():N}.sqlite");
        using (var w = new LayerWriter(path, "dbc", "1"))
        {
            var b = new DbcLayerBuilder(w);
            var columns = new[] { new DbcColumn(0, "ID", "int", false, true, true, null), new DbcColumn(1, "Name", "string", false, false, false, null) };
            using (var ins = b.BeginTable("Spell", columns))
                ins.Add(new object?[] { 133L, "Fireball" });
            b.AddTable(new DbcTableInfo("Spell", 1, 2, 2, "patch-M.MPQ", "unknown", null, "ok", null, "s", "ID", true, "2026-10-03"), columns, new RealmSet(new[] { "coa-client" }, false));
            extra(w);
            b.Finish(new Dictionary<string, string>());
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public void Guard_PassesAWellFormedLayer() => Assert.True(PublishGuard.Check(Layer(_ => { })).Ok);

    [Theory]
    [InlineData("t_NotListed")]
    [InlineData("t_")]
    [InlineData("t_Spell;x")]
    [InlineData("other")]
    [InlineData("T_Spell2")]
    public void Guard_RefusesTablesThatAreNotListed(string name)
    {
        var g = PublishGuard.Check(Layer(w => w.Execute($"CREATE TABLE \"{name}\"(a INTEGER)")));
        Assert.False(g.Ok);
        Assert.Contains(g.Problems, p => p.Contains(name));
    }

    [Fact]
    public void Guard_RefusesBlobInADataTable()
    {
        var g = PublishGuard.Check(Layer(w => w.Execute("INSERT INTO t_Spell(ID, Name) VALUES (2, x'57444243')")));
        Assert.False(g.Ok);
        Assert.Contains(g.Problems, p => p.Contains("blob"));
    }

    [Fact]
    public void Guard_RefusesWdbcTextInADataTable()
    {
        var g = PublishGuard.Check(Layer(w => w.Execute("INSERT INTO t_Spell(ID, Name) VALUES (2, 'WDBC rest')")));
        Assert.False(g.Ok);
        Assert.Contains(g.Problems, p => p.Contains("WDBC"));
    }

    [Fact]
    public void Guard_ListedOkTableMissing_IsRefused()
    {
        var g = PublishGuard.Check(Layer(w => w.Execute("DROP TABLE t_Spell")));
        Assert.False(g.Ok);
        Assert.Contains(g.Problems, p => p.Contains("t_Spell"));
    }

    [Fact]
    public void Guard_TablesPatternOnlyAppliesToDbcLayer()
    {
        // The same table name in another layer is still refused.
        var path = _dir.File("spells.sqlite");
        using (var w = new LayerWriter(path, "spells", "1"))
        {
            w.Execute("CREATE TABLE t_Spell(a INTEGER)");
            w.Finish();
        }
        SqliteConnection.ClearAllPools();
        Assert.False(PublishGuard.Check(path).Ok);
    }
}
