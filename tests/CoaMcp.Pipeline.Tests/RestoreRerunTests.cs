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

    [Fact]
    public void Rerun_ArchiveGoneFromDisk_DropsItsRowsAndFiles()
    {
        BuildNamed("a.MPQ", First, ("a.txt", "a"));
        BuildNamed("sub/b.MPQ", First, ("b.txt", "b"));
        var first = Restore("a.MPQ", "sub/b.MPQ");

        File.Delete(ArchivePath("sub/b.MPQ"));
        var second = Restore("a.MPQ");

        Assert.False(Directory.Exists(Restored("", "sub/b.MPQ")));
        Assert.Empty(Rows("SELECT * FROM files WHERE archive='sub/b.MPQ'"));
        Assert.Empty(Rows("SELECT * FROM archives WHERE archive='sub/b.MPQ'"));
        Assert.Empty(Rows("SELECT * FROM listfile_names WHERE archive='sub/b.MPQ'"));
        Assert.Equal("a", File.ReadAllText(Restored("a.txt")));
        Assert.True(second.Entries < first.Entries);
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
