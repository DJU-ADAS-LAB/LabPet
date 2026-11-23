using System.Collections.Concurrent;
using LabPet.Server.Options;

namespace LabPet.Server.Services;

/// <summary>
/// 대기 중인 친구 요청 ID와 발신자/수신자 쌍을 메모리에 임시 추적하여
/// 임의의 사용자가 FriendAccept/Reject를 사칭 중계하지 못하도록 차단한다.
/// </summary>
public sealed class PendingFriendRequestStore(ServerOptions options)
{
    private const int MaxPendingRequests = 4096;

    private sealed record PendingEntry(string RequesterPetId, string TargetPetId, DateTimeOffset ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(Math.Clamp(options.PendingRequestLifetimeSeconds, 30, 600));

    public void RecordRequest(string requestId, string requesterPetId, string targetPetId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_pending.Count >= MaxPendingRequests)
        {
            PruneExpired(now);
        }

        _pending[requestId] = new PendingEntry(requesterPetId, targetPetId, now + _lifetime);
    }

    public bool IsValidResponseTarget(string requestId, string responderPetId, string targetRequesterPetId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !_pending.TryGetValue(requestId, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            _pending.TryRemove(requestId, out _);
            return false;
        }

        return string.Equals(entry.RequesterPetId, targetRequesterPetId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.TargetPetId, responderPetId, StringComparison.OrdinalIgnoreCase);
    }

    public void Remove(string requestId) => _pending.TryRemove(requestId, out _);

    public void PruneExpired(DateTimeOffset now)
    {
        foreach (var kv in _pending)
        {
            if (kv.Value.ExpiresAtUtc < now)
            {
                _pending.TryRemove(kv.Key, out _);
            }
        }
    }
}
