using System.Collections.Concurrent;
using System.Diagnostics;
using CoaMcp.Core;
using Microsoft.Extensions.Logging;

namespace CoaMcp.Server.Tests;

/// <summary>Collects log lines so a test can prove an error was logged.</summary>
public sealed class ListLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " | " + exception.Message)));
    public IReadOnlyList<string> Errors => Lines.Where(l => l.Level >= LogLevel.Error).Select(l => l.Message).ToList();
}

public class LayerSwapTests : IDisposable
{
    private const string Slow = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 30000000) SELECT COUNT(*) AS n FROM c";
    private readonly string _root = TestLayers.Publish(TestLayers.Build());
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "coamcp-swap-" + Guid.NewGuid().ToString("N"));
    private readonly ListLogger _log = new();

    public LayerSwapTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { _root, _scratch })
            try { Directory.Delete(d, true); } catch (IOException) { /* temp data */ }
    }

    private LayerSet Open() => LayerSet.Open(_root, _log, retireGrace: TimeSpan.Zero);

    private string NewBisbeard(string version, Action<LayerWriter>? extra = null)
    {
        var path = Path.Combine(_scratch, $"bisbeard-{version}-{Guid.NewGuid():N}.sqlite");
        TestLayers.BuildBisbeard(path, version, extra);
        return path;
    }

    private static string VersionOf(LayerSet s, string name) => s.Current(name)!.Meta.Version;

    [Fact]
    public void Open_LoadsTheVersionsNamedInCurrentJson()
    {
        using var layers = Open();
        Assert.Equal("7", VersionOf(layers, "bisbeard"));
        Assert.Equal("3", VersionOf(layers, "spells"));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Open_WithoutCurrentJson_ReportsEveryLayerMissing_AndLogsAnError()
    {
        File.Delete(Path.Combine(_root, "current.json"));
        using var layers = Open();
        Assert.All(layers.Status(), s => { Assert.Null(s.Layer); Assert.Contains("missing", s.Problem); });
        Assert.NotEmpty(_log.Errors);
    }

    [Fact]
    public void Open_ReadsLayerFilesThatAreReadOnlyOnDisk()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(_root, "layers"), "*.sqlite", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.ReadOnly);
        try
        {
            using var layers = Open();
            Assert.Single(layers.Get("item:10531"));
            Assert.Empty(_log.Errors);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.Combine(_root, "layers"), "*.sqlite", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Swap_NewVersionIsServedAfterReload()
    {
        using var layers = Open();
        var v8 = NewBisbeard("8", w => w.Add(new Entity("item:424242", "item", "Brand New Item", new RealmSet(new[] { "coa" }, true),
            Trust.Derived, "BisBeard", TestLayers.Date, "Only in version eight.\n", null, Array.Empty<Link>())));
        Assert.Empty(layers.Get("item:424242"));

        TestLayers.PublishFile(_root, "bisbeard", v8);
        layers.Reload();

        Assert.Equal("8", VersionOf(layers, "bisbeard"));
        Assert.Single(layers.Get("item:424242"));
        Assert.Equal("3", VersionOf(layers, "spells"));
    }

    [Fact]
    public void Reload_WithNothingNew_KeepsTheSameLayerObjects()
    {
        using var layers = Open();
        var before = layers.Current("bisbeard");
        layers.Reload();
        Assert.Same(before, layers.Current("bisbeard"));
    }

    [Fact]
    public async Task Swap_InFlightRequestSurvives()
    {
        using var layers = Open();
        var old = layers.Current("bisbeard")!;
        var inFlight = Task.Run(() => old.Query(Slow));
        await WaitUntil(() => old.ActiveRequests > 0);

        TestLayers.PublishFile(_root, "bisbeard", NewBisbeard("8"));
        layers.Reload();

        Assert.Equal("8", VersionOf(layers, "bisbeard"));
        Assert.False(old.IsDisposed, "the old layer was disposed under a running request");
        Assert.False(inFlight.IsCompleted, "the slow query ended before the swap, so this proves nothing");
        var rows = await inFlight;
        Assert.Equal(30_000_000L, rows[0]["n"]);
        await WaitUntil(() => old.IsDisposed);
        Assert.True(old.IsDisposed, "the old layer should be released once its last request finished");
    }

    [Fact]
    public void Swap_OldLayerWithNoRequestsIsReleasedAtOnce()
    {
        using var layers = Open();
        var old = layers.Current("bisbeard")!;
        TestLayers.PublishFile(_root, "bisbeard", NewBisbeard("8"));
        layers.Reload();
        Assert.True(old.IsDisposed);
    }

    [Fact]
    public void Swap_TruncatedLayer_KeepsOldVersion_AndLogsAnError()
    {
        using var layers = Open();
        var good = NewBisbeard("8");
        var bytes = File.ReadAllBytes(good);
        var truncated = Path.Combine(_scratch, "truncated.sqlite");
        File.WriteAllBytes(truncated, bytes.AsSpan(0, bytes.Length / 3).ToArray());
        TestLayers.PublishFile(_root, "bisbeard", truncated, version: "8");   // sha256 matches the truncated file on purpose

        layers.Reload();

        Assert.Equal("7", VersionOf(layers, "bisbeard"));
        Assert.Single(layers.Get("item:10531"));
        var err = Assert.Single(_log.Errors);
        Assert.Contains("bisbeard", err);
        Assert.Contains("8", err);
    }

    [Fact]
    public void Swap_ShaMismatch_KeepsOldVersion_AndLogsAnError()
    {
        using var layers = Open();
        var file = TestLayers.PublishFile(_root, "bisbeard", NewBisbeard("8"));
        // Flip a byte after publishing: the file no longer matches the checksum in current.json.
        var path = Path.Combine(_root, file.File);
        var b = File.ReadAllBytes(path);
        b[b.Length - 10] ^= 0xFF;
        File.WriteAllBytes(path, b);

        layers.Reload();

        Assert.Equal("7", VersionOf(layers, "bisbeard"));
        Assert.Contains("sha256", Assert.Single(_log.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Swap_MissingLayerFile_KeepsOldVersion()
    {
        using var layers = Open();
        var entry = TestLayers.PublishFile(_root, "bisbeard", NewBisbeard("8"));
        File.Delete(Path.Combine(_root, entry.File));

        layers.Reload();

        Assert.Equal("7", VersionOf(layers, "bisbeard"));
        Assert.Contains("bisbeard", Assert.Single(_log.Errors));
    }

    [Fact]
    public void Swap_MetaDisagreeingWithCurrentJson_KeepsOldVersion()
    {
        using var layers = Open();
        // The file says version 9 inside; current.json claims 8.
        TestLayers.PublishFile(_root, "bisbeard", NewBisbeard("9"), version: "8");
        layers.Reload();
        Assert.Equal("7", VersionOf(layers, "bisbeard"));
        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Swap_FailedCandidate_IsReportedInStatus_AndNotRetriedEveryReload()
    {
        using var layers = Open();
        var entry = TestLayers.PublishFile(_root, "bisbeard", NewBisbeard("8"));
        File.Delete(Path.Combine(_root, entry.File));
        layers.Reload();
        layers.Reload();
        layers.Reload();

        Assert.Single(_log.Errors);
        var state = layers.Status().Single(s => s.Name == "bisbeard");
        Assert.NotNull(state.Notice);
        Assert.Contains("8", state.Notice);
        Assert.DoesNotContain(_root, state.Notice);   // no server paths in answers
    }

    [Fact]
    public void Swap_FailedCandidateThenFixedByANewVersion_Recovers()
    {
        using var layers = Open();
        var broken = TestLayers.PublishFile(_root, "bisbeard", NewBisbeard("8"));
        File.Delete(Path.Combine(_root, broken.File));
        layers.Reload();
        TestLayers.PublishFile(_root, "bisbeard", NewBisbeard("9"));
        layers.Reload();
        Assert.Equal("9", VersionOf(layers, "bisbeard"));
        Assert.Null(layers.Status().Single(s => s.Name == "bisbeard").Notice);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"layers":{"bisbeard":{"version":"8","file":"../../x","sha256":"00"}}}""")]
    public void Reload_MalformedCurrentJson_KeepsWhatIsLoaded_AndLogsAnError(string text)
    {
        using var layers = Open();
        File.WriteAllText(Path.Combine(_root, "current.json"), text);
        layers.Reload();
        Assert.Equal("7", VersionOf(layers, "bisbeard"));
        Assert.Equal("3", VersionOf(layers, "spells"));
        Assert.NotEmpty(_log.Errors);
    }

    [Fact]
    public void Reload_CurrentJsonDeleted_KeepsWhatIsLoaded_AndLogsAnError()
    {
        using var layers = Open();
        File.Delete(Path.Combine(_root, "current.json"));
        layers.Reload();
        Assert.Equal("7", VersionOf(layers, "bisbeard"));
        Assert.NotEmpty(_log.Errors);
    }

    [Fact]
    public async Task Reloader_PicksUpANewVersionOnItsOwn_AndStatusShowsIt()
    {
        using var h = new ServerHarness(settings: new Dictionary<string, string?> { ["Layers:ReloadSeconds"] = "1" });
        await using var client = await h.ConnectAsync();
        var (before, _) = await ServerHarness.CallAsync(client, "status");
        Assert.Contains("bisbeard: v7", before);

        TestLayers.PublishFile(h.Root, "bisbeard", NewBisbeard("8"));
        string after = before;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && !after.Contains("bisbeard: v8"))
        {
            await Task.Delay(250);
            (after, _) = await ServerHarness.CallAsync(client, "status");
        }
        Assert.Contains("bisbeard: v8", after);
    }

    private static async Task WaitUntil(Func<bool> cond)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
    }
}
