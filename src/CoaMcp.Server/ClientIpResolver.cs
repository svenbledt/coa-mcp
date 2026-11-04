using System.Net;
using System.Net.Sockets;

namespace CoaMcp.Server;

/// <summary>
/// Works out the real client address for rate limiting. The path is: client, Cloudflare edge, Traefik, this app. Traefik replaces
/// <c>X-Forwarded-For</c> with its immediate peer, and its port is also reachable straight from the internet, where any header can be forged.
/// <list type="number">
/// <item>The peer must be inside <c>Network:PeerNetworks</c> (default none); otherwise every header is ignored and the peer is the client.</item>
/// <item>The rightmost <c>X-Forwarded-For</c> entry is what Traefik wrote. If it is a Cloudflare edge address and <c>CF-Connecting-IP</c>
/// is one valid address, that is the client (Cloudflare sets the header itself and rejects requests that bring their own).</item>
/// <item>Otherwise the rightmost entry is the client: a direct connection, identified by its real address. Missing or invalid: the peer.</item>
/// </list>
/// Addresses are normalised first: IPv4-mapped IPv6 becomes IPv4, ports and zone ids are dropped.
/// </summary>
public static class ClientIpResolver
{
    public static IPAddress Resolve(IPAddress peer, string? forwardedFor, string? cfConnectingIp,
        IReadOnlyList<IPNetwork> peerNetworks, CloudflareRanges cloudflare)
    {
        var self = Normalize(peer);
        if (!InAny(self, peerNetworks)) return self;

        var last = (forwardedFor ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last is null || !TryParse(last, out var edge)) return self;

        if (cloudflare.Contains(edge) && TryParseSingle(cfConnectingIp, out var client)) return client;
        return edge;
    }

    /// <summary>The rate-limit bucket for an address. An IPv6 client can pick any address in its /64, so the whole /64 shares one bucket.</summary>
    public static string RateKey(IPAddress address)
    {
        var a = Normalize(address);
        if (a.AddressFamily != AddressFamily.InterNetworkV6) return a.ToString();
        var bytes = a.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static bool InAny(IPAddress address, IReadOnlyList<IPNetwork> networks)
    {
        foreach (var n in networks)
            if (n.Contains(address) || (address.AddressFamily == AddressFamily.InterNetwork && n.Contains(address.MapToIPv6()))) return true;
        return false;
    }

    private static IPAddress Normalize(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        // A zone id (fe80::1%eth0) says nothing about the client and would split one client into several buckets.
        return a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0 ? new IPAddress(a.GetAddressBytes()) : a;
    }

    private static bool TryParseSingle(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        return !string.IsNullOrWhiteSpace(text) && !text.Contains(',') && TryParse(text.Trim(), out address);
    }

    /// <summary>Accepts "1.2.3.4", "1.2.3.4:80", "2001:db8::1", "[2001:db8::1]" and "[2001:db8::1]:80".</summary>
    private static bool TryParse(string text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text.Length is 0 or > 64) return false;
        var s = text;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) return false;
            if (close + 1 < s.Length && s[close + 1] != ':') return false;
            s = s[1..close];
        }
        else if (s.Count(c => c == ':') == 1)
        {
            s = s[..s.IndexOf(':')];   // IPv4 with a port
        }
        if (!IPAddress.TryParse(s, out var parsed)) return false;
        // TryParse accepts short forms like "1.2.3" or "1"; only complete addresses count.
        if (parsed.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') != 3) return false;
        address = Normalize(parsed);
        return true;
    }
}
