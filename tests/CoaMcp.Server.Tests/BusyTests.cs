using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace CoaMcp.Server.Tests;

/// <summary>Under saturation the server answers: a clean busy message or a 503, never a hang or a fault, and /healthz keeps responding.</summary>
public class BusyTests
{
    private const string SearchCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"libram"}}}""";

    /// <summary>Takes every search slot of the layer until the returned handle is disposed.</summary>
    private static async Task<IAsyncDisposable> HoldSearchSlots(Layer layer)
    {
        var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        var holders = Enumerable.Range(0, 2).Select(_ => Task.Factory.StartNew(() => layer.Searching(() =>
        {
            entered.Signal();
            release.Wait();
            return 0;
        }), TaskCreationOptions.LongRunning)).ToList();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the slots were not taken");
        await Task.Yield();
        return new Held(release, holders);
    }

    private sealed class Held(ManualResetEventSlim release, List<Task<int>> holders) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            release.Set();
            await Task.WhenAll(holders);
            release.Dispose();
        }
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return http.SendAsync(request);
    }

    [Fact]
    public async Task Search_WithEverySearchSlotTaken_AnswersBusy_NotAHang()
    {
        using var h = new ServerHarness();
        await using var client = await h.ConnectAsync();
        using var lease = h.Services.GetRequiredService<LayerSet>().Lease("bisbeard")!;

        await using (await HoldSearchSlots(lease.Layer))
        {
            var clock = Stopwatch.StartNew();
            var (text, isError) = await ServerHarness.CallAsync(client, "search", new() { ["query"] = "libram" }).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(isError);
            Assert.Contains("busy", text);
            Assert.Contains("etry", text);
            Assert.InRange(clock.Elapsed.TotalSeconds, 4, 20);
        }

        var (again, stillError) = await ServerHarness.CallAsync(client, "search", new() { ["query"] = "libram" });
        Assert.False(stillError);
        Assert.Contains("hits", again);
    }

    [Fact]
    public async Task GetMiss_WithEverySearchSlotTaken_StillAnswersNotFound_WithoutWaiting()
    {
        using var h = new ServerHarness();
        await using var client = await h.ConnectAsync();
        using var lease = h.Services.GetRequiredService<LayerSet>().Lease("bisbeard")!;

        await using (await HoldSearchSlots(lease.Layer))
        {
            var clock = Stopwatch.StartNew();
            var (text, isError) = await ServerHarness.CallAsync(client, "get", new() { ["id"] = "item:libram" }).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(isError);
            Assert.Contains("No entity with id", text);
            Assert.True(clock.Elapsed.TotalSeconds < 3, $"the miss waited {clock.Elapsed.TotalSeconds:F1} s");
        }

        var (free, _) = await ServerHarness.CallAsync(client, "get", new() { ["id"] = "item:libram" });
        Assert.Contains("Did you mean", free);   // with slots free, suggestions are as before
    }

    [Fact]
    public async Task Connections_AllInUse_AnswerBusy_NotAHang()
    {
        var root = TestLayers.Build();
        var file = Path.Combine(root, "bisbeard.sqlite");
        using var layer = new Layer(file, TimeSpan.FromMilliseconds(100));
        const string Slow = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 40000000) SELECT COUNT(*) FROM c";
        var slow = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() => layer.Query(Slow), TaskCreationOptions.LongRunning)).ToList();
        await Task.Delay(50);

        var ex = Assert.Throws<LayerBusyException>(() => layer.Get("item:10531"));
        Assert.Contains("busy", ex.Message);

        await Task.WhenAll(slow);
        Assert.NotNull(layer.Get("item:10531"));   // the refused wait left its slot usable
    }

    [Fact]
    public async Task McpAtItsLimit_Refuses503WithAJsonRpcError_WhileHealthzAnswers()
    {
        using var h = new ServerHarness(settings: new Dictionary<string, string?> { ["Concurrency:MaxRunning"] = "2", ["Concurrency:MaxQueued"] = "1", ["Concurrency:QueueWaitSeconds"] = "60" });
        var http = h.Http();
        using var lease = h.Services.GetRequiredService<LayerSet>().Lease("bisbeard")!;

        await using (await HoldSearchSlots(lease.Layer))
        {
            // Two run, blocked on a search slot for 5 seconds. One queues.
            var admitted = Enumerable.Range(0, 3).Select(_ => Post(http, SearchCall)).ToList();
            await Task.Delay(500);

            var clock = Stopwatch.StartNew();
            using var refused = await Post(http, SearchCall);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.True(clock.Elapsed.TotalSeconds < 2, "a refusal must not wait");
            using var doc = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
            var error = doc.RootElement.GetProperty("error");
            Assert.True(error.GetProperty("code").GetInt32() < 0);
            Assert.Contains("busy", error.GetProperty("message").GetString());

            clock.Restart();
            using var health = await http.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.True(clock.Elapsed.TotalSeconds < 2, $"/healthz took {clock.Elapsed.TotalSeconds:F1} s");

            foreach (var a in admitted) Assert.Equal(HttpStatusCode.OK, (await a.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode);
        }

        using var after = await Post(http, SearchCall);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);   // permits come back
    }
}
