using System.Net;
using System.Net.Sockets;

namespace CoaMcp.Server;

/// <summary>
/// Works out the real client address for rate limiting. The path is: client, Cloudflare, cloudflared, Traefik, this app,
/// and Traefik can also be reached straight from the internet, so a header is only believed when the hop that wrote it is trusted.
/// <list type="number">
/// <item>The peer must be in the trusted networks (<c>Network:TrustedProxies</c>); otherwise every header is ignored.</item>
/// <item>Walk <c>X-Forwarded-For</c> from the right while entries are trusted. The first untrusted address is the client:
/// Traefik wrote the rightmost entry, so a direct request resolves to its real public address whatever the client put further left.</item>
/// <item>If only trusted addresses were found, the request came through cloudflared (Traefik replaces the header with its peer).
/// Then <c>CF-Connecting-IP</c> is used if it is one valid address; otherwise the rightmost address.</item>
/// </list>
/// Addresses are normalised first: IPv4-mapped IPv6 becomes IPv4, ports and zone ids are dropped.
/// </summary>
public static class ClientIpResolver
{
    public static IPAddress Resolve(IPAddress peer, string? forwardedFor, string? cfConnectingIp, IReadOnlyList<IPNetwork> trusted)
    {
        var self = Normalize(peer);
        if (trusted.Count == 0 || !IsTrusted(self, trusted)) return self;

        var chain = (forwardedFor ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (chain.Length == 0) return self;

        IPAddress? rightmost = null;
        for (var i = chain.Length - 1; i >= 0; i--)
        {
            if (!TryParse(chain[i], out var address))
                return rightmost ?? self;   // garbage is never the client; stop at the last address a trusted hop vouched for
            if (!IsTrusted(address, trusted)) return address;
            rightmost ??= address;
        }

        // Every entry is a trusted hop: the request came through cloudflared.
        return TryParseSingle(cfConnectingIp, out var cf) ? cf : rightmost ?? self;
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

    private static bool IsTrusted(IPAddress address, IReadOnlyList<IPNetwork> trusted)
    {
        foreach (var n in trusted)
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
