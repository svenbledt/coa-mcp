using System.Net;

namespace CoaMcp.Server;

/// <summary>
/// The networks the app's direct peer (Traefik) can be on. Read from <c>Network:PeerNetworks</c>, either as indexed entries
/// (<c>Network__PeerNetworks__0=10.0.0.0/8</c>) or as one list. Only a peer inside them is believed about forwarded headers;
/// the default is none, which trusts nothing. See <see cref="ClientIpResolver"/>.
/// </summary>
public static class PeerNetworks
{
    /// <summary>A malformed network throws, so a typo stops the server instead of silently trusting nothing.</summary>
    public static IReadOnlyList<IPNetwork> Read(IConfiguration config) =>
        ConfigList.Read(config, "Network:PeerNetworks").Select(IPNetwork.Parse).ToList();

    public static void Log(ILogger log, IReadOnlyList<IPNetwork> peers, CloudflareRanges cloudflare)
    {
        if (peers.Count == 0)
            log.LogWarning("No peer networks are configured (Network__PeerNetworks__0 is empty). Forwarded headers are ignored, " +
                           "so behind a reverse proxy every client shares the proxy's address and one rate limit.");
        else
            log.LogInformation("Believing forwarded client addresses from peers in: {Peers}; {Count} Cloudflare ranges are known.",
                string.Join(", ", peers), cloudflare.Count);
    }
}
