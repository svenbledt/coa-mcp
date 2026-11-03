using System.Net;

namespace CoaMcp.Server.Tests;

public class LimitTests
{
    [Theory]
    [InlineData(10, 0, 10, 0, false)]
    [InlineData(5000, 0, 100, 0, true)]
    [InlineData(-1, -1, 1, 0, true)]
    [InlineData(long.MinValue, long.MaxValue, 1, Limits.MaxOffset, true)]
    [InlineData(100, 1_000, 100, 1_000, false)]
    [InlineData(100, 10_000, 100, 1_000, true)]
    public void Page_ClampsAndNotes(long limit, long offset, int wantLimit, int wantOffset, bool noted)
    {
        var (l, o, note) = Limits.Page(limit, offset);
        Assert.Equal(wantLimit, l);
        Assert.Equal(wantOffset, o);
        Assert.Equal(noted, note is not null);
    }

    private static async Task<HttpStatusCode> Hit(HttpClient c, string peer, string? xff)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Test-Peer", peer);
        if (xff is not null) req.Headers.Add("X-Forwarded-For", xff);
        req.Headers.Add("Accept", "application/json, text/event-stream");
        using var resp = await c.SendAsync(req);
        return resp.StatusCode;
    }

    private static ServerHarness Limited() => new(settings: new Dictionary<string, string?>
    {
        ["RateLimit:PerMinute"] = "3",
        ["Network:PeerNetworks:0"] = "10.0.0.0/8",
    });

    [Fact]
    public async Task RateLimit_PerRealClientIp()
    {
        using var h = Limited();
        var c = h.Http();
        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.0.5", "198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.0.5", "198.51.100.1"));
        // A different real client behind the same trusted proxy has its own allowance.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.0.5", "198.51.100.2"));
    }

    [Fact]
    public async Task RateLimit_SpoofedHeaderFromUntrustedPeer_Ignored()
    {
        using var h = Limited();
        var c = h.Http();
        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "203.0.113.9", $"198.51.100.{i}"));
        // Same peer, yet another claimed address: still the same client.
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "203.0.113.9", "198.51.100.77"));
    }

    [Fact]
    public async Task RateLimit_DefaultTrustsNoProxy()
    {
        using var h = new ServerHarness(settings: new Dictionary<string, string?> { ["RateLimit:PerMinute"] = "2" });
        var c = h.Http();
        for (var i = 0; i < 2; i++) await Hit(c, "10.0.0.5", $"198.51.100.{i}");
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.0.5", "198.51.100.99"));
    }

    [Fact]
    public async Task Healthz_Returns200WithNoBody_AndIsNotRateLimited()
    {
        using var h = Limited();
        var c = h.Http();
        for (var i = 0; i < 10; i++)
        {
            using var resp = await c.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("", await resp.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task OversizedGarbageBody_IsRejectedCleanly()
    {
        using var h = new ServerHarness();
        var c = h.Http();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(new string('x', 200_000), System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("Accept", "application/json, text/event-stream");
        using var resp = await c.SendAsync(req);
        Assert.True((int)resp.StatusCode is >= 400 and < 500, $"status {(int)resp.StatusCode}");
        Assert.DoesNotContain("   at ", await resp.Content.ReadAsStringAsync());
    }
}
