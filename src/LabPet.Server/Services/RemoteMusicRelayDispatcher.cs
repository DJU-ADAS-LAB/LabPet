using System.Collections.Concurrent;
using LabPet.Server.Hubs;
using LabPet.Server.Options;
using LabPet.Shared.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace LabPet.Server.Services;

/// <summary>
/// 원격 음악 프레임을 단일 구독자 연결에만 비동기 전송하면서,
/// 오래된 시퀀스 폐기 및 연결별 1-슬롯 병합(최신 프레임 유지)으로 송신 큐 적체를 원천 차단한다.
/// </summary>
public sealed class RemoteMusicRelayDispatcher
{
    private sealed class SequenceTracker
    {
        public readonly object Sync = new();
        public uint? LastSequence;
        public DateTimeOffset LastSeenUtc;
    }

    private sealed class ConnectionSendSlot
    {
        public readonly object Sync = new();
        public MusicFramePacket? PendingPacket;
        public bool IsSending;
        public bool Disposed;
    }

    private readonly ServerOptions _options;
    private readonly Func<string, MusicFramePacket, CancellationToken, Task> _sendAsync;
    private readonly TimeProvider _clock;
    private readonly int _maxSequenceTrackers;
    private readonly ConcurrentDictionary<string, SequenceTracker> _sequences = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConnectionSendSlot> _slots = new(StringComparer.Ordinal);
    private long _droppedStaleFrames;
    private long _droppedQueuedFrames;

    public RemoteMusicRelayDispatcher(
        ServerOptions options,
        IHubContext<SocialHub> hubContext,
        TimeProvider? clock = null)
        : this(
            options,
            (connId, packet, ct) => hubContext.Clients.Client(connId)
                .SendAsync(SocialHubConstants.ReceiveMusicFrameClientMethod, packet, ct),
            clock)
    {
    }

    internal RemoteMusicRelayDispatcher(
        ServerOptions options,
        Func<string, MusicFramePacket, CancellationToken, Task> sendAsync,
        TimeProvider? clock = null)
    {
        _options = options;
        _sendAsync = sendAsync;
        _clock = clock ?? TimeProvider.System;
        _maxSequenceTrackers = Math.Clamp(options.MaxRateLimitTrackedKeys, 64, 16_384);
    }

    public long DroppedStaleFrames => Interlocked.Read(ref _droppedStaleFrames);
    public long DroppedQueuedFrames => Interlocked.Read(ref _droppedQueuedFrames);
    public int TrackedSequenceCount => _sequences.Count;

    /// <summary>발신자-구독자-구독ID 기준 오래된 음악 프레임인지 판정한다.</summary>
    public bool TryAcceptSequence(string senderPetId, string targetPetId, string subscriptionId, uint sequence)
    {
        var now = _clock.GetUtcNow();
        var key = $"{senderPetId}|{targetPetId}|{subscriptionId}";
        if (!_sequences.TryGetValue(key, out var tracker))
        {
            if (_sequences.Count >= _maxSequenceTrackers)
            {
                PruneExpired(now);
                if (_sequences.Count >= _maxSequenceTrackers)
                {
                    return false;
                }
            }

            tracker = _sequences.GetOrAdd(key, static _ => new SequenceTracker());
        }

        lock (tracker.Sync)
        {
            tracker.LastSeenUtc = now;
            if (tracker.LastSequence is { } prev && unchecked((int)(sequence - prev)) <= 0)
            {
                Interlocked.Increment(ref _droppedStaleFrames);
                return false;
            }

            tracker.LastSequence = sequence;
            return true;
        }
    }

    /// <summary>
    /// 대상 연결에 프레임을 1-슬롯 병합 방식으로 적재한다.
    /// 이전 프레임이 아직 송신 중이면 대기 중이던 오래된 프레임을 덮어써 큐 적체를 방지한다.
    /// </summary>
    public bool TryEnqueue(string connectionId, MusicFramePacket packet)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            return false;
        }

        var slot = _slots.GetOrAdd(connectionId, static _ => new ConnectionSendSlot());
        bool shouldStartWorker;
        lock (slot.Sync)
        {
            if (slot.Disposed)
            {
                return false;
            }

            if (slot.IsSending)
            {
                if (slot.PendingPacket is not null)
                {
                    Interlocked.Increment(ref _droppedQueuedFrames);
                }

                slot.PendingPacket = packet;
                return true;
            }

            slot.IsSending = true;
            slot.PendingPacket = packet;
            shouldStartWorker = true;
        }

        if (shouldStartWorker)
        {
            _ = ProcessSlotAsync(connectionId, slot);
        }

        return true;
    }

    public void RemoveConnection(string connectionId)
    {
        if (_slots.TryRemove(connectionId, out var slot))
        {
            lock (slot.Sync)
            {
                slot.Disposed = true;
                slot.PendingPacket = null;
            }
        }
    }

    public void PruneExpired(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromSeconds(Math.Clamp(_options.MessageDedupWindowSeconds, 30, 600));
        foreach (var kv in _sequences)
        {
            var remove = false;
            lock (kv.Value.Sync)
            {
                if (kv.Value.LastSeenUtc < cutoff)
                {
                    remove = true;
                }
            }

            if (remove)
            {
                _sequences.TryRemove(kv.Key, out _);
            }
        }
    }

    private async Task ProcessSlotAsync(string connectionId, ConnectionSendSlot slot)
    {
        while (true)
        {
            MusicFramePacket? nextPacket;
            lock (slot.Sync)
            {
                if (slot.Disposed || slot.PendingPacket is null)
                {
                    slot.IsSending = false;
                    return;
                }

                nextPacket = slot.PendingPacket;
                slot.PendingPacket = null;
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _sendAsync(connectionId, nextPacket, timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                lock (slot.Sync)
                {
                    slot.PendingPacket = null;
                    slot.IsSending = false;
                }
                return;
            }
        }
    }
}
