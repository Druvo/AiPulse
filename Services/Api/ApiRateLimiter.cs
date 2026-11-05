using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace AiPulse.Services.Api;

/// <summary>Configuration section "PublicApi". Defaults suit a small self-hosted instance.</summary>
public sealed class PublicApiOptions
{
    /// <summary>Requests per window for callers without a token, counted per client IP.</summary>
    public int AnonymousPermitLimit { get; set; } = 60;
    /// <summary>Requests per window for a valid personal token, counted per token.</summary>
    public int TokenPermitLimit { get; set; } = 600;
    public int WindowSeconds { get; set; } = 60;
}

public sealed record ApiRateLimitPolicy(string Name, int PermitLimit, TimeSpan Window);

public readonly record struct ApiRateLimitDecision(bool Allowed, int Limit, int Remaining, TimeSpan ResetAfter, string Policy)
{
    /// <summary>Whole seconds until the window resets, never less than 1 (what Retry-After and X-RateLimit-Reset carry).</summary>
    public int ResetSeconds => Math.Max(1, (int)Math.Ceiling(ResetAfter.TotalSeconds));
}

/// <summary>
/// Fixed-window rate limiting per partition ("ip:203.0.113.9" or "token:42") built on the framework's own
/// <see cref="FixedWindowRateLimiter"/> from System.Threading.RateLimiting (the same limiter ASP.NET Core's RateLimiter
/// middleware uses for its fixed-window policies). The middleware itself is not used because it cannot report how many
/// permits remain or when the window resets, which the X-RateLimit-* headers need. Here each partition's limiter runs with
/// AutoReplenishment off and is replenished by this class when its window has elapsed, so the window start is known exactly.
/// Idle partitions are swept periodically so a scan from many addresses cannot grow memory without bound.
/// </summary>
public sealed class ApiRateLimiter : IDisposable
{
    private sealed class Partition
    {
        public required FixedWindowRateLimiter Limiter { get; init; }
        public required ApiRateLimitPolicy Policy { get; init; }
        public long WindowStart;
        public bool Removed;
    }

    private readonly ConcurrentDictionary<string, Partition> _partitions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _lastSweep;
    private int _sweeping;

    public ApiRateLimiter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastSweep = _time.GetTimestamp();
    }

    public int PartitionCount => _partitions.Count;

    public ApiRateLimitDecision Acquire(string partitionKey, ApiRateLimitPolicy policy)
    {
        var now = _time.GetTimestamp();
        MaybeSweep(now);

        var key = policy.Name + "|" + partitionKey;
        while (true)
        {
            var result = TryAcquire(key, policy, now);
            if (result is { } decision) return decision;
        }
    }

    private ApiRateLimitDecision? TryAcquire(string key, ApiRateLimitPolicy policy, long now)
    {
        var partition = _partitions.GetOrAdd(key, _ => new Partition
        {
            Policy = policy,
            WindowStart = now,
            Limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = policy.Window,
                QueueLimit = 0,
                AutoReplenishment = false
            })
        });

        lock (partition)
        {
            // Swept between GetOrAdd and this lock: its limiter is disposed, so start over with a fresh partition.
            if (partition.Removed) return null;
            if (_time.GetElapsedTime(partition.WindowStart, now) >= policy.Window)
            {
                partition.Limiter.TryReplenish();
                partition.WindowStart = now;
            }

            using var lease = partition.Limiter.AttemptAcquire(1);
            var remaining = (int)(partition.Limiter.GetStatistics()?.CurrentAvailablePermits ?? 0);
            var resetAfter = policy.Window - _time.GetElapsedTime(partition.WindowStart, now);
            if (resetAfter < TimeSpan.Zero) resetAfter = TimeSpan.Zero;
            return new ApiRateLimitDecision(lease.IsAcquired, policy.PermitLimit, Math.Max(0, remaining), resetAfter, policy.Name);
        }
    }

    private void MaybeSweep(long now)
    {
        if (_time.GetElapsedTime(Interlocked.Read(ref _lastSweep), now) < TimeSpan.FromMinutes(1)) return;
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            Interlocked.Exchange(ref _lastSweep, now);
            foreach (var (key, partition) in _partitions)
            {
                lock (partition)
                {
                    if (_time.GetElapsedTime(partition.WindowStart, now) < partition.Policy.Window * 2) continue;
                    if (!_partitions.TryRemove(new KeyValuePair<string, Partition>(key, partition))) continue;
                    partition.Removed = true;
                    partition.Limiter.Dispose();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }

    public void Dispose()
    {
        foreach (var p in _partitions.Values) p.Limiter.Dispose();
        _partitions.Clear();
    }
}
