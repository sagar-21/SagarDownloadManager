namespace DM.Core.RateLimiting;

/// <summary>
/// Thread-safe token-bucket rate limiter shared across concurrent downloads.
///
/// How it works:
///   The bucket holds up to LimitBytesPerSecond tokens (1-second burst capacity).
///   Tokens refill at LimitBytesPerSecond per second.  Before writing N bytes a
///   caller awaits WaitAsync(N), which debits N tokens — blocking if the bucket
///   is empty until enough tokens have accumulated.
///
///   Multiple concurrent downloaders sharing one instance naturally contend for
///   the same pool, so the aggregate throughput never exceeds the configured limit.
///
///   LimitBytesPerSecond == 0 → unlimited; WaitAsync returns immediately.
///   Byte count is clamped to the bucket capacity so a single large read cannot
///   block indefinitely.
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private double   _tokens;
    private DateTime _lastRefill = DateTime.UtcNow;

    public long LimitBytesPerSecond { get; set; }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct = default)
    {
        long limit = LimitBytesPerSecond;
        if (limit <= 0) return;

        // Cap to capacity so a single 80 KB read cannot stall for a huge wait.
        bytes = (int)Math.Min(bytes, limit);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double waitMs;

            await _mutex.WaitAsync(ct);
            try
            {
                Refill(limit);
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                double deficit = bytes - _tokens;
                _tokens = 0;
                waitMs = deficit / limit * 1000.0;
            }
            finally { _mutex.Release(); }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(waitMs, 1, 100)), ct);
        }
    }

    private void Refill(long limit)
    {
        var    now     = DateTime.UtcNow;
        double elapsed = (now - _lastRefill).TotalSeconds;
        _tokens     = Math.Min(limit, _tokens + elapsed * limit);
        _lastRefill = now;
    }
}
