using System.Threading.RateLimiting;

namespace CoaMcp.Server;

/// <summary>
/// The write and undo allowances. There is one token, so one of each, whichever address writes from. Writes check it inside the tool,
/// because only the tool knows a request is a write; the per-IP limit still sits in front of everything. Counted in memory, so a restart resets it.
/// </summary>
public sealed class WriteLimiter : IDisposable
{
    public const int DefaultPerMinute = 60;
    public const int DefaultPerDay = 2_000;

    private readonly Allowance _writes;
    private readonly Allowance _undos;

    public WriteLimiter(int writesPerMinute = DefaultPerMinute, int writesPerDay = DefaultPerDay, int undosPerMinute = DefaultPerMinute, int undosPerDay = DefaultPerDay)
    {
        _writes = new Allowance("Write", writesPerMinute, writesPerDay);
        _undos = new Allowance("Undo", undosPerMinute, undosPerDay);
    }

    /// <returns>Why this write is refused, or null when it may go ahead.</returns>
    public string? WriteRefusal() => _writes.Refusal();

    /// <summary>Undo has its own allowance, because it is the remedy when a runaway writer has used up the other one.</summary>
    public string? UndoRefusal() => _undos.Refusal();

    public void Dispose()
    {
        _writes.Dispose();
        _undos.Dispose();
    }

    private sealed class Allowance(string name, int perMinute, int perDay) : IDisposable
    {
        private readonly FixedWindowRateLimiter _minute = New(perMinute, TimeSpan.FromMinutes(1));
        private readonly FixedWindowRateLimiter _day = New(perDay, TimeSpan.FromDays(1));

        private static FixedWindowRateLimiter New(int permits, TimeSpan window) =>
            new(new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });

        public string? Refusal()
        {
            using var minute = _minute.AttemptAcquire();
            if (!minute.IsAcquired) return $"{name} rate limit reached ({perMinute} per minute). Nothing was changed. Try again in a minute.";
            using var day = _day.AttemptAcquire();
            if (!day.IsAcquired) return $"{name} rate limit reached ({perDay} per day). Nothing was changed. Try again tomorrow.";
            return null;
        }

        public void Dispose()
        {
            _minute.Dispose();
            _day.Dispose();
        }
    }
}
