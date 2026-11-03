using System.Net;
using Microsoft.Extensions.Configuration;

namespace CoaMcp.Server.Tests;

/// <summary>
/// Real path: client, Cloudflare, cloudflared (10.0.1.2), Traefik, this app. Traefik's port is also reachable directly from the internet.
/// Trusted networks are the Coolify network, 10.0.1.0/24; Traefik is 10.0.1.5 here.
/// </summary>
public class ClientIpResolverTests
{
    private static readonly IReadOnlyList<IPNetwork> Trusted = new[] { IPNetwork.Parse("10.0.1.0/24") };
    private static readonly IPAddress Traefik = IPAddress.Parse("10.0.1.5");

    private static string R(string peer, string? xff, string? cf, IReadOnlyList<IPNetwork>? trusted = null) =>
        ClientIpResolver.Resolve(IPAddress.Parse(peer), xff, cf, trusted ?? Trusted).ToString();

    [Fact]
    public void Tunnel_UsesCfConnectingIp()
    {
        // Traefik trusts no forwarded headers, so it replaced X-Forwarded-For with its peer, cloudflared.
        Assert.Equal("198.51.100.7", R("10.0.1.5", "10.0.1.2", "198.51.100.7"));
        // If Traefik kept the chain, every entry is still on the trusted network.
        Assert.Equal("198.51.100.7", R("10.0.1.5", "10.0.1.2, 10.0.1.2", "198.51.100.7"));
    }

    [Fact]
    public void Direct_SpoofedCfConnectingIp_Ignored()
    {
        // Connected straight to Traefik from the internet: Traefik records the real address, whatever the client sent.
        Assert.Equal("203.0.113.9", R("10.0.1.5", "203.0.113.9", "198.51.100.7"));
    }

    [Fact]
    public void Direct_SpoofedForwardedFor_Ignored()
    {
        // The client's own X-Forwarded-For stays on the left; Traefik appends the real address on the right.
        Assert.Equal("203.0.113.9", R("10.0.1.5", "198.51.100.1, 192.0.2.44, 203.0.113.9", null));
        // A spoofed trusted address on the left cannot move the answer either.
        Assert.Equal("203.0.113.9", R("10.0.1.5", "10.0.1.2, 203.0.113.9", "198.51.100.7"));
    }

    [Fact]
    public void UntrustedPeer_AllHeadersIgnored()
    {
        Assert.Equal("203.0.113.9", R("203.0.113.9", "198.51.100.1", "198.51.100.7"));
        Assert.Equal("203.0.113.9", R("203.0.113.9", "10.0.1.2", "198.51.100.7"));
        // With no trusted network configured, nothing is believed.
        Assert.Equal("10.0.1.5", R("10.0.1.5", "198.51.100.1", "198.51.100.7", Array.Empty<IPNetwork>()));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("198.51.100.7, 198.51.100.8")]
    [InlineData("999.1.1.1")]
    public void Tunnel_InvalidCfConnectingIp_FallsBack(string cf) =>
        Assert.Equal("10.0.1.2", R("10.0.1.5", "10.0.1.2", cf));

    [Fact]
    public void Tunnel_MissingCfConnectingIp_FallsBackToTheRightmostAddress() =>
        Assert.Equal("10.0.1.2", R("10.0.1.5", "10.0.1.2", null));

    [Fact]
    public void Tunnel_TwoClients_SeparateBuckets()
    {
        var a = R("10.0.1.5", "10.0.1.2", "198.51.100.7");
        var b = R("10.0.1.5", "10.0.1.2", "198.51.100.8");
        Assert.NotEqual(a, b);
        Assert.NotEqual(R("10.0.1.5", "10.0.1.2", null), a);
    }

    [Fact]
    public void NoForwardedFor_UsesThePeer_EvenWithACfHeader() =>
        Assert.Equal("10.0.1.5", R("10.0.1.5", null, "198.51.100.7"));

    [Fact]
    public void GarbageInTheChain_NeverBecomesTheClient()
    {
        Assert.Equal("203.0.113.9", R("10.0.1.5", "evil, 203.0.113.9", null));
        // Garbage right of the trusted walk: stop at the last good address instead of trusting it.
        Assert.Equal("10.0.1.2", R("10.0.1.5", "198.51.100.1, garbage, 10.0.1.2", "bad"));
        Assert.Equal("203.0.113.9", R("10.0.1.5", "203.0.113.9:4711", null));
        Assert.Equal("2001:db8::9", R("10.0.1.5", "[2001:db8::9]:4711", null));
    }

    [Fact]
    public void Ipv6_AndMappedAddresses_AreNormalised()
    {
        var v6 = new[] { IPNetwork.Parse("fd00::/8"), IPNetwork.Parse("10.0.1.0/24") };
        Assert.Equal("2001:db8::1", R("fd00::5", "fd00::2", "2001:db8::1", v6));
        Assert.Equal("2001:db8::1", R("fd00::5", "2001:db8::1", "2001:db8:ffff::1", v6));
        // Kestrel reports an IPv4 peer as ::ffff:a.b.c.d; it must still match 10.0.1.0/24 and compare equal to the plain form.
        Assert.Equal("198.51.100.7", R("::ffff:10.0.1.5", "::ffff:10.0.1.2", "::ffff:198.51.100.7"));
        Assert.Equal("203.0.113.9", R("::ffff:10.0.1.5", "::ffff:203.0.113.9", null));
        Assert.Equal("fe80::1", R("::ffff:10.0.1.5", "fe80::1%eth0", null));
    }

    [Fact]
    public void ManyForwardedForEntries_AreHandledWithoutTrouble()
    {
        var chain = string.Join(", ", Enumerable.Repeat("10.0.1.2", 5000));
        Assert.Equal("198.51.100.7", R("10.0.1.5", chain, "198.51.100.7"));
    }

    [Fact]
    public void RateKey_GroupsAnIpv6Prefix()
    {
        Assert.Equal(ClientIpResolver.RateKey(IPAddress.Parse("2001:db8:1:2::1")), ClientIpResolver.RateKey(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(ClientIpResolver.RateKey(IPAddress.Parse("2001:db8:1:2::1")), ClientIpResolver.RateKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("198.51.100.7", ClientIpResolver.RateKey(IPAddress.Parse("::ffff:198.51.100.7")));
    }
}

/// <summary>The same rules through the running server: the rate limiter must key on the resolved address.</summary>
public class ClientIpEndToEndTests
{
    private static ServerHarness Server() => new(settings: new Dictionary<string, string?>
    {
        ["RateLimit:PerMinute"] = "3",
        ["Network:TrustedProxies:0"] = "10.0.1.0/24",
    });

    private static async Task<HttpStatusCode> Hit(HttpClient c, string peer, string? xff, string? cf)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Test-Peer", peer);
        if (xff is not null) req.Headers.Add("X-Forwarded-For", xff);
        if (cf is not null) req.Headers.Add("CF-Connecting-IP", cf);
        req.Headers.Add("Accept", "application/json, text/event-stream");
        using var resp = await c.SendAsync(req);
        return resp.StatusCode;
    }

    [Fact]
    public async Task Tunnel_TwoClients_SeparateBuckets()
    {
        using var h = Server();
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.1.5", "10.0.1.2", "198.51.100.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.1.5", "10.0.1.2", "198.51.100.7"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.1.5", "10.0.1.2", "198.51.100.8"));
    }

    [Fact]
    public async Task Direct_SpoofedCfConnectingIp_Ignored()
    {
        using var h = Server();
        var c = h.Http();
        // Same real address, a new forged CF-Connecting-IP each time: one bucket.
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.1.5", "203.0.113.9", $"198.51.100.{i}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.1.5", "203.0.113.9", "198.51.100.99"));
    }

    [Fact]
    public async Task Direct_SpoofedForwardedFor_Ignored()
    {
        using var h = Server();
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.1.5", $"198.51.100.{i}, 203.0.113.9", null));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.1.5", "198.51.100.99, 203.0.113.9", null));
    }

    [Fact]
    public async Task UntrustedPeer_AllHeadersIgnored()
    {
        using var h = Server();
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "203.0.113.9", $"10.0.1.2", $"198.51.100.{i}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "203.0.113.9", "10.0.1.2", "198.51.100.99"));
    }
}
