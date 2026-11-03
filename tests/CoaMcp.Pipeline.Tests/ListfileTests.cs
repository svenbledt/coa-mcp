using System.Security.Cryptography;
using System.Text;
using CoaMcp.Pipeline.Mpq;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

public class ListfileTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _data, _out, _lists;

    public ListfileTests()
    {
        _data = _dir.File("Client/Data");
        _out = _dir.File("restored");
        _lists = _dir.File("Client/Listfiles");
        Directory.CreateDirectory(_data);
    }

    public void Dispose() => _dir.Dispose();

    private string Archive(string rel) => Path.Combine(_data, rel);
    private string Manifest => Path.Combine(_out, "manifest.sqlite");

    private void RestoreAll(params string[] archives) => MpqRestorer.Run(new RestoreOptions(_data, _out,
        new ArchiveConfig(new Dictionary<string, string> { ["t"] = "fixture" },
            archives.Select((a, i) => new ArchiveEntry(a, 100 - i, "unknown", null, "t")).ToList())));

    private ListfileResult Build() => ListfileBuilder.Run(Manifest, _lists);

    private Dictionary<string, object?>? Find(string archive, string pathLc)
    {
        using var c = new SqliteConnection("Data Source=" + Manifest);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM files WHERE archive=$a AND path_lc=$p";
        cmd.Parameters.AddWithValue("$a", archive);
        cmd.Parameters.AddWithValue("$p", pathLc);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i)) : null;
    }

    /// <summary>a.MPQ names hidden.txt in its listfile; b.MPQ holds the same file with no listfile line for it.</summary>
    private void TwoArchives(string hidden = "hidden.txt", uint hiddenFlags = MpqFixture.Compress)
    {
        using (var a = new MpqFixture(Archive("a.MPQ")))
            a.Add(hidden, "from a");
        using (var b = new MpqFixture(Archive("b.MPQ"), listfile: false, attributes: false))
        {
            b.Add("named.txt", "named");
            b.Add(hidden, "from b", hiddenFlags);
            b.Add("(listfile)", "named.txt\r\n");
        }
        RestoreAll("a.MPQ", "b.MPQ");
    }

    [Fact]
    public void Candidates_ResolveUnnamedEntry()
    {
        TwoArchives();

        var result = Build();

        var row = Find("b.MPQ", "hidden.txt");
        Assert.NotNull(row);
        Assert.Equal("ok", row["status"]);
        Assert.Equal("hidden.txt", row["name"]);
        Assert.Equal("other_listfile", row["name_source"]);
        Assert.Equal(1, result.RecoveredBySource["other_listfile"]);
        Assert.Equal(0, result.StillUnnamed);
        // The bytes moved to the real path; no pseudo-name file stays behind.
        Assert.Equal("from b", File.ReadAllText(Path.Combine(_out, "b.MPQ", "hidden.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_out, "b.MPQ"), "File*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Candidates_EncryptedEntry_ExtractedOnceNamed()
    {
        TwoArchives(hiddenFlags: MpqFixture.Encrypted);

        Build();

        Assert.Equal("ok", Find("b.MPQ", "hidden.txt")!["status"]);
        Assert.Equal("from b", File.ReadAllText(Path.Combine(_out, "b.MPQ", "hidden.txt")));
    }

    [Fact]
    public void Candidates_OneHashAlone_NeverMatches()
    {
        var name1 = MpqHash.Name1("hidden.txt");
        var table = new CandidateMatcher(new[] { new UnnamedEntry("b.MPQ", 5, name1, 0x12345678u) });

        Assert.Empty(table.Match("hidden.txt"));

        var both = new CandidateMatcher(new[] { new UnnamedEntry("b.MPQ", 5, name1, MpqHash.Name2("hidden.txt")) });
        Assert.Equal(5, Assert.Single(both.Match("HIDDEN.TXT")).HashIndex);
    }

    [Fact]
    public void Candidates_NothingMatches_StaysUnnamedAndReported()
    {
        using (var b = new MpqFixture(Archive("b.MPQ"), listfile: false, attributes: false))
        {
            b.Add("named.txt", "named");
            b.Add("hidden.txt", "bytes no one names");
            b.Add("(listfile)", "named.txt\r\nwrong-guess.txt\r\n");
        }
        RestoreAll("b.MPQ");

        var result = Build();

        Assert.Equal(1, result.StillUnnamed);
        Assert.Equal(1, result.UnnamedByArchive["b.MPQ"]);
        Assert.Null(Find("b.MPQ", "wrong-guess.txt"));
        Assert.DoesNotContain("wrong-guess.txt", File.ReadAllLines(Path.Combine(_lists, "client-listfile.txt")));
    }

    [Fact]
    public void Strings_InRestoredFiles_NameEntries()
    {
        using (var a = new MpqFixture(Archive("a.MPQ")))
        {
            // A .dbc string block naming a model by the old .mdx extension, a .wdt-free .toc and a .lua.
            a.Add(@"DBFilesClient\Thing.dbc", "\0\0WDBC\0Creature\\Bear\\Bear.mdx\0\0");
            a.Add(@"Interface\AddOns\Foo\Foo.toc", "## Title: Foo\r\nFoo.lua\r\nbar\\Bar.xml\r\n");
            a.Add(@"Interface\Foo.lua", "local t = \"Interface\\\\Icons\\\\Spell_Nature.blp\"");
        }
        using (var b = new MpqFixture(Archive("b.MPQ"), listfile: false, attributes: false))
        {
            b.Add("named.txt", "named");
            b.Add(@"Creature\Bear\Bear.m2", "bear");
            b.Add(@"Creature\Bear\Bear00.skin", "skin");
            b.Add(@"Interface\AddOns\Foo\Foo.lua", "code");
            b.Add(@"Interface\AddOns\Foo\bar\Bar.xml", "<Ui/>");
            b.Add(@"Interface\Icons\Spell_Nature.blp", "icon");
            b.Add("(listfile)", "named.txt\r\n");
        }
        RestoreAll("a.MPQ", "b.MPQ");

        var result = Build();

        Assert.Equal(0, result.StillUnnamed);
        Assert.Equal("restored_strings", Find("b.MPQ", "interface/icons/spell_nature.blp")!["name_source"]);
        Assert.Equal("extension_variant", Find("b.MPQ", "creature/bear/bear.m2")!["name_source"]);
        Assert.Equal("skin_pattern", Find("b.MPQ", "creature/bear/bear00.skin")!["name_source"]);
        Assert.Equal("toc_relative", Find("b.MPQ", "interface/addons/foo/foo.lua")!["name_source"]);
        Assert.Equal("toc_relative", Find("b.MPQ", "interface/addons/foo/bar/bar.xml")!["name_source"]);
        Assert.Equal(1, result.RecoveredBySource["restored_strings"]);
    }

    [Fact]
    public void Listfile_OutputsPerArchiveAndUnion()
    {
        using (var a = new MpqFixture(Archive("patch.MPQ")))
        {
            a.Add(@"World\A.txt", "1");
            a.Add(@"World\Shared.txt", "2");
        }
        using (var b = new MpqFixture(Archive("enUS/patch-enUS.MPQ")))
        {
            b.Add(@"WORLD\SHARED.TXT", "3");
            b.Add(@"World\B.txt", "4");
        }
        RestoreAll("patch.MPQ", "enUS/patch-enUS.MPQ");

        var result = Build();

        var perArchive = File.ReadAllLines(Path.Combine(_lists, "patch.MPQ.listfile.txt"));
        Assert.Contains(@"World\A.txt", perArchive);
        Assert.Contains("(listfile)", perArchive);
        Assert.Contains(@"World\B.txt", File.ReadAllLines(Path.Combine(_lists, "enUS__patch-enUS.MPQ.listfile.txt")));
        var union = File.ReadAllLines(Path.Combine(_lists, "client-listfile.txt"));
        Assert.Single(union, l => l.Equals(@"World\Shared.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"World\A.txt", union);
        Assert.Contains(@"World\B.txt", union);
        Assert.Equal(union.Length, result.UniqueNames);
        Assert.Equal(2, result.Archives);
    }

    [Fact]
    public void Listfile_ExcludesNamesThatHaveNoEntry()
    {
        using (var a = new MpqFixture(Archive("a.MPQ"), listfile: false))
        {
            a.Add("real.txt", "x");
            a.Add("(listfile)", "real.txt\r\nstale.txt\r\n");
        }
        RestoreAll("a.MPQ");

        Build();

        var lines = File.ReadAllLines(Path.Combine(_lists, "a.MPQ.listfile.txt"));
        Assert.Contains("real.txt", lines);
        Assert.DoesNotContain("stale.txt", lines);
    }

    [Fact]
    public void Listfile_NamedTraversalEntry_ListedButNeverWritten()
    {
        using (var a = new MpqFixture(Archive("a.MPQ")))
            a.Add(@"..\..\evil.txt", "evil");
        RestoreAll("a.MPQ");

        Build();

        // The name genuinely matches both hashes, so it's listed; it just can't be a path on this disk.
        Assert.Contains(@"..\..\evil.txt", File.ReadAllLines(Path.Combine(_lists, "a.MPQ.listfile.txt")));
        Assert.Empty(Directory.GetFiles(_dir.Path, "evil.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void Candidate_TraversalName_RecordedFailedNeverEscapes()
    {
        TwoArchives(hidden: @"..\..\..\evil.txt");

        Build();

        using var c = new SqliteConnection("Data Source=" + Manifest);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT status, reason FROM files WHERE archive='b.MPQ' AND name LIKE '%evil%'";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("failed", r.GetString(0));
        Assert.Contains("traversal", r.GetString(1));
        Assert.Empty(Directory.GetFiles(_dir.Path, "evil.txt", SearchOption.AllDirectories));
        // Its name can't be used, so the bytes stay under their unnamed path rather than being lost.
        Assert.Single(Directory.GetFiles(Path.Combine(_out, "b.MPQ", "(unnamed)")));
    }

    [Fact]
    public void Listfile_NeverChangesTheArchives()
    {
        TwoArchives();
        File.SetLastWriteTimeUtc(Archive("b.MPQ"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        RestoreAll("a.MPQ", "b.MPQ");   // the stamp is part of the archive's recorded identity
        var hash = SHA256.HashData(File.ReadAllBytes(Archive("b.MPQ")));

        Build();

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Archive("b.MPQ"))));
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Archive("b.MPQ")));
    }

    [Fact]
    public void Listfile_OutInsideData_Refused()
    {
        TwoArchives();

        var ex = Assert.Throws<InvalidOperationException>(() => ListfileBuilder.Run(Manifest, Path.Combine(_data, "lists")));

        Assert.Contains("--out", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_data, "lists")));
    }

    [Fact]
    public void Restore_Rerun_KeepsNamesTheListfileRunRecovered()
    {
        TwoArchives();
        Build();

        RestoreAll("a.MPQ", "b.MPQ");

        var row = Find("b.MPQ", "hidden.txt");
        Assert.Equal("other_listfile", row!["name_source"]);
        Assert.Equal("ok", row["status"]);
    }

    [Fact]
    public void Listfile_ArchiveChangedSinceRestore_RefusedBeforeAnyWrite()
    {
        TwoArchives();
        // b.MPQ is replaced after the restore: its rows describe the old version, so names can't be applied to it.
        File.Delete(Archive("b.MPQ"));
        using (var b = new MpqFixture(Archive("b.MPQ"), listfile: false, attributes: false))
        {
            b.Add("hidden.txt", "from b, moved");
            b.Add("named.txt", "named");
            b.Add("(listfile)", "named.txt\r\n");
        }
        File.SetLastWriteTimeUtc(Archive("b.MPQ"), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var ex = Assert.Throws<InvalidOperationException>(Build);

        Assert.Contains("b.MPQ", ex.Message);
        Assert.Contains("restore", ex.Message);
        Assert.Null(Find("b.MPQ", "hidden.txt"));   // still only under its unnamed path
        Assert.False(Directory.Exists(_lists));
    }

    [Fact]
    public void Listfile_NotAManifest_CleanError()
    {
        var bogus = _dir.File("bogus.sqlite");
        File.WriteAllText(bogus, "not a database at all");
        Assert.Throws<InvalidDataException>(() => ListfileBuilder.Run(bogus, _lists));
        Assert.Throws<InvalidDataException>(() => ListfileBuilder.Run(_dir.File("missing.sqlite"), _lists));
    }

    [Fact]
    public void Strings_Extractor_FindsPathsInBinary()
    {
        var blob = Encoding.Latin1.GetBytes("\u0001\u0002World\\Wmo\\Dungeon\\Foo.wmo\0garbageÿþTexture.blp\0notapath\0x.m2");
        var tokens = PathStrings.Extract(blob).ToList();
        Assert.Contains(@"World\Wmo\Dungeon\Foo.wmo", tokens);
        Assert.Contains("Texture.blp", tokens);
        Assert.Contains("x.m2", tokens);
        Assert.DoesNotContain("garbage", tokens);
        Assert.DoesNotContain("notapath", tokens);
    }

    [Theory]
    [InlineData(@"Creature\Bear\Bear.mdx", @"Creature\Bear\Bear.m2")]
    [InlineData(@"Creature\Bear\Bear.mdl", @"Creature\Bear\Bear.m2")]
    public void Variants_ModelExtensions(string input, string expected) =>
        Assert.Contains(expected, PathStrings.Variants(input).Select(v => v.Name));

    [Fact]
    public void Variants_M2_ProducesSkins()
    {
        var names = PathStrings.Variants(@"Creature\Bear\Bear.m2").Select(v => v.Name).ToList();
        Assert.Contains(@"Creature\Bear\Bear00.skin", names);
        Assert.Contains(@"Creature\Bear\Bear03.skin", names);
    }
}
