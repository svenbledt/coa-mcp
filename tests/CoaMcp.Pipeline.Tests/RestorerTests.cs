using System.Security.Cryptography;
using System.Text;
using CoaMcp.Pipeline.Mpq;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

public class RestorerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _data, _out;

    public RestorerTests()
    {
        _data = _dir.File("Client/Data");
        _out = _dir.File("restored");
        Directory.CreateDirectory(_data);
    }

    public void Dispose() => _dir.Dispose();

    private static ArchiveConfig Config(params string[] archives) => new(
        new Dictionary<string, string> { ["test"] = "fixture" },
        archives.Select((a, i) => new ArchiveEntry(a, 100 - i, "unknown", null, "test")).ToList());

    private string Archive(string rel = "a.MPQ") => Path.Combine(_data, rel);

    private RestoreResult Restore(int parallelism = 1, params string[] archives) =>
        MpqRestorer.Run(new RestoreOptions(_data, _out, Config(archives.Length == 0 ? ["a.MPQ"] : archives), parallelism));

    private string Manifest => Path.Combine(_out, "manifest.sqlite");

    private List<Dictionary<string, object?>> Rows(string sql = "SELECT * FROM files ORDER BY path")
    {
        using var c = new SqliteConnection("Data Source=" + Manifest);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
            rows.Add(Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i)));
        return rows;
    }

    private Dictionary<string, object?> Row(string pathLc) =>
        Assert.Single(Rows("SELECT * FROM files"), r => (string)r["path_lc"]! == pathLc);

    [Fact]
    public void Restore_FixtureArchive_ExtractsAndManifests()
    {
        var body = string.Concat(Enumerable.Repeat("hello mpq world ", 2000));
        using (var f = new MpqFixture(Archive()))
        {
            f.Add(@"World\Maps\Foo.txt", body);
            f.Add("empty.txt", []);
        }

        var result = Restore();

        Assert.Equal(body, File.ReadAllText(Path.Combine(_out, "a.MPQ", "World", "Maps", "Foo.txt")));
        var row = Row("world/maps/foo.txt");
        Assert.Equal("World/Maps/Foo.txt", row["path"]);
        Assert.Equal(@"World\Maps\Foo.txt", row["name"]);
        Assert.Equal("a.MPQ", row["archive"]);
        Assert.Equal((long)body.Length, row["size"]);
        Assert.Equal("ok", row["status"]);
        Assert.Equal("own_listfile", row["name_source"]);
        Assert.Equal((long)System.IO.Hashing.Crc32.HashToUInt32(Encoding.Latin1.GetBytes(body)), row["crc"]);
        Assert.Equal("ok", Row("empty.txt")["status"]);
        Assert.Equal(0L, new FileInfo(Path.Combine(_out, "a.MPQ", "empty.txt")).Length);
        // Internal files are real entries too.
        Assert.Equal("internal", Row("(listfile)")["name_source"]);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Unnamed);
        Assert.Equal(result.Entries, result.Ok);
    }

    [Fact]
    public void Restore_ManifestKeepsArchiveFacts()
    {
        using (var f = new MpqFixture(Archive(), listfile: false))
        {
            f.Add("real.txt", "x");
            f.Add("(listfile)", "real.txt\r\nghost.txt\r\n");
        }

        Restore();

        var names = Rows("SELECT name, matched FROM listfile_names ORDER BY name");
        Assert.Equal(new object?[] { "ghost.txt", 0L, "real.txt", 1L }, names.SelectMany(r => new[] { r["name"], r["matched"] }));
        var arc = Assert.Single(Rows("SELECT * FROM archives"));
        Assert.Equal("a.MPQ", arc["archive"]);
        Assert.Equal(1L, arc["stale_lines"]);
        Assert.Equal(2L, arc["listfile_lines"]);
        Assert.Equal(Path.GetFullPath(_data), Assert.Single(Rows("SELECT value FROM meta WHERE key='data_dir'"))["value"]);
    }

    [Fact]
    public void Restore_FailedFile_RecordedNotFatal()
    {
        using (var f = new MpqFixture(Archive()))
        {
            f.Add(@"..\..\evil.txt", "evil");
            f.Add("good.txt", "good");
        }

        var result = Restore();

        Assert.Equal(1, result.Failed);
        var bad = Row("../../evil.txt");
        Assert.Equal("failed", bad["status"]);
        Assert.Contains("traversal", (string)bad["reason"]!);
        Assert.Equal("ok", Row("good.txt")["status"]);
        Assert.Empty(Directory.GetFiles(_dir.Path, "evil.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void Restore_CorruptData_RecordedFailedAndNoFileLeft()
    {
        var noise = new byte[64 * 1024];
        new Random(7).NextBytes(noise);
        var compressible = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("corrupt me please ", 20000)));
        using (var f = new MpqFixture(Archive()))
        {
            f.Add("victim.bin", compressible);
            f.Add("other.bin", noise, 0);
        }
        // Damage the compressed data of the first file: it starts right after the header and sector table.
        var bytes = File.ReadAllBytes(Archive());
        for (var i = 0x40; i < 0x400; i++) bytes[i] ^= 0x5A;
        File.WriteAllBytes(Archive(), bytes);

        var result = Restore();

        Assert.True(result.Failed >= 1);
        var row = Row("victim.bin");
        Assert.Equal("failed", row["status"]);
        Assert.False(string.IsNullOrEmpty((string?)row["reason"]));
        Assert.False(File.Exists(Path.Combine(_out, "a.MPQ", "victim.bin")));
        Assert.Empty(Directory.GetFiles(_out, "*.part", SearchOption.AllDirectories));
        Assert.Equal("ok", Row("other.bin")["status"]);
    }

    /// <summary>Stored (uncompressed) file whose bytes then change in the archive, so reading succeeds but (attributes) disagrees.</summary>
    private void ArchiveWithWrongBytes()
    {
        using (var f = new MpqFixture(Archive()))
        {
            f.Add("victim.bin", "ORIGINAL-BYTES-OF-THE-VICTIM", 0);
            f.Add("other.txt", "other");
        }
        var bytes = File.ReadAllBytes(Archive());
        var at = bytes.AsSpan().IndexOf("ORIGINAL"u8);
        Assert.True(at > 0);
        "CHANGED!"u8.CopyTo(bytes.AsSpan(at));
        File.WriteAllBytes(Archive(), bytes);
    }

    [Fact]
    public void Restore_BytesDisagreeWithAttributes_RecordedFailedNoFile()
    {
        ArchiveWithWrongBytes();

        var result = Restore();

        Assert.Equal(1, result.Failed);
        var row = Row("victim.bin");
        Assert.Equal("failed", row["status"]);
        Assert.Contains("(attributes)", (string)row["reason"]!);
        Assert.False(File.Exists(Path.Combine(_out, "a.MPQ", "victim.bin")));
        Assert.Equal("ok", Row("other.txt")["status"]);
    }

    [Fact]
    public void Restore_Rerun_FailedReadKeepsOldFileAndSaysSo()
    {
        ArchiveWithWrongBytes();
        Restore();
        // A manifest and file from a run before this check existed: marked ok, with wrong bytes on disk.
        var stale = Path.Combine(_out, "a.MPQ", "victim.bin");
        var garbage = "CHANGED!-BYTES-OF-THE-VICTIM"u8.ToArray();
        File.WriteAllBytes(stale, garbage);
        using (var c = new SqliteConnection("Data Source=" + Manifest))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE files SET status='ok', reason=NULL, crc=$c WHERE path_lc='victim.bin'";
            cmd.Parameters.AddWithValue("$c", (long)System.IO.Hashing.Crc32.HashToUInt32(garbage));
            cmd.ExecuteNonQuery();
        }

        var result = Restore();

        Assert.Equal(1, result.Failed);
        Assert.Equal("failed", Row("victim.bin")["status"]);
        Assert.Contains("older archive version", (string)Row("victim.bin")["reason"]!);
        Assert.Equal(garbage, File.ReadAllBytes(stale));
    }

    [Fact]
    public void Restore_UnnamedEntry_RecordedAsUnnamed()
    {
        using (var f = new MpqFixture(Archive(), listfile: false, attributes: false))
        {
            f.Add("known.txt", "known");
            f.Add("secret.txt", "secret stuff");
            f.Add("(listfile)", "known.txt\r\n");
        }

        var result = Restore();

        Assert.Equal(1, result.Unnamed);
        var row = Assert.Single(Rows("SELECT * FROM files WHERE status='unnamed'"));
        Assert.Null(row["name"]);
        Assert.Null(row["name_source"]);
        Assert.Equal((long)"secret stuff".Length, row["size"]);
        Assert.StartsWith("(unnamed)/File", (string)row["path"]!);
        // Bytes are kept even without a name, so strings in them still count later.
        Assert.Equal("secret stuff", File.ReadAllText(Path.Combine(_out, "a.MPQ", (string)row["path"]!)));
        Assert.NotEqual(0L, row["name1"]);
        Assert.Equal((long)MpqHash.Name1("secret.txt"), row["name1"]);
        Assert.Equal((long)MpqHash.Name2("secret.txt"), row["name2"]);
    }

    [Fact]
    public void Restore_UnnamedEncryptedEntry_NotWrittenAsGarbage()
    {
        using (var f = new MpqFixture(Archive(), listfile: false, attributes: false))
        {
            f.Add("known.txt", "known");
            f.Add("secret.txt", "secret stuff", MpqFixture.Encrypted);
            f.Add("(listfile)", "known.txt\r\n");
        }

        Restore();

        var row = Assert.Single(Rows("SELECT * FROM files WHERE status='unnamed'"));
        Assert.Contains("encrypted", (string)row["reason"]!);
        Assert.False(File.Exists(Path.Combine(_out, "a.MPQ", (string)row["path"]!)));
    }

    [Fact]
    public void Restore_NamedEncryptedEntry_Decrypts()
    {
        using (var f = new MpqFixture(Archive()))
        {
            f.Add("secret.txt", "secret stuff", MpqFixture.Encrypted | MpqFixture.Compress);
            f.Add("keyed.txt", "keyed stuff", MpqFixture.Encrypted | MpqFixture.KeyV2);
        }

        Restore();

        Assert.Equal("secret stuff", File.ReadAllText(Path.Combine(_out, "a.MPQ", "secret.txt")));
        Assert.Equal("keyed stuff", File.ReadAllText(Path.Combine(_out, "a.MPQ", "keyed.txt")));
    }

    [Fact]
    public void Restore_DeleteMarker_HasOwnStatus_NoFile()
    {
        using (var f = new MpqFixture(Archive()))
        {
            f.Add("gone.txt", [], MpqFixture.DeleteMarker);
            f.Add("kept.txt", "kept");
        }

        var result = Restore();

        Assert.Equal(1, result.DeleteMarkers);
        Assert.Equal(0, result.Failed);
        var row = Row("gone.txt");
        Assert.Equal("delete_marker", row["status"]);
        Assert.False(File.Exists(Path.Combine(_out, "a.MPQ", "gone.txt")));
    }

    [Theory]
    [InlineData(MpqFixture.Exists, "ok")]
    [InlineData(MpqFixture.Exists | MpqFixture.DeleteMarker, "delete_marker")]
    [InlineData(MpqFixture.Exists | MpqFixture.PatchFile, "patch_file")]
    [InlineData(MpqFixture.DeleteMarker, "delete_marker")]
    public void Status_FollowsBlockFlags(uint flags, string expected) =>
        Assert.Equal(expected, MpqRestorer.StatusFromFlags(flags));

    [Fact]
    public void Status_FreedBlock_IsFailure() => Assert.Equal("failed", MpqRestorer.StatusFromFlags(0));

    [Fact]
    public void Restore_LocaleVariants_EachKeepItsOwnBytes()
    {
        using (var f = new MpqFixture(Archive()))
        {
            f.Add("hello.txt", "neutral");
            f.Add("hello.txt", "german", locale: 0x407);
        }

        Restore();

        var rows = Rows("SELECT * FROM files WHERE path_lc LIKE '%hello.txt' ORDER BY locale");
        Assert.Equal(2, rows.Count);
        Assert.Equal(0L, rows[0]["locale"]);
        Assert.Equal(0x407L, rows[1]["locale"]);
        Assert.Equal("hello.txt", rows[0]["path_lc"]);
        Assert.Equal("(locale-0407)/hello.txt", rows[1]["path_lc"]);
        Assert.Equal("neutral", File.ReadAllText(Path.Combine(_out, "a.MPQ", "hello.txt")));
        Assert.Equal("german", File.ReadAllText(Path.Combine(_out, "a.MPQ", "(locale-0407)", "hello.txt")));
    }

    [Fact]
    public void Restore_OnlyLocaleEntry_UsesPlainPath()
    {
        using (var f = new MpqFixture(Archive()))
            f.Add("hello.txt", "german", locale: 0x407);

        Restore();

        Assert.Equal("german", File.ReadAllText(Path.Combine(_out, "a.MPQ", "hello.txt")));
        Assert.Equal(0x407L, Row("hello.txt")["locale"]);
    }

    [Fact]
    public void Restore_LongPath_Survives()
    {
        var name = string.Join('\\', Enumerable.Repeat(new string('d', 60), 6)) + @"\f.txt";
        using (var f = new MpqFixture(Archive())) f.Add(name, "deep");

        Restore();

        var row = Row(name.Replace('\\', '/').ToLowerInvariant());
        Assert.Equal("ok", row["status"]);
        Assert.True(File.Exists(Path.Combine(_out, "a.MPQ", name)));
    }

    [Fact]
    public void Restore_NonAsciiName_RoundTripsBytes()
    {
        using (var f = new MpqFixture(Archive())) f.Add("caf\u00e9.txt", "latte");

        var result = Restore();

        Assert.Equal(0, result.Failed);
        Assert.Equal("latte", File.ReadAllText(Path.Combine(_out, "a.MPQ", "caf\u00e9.txt")));
    }

    [Fact]
    public void Restore_FileAndFolderOfSameName_FailsOneWithReason()
    {
        using (var f = new MpqFixture(Archive()))
        {
            f.Add("thing", "a file");
            f.Add(@"thing\inner.txt", "under the same name");
        }

        var result = Restore();

        Assert.Equal(1, result.Failed);
        Assert.Contains(Rows("SELECT * FROM files"), r => (string)r["status"]! == "failed" && ((string)r["reason"]!).Length > 0);
    }

    [Fact]
    public void Restore_NeverChangesTheArchive()
    {
        using (var f = new MpqFixture(Archive()))
        {
            f.Add("one.txt", "1");
            f.Add("two.txt", "2", MpqFixture.Encrypted);
        }
        File.SetLastWriteTimeUtc(Archive(), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var hash = SHA256.HashData(File.ReadAllBytes(Archive()));

        Restore();
        Restore();

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Archive())));
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Archive()));
        Assert.Equal(new[] { "a.MPQ" }, Directory.GetFiles(_data).Select(Path.GetFileName));
    }

    [Fact]
    public void Restore_ArchiveIsOpenedForReadingOnly()
    {
        using (var f = new MpqFixture(Archive())) f.Add("one.txt", "1");
        File.SetAttributes(Archive(), FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(0, Restore().Failed);
        }
        finally { File.SetAttributes(Archive(), FileAttributes.Normal); }
    }

    [Fact]
    public void Restore_Rerun_SkipsCorrectFiles_RepairsDamaged()
    {
        using (var f = new MpqFixture(Archive()))
        {
            f.Add("same.txt", "unchanged body");
            f.Add("deleted.txt", "will be deleted");
            f.Add("tampered.txt", "will be tampered");
        }
        var first = Restore();
        var root = Path.Combine(_out, "a.MPQ");
        var stamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(root, "same.txt"), stamp);
        File.Delete(Path.Combine(root, "deleted.txt"));
        File.WriteAllText(Path.Combine(root, "tampered.txt"), "WILL BE TAMPERED");   // same size, wrong bytes

        var second = Restore();

        Assert.Equal(first.Entries, second.Entries);
        Assert.Equal(2, second.Extracted);
        Assert.Equal(first.Ok - 2, second.Skipped);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(root, "same.txt")));
        Assert.Equal("will be deleted", File.ReadAllText(Path.Combine(root, "deleted.txt")));
        Assert.Equal("will be tampered", File.ReadAllText(Path.Combine(root, "tampered.txt")));
    }

    [Fact]
    public void Restore_ManifestLost_SkipsByAttributesCrc()
    {
        using (var f = new MpqFixture(Archive()))
            f.Add("same.txt", "unchanged body");
        Restore();
        SqliteConnection.ClearAllPools();
        File.Delete(Manifest);
        var stamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_out, "a.MPQ", "same.txt"), stamp);

        var second = Restore();

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(_out, "a.MPQ", "same.txt")));
        Assert.Equal("ok", Row("same.txt")["status"]);
        Assert.True(second.Skipped >= 1);
    }

    [Fact]
    public void Restore_LeftoverPartFile_IsReplaced()
    {
        using (var f = new MpqFixture(Archive())) f.Add("x.txt", "whole");
        Directory.CreateDirectory(Path.Combine(_out, "a.MPQ"));
        File.WriteAllText(Path.Combine(_out, "a.MPQ", "x.txt.part"), "half");

        Restore();

        Assert.Equal("whole", File.ReadAllText(Path.Combine(_out, "a.MPQ", "x.txt")));
        Assert.False(File.Exists(Path.Combine(_out, "a.MPQ", "x.txt.part")));
    }

    [Fact]
    public void Restore_NotEnoughSpace_StopsBeforeWriting()
    {
        using (var f = new MpqFixture(Archive())) f.Add("x.txt", new byte[100_000], 0);

        var ex = Assert.Throws<IOException>(() => MpqRestorer.Run(
            new RestoreOptions(_data, _out, Config("a.MPQ"), 1, FreeBytes: _ => 1000)));

        Assert.Contains("space", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_out, "a.MPQ")));
    }

    [Fact]
    public void Restore_ParallelArchives_MatchSerial()
    {
        string[] names = ["a.MPQ", "b.MPQ", "c.MPQ", "sub/d.MPQ"];
        foreach (var n in names)
            using (var f = new MpqFixture(Archive(n)))
                for (var i = 0; i < 20; i++) f.Add($@"dir\file{i}.txt", $"{n} {i}", i % 2 == 0 ? MpqFixture.Encrypted : MpqFixture.Compress);

        var result = Restore(4, names);

        Assert.Equal(0, result.Failed);
        foreach (var n in names)
            for (var i = 0; i < 20; i++)
                Assert.Equal($"{n} {i}", File.ReadAllText(Path.Combine(_out, n, "dir", $"file{i}.txt")));
        Assert.Equal(4, Rows("SELECT * FROM archives").Count);
    }

    [Fact]
    public void Restore_BrokenArchive_ReportedNotFatal()
    {
        using (var f = new MpqFixture(Archive())) f.Add("x.txt", "x");
        File.WriteAllText(Archive("b.MPQ"), "not an mpq at all, just text");

        var result = Restore(1, "a.MPQ", "b.MPQ");

        Assert.Equal("ok", Row("x.txt")["status"]);
        var broken = Assert.Single(result.BrokenArchives);
        Assert.Contains("b.MPQ", broken);
        var row = Assert.Single(Rows("SELECT * FROM archives WHERE archive='b.MPQ'"));
        Assert.False(string.IsNullOrEmpty((string?)row["error"]));
    }

    [Theory]
    [InlineData("Client/Data")]            // the client's own data
    [InlineData("Client/Data/inner")]
    [InlineData("Client")]                 // the client folder holds Data
    [InlineData("Client/restored")]        // restore may not write under the client at all
    public void Restore_OutInsideClient_Refused(string relOut)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MpqRestorer.Run(
            new RestoreOptions(_data, _dir.File(relOut), Config())));
        Assert.Contains("--out", ex.Message);
    }

    [Fact]
    public void Restore_OutInsideRepo_Refused()
    {
        var repo = _dir.File("repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "CoaMcp.sln"), "");
        var ex = Assert.Throws<InvalidOperationException>(() => MpqRestorer.Run(
            new RestoreOptions(_data, Path.Combine(repo, "out"), Config())));
        Assert.Contains("repo", ex.Message);
    }
}

public class SafePathTests
{
    [Theory]
    [InlineData(@"World\Maps\a.adt", "World/Maps/a.adt")]
    [InlineData("World/Maps/a.adt", "World/Maps/a.adt")]
    [InlineData("(listfile)", "(listfile)")]
    [InlineData(@"a\\b.txt", "a/b.txt")]
    public void Normalize_Accepts(string name, string expected) => Assert.Equal(expected, SafePath.Normalize(name, out _));

    [Theory]
    [InlineData(@"..\evil", "traversal")]
    [InlineData(@"a\..\..\evil", "traversal")]
    [InlineData(@"a/../evil", "traversal")]
    [InlineData(@".\evil", "traversal")]
    [InlineData(@"\evil", "absolute")]
    [InlineData(@"/evil", "absolute")]
    [InlineData(@"\\server\share\evil", "absolute")]
    [InlineData(@"\\?\C:\evil", "absolute")]
    [InlineData(@"C:\evil", "drive")]
    [InlineData(@"C:evil", "drive")]
    [InlineData(@"a\b:stream", "invalid")]
    [InlineData("a<b", "invalid")]
    [InlineData("a>b", "invalid")]
    [InlineData("a\"b", "invalid")]
    [InlineData("a|b", "invalid")]
    [InlineData("a?b", "invalid")]
    [InlineData("a*b", "invalid")]
    [InlineData("a\u0001b", "invalid")]
    [InlineData(@"CON", "reserved")]
    [InlineData(@"dir\nul.txt", "reserved")]
    [InlineData(@"COM1.blp", "reserved")]
    [InlineData(@"lpt9\a", "reserved")]
    [InlineData(@"name.", "trailing")]
    [InlineData(@"name ", "trailing")]
    [InlineData(@"dir.\x", "trailing")]
    [InlineData("", "empty")]
    public void Normalize_Rejects(string name, string reasonPart)
    {
        Assert.Null(SafePath.Normalize(name, out var reason));
        Assert.Contains(reasonPart, reason);
    }

    [Fact]
    public void Normalize_RejectsOverlongSegment()
    {
        Assert.Null(SafePath.Normalize(new string('a', 256), out var reason));
        Assert.Contains("long", reason);
    }

    [Theory]
    [InlineData("patch.MPQ", "patch.MPQ.listfile.txt")]
    [InlineData("enUS/patch-enUS.MPQ", "enUS__patch-enUS.MPQ.listfile.txt")]
    [InlineData("area-52/patch-D.MPQ", "area-52__patch-D.MPQ.listfile.txt")]
    public void ListfileName(string archive, string expected) => Assert.Equal(expected, SafePath.ListfileName(archive));
}

public class MpqHashTests
{
    [Fact]
    public void FileKey_MatchesPublishedConstants()
    {
        // The MPQ format's documented table keys.
        Assert.Equal(0xC3AF3770u, MpqHash.FileKey("(hash table)"));
        Assert.Equal(0xEC83B3A3u, MpqHash.FileKey("(block table)"));
    }

    [Fact]
    public void Names_IgnoreCaseAndSlashDirection()
    {
        Assert.Equal(MpqHash.Name1(@"World\Foo.txt"), MpqHash.Name1("world/foo.TXT"));
        Assert.Equal(MpqHash.Name2(@"World\Foo.txt"), MpqHash.Name2("world/foo.TXT"));
        Assert.NotEqual(MpqHash.Name1("a"), MpqHash.Name2("a"));
    }
}
