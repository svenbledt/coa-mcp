using System.IO.Hashing;
using System.Text;
using CoaMcp.Pipeline.Mpq;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

/// <summary>Archives change between client revisions. A re-run must follow what the archive holds now, not what the manifest remembers.</summary>
public class RestoreRerunTests : IDisposable
{
    private static readonly DateTime First = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Second = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TempDir _dir = new();
    private readonly string _data, _out;

    public RestoreRerunTests()
    {
        _data = _dir.File("Client/Data");
        _out = _dir.File("restored");
        Directory.CreateDirectory(_data);
    }

    public void Dispose() => _dir.Dispose();

    private string ArchivePath(string name = "a.MPQ") => Path.Combine(_data, name);
    private string Restored(string relative, string archive = "a.MPQ") => Path.Combine(_out, archive, relative);

    /// <summary>Writes the archive from scratch, stored uncompressed so equal-size files give an equal-size archive.</summary>
    private void Build(DateTime stamp, params (string Name, string Body)[] files) => BuildNamed("a.MPQ", stamp, files);

    private void BuildNamed(string archive, DateTime stamp, params (string Name, string Body)[] files)
    {
        if (File.Exists(ArchivePath(archive))) File.Delete(ArchivePath(archive));
        using (var f = new MpqFixture(ArchivePath(archive)))
            foreach (var (name, body) in files) f.Add(name, body, 0);
        File.SetLastWriteTimeUtc(ArchivePath(archive), stamp);
    }

    private RestoreResult Restore(params string[] archives) => MpqRestorer.Run(new RestoreOptions(_data, _out, new ArchiveConfig(
        new Dictionary<string, string> { ["t"] = "fixture" },
        (archives.Length == 0 ? ["a.MPQ"] : archives).Select((a, i) => new ArchiveEntry(a, 100 - i, "unknown", null, "t")).ToList())));

    private List<Dictionary<string, object?>> Rows(string sql)
    {
        using var c = new SqliteConnection("Data Source=" + Path.Combine(_out, "manifest.sqlite"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
            rows.Add(Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i)));
        return rows;
    }

    private void Sql(string sql)
    {
        using var c = new SqliteConnection("Data Source=" + Path.Combine(_out, "manifest.sqlite"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private Dictionary<string, object?> Row(string pathLc) =>
        Assert.Single(Rows($"SELECT * FROM files WHERE path_lc='{pathLc}'"));

    private static long Crc(string body) => Crc32.HashToUInt32(Encoding.ASCII.GetBytes(body));

    [Fact]
    public void Rerun_SameSizeChange_RestoresNewBytes()
    {
        Build(First, ("same.bin", "OLD-BODY-OLD-BODY"), ("other.txt", "other"));
        Restore();
        var sizeBefore = new FileInfo(ArchivePath()).Length;

        Build(Second, ("same.bin", "NEW-BODY-NEW-BODY"), ("other.txt", "other"));
        Assert.Equal(sizeBefore, new FileInfo(ArchivePath()).Length);
        var result = Restore();

        Assert.Equal("NEW-BODY-NEW-BODY", File.ReadAllText(Restored("same.bin")));
        var row = Row("same.bin");
        Assert.Equal("ok", row["status"]);
        Assert.Equal(Crc("NEW-BODY-NEW-BODY"), row["crc"]);
        Assert.Equal(Crc("NEW-BODY-NEW-BODY"), row["attr_crc"]);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void Rerun_Resize_RestoresNewBytes()
    {
        Build(First, ("grow.bin", "short"), ("other.txt", "other"));
        Restore();

        Build(Second, ("grow.bin", "a much longer body than before"), ("other.txt", "other"));
        var result = Restore();

        Assert.Equal("a much longer body than before", File.ReadAllText(Restored("grow.bin")));
        var row = Row("grow.bin");
        Assert.Equal("ok", row["status"]);
        Assert.Equal((long)"a much longer body than before".Length, row["size"]);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void Rerun_RemovedEntry_DropsRowAndFile()
    {
        Build(First, (@"gone\deep\x.txt", "bye"), ("kept.txt", "kept"));
        var first = Restore();

        Build(Second, ("kept.txt", "kept"));
        var second = Restore();

        Assert.Empty(Rows("SELECT * FROM files WHERE path_lc='gone/deep/x.txt'"));
        Assert.False(File.Exists(Restored(@"gone\deep\x.txt")));
        Assert.False(Directory.Exists(Restored("gone")));
        Assert.Equal("kept", File.ReadAllText(Restored("kept.txt")));
        Assert.Equal(first.Entries - 1, second.Entries);
        Assert.Equal(first.Ok - 1, second.Ok);
        Assert.Equal(new[] { "a.MPQ" }, second.ChangedArchives);
    }

    [Fact]
    public void Rerun_EntryBecomesDeleteMarker_RemovesOldFile()
    {
        Build(First, ("doomed.txt", "here for now"), ("kept.txt", "kept"));
        Restore();

        File.Delete(ArchivePath());
        using (var f = new MpqFixture(ArchivePath()))
        {
            f.Add("doomed.txt", [], MpqFixture.DeleteMarker);
            f.Add("kept.txt", "kept", 0);
        }
        File.SetLastWriteTimeUtc(ArchivePath(), Second);
        Restore();

        Assert.Equal("delete_marker", Row("doomed.txt")["status"]);
        Assert.False(File.Exists(Restored("doomed.txt")));
    }

    [Fact]
    public void Rerun_UnchangedArchive_SkipsFiles()
    {
        Build(First, ("same.txt", "unchanged"), ("other.txt", "other"));
        var first = Restore();
        var stamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Restored("same.txt"), stamp);

        var second = Restore();

        Assert.Equal(0, second.Extracted);
        Assert.Equal(first.Ok, second.Skipped);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Restored("same.txt")));
        Assert.Empty(second.ChangedArchives);
    }

    [Fact]
    public void Manifest_RecordsArchiveSizeAndTime()
    {
        Build(First, ("x.txt", "x"));
        Restore();

        var row = Assert.Single(Rows("SELECT * FROM archives"));
        Assert.Equal(new FileInfo(ArchivePath()).Length, row["file_size"]);
        Assert.Equal(First.Ticks, row["file_mtime"]);
    }

    [Fact]
    public void Rerun_NewBytesBehindOldSizeAndTime_StillUsesCurrentCrc()
    {
        Build(First, ("same.bin", "OLD-BODY-OLD-BODY"));
        Restore();

        Build(First, ("same.bin", "NEW-BODY-NEW-BODY"));   // the identity check can't see this one
        Restore();

        Assert.Equal("NEW-BODY-NEW-BODY", File.ReadAllText(Restored("same.bin")));
        Assert.Equal(Crc("NEW-BODY-NEW-BODY"), Row("same.bin")["attr_crc"]);
    }

    [Fact]
    public void Rerun_ChangedArchive_NoCrcFileIsNotTrusted()
    {
        void BuildNoCrc(string body, DateTime stamp)
        {
            File.Delete(ArchivePath());
            using (var f = new MpqFixture(ArchivePath(), listfile: false))
            {
                f.Add("x.bin", body);
                f.Add("(listfile)", "x.bin\r\n");
            }
            File.SetLastWriteTimeUtc(ArchivePath(), stamp);
        }
        BuildNoCrc("OLD-BODY", First);
        Restore();

        BuildNoCrc("NEW-BODY", Second);
        Restore();

        Assert.Equal("NEW-BODY", File.ReadAllText(Restored("x.bin")));
    }

    [Fact]
    public void Rerun_ChangedArchive_LeavesOtherArchivesAlone()
    {
        BuildNamed("a.MPQ", First, ("shared.txt", "from a"), ("only-a.txt", "a"));
        BuildNamed("b.MPQ", First, ("shared.txt", "from b"), ("only-a.txt", "b too"));
        Restore("a.MPQ", "b.MPQ");
        var stamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Restored("only-a.txt", "b.MPQ"), stamp);

        BuildNamed("a.MPQ", Second, ("shared.txt", "from a"));
        var result = Restore("a.MPQ", "b.MPQ");

        Assert.False(File.Exists(Restored("only-a.txt", "a.MPQ")));
        Assert.Equal("b too", File.ReadAllText(Restored("only-a.txt", "b.MPQ")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Restored("only-a.txt", "b.MPQ")));
        Assert.Single(Rows("SELECT * FROM files WHERE path_lc='only-a.txt'"));
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void Rerun_ChangedArchive_FileMayReplaceOldFolder()
    {
        Build(First, (@"thing\inner.txt", "inside"));
        Restore();

        Build(Second, ("thing", "now a file"));
        var result = Restore();

        Assert.Equal(0, result.Failed);
        Assert.Equal("now a file", File.ReadAllText(Restored("thing")));
    }

    [Fact]
    public void Rerun_ChangedArchive_KeepsRecoveryNote()
    {
        var body = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2500).Select(i => $"line {i} of the victim\r\n")));
        void BuildWrongHeader(DateTime stamp)
        {
            File.Delete(ArchivePath());
            using (var f = new MpqFixture(ArchivePath(), listfile: false, headerShift: 5))
            {
                f.Add("(listfile)", "victim.bin\r\n");
                f.AddSectored("victim.bin", body, storedShift: 3, attributesCrc: Crc32.HashToUInt32(body));
            }
            File.SetLastWriteTimeUtc(ArchivePath(), stamp);
        }
        BuildWrongHeader(First);
        Restore();

        BuildWrongHeader(Second);
        var result = Restore();

        Assert.Equal(1, result.Recovered);
        Assert.Equal("recovered: header sector shift 5 wrong, used 3", Row("victim.bin")["reason"]);
        Assert.Equal(body, File.ReadAllBytes(Restored("victim.bin")));
    }

    private RestoreResult RestoreFrom(string data, bool prune, params string[] archives) => MpqRestorer.Run(new RestoreOptions(data, _out, new ArchiveConfig(
        new Dictionary<string, string> { ["t"] = "fixture" },
        archives.Select((a, i) => new ArchiveEntry(a, 100 - i, "unknown", null, "t")).ToList()), PruneGone: prune));

    private void AssertRestoreIntact(string originalData, long entries)
    {
        Assert.Equal(entries, Rows("SELECT * FROM files").Count);
        Assert.Equal(Path.GetFullPath(originalData), Assert.Single(Rows("SELECT value FROM meta WHERE key='data_dir'"))["value"]);
        foreach (var row in Rows("SELECT archive, path FROM files WHERE status='ok'"))
            Assert.True(File.Exists(Path.Combine(_out, (string)row["archive"]!, (string)row["path"]!)), (string)row["path"]!);
    }

    [Fact]
    public void Rerun_WrongDataFolder_ErrorsAndKeepsEverything()
    {
        Build(First, ("a.txt", "a"), ("b.txt", "b"));
        var first = Restore();
        // Another client's folder: it has an archive of the same name, with other contents.
        var other = _dir.File("Other/Data");
        Directory.CreateDirectory(other);
        using (var f = new MpqFixture(Path.Combine(other, "a.MPQ"))) f.Add("different.txt", "different", 0);

        var ex = Assert.Throws<InvalidOperationException>(() => RestoreFrom(other, false, "a.MPQ"));

        Assert.Contains("--data", ex.Message);
        AssertRestoreIntact(_data, first.Entries);
        Assert.False(File.Exists(Restored("different.txt")));
    }

    [Fact]
    public void Rerun_EmptyDataFolder_ErrorsAndKeepsEverything()
    {
        Build(First, ("a.txt", "a"), ("b.txt", "b"));
        var first = Restore();
        var empty = _dir.File("Other/Data");
        Directory.CreateDirectory(empty);

        Assert.Throws<InvalidDataException>(() => RestoreFrom(empty, false, "a.MPQ"));
        Assert.Throws<InvalidDataException>(() => RestoreFrom(empty, true, "a.MPQ"));

        AssertRestoreIntact(_data, first.Entries);
    }

    [Fact]
    public void Rerun_RightDataFolderEmptied_ErrorsAndKeepsEverything()
    {
        Build(First, ("a.txt", "a"));
        var first = Restore();
        File.Delete(ArchivePath());

        Assert.Throws<InvalidDataException>(() => RestoreFrom(_data, true, "a.MPQ"));

        AssertRestoreIntact(_data, first.Entries);
    }

    [Fact]
    public void Rerun_GoneArchive_ReportedNotDeleted()
    {
        BuildNamed("a.MPQ", First, ("a.txt", "a"));
        BuildNamed("sub/b.MPQ", First, ("b.txt", "b"));
        var first = RestoreFrom(_data, false, "a.MPQ", "sub/b.MPQ");

        File.Delete(ArchivePath("sub/b.MPQ"));
        var second = RestoreFrom(_data, false, "a.MPQ");

        Assert.Equal(new[] { "sub/b.MPQ" }, second.GoneArchives);
        Assert.Equal("b", File.ReadAllText(Restored("b.txt", "sub/b.MPQ")));
        AssertRestoreIntact(_data, first.Entries);
        Assert.Single(Rows("SELECT * FROM archives WHERE archive='sub/b.MPQ'"));
    }

    [Fact]
    public void Rerun_GoneArchive_PruneFlag_Removes()
    {
        BuildNamed("a.MPQ", First, ("a.txt", "a"));
        BuildNamed("sub/b.MPQ", First, ("b.txt", "b"));
        var first = RestoreFrom(_data, false, "a.MPQ", "sub/b.MPQ");

        File.Delete(ArchivePath("sub/b.MPQ"));
        var second = RestoreFrom(_data, true, "a.MPQ");

        Assert.Equal(new[] { "sub/b.MPQ" }, second.GoneArchives);
        Assert.False(Directory.Exists(Path.Combine(_out, "sub")));
        Assert.Empty(Rows("SELECT * FROM files WHERE archive='sub/b.MPQ'"));
        Assert.Empty(Rows("SELECT * FROM archives WHERE archive='sub/b.MPQ'"));
        Assert.Empty(Rows("SELECT * FROM listfile_names WHERE archive='sub/b.MPQ'"));
        Assert.Equal("a", File.ReadAllText(Restored("a.txt")));
        Assert.True(second.Entries < first.Entries);
    }

    [Fact]
    public void Rerun_PruneFlag_WrongDataFolder_Refused()
    {
        BuildNamed("a.MPQ", First, ("a.txt", "a"));
        BuildNamed("b.MPQ", First, ("b.txt", "b"));
        var first = RestoreFrom(_data, false, "a.MPQ", "b.MPQ");
        var other = _dir.File("Other/Data");
        Directory.CreateDirectory(other);
        using (var f = new MpqFixture(Path.Combine(other, "a.MPQ"))) f.Add("a.txt", "a", 0);

        var ex = Assert.Throws<InvalidOperationException>(() => RestoreFrom(other, true, "a.MPQ"));

        Assert.Contains("--data", ex.Message);
        AssertRestoreIntact(_data, first.Entries);
    }

    [Theory]
    [InlineData("x/../b.MPQ")]
    [InlineData("../b.MPQ")]
    public void Rerun_GoneArchiveWithUnsafeName_PruneTouchesNoFiles(string hostile)
    {
        BuildNamed("a.MPQ", First, ("a.txt", "a"));
        BuildNamed("b.MPQ", First, ("b.txt", "precious"));
        RestoreFrom(_data, false, "a.MPQ", "b.MPQ");
        Sql($"INSERT INTO archives(archive, priority, origin) VALUES('{hostile}', 1, 'unknown')");
        Sql($"INSERT INTO files(archive, path, path_lc, size, status, hash_index, block_index, locale, flags, name1, name2) VALUES('{hostile}', 'b.txt', 'b.txt', 8, 'ok', 1, 1, 0, 0, 1, 1)");
        File.WriteAllText(_dir.File("b.txt"), "outside");

        var result = RestoreFrom(_data, true, "a.MPQ", "b.MPQ");

        Assert.Contains(hostile, result.GoneArchives);
        Assert.Equal("precious", File.ReadAllText(Restored("b.txt", "b.MPQ")));
        Assert.Equal("outside", File.ReadAllText(_dir.File("b.txt")));
        Assert.Empty(Rows($"SELECT * FROM files WHERE archive='{hostile}'"));
    }

    private (string Name, string Body)[] ManyFiles() =>
        Enumerable.Range(0, 42).Select(i => ($"dir{i % 3}\\file{i}.txt", new string((char)('a' + i % 26), 5000))).ToArray();

    private void AssertBrokenAndIntact(RestoreResult result, long entries)
    {
        Assert.Contains("a.MPQ", Assert.Single(result.BrokenArchives));
        AssertRestoreIntact(_data, entries);
        Assert.Equal(First.Ticks, Assert.Single(Rows("SELECT file_mtime FROM archives"))["file_mtime"]);
    }

    [Fact]
    public void Rerun_ZeroTailArchive_RemovesNothing()
    {
        Build(First, ManyFiles());
        var first = Restore();
        var bytes = File.ReadAllBytes(ArchivePath());
        Array.Clear(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2);
        File.WriteAllBytes(ArchivePath(), bytes);
        File.SetLastWriteTimeUtc(ArchivePath(), Second);

        AssertBrokenAndIntact(Restore(), first.Entries);
    }

    [Fact]
    public void Rerun_TruncatedArchive_RemovesNothing()
    {
        Build(First, ManyFiles());
        var first = Restore();
        var bytes = File.ReadAllBytes(ArchivePath());
        File.WriteAllBytes(ArchivePath(), bytes[..(bytes.Length - bytes.Length / 1000 - 1)]);
        File.SetLastWriteTimeUtc(ArchivePath(), Second);

        AssertBrokenAndIntact(Restore(), first.Entries);
    }

    [Fact]
    public void Rerun_ZeroedBlockTableOnly_RemovesNothing()
    {
        // The hash table survives, so entries still point at real slots; only the blocks they describe are damaged.
        Build(First, ManyFiles());
        var first = Restore();
        var bytes = File.ReadAllBytes(ArchivePath());
        var blockTablePos = BitConverter.ToInt32(bytes, 20);
        var blockCount = BitConverter.ToInt32(bytes, 28);
        Array.Clear(bytes, blockTablePos, blockCount * 16);
        File.WriteAllBytes(ArchivePath(), bytes);
        File.SetLastWriteTimeUtc(ArchivePath(), Second);

        AssertBrokenAndIntact(Restore(), first.Entries);
    }

    [Fact]
    public void Rerun_FailedChangedArchive_StaysChangedNextRun()
    {
        Build(First, ("same.bin", "OLD-BODY-OLD-BODY"), ("gone.txt", "gone"));
        Restore();
        Build(Second, ("same.bin", "NEW-BODY-NEW-BODY"));

        RestoreResult failed;
        using (new FileStream(ArchivePath(), FileMode.Open, FileAccess.Read, FileShare.None))   // locked: can't open, though its identity can be read
            failed = Restore();

        Assert.Contains("a.MPQ", Assert.Single(failed.BrokenArchives));
        Assert.Equal(First.Ticks, Assert.Single(Rows("SELECT file_mtime FROM archives"))["file_mtime"]);
        Assert.False(string.IsNullOrEmpty((string?)Assert.Single(Rows("SELECT error FROM archives"))["error"]));
        Assert.Equal("OLD-BODY-OLD-BODY", File.ReadAllText(Restored("same.bin")));

        var next = Restore();

        Assert.Equal(new[] { "a.MPQ" }, next.ChangedArchives);
        Assert.Empty(next.BrokenArchives);
        Assert.Equal("NEW-BODY-NEW-BODY", File.ReadAllText(Restored("same.bin")));
        Assert.Empty(Rows("SELECT * FROM files WHERE path_lc='gone.txt'"));
        Assert.Equal(Second.Ticks, Assert.Single(Rows("SELECT file_mtime FROM archives"))["file_mtime"]);
        Assert.Null(Assert.Single(Rows("SELECT error FROM archives"))["error"]);
    }

    [Fact]
    public void Run_NewIdentityWrittenOnlyAfterTheArchiveFinished()
    {
        Build(First, ("a.txt", "a"));
        Restore();
        Build(Second, ("a.txt", "a"), ("b.txt", "b"));

        // Free space runs out partway through: the archive didn't finish, so it must still count as changed.
        var calls = 0;
        Build(Second, Enumerable.Range(0, 300).Select(i => ($"f{i}.txt", "x")).ToArray());
        Assert.Throws<IOException>(() => MpqRestorer.Run(new RestoreOptions(_data, _out, new ArchiveConfig(
            new Dictionary<string, string> { ["t"] = "fixture" }, [new ArchiveEntry("a.MPQ", 1, "unknown", null, "t")]),
            FreeBytes: _ => ++calls == 1 ? long.MaxValue : 0)));

        Assert.Equal(First.Ticks, Assert.Single(Rows("SELECT file_mtime FROM archives"))["file_mtime"]);
        Assert.Equal(new[] { "a.MPQ" }, Restore().ChangedArchives);
    }

    private string ConfigFile()
    {
        var path = _dir.File("archives.json");
        File.WriteAllText(path, """{"rules":{"t":"fixture"},"archives":[{"path":"a.MPQ","priority":1,"origin":"unknown","realm":null,"rule":"t"}]}""");
        return path;
    }

    [Fact]
    public void Cli_PruneFlag_IsAcceptedAndOffByDefault()
    {
        BuildNamed("a.MPQ", First, ("a.txt", "a"));
        BuildNamed("b.MPQ", First, ("b.txt", "b"));
        RestoreFrom(_data, false, "a.MPQ", "b.MPQ");
        File.Delete(ArchivePath("b.MPQ"));
        string[] common = ["--data", _data, "--out", _out, "--config", ConfigFile()];

        var output = new StringWriter();
        Assert.Equal(1, MpqCli.Run("restore", common, output, TextWriter.Null));   // gone archives aren't a clean run
        Assert.Contains("b.MPQ", output.ToString());
        Assert.Contains("--prune-gone", output.ToString());
        Assert.True(File.Exists(Restored("b.txt", "b.MPQ")));

        Assert.Equal(0, MpqCli.Run("restore", [.. common, "--prune-gone"], TextWriter.Null, TextWriter.Null));
        Assert.False(File.Exists(Restored("b.txt", "b.MPQ")));
    }

    [Fact]
    public void Cli_ArchiveCheck_ReportsAndWritesNothing()
    {
        Build(First, ManyFiles());
        string[] args = ["--data", _data, "--config", ConfigFile()];
        var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ArchivePath()));

        var good = new StringWriter();
        Assert.Equal(0, MpqCli.Run("archive-check", args, good, TextWriter.Null));
        Assert.Contains("1 of 1", good.ToString());
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ArchivePath())));
        Assert.False(Directory.Exists(_out));

        var bytes = File.ReadAllBytes(ArchivePath());
        File.WriteAllBytes(ArchivePath(), bytes[..^500]);
        var bad = new StringWriter();
        Assert.Equal(1, MpqCli.Run("archive-check", args, bad, TextWriter.Null));
        Assert.Contains("a.MPQ", bad.ToString());
    }

    [Theory]
    [InlineData("../outside.txt")]         // climbs out of this archive's folder
    [InlineData("../../outside.txt")]
    public void Rerun_HostilePathInOldRow_NeverRemovesAnythingOutside(string hostile)
    {
        Build(First, ("kept.txt", "kept"), ("gone.txt", "gone"));
        Restore();
        // Both targets exist and sit outside a.MPQ's folder.
        File.WriteAllText(Path.Combine(_out, "outside.txt"), "precious");
        File.WriteAllText(_dir.File("outside.txt"), "precious");
        Sql($"UPDATE files SET path='{hostile}', path_lc='{hostile}' WHERE path_lc='gone.txt'");

        Build(Second, ("kept.txt", "kept"));
        Restore();

        Assert.Equal("precious", File.ReadAllText(Path.Combine(_out, "outside.txt")));
        Assert.Equal("precious", File.ReadAllText(_dir.File("outside.txt")));
        Assert.Empty(Rows($"SELECT * FROM files WHERE path_lc='{hostile}'"));
    }

    [Fact]
    public void Rerun_OldManifestWithoutIdentity_MigratesAndVerifies()
    {
        Build(First, ("same.txt", "unchanged"), ("other.txt", "other"));
        var first = Restore();
        Sql("ALTER TABLE archives DROP COLUMN file_size");
        Sql("ALTER TABLE archives DROP COLUMN file_mtime");
        var stamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Restored("same.txt"), stamp);

        var second = Restore();

        Assert.Equal(0, second.Extracted);
        Assert.Equal(first.Ok, second.Skipped);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Restored("same.txt")));
        var row = Assert.Single(Rows("SELECT * FROM archives"));
        Assert.Equal(First.Ticks, row["file_mtime"]);
    }

    [Fact]
    public void Rerun_OldManifest_StillDropsEntriesNoLongerInTheArchive()
    {
        Build(First, ("kept.txt", "kept"), ("gone.txt", "gone"));
        Restore();
        Sql("ALTER TABLE archives DROP COLUMN file_size");
        Sql("ALTER TABLE archives DROP COLUMN file_mtime");

        Build(Second, ("kept.txt", "kept"));
        Restore();

        Assert.Empty(Rows("SELECT * FROM files WHERE path_lc='gone.txt'"));
        Assert.False(File.Exists(Restored("gone.txt")));
    }
}
