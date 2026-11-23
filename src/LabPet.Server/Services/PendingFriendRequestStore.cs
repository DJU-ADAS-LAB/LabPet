using System.Collections.Concurrent;
using LabPet.Server.Options;

namespace LabPet.Server.Services;

public enum PendingResponseValidation
{
    Valid = 0,
    AlreadyApproved = 1,
    Expired = 2,
    Unknown = 3
}

/// <summary>
/// 대기 중인 친구 요청 ID와 발신자/수신자 쌍을 메모리에 임시 추적하여
/// 임의의 사용자가 FriendAccept/Reject를 사칭 중계하지 못하도록 차단하고,
/// 만료 여부와 승인 완료 후 ACK 유실 재시도를 구분한다.
/// </summary>
public sealed class PendingFriendRequestStore(ServerOptions options)
{
    private const int MaxPendingRequests = 4096;
    private const int MaxExpiredTombstones = 1024;
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromMinutes(10);

    private sealed class PendingEntry(string requesterPetId, string targetPetId, DateTimeOffset expiresAtUtc)
    {
        public string RequesterPetId { get; } = requesterPetId;
        public string TargetPetId { get; } = targetPetId;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public bool IsApproved { get; set; }
    }

    private sealed record ExpiredEntry(string RequesterPetId, string TargetPetId, DateTimeOffset TombstoneExpiresAtUtc);

    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExpiredEntry> _expiredTombstones = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(Math.Clamp(options.PendingRequestLifetimeSeconds, 30, 600));

    public void RecordRequest(string requestId, string requesterPetId, string targetPetId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_pending.Count >= MaxPendingRequests)
        {
            PruneExpired(now);
        }

        _expiredTombstones.TryRemove(requestId, out _);
        _pending[requestId] = new PendingEntry(requesterPetId, targetPetId, now + _lifetime);
    }

    public PendingResponseValidation ValidateResponseTarget(
        string requestId,
        string responderPetId,
        string targetRequesterPetId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return PendingResponseValidation.Unknown;
        }

        var now = DateTimeOffset.UtcNow;
        if (_pending.TryGetValue(requestId, out var entry))
        {
            var sameParticipants =
                string.Equals(entry.RequesterPetId, targetRequesterPetId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetPetId, responderPetId, StringComparison.OrdinalIgnoreCase);

            if (entry.ExpiresAtUtc < now)
            {
                if (_pending.TryRemove(requestId, out _))
                {
                    RecordTombstone(requestId, entry, now);
                }

                return sameParticipants ? PendingResponseValidation.Expired : PendingResponseValidation.Unknown;
            }

            if (!sameParticipants)
            {
                return PendingResponseValidation.Unknown;
            }

            lock (entry)
            {
                return entry.IsApproved ? PendingResponseValidation.AlreadyApproved : PendingResponseValidation.Valid;
            }
        }

        if (_expiredTombstones.TryGetValue(requestId, out var tombstone))
        {
            if (tombstone.TombstoneExpiresAtUtc < now)
            {
                _expiredTombstones.TryRemove(requestId, out _);
                return PendingResponseValidation.Unknown;
            }

            var sameParticipants =
                string.Equals(tombstone.RequesterPetId, targetRequesterPetId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tombstone.TargetPetId, responderPetId, StringComparison.OrdinalIgnoreCase);
            return sameParticipants ? PendingResponseValidation.Expired : PendingResponseValidation.Unknown;
        }

        return PendingResponseValidation.Unknown;
    }

    public bool IsValidResponseTarget(string requestId, string responderPetId, string targetRequesterPetId)
        => ValidateResponseTarget(requestId, responderPetId, targetRequesterPetId) == PendingResponseValidation.Valid;

    public void MarkApproved(string requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId) && _pending.TryGetValue(requestId, out var entry))
        {
            lock (entry)
            {
                entry.IsApproved = true;
            }
        }
    }

    public void Remove(string requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public void PruneExpired(DateTimeOffset now)
    {
        foreach (var kv in _pending)
        {
            if (kv.Value.ExpiresAtUtc < now && _pending.TryRemove(kv.Key, out var removed))
            {
                RecordTombstone(kv.Key, removed, now);
            }
        }

        foreach (var kv in _expiredTombstones)
        {
            if (kv.Value.TombstoneExpiresAtUtc < now)
            {
                _expiredTombstones.TryRemove(kv.Key, out _);
            }
        }
    }

    private void RecordTombstone(string requestId, PendingEntry entry, DateTimeOffset now)
    {
        if (_expiredTombstones.Count >= MaxExpiredTombstones)
        {
            var excess = _expiredTombstones
                .OrderBy(kv => kv.Value.TombstoneExpiresAtUtc)
                .Take(_expiredTombstones.Count - MaxExpiredTombstones + 16)
                .Select(kv => kv.Key)
                .ToArray();
            foreach (var key in excess)
            {
                _expiredTombstones.TryRemove(key, out _);
            }
        }

        _expiredTombstones[requestId] = new ExpiredEntry(
            entry.RequesterPetId,
            entry.TargetPetId,
            now + TombstoneLifetime);
    }
}
