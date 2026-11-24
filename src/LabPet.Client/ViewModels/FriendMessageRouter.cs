using LabPet.Client.Social;

namespace LabPet.Client.ViewModels;

public enum FriendMessageRouteOutcome
{
    Dropped = 0,
    DuplicateIgnored = 1,
    Visitor = 2,
    Host = 3
}

/// <summary>
/// 수신한 친구 메시지를 말풍선으로 표시할 수 있는 창 대상(방문 펫 또는 내 펫).
/// </summary>
public interface IMessageBubbleTarget
{
    bool IsAvailableForMessages { get; }
    bool TryShowFriendMessage(ReceivedFriendMessage message);
    bool TryShowFriendStatus(string text, int durationMs, bool animate);
}

/// <summary>
/// 수신한 친구 메시지를 발신자 PetId 기준으로 단일 대상(방문 펫 우선, 없으면 내 펫)에 라우팅하고
/// 동일 메시지의 중복 표시를 차단한다.
/// </summary>
public sealed class FriendMessageRouter
{
    private readonly Func<string, IMessageBubbleTarget?> _resolveVisitor;
    private readonly Func<IMessageBubbleTarget?> _resolveHost;
    private readonly int _maxDedupEntries;
    private readonly TimeSpan _dedupTtl;
    private readonly TimeProvider _timeProvider;
    private readonly object _dedupLock = new();
    private readonly Queue<(string Key, DateTimeOffset ExpiresAtUtc)> _dedupQueue = new();
    private readonly Dictionary<string, DateTimeOffset> _dedupKeys = new(StringComparer.Ordinal);

    public FriendMessageRouter(
        Func<string, IMessageBubbleTarget?> resolveVisitor,
        Func<IMessageBubbleTarget?> resolveHost,
        int maxDedupEntries = 256,
        TimeSpan? dedupTtl = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(resolveVisitor);
        ArgumentNullException.ThrowIfNull(resolveHost);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDedupEntries, 1);

        _resolveVisitor = resolveVisitor;
        _resolveHost = resolveHost;
        _maxDedupEntries = maxDedupEntries;
        _dedupTtl = dedupTtl ?? TimeSpan.FromMinutes(2);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public FriendMessageRouteOutcome Route(ReceivedFriendMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!TryReserveMessageId(message.SenderPetId, message.MessageId))
        {
            return FriendMessageRouteOutcome.DuplicateIgnored;
        }

        if (!string.IsNullOrWhiteSpace(message.SenderPetId))
        {
            var visitor = _resolveVisitor(message.SenderPetId);
            if (visitor is { IsAvailableForMessages: true } && visitor.TryShowFriendMessage(message))
            {
                return FriendMessageRouteOutcome.Visitor;
            }
        }

        var host = _resolveHost();
        if (host is { IsAvailableForMessages: true } && host.TryShowFriendMessage(message))
        {
            return FriendMessageRouteOutcome.Host;
        }

        return FriendMessageRouteOutcome.Dropped;
    }

    internal FriendMessageRouteOutcome RouteStatus(FriendStatusNotice notice)
    {
        var visitor = _resolveVisitor(notice.FriendPetId);
        if (visitor is { IsAvailableForMessages: true } && visitor.TryShowFriendStatus(notice.Text, notice.DurationMs, animate: true))
        {
            return FriendMessageRouteOutcome.Visitor;
        }

        var host = _resolveHost();
        return host is { IsAvailableForMessages: true } && host.TryShowFriendStatus(notice.Text, notice.DurationMs, notice.AnimateHost)
            ? FriendMessageRouteOutcome.Host
            : FriendMessageRouteOutcome.Dropped;
    }

    private bool TryReserveMessageId(string senderPetId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return true;
        }

        var key = $"{senderPetId}|{messageId}";
        var now = _timeProvider.GetUtcNow();

        lock (_dedupLock)
        {
            while (_dedupQueue.TryPeek(out var oldest)
                && (now >= oldest.ExpiresAtUtc || _dedupQueue.Count >= _maxDedupEntries))
            {
                _dedupQueue.Dequeue();
                if (_dedupKeys.TryGetValue(oldest.Key, out var recordedExpiry) && recordedExpiry <= oldest.ExpiresAtUtc)
                {
                    _dedupKeys.Remove(oldest.Key);
                }
            }

            if (_dedupKeys.TryGetValue(key, out var existingExpiry) && existingExpiry > now)
            {
                return false;
            }

            var expiresAt = now + _dedupTtl;
            _dedupKeys[key] = expiresAt;
            _dedupQueue.Enqueue((key, expiresAt));
            return true;
        }
    }
}
