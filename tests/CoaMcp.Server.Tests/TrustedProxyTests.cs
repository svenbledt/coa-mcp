using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoaMcp.Server.Tests;

public class TrustedProxyTests
{
    private static async Task<HttpStatusCode> Hit(HttpClient c, string peer, string xff)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Test-Peer", peer);
        req.Headers.Add("X-Forwarded-For", xff);
        req.Headers.Add("Accept", "application/json, text/event-stream");
        using var resp = await c.SendAsync(req);
        return resp.StatusCode;
    }

    [Fact]
    public async Task ChainedHeaderThroughTrustedProxy_KeysTheLimitOnTheRealClient()
    {
        using var h = new ServerHarness(settings: new Dictionary<string, string?>
        {
            ["RateLimit:PerMinute"] = "3",
            ["Network:TrustedProxies:0"] = "10.0.0.0/8",
        });
        var c = h.Http();
        // The client forges "spoof" on the left; the proxy appends the real address on the right.
        // A different forged value each time must not give the client a fresh allowance.
        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.0.5", $"203.0.113.{i}, 198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.0.5", "203.0.113.99, 198.51.100.1"));
        // Another real client behind the same proxy is unaffected, whatever it forges.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await Hit(c, "10.0.0.5", "203.0.113.99, 198.51.100.2"));
    }

    [Fact]
    public void IndexedEnvironmentVariable_Binds()
    {
        var nets = WithEnv("COATEST_Network__TrustedProxies__0", "10.0.1.0/24", cfg => TrustedProxies.Read(cfg));
        Assert.Equal("10.0.1.0/24", Assert.Single(nets).ToString());
    }

    [Fact]
    public void ScalarEnvironmentVariable_AlsoBinds_InsteadOfTrustingNothingSilently()
    {
        var nets = WithEnv("COATEST_Network__TrustedProxies", "10.0.1.0/24, 172.16.0.0/12", cfg => TrustedProxies.Read(cfg));
        Assert.Equal(new[] { "10.0.1.0/24", "172.16.0.0/12" }, nets.Select(n => n.ToString()));
    }

    [Fact]
    public void UntrustedAddresses_ReadFromIndexedAndScalarForms()
    {
        var indexed = WithEnv("COATEST_Network__UntrustedAddresses__0", "10.0.1.1", cfg => TrustedProxies.ReadUntrusted(cfg));
        Assert.Equal("10.0.1.1", Assert.Single(indexed).ToString());
        var scalar = WithEnv("COATEST_Network__UntrustedAddresses", "10.0.1.1, ::ffff:10.0.1.9", cfg => TrustedProxies.ReadUntrusted(cfg));
        Assert.Equal(new[] { "10.0.1.1", "10.0.1.9" }, scalar.Select(a => a.ToString()));
        Assert.Empty(TrustedProxies.ReadUntrusted(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void BadUntrustedAddress_FailsLoudly() =>
        Assert.ThrowsAny<Exception>(() => TrustedProxies.ReadUntrusted(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Network:UntrustedAddresses:0"] = "10.0.1.0/24" }).Build()));

    [Fact]
    public void Startup_LogsTheUntrustedAddresses()
    {
        var log = new ListLogger();
        TrustedProxies.Log(log, new[] { IPNetwork.Parse("10.0.1.0/24") }, new[] { IPAddress.Parse("10.0.1.1") });
        var line = Assert.Single(log.Lines);
        Assert.Contains("10.0.1.0/24", line.Message);
        Assert.Contains("10.0.1.1", line.Message);
        Assert.Contains("never", line.Message);
    }

    [Fact]
    public void NoSetting_TrustsNothing() =>
        Assert.Empty(TrustedProxies.Read(new ConfigurationBuilder().Build()));

    [Fact]
    public void BadNetwork_FailsLoudly() =>
        Assert.ThrowsAny<Exception>(() => TrustedProxies.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Network:TrustedProxies:0"] = "not-a-network" }).Build()));

    [Fact]
    public void Startup_LogsTheTrustedNetworks()
    {
        var log = new ListLogger();
        TrustedProxies.Log(log, TrustedProxies.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Network:TrustedProxies:0"] = "10.0.1.0/24" }).Build()));
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("10.0.1.0/24", line.Message);
    }

    [Fact]
    public void Startup_WarnsWhenNoNetworkIsTrusted()
    {
        var log = new ListLogger();
        TrustedProxies.Log(log, TrustedProxies.Read(new ConfigurationBuilder().Build()));
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("Network__TrustedProxies__0", line.Message);
    }

    [Fact]
    public async Task RealServer_LogsItsTrustedNetworksAtStartup()
    {
        var provider = new CapturingProvider();
        using var h = new ServerHarness(settings: new Dictionary<string, string?> { ["Network:TrustedProxies:0"] = "10.0.1.0/24" },
            logging: b => b.AddProvider(provider));
        using var _ = h.Http();   // starts the host
        await Task.Yield();
        Assert.Contains(provider.Log.Lines, l => l.Level == LogLevel.Information && l.Message.Contains("10.0.1.0/24"));
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ListLogger Log { get; } = new();
        public ILogger CreateLogger(string categoryName) => Log;
        public void Dispose() { }
    }

    private static T WithEnv<T>(string name, string value, Func<IConfiguration, T> read)
    {
        Environment.SetEnvironmentVariable(name, value);
        try { return read(new ConfigurationBuilder().AddEnvironmentVariables("COATEST_").Build()); }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
}
