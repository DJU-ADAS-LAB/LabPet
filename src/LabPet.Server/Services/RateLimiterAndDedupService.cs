using System.Collections.Concurrent;
using LabPet.Server.Options;

namespace LabPet.Server.Services;

/// <summary>
/// 펫별 전송 빈도 제한(Rate Limit)과 바운디드 MessageId 중복 수신 방지 필터.
/// 저사양 VM(e2-micro) 보호를 위해 모든 상태는 고정 상한 내 슬라이딩 윈도우로 유지한다.
/// </summary>
public sealed class RateLimiterAndDedupService(ServerOptions options)
{
    private const int MaxSeenMessageIdsPerSender = 512;

    private sealed class SlidingRateBucket
    {
        public readonly object Sync = new();
        public readonly Queue<long> TimestampsMs = new();
    }

    private sealed class DedupBucket
    {
        public readonly object Sync = new();
        public readonly Queue<(string MessageId, long SeenAtMs)> Order = new();
        public readonly HashSet<string> Set = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, SlidingRateBucket> _controlRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _musicRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DedupBucket> _dedupBySender = new(StringComparer.OrdinalIgnoreCase);

    private readonly int _controlPerSec = Math.Clamp(options.ControlRateLimitPerSecond, 5, 100);
    private readonly int _musicPerSec = Math.Clamp(options.MusicFrameRateLimitPerSecond, 5, 60);
    private readonly long _dedupWindowMs = Math.Clamp(options.MessageDedupWindowSeconds, 30, 600) * 1000L;

    public bool AllowControlPacket(string petId)
        => AllowWithinWindow(_controlRates, petId, windowMs: 1000L, limit: _controlPerSec);

    public bool AllowMusicFrame(string petId)
        => AllowWithinWindow(_musicRates, petId, windowMs: 1000L, limit: _musicPerSec);

    /// <summary>처음 보는 messageId면 기록 후 true, 중복이면 false.</summary>
    public bool TryMarkMessageSeen(string senderPetId, string messageId)
    {
        var bucket = _dedupBySender.GetOrAdd(senderPetId, _ => new DedupBucket());
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff = nowMs - _dedupWindowMs;

        lock (bucket.Sync)
        {
            while (bucket.Order.Count > 0 && (bucket.Order.Peek().SeenAtMs < cutoff || bucket.Order.Count >= MaxSeenMessageIdsPerSender))
            {
                var (oldId, _) = bucket.Order.Dequeue();
                bucket.Set.Remove(oldId);
            }

            if (!bucket.Set.Add(messageId))
            {
                return false;
            }

            bucket.Order.Enqueue((messageId, nowMs));
            return true;
        }
    }

    public void PruneExpired(DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var cutoff = nowMs - _dedupWindowMs;

        foreach (var kv in _dedupBySender)
        {
            lock (kv.Value.Sync)
            {
                while (kv.Value.Order.Count > 0 && kv.Value.Order.Peek().SeenAtMs < cutoff)
                {
                    var (oldId, _) = kv.Value.Order.Dequeue();
                    kv.Value.Set.Remove(oldId);
                }
            }
        }
    }

    private static bool AllowWithinWindow(
        ConcurrentDictionary<string, SlidingRateBucket> buckets,
        string petId,
        long windowMs,
        int limit)
    {
        var bucket = buckets.GetOrAdd(petId, _ => new SlidingRateBucket());
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff = nowMs - windowMs;

        lock (bucket.Sync)
        {
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
}
