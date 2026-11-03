using Microsoft.Extensions.Configuration;
using System.Net;

namespace CoaMcp.Server.Tests;

/// <summary>
/// The real path: client, Cloudflare edge, Traefik on public port 443, this app. Captured on the live server:
/// peer 10.0.13.x (Traefik on the app's own network), X-Forwarded-For = Traefik's immediate peer (a Cloudflare edge address),
/// CF-Connecting-IP = the client. Traefik's port is also reachable directly, where any header can be forged.
/// </summary>
public class ClientIpResolverTests
{
    private static readonly IReadOnlyList<IPNetwork> Peers = new[] { IPNetwork.Parse("10.0.0.0/8") };
    private const string EdgeAddress = "172.69.223.47";                    // inside 172.64.0.0/13
    private const string RealClient = "2001:db8:1c19:62a6::1";

    private static string R(string peer, string? xff, string? cf, IReadOnlyList<IPNetwork>? peers = null, CloudflareRanges? ranges = null) =>
        ClientIpResolver.Resolve(IPAddress.Parse(peer), xff, cf, peers ?? Peers, ranges ?? CloudflareRanges.Default).ToString();

    [Fact]
    public void Real_CloudflareRequest_UsesCfConnectingIp() =>
        Assert.Equal(RealClient, R("10.0.13.2", EdgeAddress, RealClient));

    [Fact]
    public void Direct_SpoofedCfConnectingIp_Ignored()
    {
        // Straight to Traefik from the internet: Traefik writes the real address, which is not a Cloudflare address.
        Assert.Equal("203.0.113.9", R("10.0.13.2", "203.0.113.9", RealClient));
        Assert.Equal("203.0.113.9", R("10.0.13.2", "203.0.113.9", "198.51.100.7"));
        Assert.Equal("203.0.113.9", R("10.0.13.2", "203.0.113.9", "198.51.100.8"));
        Assert.Equal("203.0.113.9", R("10.0.13.2", "203.0.113.9", "garbage"));
    }

    [Fact]
    public void Direct_SpoofedLeftXff_Ignored()
    {
        Assert.Equal("203.0.113.9", R("10.0.13.2", "spoof-cf-ip, 203.0.113.9", "198.51.100.7"));
        // A forged Cloudflare-looking address on the left cannot make the request look like it came through Cloudflare.
        Assert.Equal("203.0.113.9", R("10.0.13.2", EdgeAddress + ", 203.0.113.9", "198.51.100.7"));
        Assert.Equal("203.0.113.9", R("10.0.13.2", "198.51.100.1, 192.0.2.44, 203.0.113.9", null));
    }

    [Fact]
    public void Gateway_SharesOneBucket()
    {
        // Direct IPv6 clients reach Traefik through docker-proxy and appear to come from the Docker gateway.
        // That is not a Cloudflare address, so it is the client and the forged CF-Connecting-IP is ignored.
        foreach (var cf in new[] { "198.51.100.7", "198.51.100.8", RealClient, null })
            Assert.Equal("10.0.1.1", R("10.0.13.2", "10.0.1.1", cf));
    }

    [Fact]
    public void UntrustedPeer_AllHeadersIgnored()
    {
        Assert.Equal("203.0.113.9", R("203.0.113.9", EdgeAddress, RealClient));
        Assert.Equal("203.0.113.9", R("203.0.113.9", "10.0.1.1", "198.51.100.7"));
        Assert.Equal("2001:db8::9", R("2001:db8::9", EdgeAddress, RealClient));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("198.51.100.7, 198.51.100.8")]
    [InlineData("999.1.1.1")]
    [InlineData("198.51.100.7/32")]
    public void Cloudflare_InvalidCfConnectingIp_UsesEdgeAddress(string cf) =>
        Assert.Equal(EdgeAddress, R("10.0.13.2", EdgeAddress, cf));

    [Fact]
    public void Cloudflare_MissingCfConnectingIp_UsesEdgeAddress() =>
        Assert.Equal(EdgeAddress, R("10.0.13.2", EdgeAddress, null));

    [Fact]
    public void Cloudflare_Ipv6EdgeAddress_IsRecognised() =>
        Assert.Equal("198.51.100.7", R("10.0.13.2", "2606:4700::1111", "198.51.100.7"));

    [Fact]
    public void DefaultConfig_TrustsNothing()
    {
        var none = PeerNetworks.Read(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Empty(none);
        Assert.Equal("10.0.13.2", R("10.0.13.2", EdgeAddress, RealClient, none));
        Assert.Equal("10.0.13.2", R("10.0.13.2", "203.0.113.9", null, none));
    }

    [Fact]
    public void MissingOrInvalidForwardedFor_UsesThePeer()
    {
        Assert.Equal("10.0.13.2", R("10.0.13.2", null, RealClient));
        Assert.Equal("10.0.13.2", R("10.0.13.2", "", RealClient));
        Assert.Equal("10.0.13.2", R("10.0.13.2", "garbage", RealClient));
        Assert.Equal("10.0.13.2", R("10.0.13.2", "203.0.113.9, garbage", RealClient));
    }

    [Fact]
    public void RightmostEntryWins_EvenWithATrailingComma()
    {
        // "a, b," has an empty last entry; the rightmost real entry is the one Traefik wrote.
        Assert.Equal("203.0.113.9", R("10.0.13.2", "198.51.100.1, 203.0.113.9,", "198.51.100.7"));
    }

    [Fact]
    public void Ports_BracketsAndMappedAddresses_AreNormalised()
    {
        Assert.Equal("203.0.113.9", R("10.0.13.2", "203.0.113.9:4711", null));
        Assert.Equal("2001:db8::9", R("10.0.13.2", "[2001:db8::9]:4711", null));
        Assert.Equal("fe80::1", R("10.0.13.2", "fe80::1%eth0", null));
        // Kestrel reports an IPv4 peer as ::ffff:a.b.c.d.
        Assert.Equal("198.51.100.7", R("::ffff:10.0.13.2", "::ffff:" + EdgeAddress, "::ffff:198.51.100.7"));
        Assert.Equal("203.0.113.9", R("::ffff:10.0.13.2", "::ffff:203.0.113.9", "198.51.100.7"));
    }

    [Fact]
    public void ManyForwardedForEntries_AreHandledWithoutTrouble()
    {
        var chain = string.Join(", ", Enumerable.Repeat("198.51.100.1", 5000)) + ", " + EdgeAddress;
        Assert.Equal(RealClient, R("10.0.13.2", chain, RealClient));
    }

    [Fact]
    public void RateKey_GroupsAnIpv6Prefix()
    {
        Assert.Equal(ClientIpResolver.RateKey(IPAddress.Parse("2001:db8:1:2::1")), ClientIpResolver.RateKey(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(ClientIpResolver.RateKey(IPAddress.Parse("2001:db8:1:2::1")), ClientIpResolver.RateKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("198.51.100.7", ClientIpResolver.RateKey(IPAddress.Parse("::ffff:198.51.100.7")));
    }

    [Fact]
    public void TwoCloudflareClients_AreDifferentKeys()
    {
        var a = ClientIpResolver.RateKey(IPAddress.Parse(R("10.0.13.2", EdgeAddress, "198.51.100.7")));
        var b = ClientIpResolver.RateKey(IPAddress.Parse(R("10.0.13.2", EdgeAddress, "198.51.100.8")));
        Assert.NotEqual(a, b);
    }
}

public class CloudflareRangesTests
{
    [Fact]
    public void Defaults_AreCloudflaresPublishedLists()
    {
        var d = CloudflareRanges.Default;
        Assert.Equal(22, d.Count);   // 15 IPv4 and 7 IPv6 ranges, as published on 2026-11-03
        Assert.True(d.Contains(IPAddress.Parse("172.69.223.47")));
        Assert.True(d.Contains(IPAddress.Parse("104.16.0.1")));
        Assert.True(d.Contains(IPAddress.Parse("2606:4700::1")));
        Assert.True(d.Contains(IPAddress.Parse("::ffff:172.69.223.47")));
        Assert.False(d.Contains(IPAddress.Parse("203.0.113.9")));
        Assert.False(d.Contains(IPAddress.Parse("10.0.1.1")));
        Assert.False(d.Contains(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void ConfigOverridesTheDefaults()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Network:CloudflareRanges:0"] = "203.0.113.0/24" }).Build();
        var r = CloudflareRanges.FromConfig(cfg);
        Assert.Equal(1, r.Count);
        Assert.True(r.Contains(IPAddress.Parse("203.0.113.9")));
        Assert.False(r.Contains(IPAddress.Parse("172.69.223.47")));
        Assert.Same(CloudflareRanges.Default, CloudflareRanges.FromConfig(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
    }

    [Fact]
    public void BadRange_FailsLoudly() =>
        Assert.ThrowsAny<Exception>(() => CloudflareRanges.FromConfig(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Network:CloudflareRanges:0"] = "nope" }).Build()));
}

/// <summary>The same rules through the running server: the rate limiter must key on the resolved address.</summary>
public class ClientIpEndToEndTests
{
    private static ServerHarness Server() => new(settings: new Dictionary<string, string?>
    {
        ["RateLimit:PerMinute"] = "3",
        ["Network:PeerNetworks:0"] = "10.0.0.0/8",
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
    public async Task TwoCloudflareClients_SeparateBuckets()
    {
        using var h = Server();
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "172.69.223.47", "2001:db8:1c19:62a6::1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "172.69.223.47", "2001:db8:1c19:62a6::1"));
        // The second client arrives through the same Cloudflare edge address and the same Traefik.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "172.69.223.47", "198.51.100.8"));
    }

    [Fact]
    public async Task Direct_SpoofedCfConnectingIp_Ignored()
    {
        using var h = Server();
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "203.0.113.9", $"198.51.100.{i}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "203.0.113.9", "198.51.100.99"));
    }

    [Fact]
    public async Task Direct_SpoofedLeftXff_Ignored()
    {
        using var h = Server();
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", $"spoof-{i}, 203.0.113.9", null));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "198.51.100.99, 203.0.113.9", null));
    }

    [Fact]
    public async Task Gateway_SharesOneBucket()
    {
        using var h = Server();
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "10.0.1.1", $"198.51.100.{i}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "10.0.1.1", "198.51.100.99"));
    }

    [Fact]
    public async Task UntrustedPeer_AllHeadersIgnored()
    {
        using var h = Server();
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "203.0.113.9", "172.69.223.47", $"198.51.100.{i}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "203.0.113.9", "172.69.223.47", "198.51.100.99"));
    }

    [Fact]
    public async Task DefaultConfig_TrustsNothing()
    {
        using var h = new ServerHarness(settings: new Dictionary<string, string?> { ["RateLimit:PerMinute"] = "3" });
        var c = h.Http();
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "172.69.223.47", $"198.51.100.{i}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.13.2", "172.69.223.47", "198.51.100.99"));
    }
}
