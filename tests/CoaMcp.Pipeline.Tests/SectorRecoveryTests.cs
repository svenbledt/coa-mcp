using System.IO.Hashing;
using System.Text;
using CoaMcp.Pipeline.Mpq;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

/// <summary>A real archive's header can claim the wrong sector size. A recovered file must prove itself against (attributes).</summary>
public class SectorRecoveryTests : IDisposable
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2500).Select(i => $"line {i} of the victim\r\n")));

    private readonly TempDir _dir = new();
    private readonly string _data, _out;

    public SectorRecoveryTests()
    {
        _data = _dir.File("Client/Data");
        _out = _dir.File("restored");
        Directory.CreateDirectory(_data);
        Assert.True(Body.Length > 3 * 4096);   // several sectors at the true size
    }

    public void Dispose() => _dir.Dispose();

    private string Archive => Path.Combine(_data, "a.MPQ");
    private string Restored => Path.Combine(_out, "a.MPQ", "victim.bin");

    private MpqFixture Fixture(int headerShift = 5)
    {
        var f = new MpqFixture(Archive, listfile: false, headerShift: headerShift);
        f.Add("(listfile)", "victim.bin\r\n");
        return f;
    }

    private RestoreResult Restore() => MpqRestorer.Run(new RestoreOptions(_data, _out, new ArchiveConfig(
        new Dictionary<string, string> { ["t"] = "fixture" }, [new ArchiveEntry("a.MPQ", 1, "unknown", null, "t")])));

    private Dictionary<string, object?> Victim()
    {
        using var c = new SqliteConnection("Data Source=" + Path.Combine(_out, "manifest.sqlite"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM files WHERE path_lc='victim.bin'";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i));
    }

    [Fact]
    public void WrongHeaderSectorSize_RecoversAndIsCrcChecked()
    {
        var crc = Crc32.HashToUInt32(Body);
        using (var f = Fixture()) f.AddSectored("victim.bin", Body, storedShift: 3, attributesCrc: crc);

        var result = Restore();

        var row = Victim();
        Assert.Equal("ok", row["status"]);
        Assert.Equal("recovered: header sector shift 5 wrong, used 3", row["reason"]);
        Assert.Equal((long)crc, row["crc"]);
        Assert.Equal((long)crc, row["attr_crc"]);
        Assert.Equal(Body, File.ReadAllBytes(Restored));
        Assert.Equal(1, result.Recovered);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void RightSectorSize_NeedsNoRecovery()
    {
        using (var f = Fixture(headerShift: 3)) f.AddSectored("victim.bin", Body, storedShift: 3, attributesCrc: Crc32.HashToUInt32(Body));

        var result = Restore();

        Assert.Equal("ok", Victim()["status"]);
        Assert.Null(Victim()["reason"]);
        Assert.Equal(0, result.Recovered);
        Assert.Equal(Body, File.ReadAllBytes(Restored));
    }

    [Fact]
    public void RightLength_WrongCrc_StaysFailed()
    {
        var wrong = Crc32.HashToUInt32(Body) ^ 0x5A5A5A5A;
        using (var f = Fixture()) f.AddSectored("victim.bin", Body, storedShift: 3, attributesCrc: wrong);

        var result = Restore();

        var row = Victim();
        Assert.Equal("failed", row["status"]);
        Assert.Equal((long)wrong, row["attr_crc"]);
        Assert.Contains("CRC", (string)row["reason"]!);
        Assert.False(File.Exists(Restored));
        Assert.Equal(0, result.Recovered);
    }

    [Fact]
    public void WrongLength_StaysFailed()
    {
        using (var f = Fixture())
            f.AddSectored("victim.bin", Body, storedShift: 3, attributesCrc: Crc32.HashToUInt32(Body), encodeFrom: Body[..^100]);

        Restore();

        Assert.Equal("failed", Victim()["status"]);
        Assert.False(File.Exists(Restored));
    }

    [Fact]
    public void NoCrc_NeverAcceptedOnLengthAlone()
    {
        // StormLib reads this without an error and hands back wrong bytes, so with no CRC only the table shows it.
        using (var f = Fixture()) f.AddSectored("victim.bin", Body, storedShift: 3, attributesCrc: null);

        var result = Restore();

        var row = Victim();
        Assert.Equal("failed", row["status"]);
        Assert.Null(row["attr_crc"]);
        Assert.Contains("no CRC", (string)row["reason"]!);
        Assert.False(File.Exists(Restored));
        Assert.Equal(0, result.Recovered);
    }

    [Fact]
    public void UnsupportedCompression_StaysFailedWithClearReason()
    {
        using (var f = Fixture()) f.AddSectored("victim.bin", Body, storedShift: 3, attributesCrc: Crc32.HashToUInt32(Body), mask: 0x10);

        Restore();

        var row = Victim();
        Assert.Equal("failed", row["status"]);
        Assert.Contains("compression 0x10", (string)row["reason"]!);
        Assert.NotNull(row["attr_crc"]);
    }

    [Fact]
    public void Rerun_KeepsRecoveryNote_AndSkipsTheFile()
    {
        using (var f = Fixture()) f.AddSectored("victim.bin", Body, storedShift: 3, attributesCrc: Crc32.HashToUInt32(Body));
        Restore();
        var stamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Restored, stamp);

        var second = Restore();

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Restored));
        Assert.Equal("recovered: header sector shift 5 wrong, used 3", Victim()["reason"]);
        Assert.Equal(1, second.Recovered);
    }

    [Fact]
    public void Recovery_LeavesTheArchiveUnchanged()
    {
        using (var f = Fixture()) f.AddSectored("victim.bin", Body, storedShift: 3, attributesCrc: Crc32.HashToUInt32(Body));
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Archive, stamp);
        var before = File.ReadAllBytes(Archive);
        File.SetAttributes(Archive, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(1, Restore().Recovered);
        }
        finally { File.SetAttributes(Archive, FileAttributes.Normal); }

        Assert.Equal(before, File.ReadAllBytes(Archive));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Archive));
    }

    [Theory]
    [InlineData(new uint[] { 3, 16 })]            // table shorter than itself
    [InlineData(new uint[] { 8, 4 })]             // offsets go backwards
    [InlineData(new uint[] { 8, 900000 })]        // past the end of the file's data
    public void HostileSectorTable_ReturnsReasonNotFault(uint[] table)
    {
        var bytes = new byte[64];
        Buffer.BlockCopy(table, 0, bytes, 0, table.Length * 4);

        var decoded = SectorReader.Decode(bytes, 100, MpqArchive.FlagExists | SectorReader.FlagCompress, 3, out var why);

        Assert.Null(decoded);
        Assert.False(string.IsNullOrEmpty(why));
    }
}
