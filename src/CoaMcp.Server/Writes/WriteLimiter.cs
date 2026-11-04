using System.Threading.RateLimiting;

namespace CoaMcp.Server;

/// <summary>
/// The write allowance. There is one token, so one allowance, whichever address writes from. Writes check it inside the tool,
/// because only the tool knows a request is a write; the per-IP limit still sits in front of everything. Counted in memory, so a restart resets it.
/// </summary>
public sealed class WriteLimiter : IDisposable
{
    public const int DefaultPerMinute = 60;
    public const int DefaultPerDay = 2_000;

    private readonly int _perMinute;
    private readonly int _perDay;
    private readonly FixedWindowRateLimiter _minute;
    private readonly FixedWindowRateLimiter _day;

    public WriteLimiter(int perMinute = DefaultPerMinute, int perDay = DefaultPerDay)
    {
        _perMinute = perMinute;
        _perDay = perDay;
        _minute = New(perMinute, TimeSpan.FromMinutes(1));
        _day = New(perDay, TimeSpan.FromDays(1));
    }

    private static FixedWindowRateLimiter New(int permits, TimeSpan window) =>
        new(new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });

    /// <returns>Why this write is refused, or null when it may go ahead.</returns>
    public string? Refusal()
    {
        using var minute = _minute.AttemptAcquire();
        if (!minute.IsAcquired) return $"Write rate limit reached ({_perMinute} per minute). Nothing was written. Try again in a minute.";
        using var day = _day.AttemptAcquire();
        if (!day.IsAcquired) return $"Write rate limit reached ({_perDay} per day). Nothing was written. Try again tomorrow.";
        return null;
    }

    public void Dispose()
    {
        _minute.Dispose();
        _day.Dispose();
    }
}
