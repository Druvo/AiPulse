using System.Threading.RateLimiting;

namespace AiPulse.Services.Security;

/// <summary>
/// "At most N per window, per key" for actions that arrive over a Blazor circuit (registration, password-reset requests) and
/// so never pass through the HTTP rate limiter. Built on the framework's fixed-window limiter; in memory, per process.
/// </summary>
public sealed class AttemptThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public AttemptThrottle(int permitLimit, TimeSpan window)
    {
        _limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    }

    /// <summary>True (and counts the attempt) when <paramref name="key"/> is still under its limit.</summary>
    public bool TryAcquire(string key)
    {
        using var lease = _limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
