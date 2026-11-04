using System.Diagnostics;
using CoaMcp.Core;
using ModelContextProtocol.Client;

namespace CoaMcp.Server.Tests;

/// <summary>A large spells layer whose broad words match every row, to keep search cost and lock sharing in check.</summary>
public sealed class BigLayerFixture : IAsyncLifetime
{
    public const int Rows = 60_000;
    public ServerHarness Harness { get; private set; } = null!;
    public McpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dir = TestLayers.Build(withSpells: false);
        using (var w = new LayerWriter(Path.Combine(dir, "spells.sqlite"), "spells", "1"))
        {
            for (var i = 0; i < Rows; i++)
                w.Add(new Entity($"spell:{i + 1000}", "spell", i == 5 ? "Damage" : $"Spell number {i}",
                    new RealmSet(new[] { "coa-client" }, false), Trust.Client, "CoA client DBC (via Spellwork)", TestLayers.Date,
                    $"## summary\nspell damage text {i}\n", null, Array.Empty<Link>()));
            w.Finish();
        }
        Harness = new ServerHarness(dir);
        Client = await Harness.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
        Harness.Dispose();
    }
}

public class SearchBudgetTests : IClassFixture<BigLayerFixture>
{
    private readonly BigLayerFixture _f;
    public SearchBudgetTests(BigLayerFixture f) => _f = f;

    private Task<(string Text, bool IsError)> Call(string tool, Dictionary<string, object?> args) =>
        ServerHarness.CallAsync(_f.Client, tool, args);

    [Theory]
    [InlineData("spell", null, 0)]
    [InlineData("damage", "classes", 0)]
    [InlineData("spell", null, 1000)]
    [InlineData("damage", "spells", 500)]
    public async Task Search_BroadMatch_StaysWithinBudget(string query, string? topic, int offset)
    {
        await Call("search", new() { ["query"] = "warmup" });
        var sw = Stopwatch.StartNew();
        var (text, isError) = await Call("search", new() { ["query"] = query, ["topic"] = topic, ["offset"] = offset, ["limit"] = 100 });
        sw.Stop();
        Assert.False(isError);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{query}/{topic}/{offset} took {sw.ElapsedMilliseconds} ms");
        if (topic == "classes") Assert.Contains("0 hits", text); // the spells layer holds no class rows
    }

    [Fact]
    public async Task Search_ExactTitleStillFirst_OnLargeLayer()
    {
        var (text, _) = await Call("search", new() { ["query"] = "damage" });
        Assert.Matches(@"(?m)^1\. .*\| spell:1005\]", text);
    }

    [Fact]
    public async Task Get_IsNotBlockedBySlowSearches()
    {
        using var stop = new CancellationTokenSource();
        var searches = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await Call("search", new() { ["query"] = "spell damage", ["limit"] = 100, ["offset"] = 900 });
        })).ToList();

        await Task.Delay(100);
        var worst = 0L;
        for (var i = 0; i < 30; i++)
        {
            var sw = Stopwatch.StartNew();
            var (text, _) = await Call("get", new() { ["id"] = "item:10531" });
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
            Assert.Contains("Epic Libram", text);
        }
        stop.Cancel();
        await Task.WhenAll(searches);
        Assert.True(worst < 1000, $"slowest get took {worst} ms while searches ran");
    }
}
