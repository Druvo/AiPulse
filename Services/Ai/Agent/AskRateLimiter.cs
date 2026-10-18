using System.Collections.Concurrent;

namespace AiPulse.Services.Ai.Agent;

/// <summary>Rolling-window limiter keyed by person, so one account cannot run up the model bill. In memory: a restart resets it.</summary>
public sealed class AskRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    /// <summary>Records a use and returns true if the caller is under the limit; otherwise returns false and how long until a slot frees up.</summary>
    public bool TryAcquire(string key, int limit, TimeSpan window, DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (limit <= 0) return true;

        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= window)
                queue.Dequeue();

            if (queue.Count >= limit)
            {
                retryAfter = window - (now - queue.Peek());
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
