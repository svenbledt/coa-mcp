using System.Net;
using System.Net.Sockets;

namespace CoaMcp.Server;

/// <summary>
/// Cloudflare's published edge address ranges. A request whose last hop before Traefik was one of these came through Cloudflare,
/// which sets <c>CF-Connecting-IP</c> itself and refuses requests that bring their own.
/// Override with <c>Network:CloudflareRanges</c> (indexed or one list) when Cloudflare changes them.
/// </summary>
public sealed class CloudflareRanges
{
    // Fetched from https://www.cloudflare.com/ips-v4 and https://www.cloudflare.com/ips-v6 on 2026-11-03.
    private static readonly string[] Published =
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
        "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
    };

    public static readonly CloudflareRanges Default = new(Published.Select(IPNetwork.Parse).ToList());

    private readonly IReadOnlyList<IPNetwork> _ranges;

    private CloudflareRanges(IReadOnlyList<IPNetwork> ranges) => _ranges = ranges;

    public int Count => _ranges.Count;

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var r in _ranges)
            if (r.Contains(address)) return true;
        return false;
    }

    /// <summary>The configured list if there is one, otherwise <see cref="Default"/>. A malformed range throws, so a typo stops the server.</summary>
    public static CloudflareRanges FromConfig(IConfiguration config)
    {
        var entries = ConfigList.Read(config, "Network:CloudflareRanges");
        return entries.Count == 0 ? Default : new CloudflareRanges(entries.Select(IPNetwork.Parse).ToList());
    }
}

/// <summary>Reads a list setting given either as indexed entries (<c>Key__0=a</c>) or as one value split on commas, semicolons and spaces.</summary>
internal static class ConfigList
{
    public static List<string> Read(IConfiguration config, string key)
    {
        var section = config.GetSection(key);
        var entries = new List<string>();
        if (!string.IsNullOrWhiteSpace(section.Value))
            entries.AddRange(section.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        entries.AddRange(section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
        return entries;
    }
}
