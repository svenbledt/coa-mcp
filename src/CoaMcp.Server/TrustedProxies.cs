using System.Net;

namespace CoaMcp.Server;

/// <summary>
/// The networks whose X-Forwarded-For header is believed. Read from <c>Network:TrustedProxies</c>, either as indexed
/// entries (<c>Network__TrustedProxies__0=10.0.1.0/24</c>) or as one list (<c>Network__TrustedProxies=10.0.1.0/24,172.16.0.0/12</c>).
/// </summary>
public static class TrustedProxies
{
    /// <summary>A malformed network throws, so a typo stops the server instead of silently trusting nothing.</summary>
    public static IReadOnlyList<IPNetwork> Read(IConfiguration config)
    {
        var section = config.GetSection("Network:TrustedProxies");
        var entries = new List<string>();
        if (!string.IsNullOrWhiteSpace(section.Value))
            entries.AddRange(section.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        entries.AddRange(section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
        return entries.Select(IPNetwork.Parse).ToList();
    }

    public static void Log(ILogger log, IReadOnlyList<IPNetwork> networks)
    {
        if (networks.Count == 0)
            log.LogWarning("No trusted proxy networks are configured (Network__TrustedProxies__0 is empty). X-Forwarded-For is ignored, " +
                           "so behind a reverse proxy every client shares the proxy's address and one rate limit.");
        else
            log.LogInformation("Trusting X-Forwarded-For from: {Networks}", string.Join(", ", networks));
    }
}
