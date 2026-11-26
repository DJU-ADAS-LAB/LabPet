using System.Collections.Concurrent;

namespace LabPet.Client.Network;

/// <summary>
/// 키 수 상한과 만료 정리를 갖춘 경량 슬라이딩 윈도우 레이트 리미터.
/// 공격 트래픽으로 인해 버킷 딕셔너리가 무한정 커지지 않도록 보장한다.
/// </summary>
internal sealed class BoundedSlidingRateLimiter
{
    private sealed class Bucket
    {
        public readonly object Sync = new();
        public readonly Queue<long> TimestampsMs = new();
        public long LastAccessMs;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxBuckets;
    private readonly TimeProvider _clock;
    private long _lastPruneMs;

    public BoundedSlidingRateLimiter(int maxBuckets = 512, TimeProvider? clock = null)
    {
        _maxBuckets = Math.Clamp(maxBuckets, 8, 16_384);
        _clock = clock ?? TimeProvider.System;
    }

    public int TrackedBucketCount => _buckets.Count;

    public bool Allow(string key, long windowMs, int limit)
    {
        if (string.IsNullOrWhiteSpace(key) || limit <= 0 || windowMs <= 0)
        {
            return false;
        }

        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var cutoff = nowMs - windowMs;

        if (!_buckets.TryGetValue(key, out var bucket))
        {
            if (_buckets.Count >= _maxBuckets)
            {
                PruneExpired(windowMs, nowMs);
                if (_buckets.Count >= _maxBuckets)
                {
                    EvictOldestOrFail(nowMs);
                    if (_buckets.Count >= _maxBuckets)
                    {
                        return false;
                    }
                }
            }

            bucket = _buckets.GetOrAdd(key, static _ => new Bucket());
        }

        lock (bucket.Sync)
        {
            bucket.LastAccessMs = nowMs;
            while (bucket.TimestampsMs.Count > 0 && bucket.TimestampsMs.Peek() < cutoff)
            {
                bucket.TimestampsMs.Dequeue();
            }

            if (bucket.TimestampsMs.Count >= limit)
            {
                return false;
            }

            bucket.TimestampsMs.Enqueue(nowMs);
            return true;
        }
    }

    public void Prune(long maxWindowMs)
    {
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (nowMs - Interlocked.Read(ref _lastPruneMs) < 1000L && _buckets.Count < _maxBuckets)
        {
            return;
        }

        PruneExpired(maxWindowMs, nowMs);
    }

    public void Clear() => _buckets.Clear();

    private void PruneExpired(long windowMs, long nowMs)
    {
        Interlocked.Exchange(ref _lastPruneMs, nowMs);
        var cutoff = nowMs - Math.Max(1000L, windowMs);

        foreach (var kv in _buckets)
        {
            var shouldRemove = false;
            lock (kv.Value.Sync)
            {
                while (kv.Value.TimestampsMs.Count > 0 && kv.Value.TimestampsMs.Peek() < cutoff)
                {
                    kv.Value.TimestampsMs.Dequeue();
                }

                if (kv.Value.TimestampsMs.Count == 0 && kv.Value.LastAccessMs < cutoff)
                {
                    shouldRemove = true;
                }
            }

            if (shouldRemove)
            {
                _buckets.TryRemove(kv.Key, out _);
            }
        }
    }

    private void EvictOldestOrFail(long nowMs)
    {
        var evicted = 0;
        var targetEvictions = Math.Max(1, _maxBuckets / 8);
        foreach (var kv in _buckets)
        {
            var isIdle = false;
            lock (kv.Value.Sync)
            {
                if (kv.Value.TimestampsMs.Count == 0 || nowMs - kv.Value.LastAccessMs > 10_000L)
                {
                    isIdle = true;
                }
            }

            if (isIdle && _buckets.TryRemove(kv.Key, out _))
            {
                evicted++;
                if (evicted >= targetEvictions)
                {
                    break;
                }
            }
        }
    }
}
