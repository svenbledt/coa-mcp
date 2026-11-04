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

    private string Staging => Path.Combine(_out, ".staging");

    private long RecordedMtime() => Convert.ToInt64(Assert.Single(Rows("SELECT file_mtime FROM archives"))["file_mtime"]);

    /// <summary>A whole-archive restore that was rejected: files, rows and identity exactly as before, no staging left, and the run says why.</summary>
    private void AssertRejectedAndIntact(long entries, IReadOnlyDictionary<string, string> bodies, int exit, string output)
    {
        Assert.NotEqual(0, exit);
        foreach (var (name, body) in bodies)
            Assert.Equal(body, File.ReadAllText(Restored(name)));
        Assert.Equal(entries, Rows("SELECT * FROM files").Count);
        Assert.Empty(Rows("SELECT * FROM files WHERE status<>'ok' OR reason IS NOT NULL"));
        Assert.Equal(First.Ticks, RecordedMtime());
        Assert.False(Directory.Exists(Restored("(unnamed)")));
        Assert.False(Directory.Exists(Staging));
        Assert.Contains("broken archive: a.MPQ", output);
        Assert.Contains("older version", output);
    }

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
        Assert.Equal(new[] { "a.MPQ" }, result.ChangedArchives);
        Assert.Equal(Second.Ticks, RecordedMtime());
        Assert.False(Directory.Exists(Staging));
        Assert.Empty(Rows("SELECT * FROM meta WHERE key LIKE 'pending_swap:%'"));
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
        Assert.Equal(new[] { "a.MPQ" }, result.ChangedArchives);
        Assert.False(Directory.Exists(Staging));
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
        Assert.False(Directory.Exists(Staging));
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

        // Free space runs out partway through (the first two checks pass: before the run, and before staging): the archive
        // didn't finish, so it must still count as changed.
        var calls = 0;
        Build(Second, Enumerable.Range(0, 300).Select(i => ($"f{i}.txt", "x")).ToArray());
        Assert.Throws<IOException>(() => RestoreWith(FreeBytes: _ => ++calls <= 2 ? long.MaxValue : 0));

        Assert.Equal(First.Ticks, RecordedMtime());
        Assert.Equal("a", File.ReadAllText(Restored("a.txt")));
        Assert.False(File.Exists(Restored("f0.txt")));
        Assert.Equal(new[] { "a.MPQ" }, Restore().ChangedArchives);
        Assert.False(Directory.Exists(Staging));
    }

    private RestoreResult RestoreWith(Func<string, long>? FreeBytes = null, Action<string>? Checkpoint = null, bool AllowEmpty = false) => MpqRestorer.Run(new RestoreOptions(_data, _out,
        new ArchiveConfig(new Dictionary<string, string> { ["t"] = "fixture" }, [new ArchiveEntry("a.MPQ", 1, "unknown", null, "t")]),
        FreeBytes: FreeBytes, Checkpoint: Checkpoint, AllowEmpty: AllowEmpty));

    /// <summary>A replacement holding only (listfile) and (attributes): what a failed rebuild leaves behind. Every other rule passes on it vacuously.</summary>
    private void BuildHollow(DateTime stamp)
    {
        File.Delete(ArchivePath());
        using (new MpqFixture(ArchivePath())) { }
        File.SetLastWriteTimeUtc(ArchivePath(), stamp);
    }

    [Fact]
    public void Rerun_HollowReplacement_ChangesNothing()
    {
        var bodies = Bodies();
        BuildFlagged(First, bodies, 0);
        var first = Restore();
        BuildHollow(Second);
        Assert.Null(ArchiveIntegrity.Check(ArchivePath()));

        var output = new StringWriter();
        var exit = MpqCli.Run("restore", ["--data", _data, "--out", _out, "--config", ConfigFile()], output, TextWriter.Null);

        AssertRejectedAndIntact(first.Entries, bodies, exit, output.ToString());
        Assert.Contains("--allow-empty", output.ToString());
    }

    [Fact]
    public void Rerun_HollowReplacement_AllowEmpty_Replaces()
    {
        var bodies = Bodies();
        BuildFlagged(First, bodies, 0);
        Restore();
        BuildHollow(Second);

        var output = new StringWriter();
        var exit = MpqCli.Run("restore", ["--data", _data, "--out", _out, "--config", ConfigFile(), "--allow-empty"], output, TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Contains("restored again as a whole: a.MPQ", output.ToString());
        foreach (var name in bodies.Keys) Assert.False(File.Exists(Restored(name)));
        Assert.Equal(new[] { "(attributes)", "(listfile)" }, Rows("SELECT name FROM files ORDER BY name").Select(r => (string)r["name"]!));
        Assert.Equal(Second.Ticks, RecordedMtime());
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public void Startup_NoNote_LiveMissing_RestoresOld()
    {
        // A power cut after "old moved" that also lost the pending note: the live folder is gone, .old holds the only copies, .staging holds a half copy.
        Build(First, ("a.txt", "a"), (@"deep\b.txt", "b"));
        var first = Restore();
        Directory.CreateDirectory(Staging);
        Directory.Move(Restored(""), Path.Combine(Staging, "a.MPQ.old"));
        Directory.CreateDirectory(Path.Combine(Staging, "a.MPQ"));
        File.WriteAllText(Path.Combine(Staging, "a.MPQ", "a.txt"), "half");
        Assert.False(Directory.Exists(Restored("")));

        var next = Restore();

        Assert.Equal("a", File.ReadAllText(Restored("a.txt")));
        Assert.Equal("b", File.ReadAllText(Restored(@"deep\b.txt")));
        Assert.Equal(0, next.Extracted);
        Assert.Equal(first.Ok, next.Skipped);
        Assert.Empty(next.BrokenArchives);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public void Rerun_InsufficientSpaceForStaging_ChangesNothing()
    {
        var bodies = Bodies();
        BuildFlagged(First, bodies, 0);
        var first = Restore();
        bodies[bodies.Keys.First()] = "a changed body";
        BuildFlagged(Second, bodies, 0);
        bodies[bodies.Keys.First()] = Bodies()[bodies.Keys.First()];   // what's on disk is still the old body

        var calls = 0;
        var result = RestoreWith(FreeBytes: _ => ++calls == 1 ? long.MaxValue : 0);   // the pre-run check passes, the staging check doesn't

        Assert.Contains("space", Assert.Single(result.BrokenArchives));
        Assert.Equal(0, result.Extracted);
        foreach (var (name, body) in bodies)
            Assert.Equal(body, File.ReadAllText(Restored(name)));
        Assert.Equal(first.Entries, Rows("SELECT * FROM files").Count);
        Assert.Equal(First.Ticks, RecordedMtime());
        Assert.False(Directory.Exists(Staging));
    }

    private sealed class SimulatedCrash : Exception;

    private void CrashAt(string step)
    {
        Build(First, ("same.bin", "OLD-BODY-OLD-BODY"), ("gone.txt", "gone"));
        Restore();
        Build(Second, ("same.bin", "NEW-BODY-NEW-BODY"), ("new.txt", "new"));
        Assert.ThrowsAny<Exception>(() => RestoreWith(Checkpoint: at => { if (at == "a.MPQ: " + step) throw new SimulatedCrash(); }));
    }

    [Fact]
    public void Rerun_CrashBeforeSwap_OldStateIntact()
    {
        CrashAt("staged");

        Assert.Equal("OLD-BODY-OLD-BODY", File.ReadAllText(Restored("same.bin")));
        Assert.Equal("gone", File.ReadAllText(Restored("gone.txt")));
        Assert.False(File.Exists(Restored("new.txt")));
        Assert.Single(Rows("SELECT * FROM files WHERE path_lc='gone.txt'"));
        Assert.Empty(Rows("SELECT * FROM files WHERE path_lc='new.txt'"));
        Assert.Equal(First.Ticks, RecordedMtime());

        var next = Restore();

        Assert.Equal(new[] { "a.MPQ" }, next.ChangedArchives);
        Assert.Empty(next.BrokenArchives);
        Assert.Equal("NEW-BODY-NEW-BODY", File.ReadAllText(Restored("same.bin")));
        Assert.Equal("new", File.ReadAllText(Restored("new.txt")));
        Assert.False(File.Exists(Restored("gone.txt")));
        Assert.Empty(Rows("SELECT * FROM files WHERE path_lc='gone.txt'"));
        Assert.Equal(Second.Ticks, RecordedMtime());
        Assert.False(Directory.Exists(Staging));
    }

    [Theory]
    [InlineData("committed")]    // rows and identity are new, the folder is still old
    [InlineData("old moved")]    // the old folder is set aside, the new one not yet in place
    [InlineData("new moved")]    // the new folder is in place, the old one not yet removed
    public void Rerun_CrashMidSwap_RecoversNextRun(string step)
    {
        CrashAt(step);
        Assert.Single(Rows("SELECT * FROM meta WHERE key='pending_swap:a.MPQ'"));

        var next = Restore();

        Assert.Empty(next.BrokenArchives);
        Assert.Equal(0, next.Extracted);   // the swap was finished first, then the archive counted as unchanged
        Assert.Equal("NEW-BODY-NEW-BODY", File.ReadAllText(Restored("same.bin")));
        Assert.Equal("new", File.ReadAllText(Restored("new.txt")));
        Assert.False(File.Exists(Restored("gone.txt")));
        Assert.Empty(Rows("SELECT * FROM files WHERE path_lc='gone.txt'"));
        Assert.Equal("ok", Row("new.txt")["status"]);
        Assert.Equal(Second.Ticks, RecordedMtime());
        Assert.Empty(Rows("SELECT * FROM meta WHERE key LIKE 'pending_swap:%'"));
        Assert.False(Directory.Exists(Staging));
    }

    /// <summary>Random text, so it compresses but stays long enough that zeroed bytes land inside each file's data.</summary>
    private Dictionary<string, string> Bodies()
    {
        var rng = new Random(1);
        var bodies = new Dictionary<string, string>();
        for (var i = 0; i < 42; i++)
            bodies[$"dir{i % 3}\\file{i}.txt"] = string.Concat(Enumerable.Range(0, 900).Select(_ => (char)('a' + rng.Next(26))));
        return bodies;
    }

    private void BuildFlagged(DateTime stamp, IReadOnlyDictionary<string, string> bodies, uint flags)
    {
        if (File.Exists(ArchivePath())) File.Delete(ArchivePath());
        using (var f = new MpqFixture(ArchivePath()))
            foreach (var (name, body) in bodies) f.Add(name, body, flags);
        File.SetLastWriteTimeUtc(ArchivePath(), stamp);
    }

    /// <summary>Header, size and tables stay intact; most of the file data becomes zeros.</summary>
    private void ZeroDataRegion(DateTime stamp)
    {
        var bytes = File.ReadAllBytes(ArchivePath());
        var hashTable = BitConverter.ToInt32(bytes, 16);
        Array.Clear(bytes, 32, (int)((hashTable - 32) * 0.7));
        File.WriteAllBytes(ArchivePath(), bytes);
        File.SetLastWriteTimeUtc(ArchivePath(), stamp);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(MpqFixture.Compress)]
    public void Rerun_ZeroedDataWithIntactTables_KeepsAllFiles(uint flags)
    {
        var bodies = Bodies();
        BuildFlagged(First, bodies, flags);
        var first = Restore();
        ZeroDataRegion(Second);
        Assert.Null(ArchiveIntegrity.Check(ArchivePath()));   // the tables can't show this damage

        var output = new StringWriter();
        var exit = MpqCli.Run("restore", ["--data", _data, "--out", _out, "--config", ConfigFile()], output, TextWriter.Null);

        AssertRejectedAndIntact(first.Entries, bodies, exit, output.ToString());
    }

    /// <summary>Header, tables and archive size stay intact; every byte of file data, (listfile) and (attributes) included, becomes zero.</summary>
    private void ZeroAllData(DateTime stamp)
    {
        var bytes = File.ReadAllBytes(ArchivePath());
        var hashTable = BitConverter.ToInt32(bytes, 16);
        Array.Clear(bytes, 32, hashTable - 32);
        File.WriteAllBytes(ArchivePath(), bytes);
        File.SetLastWriteTimeUtc(ArchivePath(), stamp);
    }

    /// <summary>Proof A: the replacement has no (listfile), another hash table size and zeroed data. Before, this deleted every file and exited 0.</summary>
    [Fact]
    public void Rerun_UnlistedZeroedReplacement_ChangesNothing()
    {
        var bodies = Bodies();
        BuildFlagged(First, bodies, MpqFixture.Compress);
        var first = Restore();
        File.Delete(ArchivePath());
        using (var f = new MpqFixture(ArchivePath(), listfile: false))
            foreach (var (name, body) in bodies)
                f.AddSectored(name, Encoding.ASCII.GetBytes(body), storedShift: 3, attributesCrc: null);
        ZeroAllData(Second);
        Assert.Null(ArchiveIntegrity.Check(ArchivePath()));

        var output = new StringWriter();
        var exit = MpqCli.Run("restore", ["--data", _data, "--out", _out, "--config", ConfigFile()], output, TextWriter.Null);

        AssertRejectedAndIntact(first.Entries, bodies, exit, output.ToString());
    }

    /// <summary>Proof B: (attributes) and every stored file are zeroed, (listfile) is intact. Before, the zeros read without error and replaced every file as ok.</summary>
    [Fact]
    public void Rerun_AttributesAndStoredDataZeroed_ChangesNothing()
    {
        var bodies = Bodies();
        BuildFlagged(First, bodies, 0);
        var first = Restore();
        var regions = new List<(long Offset, long Length)>();
        using (var m = MpqArchive.Open(ArchivePath()))
            foreach (var e in m.ReadEntries().Entries)
            {
                var name = bodies.Keys.Concat(["(attributes)"]).FirstOrDefault(k => MpqHash.Name1(k) == e.Name1 && MpqHash.Name2(k) == e.Name2);
                if (name is null) continue;
                var at = m.Locate(e, name);
                regions.Add((at.Offset, at.CompressedSize));
            }
        Assert.Equal(bodies.Count + 1, regions.Count);
        var bytes = File.ReadAllBytes(ArchivePath());
        foreach (var (offset, length) in regions) Array.Clear(bytes, (int)offset, (int)length);
        File.WriteAllBytes(ArchivePath(), bytes);
        File.SetLastWriteTimeUtc(ArchivePath(), Second);
        Assert.Null(ArchiveIntegrity.Check(ArchivePath()));

        var output = new StringWriter();
        var exit = MpqCli.Run("restore", ["--data", _data, "--out", _out, "--config", ConfigFile()], output, TextWriter.Null);

        AssertRejectedAndIntact(first.Entries, bodies, exit, output.ToString());
    }

    [Fact]
    public void Rerun_RejectedReplacement_KeepsRowsOfEntriesItNoLongerLists()
    {
        var bodies = Bodies();
        bodies["only-in-old.txt"] = "old";
        BuildFlagged(First, bodies, 0);
        var first = Restore();

        bodies.Remove("only-in-old.txt");
        BuildFlagged(Second, bodies, 0);
        ZeroDataRegion(Second);
        var second = Restore();

        Assert.NotEmpty(second.BrokenArchives);
        Assert.Equal(0, second.Failed);
        Assert.Equal("old", File.ReadAllText(Restored("only-in-old.txt")));
        Assert.Equal("ok", Row("only-in-old.txt")["status"]);
        Assert.Equal(first.Entries, second.Entries);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(MpqFixture.Compress)]
    public void Rerun_GoodReplacementAfterBadOne_Recovers(uint flags)
    {
        var bodies = Bodies();
        BuildFlagged(First, bodies, flags);
        Restore();
        ZeroDataRegion(Second);
        Assert.NotEmpty(Restore().BrokenArchives);
        var third = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        bodies[bodies.Keys.First()] = "a changed body";
        BuildFlagged(third, bodies, flags);
        var good = Restore();

        Assert.Equal(0, good.Failed);
        Assert.Empty(good.BrokenArchives);
        Assert.Equal(new[] { "a.MPQ" }, good.ChangedArchives);
        foreach (var (name, body) in bodies)
            Assert.Equal(body, File.ReadAllText(Restored(name)));
        Assert.Empty(Rows("SELECT * FROM files WHERE status='failed' OR reason IS NOT NULL"));
        Assert.Equal(third.Ticks, RecordedMtime());
        Assert.Null(Assert.Single(Rows("SELECT error FROM archives"))["error"]);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public void Rerun_SameBadArchiveAgain_StillRejectedAndKeepsFiles()
    {
        var bodies = Bodies();
        BuildFlagged(First, bodies, 0);
        Restore();
        ZeroDataRegion(Second);
        Restore();

        var again = Restore();

        Assert.Contains("a.MPQ", Assert.Single(again.BrokenArchives));
        Assert.Empty(again.ChangedArchives);
        Assert.Equal(0, again.Failed);
        foreach (var (name, body) in bodies)
            Assert.Equal(body, File.ReadAllText(Restored(name)));
        Assert.Equal(First.Ticks, RecordedMtime());
    }

    [Fact]
    public void FreshRestore_UnnamedEntryThatCannotBeRead_IsFailedNotUnnamed()
    {
        using (var f = new MpqFixture(ArchivePath(), listfile: false))
        {
            f.Add("named.txt", "named");
            f.AddSectored("secret.bin", Encoding.ASCII.GetBytes(new string('s', 3000)), storedShift: 3, attributesCrc: null);
            f.Add("(listfile)", "named.txt\r\n");
        }
        MpqLocation secret;
        using (var m = MpqArchive.Open(ArchivePath()))
            secret = m.Locate(m.ReadEntries().Entries.Single(e => e.Name1 == MpqHash.Name1("secret.bin")), "secret.bin");
        var bytes = File.ReadAllBytes(ArchivePath());
        Array.Clear(bytes, (int)secret.Offset, (int)secret.CompressedSize);   // its sector table and data
        File.WriteAllBytes(ArchivePath(), bytes);

        var result = Restore();

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Unnamed);
        var row = Assert.Single(Rows("SELECT * FROM files WHERE name IS NULL"));
        Assert.Equal("failed", row["status"]);
        Assert.False(File.Exists(Restored((string)row["path"]!)));
        Assert.Equal("named", File.ReadAllText(Restored("named.txt")));
    }

    [Fact]
    public void FreshRestore_FailedEntry_ExitsNonZero()
    {
        BuildFlagged(First, Bodies(), MpqFixture.Compress);
        ZeroDataRegion(Second);

        var output = new StringWriter();
        var exit = MpqCli.Run("restore", ["--data", _data, "--out", _out, "--config", ConfigFile()], output, TextWriter.Null);

        Assert.NotEqual(0, exit);
        Assert.Contains("a.MPQ", output.ToString());
        Assert.NotEmpty(Rows("SELECT * FROM files WHERE status='failed'"));
    }

    [Fact]
    public void CleanRestore_ExitsZero()
    {
        BuildFlagged(First, Bodies(), MpqFixture.Compress);

        Assert.Equal(0, MpqCli.Run("restore", ["--data", _data, "--out", _out, "--config", ConfigFile()], TextWriter.Null, TextWriter.Null));
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
    public void Rerun_OldManifest_DropsRowsOfEntriesNoLongerInTheArchive()
    {
        // With no recorded identity the archive counts as never finished, so it's restored in place. Rows follow the archive; no file is removed.
        Build(First, ("kept.txt", "kept"), ("gone.txt", "gone"));
        Restore();
        Sql("ALTER TABLE archives DROP COLUMN file_size");
        Sql("ALTER TABLE archives DROP COLUMN file_mtime");

        Build(Second, ("kept.txt", "kept"));
        var result = Restore();

        Assert.Empty(Rows("SELECT * FROM files WHERE path_lc='gone.txt'"));
        Assert.Equal("kept", File.ReadAllText(Restored("kept.txt")));
        Assert.Empty(result.ChangedArchives);
        Assert.Equal(Second.Ticks, RecordedMtime());
    }
}
