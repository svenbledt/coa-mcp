using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CoaMcp.Server.Tests;

public class PeerNetworkTests
{
    [Fact]
    public void IndexedEnvironmentVariable_Binds()
    {
        var nets = WithEnv("COATEST_Network__PeerNetworks__0", "10.0.0.0/8", cfg => PeerNetworks.Read(cfg));
        Assert.Equal("10.0.0.0/8", Assert.Single(nets).ToString());
    }

    [Fact]
    public void ScalarEnvironmentVariable_AlsoBinds_InsteadOfTrustingNothingSilently()
    {
        var nets = WithEnv("COATEST_Network__PeerNetworks", "10.0.0.0/8, 172.16.0.0/12", cfg => PeerNetworks.Read(cfg));
        Assert.Equal(new[] { "10.0.0.0/8", "172.16.0.0/12" }, nets.Select(n => n.ToString()));
    }

    [Fact]
    public void NoSetting_TrustsNothing() =>
        Assert.Empty(PeerNetworks.Read(new ConfigurationBuilder().Build()));

    [Fact]
    public void BadNetwork_FailsLoudly() =>
        Assert.ThrowsAny<Exception>(() => PeerNetworks.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Network:PeerNetworks:0"] = "not-a-network" }).Build()));

    [Fact]
    public void Startup_LogsThePeerNetworksAndTheRangeCount()
    {
        var log = new ListLogger();
        PeerNetworks.Log(log, PeerNetworks.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Network:PeerNetworks:0"] = "10.0.0.0/8" }).Build()), CloudflareRanges.Default);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("10.0.0.0/8", line.Message);
        Assert.Contains("22", line.Message);
    }

    [Fact]
    public void Startup_WarnsWhenNoPeerNetworkIsSet()
    {
        var log = new ListLogger();
        PeerNetworks.Log(log, PeerNetworks.Read(new ConfigurationBuilder().Build()), CloudflareRanges.Default);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("Network__PeerNetworks__0", line.Message);
    }

    [Fact]
    public async Task RealServer_LogsItsPeerNetworksAtStartup()
    {
        var provider = new CapturingProvider();
        using var h = new ServerHarness(settings: new Dictionary<string, string?> { ["Network:PeerNetworks:0"] = "10.0.0.0/8" },
            logging: b => b.AddProvider(provider));
        using var _ = h.Http();   // starts the host
        await Task.Yield();
        Assert.Contains(provider.Log.Lines, l => l.Level == LogLevel.Information && l.Message.Contains("10.0.0.0/8"));
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
