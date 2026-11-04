using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CoaMcp.Server;

/// <summary>
/// The caps on /mcp. Each client may run a few requests at once, so one client can't take the capacity of all. Across all clients a limited number run,
/// and a few may wait a bounded time for a turn. Past the client cap a request gets a 429 at once; past the shared cap, or the wait, a 503.
/// None of this covers /healthz, so it answers however busy the tools are.
/// Config: Concurrency:MaxPerClient, Concurrency:MaxRunning, Concurrency:MaxQueued and Concurrency:QueueWaitSeconds.
/// </summary>
public sealed class McpConcurrency(int maxPerClient, int maxRunning, int maxQueued, TimeSpan queueWait) : IRateLimiterPolicy<string>
{
    public const string PolicyName = "mcp-concurrency";
    public const int DefaultMaxPerClient = 8;
    public const int DefaultMaxRunning = 64;
    public const int DefaultMaxQueued = 32;
    public const int DefaultQueueWaitSeconds = 5;

    public int MaxRunning { get; } = maxRunning;

    public static McpConcurrency FromConfig(IConfiguration config) => new(
        Math.Max(1, config.GetValue("Concurrency:MaxPerClient", DefaultMaxPerClient)),
        Math.Max(1, config.GetValue("Concurrency:MaxRunning", DefaultMaxRunning)),
        Math.Max(0, config.GetValue("Concurrency:MaxQueued", DefaultMaxQueued)),
        TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Concurrency:QueueWaitSeconds", DefaultQueueWaitSeconds))));

    /// <summary>The bucket a request counts against, for every per-client limit.</summary>
    public static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip ? ClientIpResolver.RateKey(ip) : "unknown";

    /// <summary>The per-client cap, for the global limiter, which runs before the shared cap. It never queues: waiting would hold a connection for nothing.</summary>
    public RateLimitPartition<string> PerClient(HttpContext ctx) => ctx.Request.Path.StartsWithSegments("/mcp")
        ? RateLimitPartition.GetConcurrencyLimiter(ClientKey(ctx), _ => new ConcurrencyLimiterOptions { PermitLimit = maxPerClient, QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("not-mcp");

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => (context, _) => McpRequestGuard.RefuseBusy(context.HttpContext);

    public RateLimitPartition<string> GetPartition(HttpContext httpContext) => RateLimitPartition.Get("mcp", _ => new BoundedWaitLimiter(
        new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = MaxRunning, QueueLimit = maxQueued, QueueProcessingOrder = QueueProcessingOrder.OldestFirst }), queueWait));

    /// <summary>A limiter whose queue has a time limit, which ConcurrencyLimiter's doesn't: a wait that runs out is a refusal.</summary>
    private sealed class BoundedWaitLimiter(RateLimiter inner, TimeSpan wait) : RateLimiter
    {
        public override TimeSpan? IdleDuration => inner.IdleDuration;
        public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics();
        protected override RateLimitLease AttemptAcquireCore(int permitCount) => inner.AttemptAcquire(permitCount);

        protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(wait);
            try
            {
                return await inner.AcquireAsync(permitCount, limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Refused();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private sealed class Refused : RateLimitLease
        {
            public override bool IsAcquired => false;
            public override IEnumerable<string> MetadataNames => [];
            public override bool TryGetMetadata(string metadataName, out object? metadata) { metadata = null; return false; }
        }
    }
}
