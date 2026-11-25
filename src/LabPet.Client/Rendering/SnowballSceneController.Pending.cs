using LabPet.Client.Diagnostics;

namespace LabPet.Client.Rendering;

internal sealed partial class SnowballSceneController
{
    private sealed record PendingThrow(Guid SenderId, Guid EventId,
        Func<Guid, ISnowballActor> CreateActor, long ReceivedAt)
    {
        internal bool Cancelled;
        internal bool Admitted;
    }

    private readonly LinkedList<PendingThrow> _pending = new();
    private TimeProvider _pendingClock = TimeProvider.System;
    private bool _drainingPending;
    private PendingThrow? _creatingPending;
    internal int PendingCount => _pending.Count;
    internal const int MaximumPendingSenders = 12;
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(4);

    // factory는 슬롯 확보 후 새 actor를 생성한다. true는 등록 또는 대기 수락이며 명중 보장이 아니다.
    // 네트워크 중복 제거와 1초 제한은 수신 서비스가 담당한다.
    internal bool TryReceive(Guid senderId, Guid eventId,
        Func<Guid, ISnowballActor> createActor)
    {
        _overlay.Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(createActor);
        if (_disposed || _advancing || _drainingPending
            || senderId == Guid.Empty || eventId == Guid.Empty) return false;
        ExpirePending();
        if (_actors.TryGetValue(senderId, out var actor))
            return _pending.Count == 0 && actor.TryEnqueue(eventId);
        if (_pending.Any(p => p.SenderId == senderId)
            || _pending.Count >= MaximumPendingSenders) return false;
        var pending = new PendingThrow(senderId, eventId, createActor, _pendingClock.GetTimestamp());
        _pending.AddLast(pending);
        DrainPending();
        var accepted = !_disposed && !pending.Cancelled && (pending.Admitted || _pending.Contains(pending));
        if (accepted) _loop?.Wake();
        return accepted;
    }

    private bool IsExpired(PendingThrow pending) =>
        _pendingClock.GetElapsedTime(pending.ReceivedAt, _pendingClock.GetTimestamp()) >= PendingLifetime;

    private void ExpirePending()
    {
        for (var node = _pending.First; node is not null;)
        {
            var next = node.Next;
            if (IsExpired(node.Value)) _pending.Remove(node);
            node = next;
        }
    }

    private void RemovePending(Guid senderId)
    {
        if (_creatingPending is { } creating && creating.SenderId == senderId)
            creating.Cancelled = true;
        for (var node = _pending.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.SenderId == senderId) _pending.Remove(node);
            node = next;
        }
    }

    private void DrainPending()
    {
        if (_disposed || _advancing || _drainingPending) return;
        _drainingPending = true;
        try
        {
            ExpirePending();
            while (!_disposed && _actors.Count < MaximumActors && _pending.First is { } node)
            {
                _pending.RemoveFirst();
                var pending = node.Value;
                if (IsExpired(pending)) continue;
                _creatingPending = pending;
                ISnowballActor? created = null;
                try
                {
                    created = pending.CreateActor(pending.EventId);
                    if (created is null)
                        throw new InvalidOperationException("The snowball actor factory returned null.");
                    if (_disposed || pending.Cancelled || IsExpired(pending)) continue;
                    if (!TryAddCore(pending.SenderId, created))
                        throw new InvalidOperationException("Cannot admit the queued snowball actor.");
                    pending.Admitted = true;
                }
                catch (Exception ex)
                {
                    CrashLogWriter.Write("SnowballScene.Pending", ex);
                }
                finally
                {
                    _creatingPending = null;
                    if (created is not null && !_actors.ContainsValue(created))
                    {
                        try { created.Dispose(); }
                        catch (Exception ex) { CrashLogWriter.Write("SnowballScene.PendingDispose", ex); }
                    }
                }
            }
        }
        finally { _drainingPending = false; }
    }
}
