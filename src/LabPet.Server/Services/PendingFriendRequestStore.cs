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
/// 대기 중인 친구 요청 ID와 발신자/수신자 쌍을 바운디드 메모리(전체·발신자별 상한, TTL)로 추적해
/// 친구 수락/거절 사칭 및 요청 저장소 고갈 공격을 차단한다.
/// </summary>
public sealed class PendingFriendRequestStore(ServerOptions options)
{
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

    private readonly object _sync = new();
    private readonly Dictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingPerSender = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExpiredEntry> _expiredTombstones = new(StringComparer.Ordinal);
    private readonly int _maxPendingRequests = Math.Clamp(options.MaxPendingFriendRequests, 16, 16_384);
    private readonly int _maxPendingPerSender = Math.Clamp(options.MaxPendingFriendRequestsPerSender, 2, 200);
    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(Math.Clamp(options.PendingRequestLifetimeSeconds, 30, 600));

    public int PendingCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    public bool TryRecordRequest(string requestId, string requesterPetId, string targetPetId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (_pending.Count >= _maxPendingRequests
                || (_pendingPerSender.TryGetValue(requesterPetId, out var senderCount) && senderCount >= _maxPendingPerSender))
            {
                PruneExpiredLocked(now);
            }

            if (_pending.Count >= _maxPendingRequests && !_pending.ContainsKey(requestId))
            {
                return false;
            }

            if (!_pending.ContainsKey(requestId)
                && _pendingPerSender.TryGetValue(requesterPetId, out var currentSenderCount)
                && currentSenderCount >= _maxPendingPerSender)
            {
                return false;
            }

            _expiredTombstones.Remove(requestId);
            if (_pending.TryGetValue(requestId, out var previous))
            {
                DecrementSenderLocked(previous.RequesterPetId);
            }

            _pending[requestId] = new PendingEntry(requesterPetId, targetPetId, now + _lifetime);
            _pendingPerSender[requesterPetId] = _pendingPerSender.GetValueOrDefault(requesterPetId) + 1;
            return true;
        }
    }

    public void RecordRequest(string requestId, string requesterPetId, string targetPetId)
        => TryRecordRequest(requestId, requesterPetId, targetPetId);

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
        lock (_sync)
        {
            if (_pending.TryGetValue(requestId, out var entry))
            {
                var sameParticipants =
                    string.Equals(entry.RequesterPetId, targetRequesterPetId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.TargetPetId, responderPetId, StringComparison.OrdinalIgnoreCase);

                if (entry.ExpiresAtUtc < now)
                {
                    RemovePendingLocked(requestId);
                    RecordTombstoneLocked(requestId, entry, now);
                    return sameParticipants ? PendingResponseValidation.Expired : PendingResponseValidation.Unknown;
                }

                if (!sameParticipants)
                {
                    return PendingResponseValidation.Unknown;
                }

                return entry.IsApproved ? PendingResponseValidation.AlreadyApproved : PendingResponseValidation.Valid;
            }

            if (_expiredTombstones.TryGetValue(requestId, out var tombstone))
            {
                if (tombstone.TombstoneExpiresAtUtc < now)
                {
                    _expiredTombstones.Remove(requestId);
                    return PendingResponseValidation.Unknown;
                }

                var sameParticipants =
                    string.Equals(tombstone.RequesterPetId, targetRequesterPetId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(tombstone.TargetPetId, responderPetId, StringComparison.OrdinalIgnoreCase);
                return sameParticipants ? PendingResponseValidation.Expired : PendingResponseValidation.Unknown;
            }

            return PendingResponseValidation.Unknown;
        }
    }

    public bool IsValidResponseTarget(string requestId, string responderPetId, string targetRequesterPetId)
        => ValidateResponseTarget(requestId, responderPetId, targetRequesterPetId) == PendingResponseValidation.Valid;

    public void MarkApproved(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        lock (_sync)
        {
            if (_pending.TryGetValue(requestId, out var entry))
            {
                entry.IsApproved = true;
            }
        }
    }

    public void Remove(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        lock (_sync)
        {
            RemovePendingLocked(requestId);
        }
    }

    public void PruneExpired(DateTimeOffset now)
    {
        lock (_sync)
        {
            PruneExpiredLocked(now);
        }
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        foreach (var kv in _pending.Where(kv => kv.Value.ExpiresAtUtc < now).ToArray())
        {
            if (_pending.Remove(kv.Key, out var removed))
            {
                DecrementSenderLocked(removed.RequesterPetId);
                RecordTombstoneLocked(kv.Key, removed, now);
            }
        }

        foreach (var kv in _expiredTombstones.Where(kv => kv.Value.TombstoneExpiresAtUtc < now).ToArray())
        {
            _expiredTombstones.Remove(kv.Key);
        }
    }

    private void RemovePendingLocked(string requestId)
    {
        if (_pending.Remove(requestId, out var removed))
        {
            DecrementSenderLocked(removed.RequesterPetId);
        }
    }

    private void DecrementSenderLocked(string senderId)
    {
        if (_pendingPerSender.TryGetValue(senderId, out var count))
        {
            if (count <= 1)
            {
                _pendingPerSender.Remove(senderId);
            }
            else
            {
                _pendingPerSender[senderId] = count - 1;
            }
        }
    }

    private void RecordTombstoneLocked(string requestId, PendingEntry entry, DateTimeOffset now)
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
                _expiredTombstones.Remove(key);
            }
        }

        _expiredTombstones[requestId] = new ExpiredEntry(
            entry.RequesterPetId,
            entry.TargetPetId,
            now + TombstoneLifetime);
    }
}
