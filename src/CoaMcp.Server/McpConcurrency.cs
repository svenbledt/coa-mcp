using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CoaMcp.Server;

/// <summary>
/// How many /mcp requests run at once across all callers, and how many may wait for a turn. Anything past that gets a 503 at once.
/// It covers /mcp only, so /healthz answers however busy the tools are. Config: Concurrency:MaxRunning and Concurrency:MaxQueued.
/// </summary>
public sealed class McpConcurrency(int maxRunning, int maxQueued) : IRateLimiterPolicy<string>
{
    public const string PolicyName = "mcp-concurrency";
    public const int DefaultMaxRunning = 64;
    public const int DefaultMaxQueued = 32;

    public int MaxRunning { get; } = maxRunning;

    public static McpConcurrency FromConfig(IConfiguration config) => new(
        Math.Max(1, config.GetValue("Concurrency:MaxRunning", DefaultMaxRunning)),
        Math.Max(0, config.GetValue("Concurrency:MaxQueued", DefaultMaxQueued)));

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => (context, _) => McpRequestGuard.RefuseBusy(context.HttpContext);

    public RateLimitPartition<string> GetPartition(HttpContext httpContext) => RateLimitPartition.GetConcurrencyLimiter("mcp", _ =>
        new ConcurrencyLimiterOptions { PermitLimit = MaxRunning, QueueLimit = maxQueued, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
}
