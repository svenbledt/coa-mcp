using System.Diagnostics;

namespace CoaMcp.Server.Tests;

public class LayerConcurrencyTests
{
    private const string Slow = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 40000000) SELECT COUNT(*) FROM c";

    [Fact]
    public async Task Get_IsNotBlockedWhileOtherQueriesRunOnTheSameLayer()
    {
        var root = TestLayers.Publish(TestLayers.Build());
        using var layers = LayerSet.Open(root);
        using var lease = layers.Lease("bisbeard")!;
        var layer = lease.Layer;

        var slow = Enumerable.Range(0, 3).Select(_ => Task.Run(() => layer.Query(Slow))).ToList();
        await Task.Delay(100);
        var sw = Stopwatch.StartNew();
        var found = layers.Get("item:10531");
        sw.Stop();
        var stillRunning = slow.Count(t => !t.IsCompleted);

        Assert.Single(found);
        Assert.True(stillRunning > 0, "the slow queries finished too early to prove anything");
        Assert.True(sw.ElapsedMilliseconds < 500, $"get waited {sw.ElapsedMilliseconds} ms behind slow queries");
        await Task.WhenAll(slow);
    }
}
