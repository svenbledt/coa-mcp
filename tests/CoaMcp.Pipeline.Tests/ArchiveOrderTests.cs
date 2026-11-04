using CoaMcp.Pipeline.Mpq;

namespace CoaMcp.Pipeline.Tests;

public class ArchiveOrderTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static ArchiveConfig RealConfig() =>
        ArchiveConfig.Load(Path.Combine(AppContext.BaseDirectory, "config", "archives.json"));

    private string DataWith(params string[] relPaths)
    {
        var data = _dir.File("Data");
        foreach (var rel in relPaths)
        {
            var full = Path.Combine(data, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, [1]);
        }
        return data;
    }

    [Fact]
    public void Order_LaterPatchBeatsBase()
    {
        var order = ArchiveOrder.Resolve(DataWith("common.MPQ", "patch.MPQ", "patch-A.MPQ", "patch-2.MPQ"), RealConfig());

        Assert.Equal(new[] { "patch-A.MPQ", "patch-2.MPQ", "patch.MPQ", "common.MPQ" }, order.Select(a => a.RelPath));
        Assert.True(order[0].Priority > order[3].Priority);
    }

    [Fact]
    public void Order_RealmArchiveHandled()
    {
        var order = ArchiveOrder.Resolve(DataWith("area-52/patch-D.MPQ", "patch-ZG.MPQ", "common.MPQ"), RealConfig());

        var realm = order[0];
        Assert.Equal("area-52/patch-D.MPQ", realm.RelPath);
        Assert.Equal("area-52", realm.Realm);
        Assert.Equal(0x200, realm.Priority);
        Assert.All(order.Skip(1), a => Assert.Null(a.Realm));
    }

    [Fact]
    public void Order_EveryArchiveOnDiskIsClassified()
    {
        var data = DataWith("common.MPQ", "enUS/patch-new.mpq");

        var ex = Assert.Throws<InvalidDataException>(() => ArchiveOrder.Resolve(data, RealConfig()));
        Assert.Contains("enUS/patch-new.mpq", ex.Message);
    }

    [Fact]
    public void Order_NoArchives_IsAnError()
    {
        var data = _dir.File("EmptyData");
        Directory.CreateDirectory(data);

        var ex = Assert.Throws<InvalidDataException>(() => ArchiveOrder.Resolve(data, RealConfig()));
        Assert.Contains("no archives", ex.Message);
    }

    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Order_UnreadableFolder_FailsLoudly()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The deny rule this test sets is Windows-only.");
        var data = DataWith("common.MPQ", "enUS/locale-enUS.MPQ");
        var denied = new DirectoryInfo(Path.Combine(data, "enUS"));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var acl = denied.GetAccessControl();
        acl.AddAccessRule(rule);
        denied.SetAccessControl(acl);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => ArchiveOrder.Resolve(data, RealConfig()));
        }
        finally
        {
            acl.RemoveAccessRule(rule);
            denied.SetAccessControl(acl);
        }
    }

    [Fact]
    public void Order_ArchiveNamesMatchIgnoringCase()
    {
        var order = ArchiveOrder.Resolve(DataWith("patch-p.MPQ"), RealConfig());
        Assert.Equal("patch-P.mpq", Assert.Single(order).RelPath);
    }

    [Fact]
    public void Order_OnlyProjectArchivesAreProject()
    {
        var config = RealConfig();
        Assert.Equal(new[] { "patch-B.MPQ" },
            config.Archives.Where(a => a.Origin == "project").Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(config.Archives.Where(a => a.Origin != "project"), a => Assert.Equal("unknown", a.Origin));
        Assert.Contains("patch-T.MPQ: shipped in the project's client package, unchanged since revision 7; who built it is unconfirmed", config.Rules["origin"]);
    }

    [Fact]
    public void Order_RealConfig_FollowsClientSortRule()
    {
        // Client: Data\patch-*.MPQ and Data\enUS\patch-enUS-?.MPQ sorted by full path, opened ascending from
        // 66; patch-enUS.MPQ then patch.MPQ take 64 and 65 (Client.cpp:1420-1429, PatchFiles.cpp:33-36,75).
        var patches = RealConfig().Archives.Where(a => a.Priority >= 66 && a.Realm is null).ToList();
        var sorted = patches.Select(a => a.Path)
            .OrderBy(p => @"Data\" + p.Replace('/', '\\'), StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, patches.OrderBy(a => a.Priority).Select(a => a.Path));
        Assert.Equal(Enumerable.Range(66, patches.Count), patches.Select(a => a.Priority).OrderBy(p => p));
        Assert.Equal("enUS/patch-enUS-2.MPQ", sorted[0]);
        Assert.Equal("patch-ZG.MPQ", sorted[^1]);

        var byPath = RealConfig().Archives.ToDictionary(a => a.Path);
        Assert.Equal(64, byPath["enUS/patch-enUS.MPQ"].Priority);
        Assert.Equal(65, byPath["patch.MPQ"].Priority);
    }

    [Fact]
    public void Order_RealConfig_BaseTableSlots()
    {
        // basePriority starts at 63 and drops once per s_archives entry, present or not (Client.cpp:1465,1508).
        var byPath = RealConfig().Archives.ToDictionary(a => a.Path);
        Assert.Equal(49, byPath["expansion.MPQ"].Priority);
        Assert.Equal(48, byPath["lichking.MPQ"].Priority);
        Assert.Equal(47, byPath["common.MPQ"].Priority);
        Assert.Equal(46, byPath["common-2.MPQ"].Priority);
        Assert.Equal(40, byPath["enUS/lichking-speech-enUS.MPQ"].Priority);
    }

    [Fact]
    public void Order_ArchivesTheClientNeverOpens_RankBelowEverything()
    {
        var order = ArchiveOrder.Resolve(DataWith("enUS/base-enUS.MPQ", "enUS/backup-enUS.MPQ", "enUS/lichking-speech-enUS.MPQ"), RealConfig());

        Assert.Equal("enUS/lichking-speech-enUS.MPQ", order[0].RelPath);
        Assert.True(order[1].Priority < 0 && order[2].Priority < 0);
    }

    [Fact]
    public void Config_EveryArchiveCitesEvidence()
    {
        var config = RealConfig();
        Assert.Equal(78, config.Archives.Count);
        Assert.Equal(78, config.Archives.Select(a => a.Priority).Distinct().Count());
        Assert.All(config.Archives, a => Assert.True(config.Rules.ContainsKey(a.Rule), a.Path + " cites unknown rule " + a.Rule));
        Assert.All(config.Rules.Values, r => Assert.Matches(@"\.cpp:\d+", r));
    }

    [Theory]
    [InlineData("""{"rules":{},"archives":[{"path":"a.MPQ","priority":1,"origin":"mystery","realm":null,"rule":"r"}]}""")]
    [InlineData("""{"rules":{"r":"x"},"archives":[{"path":"a.MPQ","priority":1,"origin":"unknown","realm":null,"rule":"r"},{"path":"b.MPQ","priority":1,"origin":"unknown","realm":null,"rule":"r"}]}""")]
    [InlineData("""{"rules":{"r":"x"},"archives":[{"path":"..\\a.MPQ","priority":1,"origin":"unknown","realm":null,"rule":"r"}]}""")]
    public void Config_Malformed_Rejected(string json)
    {
        var path = _dir.File("archives.json");
        File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => ArchiveConfig.Load(path));
    }
}
