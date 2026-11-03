using System.Net;

namespace CoaMcp.Server;

/// <summary>
/// The networks whose peers may vouch for a client address (X-Forwarded-For, and CF-Connecting-IP behind cloudflared); see <see cref="ClientIpResolver"/>. Read from <c>Network:TrustedProxies</c>, either as indexed
/// entries (<c>Network__TrustedProxies__0=10.0.1.0/24</c>) or as one list (<c>Network__TrustedProxies=10.0.1.0/24,172.16.0.0/12</c>).
/// </summary>
public static class TrustedProxies
{
    /// <summary>A malformed network throws, so a typo stops the server instead of silently trusting nothing.</summary>
    public static IReadOnlyList<IPNetwork> Read(IConfiguration config) =>
        Entries(config, "Network:TrustedProxies").Select(IPNetwork.Parse).ToList();

    /// <summary>
    /// Exact addresses that are never trusted, even inside a trusted network (<c>Network:UntrustedAddresses</c>, same two forms).
    /// For Docker's gateway: direct IPv6 clients appear to come from it, so it must count as the client.
    /// </summary>
    public static IReadOnlyList<IPAddress> ReadUntrusted(IConfiguration config) =>
        Entries(config, "Network:UntrustedAddresses").Select(e =>
        {
            var a = IPAddress.Parse(e);   // a network or a name throws, so a typo stops the server
            return a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
        }).ToList();

    private static List<string> Entries(IConfiguration config, string key)
    {
        var section = config.GetSection(key);
        var entries = new List<string>();
        if (!string.IsNullOrWhiteSpace(section.Value))
            entries.AddRange(section.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        entries.AddRange(section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
        return entries;
    }

    public static void Log(ILogger log, IReadOnlyList<IPNetwork> networks, IReadOnlyList<IPAddress>? untrusted = null)
    {
        if (networks.Count == 0)
            log.LogWarning("No trusted proxy networks are configured (Network__TrustedProxies__0 is empty). X-Forwarded-For is ignored, " +
                           "so behind a reverse proxy every client shares the proxy's address and one rate limit.");
        else
            log.LogInformation("Trusting forwarded client addresses (X-Forwarded-For, CF-Connecting-IP) from peers in: {Networks}; never trusting: {Untrusted}",
                string.Join(", ", networks), untrusted is { Count: > 0 } ? string.Join(", ", untrusted) : "(none)");
    }
}
