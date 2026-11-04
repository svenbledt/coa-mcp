using System.Text;
using System.Text.RegularExpressions;
using CoaMcp.Core;
using CoaMcp.Pipeline.Bisbeard;
using CoaMcp.Pipeline.Publish;

namespace CoaMcp.Pipeline.Tests;

public class LayerVersionTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string Build(string file, string? version = null)
    {
        var path = _dir.File(file);
        BisbeardImport.Run(Path.Combine(Fixtures, "data"), Path.Combine(Fixtures, "guides.json"), path, version);
        return path;
    }

    private static string VersionIn(string path)
    {
        using var r = new LayerReader(path);
        return r.Meta.Version;
    }

    [Fact]
    public void DefaultVersion_IsTheUtcBuildTime()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var v = VersionIn(Build("a.sqlite"));
        Assert.Matches(@"^\d{14}$", v);
        var stamp = DateTime.ParseExact(v, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(stamp, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void VersionOption_OverridesTheDefault()
    {
        Assert.Equal("2026-hotfix.1", VersionIn(Build("a.sqlite", "2026-hotfix.1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("1;rm")]
    public void BadVersion_IsRefusedBeforeAnythingIsWritten(string version)
    {
        var path = _dir.File("bad.sqlite");
        Assert.Throws<InvalidOperationException>(() =>
            BisbeardImport.Run(Path.Combine(Fixtures, "data"), Path.Combine(Fixtures, "guides.json"), path, version));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task TwoBuilds_GetDistinctVersions_AndBothPublish()
    {
        var first = Build("first.sqlite");
        await Task.Delay(1100);   // versions have one-second resolution
        var second = Build("second.sqlite");
        Assert.NotEqual(VersionIn(first), VersionIn(second));

        // Each rebuild differs byte-wise (built_at), so a fixed version would be refused as "same version, different content".
        var remote = new FakeRemote();
        var publisher = new Publisher(remote, new StringWriter());
        await publisher.PublishAsync(first, false, default);
        await publisher.PublishAsync(second, false, default);

        var manifest = Manifest.Parse(Encoding.UTF8.GetString(remote.Files["/data/coamcp/current.json"]));
        Assert.Equal(VersionIn(second), manifest.Layers["bisbeard"].Version);
        Assert.True(remote.Files.ContainsKey($"/data/coamcp/layers/bisbeard/{VersionIn(first)}.sqlite"), "the previous version stays for rollback");
    }
}
