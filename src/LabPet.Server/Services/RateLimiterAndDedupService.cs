using System.Collections.Concurrent;
using LabPet.Server.Options;
using LabPet.Shared.Protocol;

namespace LabPet.Server.Services;

/// <summary>
/// 펫·기능·IP별 슬라이딩 윈도우 요청 제한, 원격 음악 3계층 제한, 바운디드 MessageId 중복 필터.
/// 저사양 VM(e2-micro) 및 Raspberry Pi 보호를 위해 모든 추적 딕셔너리 키 수를 상한 내로 유지한다.
/// </summary>
public sealed class RateLimiterAndDedupService
{
    private const int MaxSeenMessageIdsPerSender = 512;

    private sealed class SlidingRateBucket
    {
        public readonly object Sync = new();
        public readonly Queue<long> TimestampsMs = new();
        public long LastAccessMs;
    }

    private sealed class DedupBucket
    {
        public readonly object Sync = new();
        public readonly Queue<(string MessageId, long SeenAtMs)> Order = new();
        public readonly HashSet<string> Set = new(StringComparer.Ordinal);
        public long LastAccessMs;
    }

    private readonly ServerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _controlRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _featureRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _hubMethodRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _registrationIpRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _registrationGlobalRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _musicSenderRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _musicSubscriberRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlidingRateBucket> _musicGlobalRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DedupBucket> _dedupBySender = new(StringComparer.OrdinalIgnoreCase);

    private readonly int _controlPerSec;
    private readonly int _musicPerSec;
    private readonly long _dedupWindowMs;
    private readonly int _maxTrackedKeys;

    public RateLimiterAndDedupService(ServerOptions options)
        : this(options, TimeProvider.System)
    {
    }

    internal RateLimiterAndDedupService(ServerOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
        _controlPerSec = Math.Clamp(options.ControlRateLimitPerSecond, 5, 100);
        _musicPerSec = Math.Clamp(options.MusicFrameRateLimitPerSecond, 5, 60);
        _dedupWindowMs = Math.Clamp(options.MessageDedupWindowSeconds, 30, 600) * 1000L;
        _maxTrackedKeys = Math.Clamp(options.MaxRateLimitTrackedKeys, 64, 32_768);
    }

    public int TrackedBucketCount =>
        _controlRates.Count + _featureRates.Count + _hubMethodRates.Count +
        _musicSenderRates.Count + _musicSubscriberRates.Count + _dedupBySender.Count;

    public bool AllowControlPacket(string petId)
        => AllowWithinWindow(_controlRates, petId, windowMs: 1000L, limit: _controlPerSec);

    public bool AllowMusicFrame(string petId)
        => AllowWithinWindow(_musicSenderRates, petId, windowMs: 1000L, limit: _musicPerSec);

    public bool AllowHubMethod(string petId, string methodName, long windowMs, int limit)
        => AllowWithinWindow(_hubMethodRates, $"{petId}|{methodName}", windowMs, limit);

    public bool AllowControlFeature(string senderPetId, PacketType packetType)
    {
        return packetType switch
        {
            PacketType.FriendRequest =>
                AllowWithinWindow(_featureRates, $"{senderPetId}|fr", 60_000L, Math.Clamp(_options.FriendRequestPerMinute, 2, 120)),
            PacketType.BubbleMessage or PacketType.QuickReaction =>
                AllowWithinWindow(_featureRates, $"{senderPetId}|msg", 10_000L, Math.Clamp(_options.MessagePer10Seconds, 2, 200)),
            PacketType.PetInteraction =>
                AllowWithinWindow(_featureRates, $"{senderPetId}|act", 10_000L, Math.Clamp(_options.InteractionPer10Seconds, 2, 120)),
            PacketType.GameEvent =>
                AllowWithinWindow(_featureRates, $"{senderPetId}|game", 10_000L, Math.Clamp(_options.GameEventPer10Seconds, 4, 300)),
            PacketType.MusicFollowOn or PacketType.MusicFollowOff =>
                AllowWithinWindow(_featureRates, $"{senderPetId}|musctrl", 10_000L, Math.Clamp(_options.MusicControlPer10Seconds, 2, 120)),
            _ => true
        };
    }

    public bool AllowNewRegistration(string remoteIp)
    {
        var ipKey = string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp.Trim();
        return AllowWithinWindow(_registrationIpRates, ipKey, 3_600_000L, Math.Clamp(_options.NewRegistrationsPerIpPerHour, 2, 2000))
            && AllowWithinWindow(_registrationGlobalRates, "global", 3_600_000L, Math.Clamp(_options.NewRegistrationsGlobalPerHour, 10, 10_000));
    }

    public bool AllowMusicRelay(string senderPetId, string targetPetId)
    {
        var globalLimit = Math.Clamp(_options.MusicFrameGlobalPerSecond, 20, 10_000);
        var senderLimit = Math.Clamp(_options.MusicFramePerSenderPerSecond, 5, 500);
        var subLimit = Math.Clamp(_options.MusicFramePerSubscriberPerSecond, 5, 120);

        return AllowWithinWindow(_musicSubscriberRates, $"{senderPetId}->{targetPetId}", 1000L, subLimit)
            && AllowWithinWindow(_musicSenderRates, senderPetId, 1000L, senderLimit)
            && AllowWithinWindow(_musicGlobalRates, "global", 1000L, globalLimit);
    }

    /// <summary>처음 보는 messageId면 기록 후 true, 중복이면 false.</summary>
    public bool TryMarkMessageSeen(string senderPetId, string messageId)
    {
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var cutoff = nowMs - _dedupWindowMs;

        if (!_dedupBySender.TryGetValue(senderPetId, out var bucket))
        {
            if (_dedupBySender.Count >= _maxTrackedKeys)
            {
                PruneDedupMap(_dedupBySender, cutoff, nowMs);
                if (_dedupBySender.Count >= _maxTrackedKeys)
                {
                    return false;
                }
            }

            bucket = _dedupBySender.GetOrAdd(senderPetId, static _ => new DedupBucket());
        }

        lock (bucket.Sync)
        {
            bucket.LastAccessMs = nowMs;
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
        PruneDedupMap(_dedupBySender, nowMs - _dedupWindowMs, nowMs);
        PruneRateMap(_controlRates, nowMs - 10_000L);
        PruneRateMap(_featureRates, nowMs - 120_000L);
        PruneRateMap(_hubMethodRates, nowMs - 120_000L);
        PruneRateMap(_registrationIpRates, nowMs - 3_600_000L);
        PruneRateMap(_registrationGlobalRates, nowMs - 3_600_000L);
        PruneRateMap(_musicSenderRates, nowMs - 10_000L);
        PruneRateMap(_musicSubscriberRates, nowMs - 10_000L);
        PruneRateMap(_musicGlobalRates, nowMs - 10_000L);
    }

    private bool AllowWithinWindow(
        ConcurrentDictionary<string, SlidingRateBucket> buckets,
        string key,
        long windowMs,
        int limit)
    {
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var cutoff = nowMs - windowMs;

        if (!buckets.TryGetValue(key, out var bucket))
        {
            if (buckets.Count >= _maxTrackedKeys)
            {
                PruneRateMap(buckets, nowMs - Math.Max(windowMs, 10_000L));
                if (buckets.Count >= _maxTrackedKeys)
                {
                    return false;
                }
            }

            bucket = buckets.GetOrAdd(key, static _ => new SlidingRateBucket());
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

    private static void PruneRateMap(ConcurrentDictionary<string, SlidingRateBucket> map, long cutoffMs)
    {
        foreach (var kv in map)
        {
            var remove = false;
            lock (kv.Value.Sync)
            {
                while (kv.Value.TimestampsMs.Count > 0 && kv.Value.TimestampsMs.Peek() < cutoffMs)
                {
                    kv.Value.TimestampsMs.Dequeue();
                }

                if (kv.Value.TimestampsMs.Count == 0 && kv.Value.LastAccessMs < cutoffMs)
                {
                    remove = true;
                }
            }

            if (remove)
            {
                map.TryRemove(kv.Key, out _);
            }
        }
    }

    private void PruneDedupMap(ConcurrentDictionary<string, DedupBucket> map, long cutoffMs, long nowMs)
    {
        foreach (var kv in map)
        {
            var remove = false;
            lock (kv.Value.Sync)
            {
                while (kv.Value.Order.Count > 0 && kv.Value.Order.Peek().SeenAtMs < cutoffMs)
                {
                    var (oldId, _) = kv.Value.Order.Dequeue();
                    kv.Value.Set.Remove(oldId);
                }

                if (kv.Value.Order.Count == 0 && nowMs - kv.Value.LastAccessMs > _dedupWindowMs)
                {
                    remove = true;
                }
            }

            if (remove)
            {
                map.TryRemove(kv.Key, out _);
            }
        }
    }
}
