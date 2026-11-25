using System.Windows;
using LabPet.Client.Diagnostics;
using LabPet.Client.Views;

namespace LabPet.Client.Rendering;

/// <summary>한 오버레이의 임시 펫과 비행을 함께 갱신한다. 창과 갱신 루프는 호출자가 소유한다.</summary>
internal sealed partial class SnowballSceneController : IDisposable
{
    private readonly SnowballOverlayWindow _overlay;
    private readonly Dictionary<Guid, ISnowballActor> _actors = [];
    private readonly Dictionary<Guid, Guid> _flightOwners = [];
    private bool _disposed;
    private bool _advancing;
    private SnowballOutgoingActor? _outgoing;
    internal int ActorCount => _actors.Count;
    internal int MaximumActors { get; }

    internal bool HasWork
    {
        get
        {
            _overlay.Dispatcher.VerifyAccess();
            return !_disposed && (_actors.Count > 0 || _pending.Count > 0 || _overlay.HasActiveFlights
                || _outgoing?.HasWork == true);
        }
    }

    /// <summary>actor factory에서 생성 직전에 호출한다. 기존 actor의 위치는 변경하지 않는다.</summary>
    internal bool TrySelectSpawn(Rect monitorPhysical, Size petPhysical,
        out SnowballSpawnGeometry spawn, Random? random = null)
    {
        _overlay.Dispatcher.VerifyAccess();
        spawn = default;
        if (_disposed || !_overlay.IsVisible || _actors.Count >= MaximumActors) return false;
        var occupied = _actors.Values.Select(actor => actor.OccupiedBounds).ToArray();
        return SnowballSpawnGeometry.TrySelect(monitorPhysical, petPhysical, occupied,
            random ?? Random.Shared, out spawn);
    }

    private SnowballSceneLoop? _loop;
    internal void AttachOutgoing(SnowballOutgoingActor actor) => _outgoing = actor;
    internal void WakeOutgoing() => _loop?.Wake();
    internal SnowballTemporaryActor? FindTemporary(Guid petId) =>
        !_disposed && _actors.TryGetValue(petId, out var actor) ? actor as SnowballTemporaryActor : null;

    internal void PromoteVisitor(Guid petId, VisitorPetWindow visitor)
    {
        _overlay.Dispatcher.VerifyAccess();
        if (FindTemporary(petId) is not { } temporary) return;
        try
        {
            var timeline = temporary.TransferToVisitor();
            if (timeline.Phase == SnowballActorPhase.Complete) CancelSender(petId);
            else _actors[petId] = new SnowballVisitorActor(_overlay, visitor, timeline);
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("SnowballScene.PromoteVisitor", ex);
            CancelSender(petId);
        }
    }

    internal bool TryAttachLoop(SnowballOverlayWindow overlay, SnowballSceneLoop loop)
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_disposed || _loop is not null || !ReferenceEquals(overlay, _overlay)) return false;
        _loop = loop;
        return true;
    }

    internal void DetachLoop(SnowballSceneLoop loop)
    {
        _overlay.Dispatcher.VerifyAccess();
        if (ReferenceEquals(_loop, loop)) _loop = null;
    }

    internal SnowballSceneController(SnowballOverlayWindow overlay, int maximumActors = 6,
        TimeProvider? pendingClock = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        overlay.Dispatcher.VerifyAccess();
        if (maximumActors < 1 || maximumActors > 6)
            throw new ArgumentOutOfRangeException(nameof(maximumActors));
        _overlay = overlay;
        _pendingClock = pendingClock ?? TimeProvider.System;
        MaximumActors = maximumActors;
        _overlay.Closed += OnOverlayClosed;
    }

    // 성공했을 때만 actor의 소유권을 가져온다. 중복 발신자는 기존 actor에 TryEnqueue한다.
    internal bool TryAdd(Guid senderId, ISnowballActor actor)
    {
        _overlay.Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(actor);
        if (_disposed || _advancing || _drainingPending) return false;
        ExpirePending();
        return _pending.Count == 0 && TryAddCore(senderId, actor);
    }

    private bool TryAddCore(Guid senderId, ISnowballActor actor)
    {
        if (_disposed || senderId == Guid.Empty || _actors.ContainsKey(senderId)
            || _actors.Count >= MaximumActors || _actors.ContainsValue(actor)
            || !ReferenceEquals(actor.Overlay, _overlay)
            || actor.Phase == SnowballActorPhase.Complete) return false;
        _actors.Add(senderId, actor);
        return true;
    }

    internal bool TryEnqueue(Guid senderId, Guid eventId)
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_disposed || _advancing || _drainingPending) return false;
        ExpirePending();
        return _pending.Count == 0 && _actors.TryGetValue(senderId, out var actor)
            && actor.TryEnqueue(eventId);
    }

    internal void Advance(double seconds, Point targetPhysical, Action<Guid> onImpact)
    {
        _overlay.Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(onImpact);
        if (_disposed || _advancing || _drainingPending || !double.IsFinite(seconds) || seconds <= 0) return;
        if (!_overlay.TryScreenPhysicalToSceneDip(targetPhysical, out _))
        {
            Dispose();
            return;
        }
        _advancing = true;
        try
        {
            // 한 프레임의 발사 이후 잔여 시간만 비행에 적용하도록 작은 공용 구간으로 나눈다.
            var remaining = Math.Min(seconds, 0.25);
            AdvanceOutgoing(remaining);
            while (remaining > 0 && !_disposed)
            {
                var step = Math.Min(remaining, 0.01);
                remaining -= step;
                foreach (var id in _overlay.AdvanceFlightsPhysical(step, targetPhysical))
                {
                    if (!_flightOwners.Remove(id, out var sender)) continue;
                    if (_actors.TryGetValue(sender, out var owner)) owner.NotifyImpact(id);
                    try { onImpact(id); }
                    catch (Exception ex) { CrashLogWriter.Write("SnowballScene.Impact", ex); }
                    if (_disposed) break;
                }
                if (_disposed) break;
                foreach (var pair in _actors.ToArray())
                {
                    try
                    {
                        var bounds = pair.Value.OccupiedBounds;
                        var lookX = Math.Clamp((targetPhysical.X - bounds.Left - bounds.Width / 2) / bounds.Width, -1, 1);
                        var lookY = Math.Clamp((targetPhysical.Y - bounds.Top - bounds.Height / 2) / bounds.Height, -1, 1);
                        pair.Value.Advance(step, lookX, lookY, (id, hand) =>
                        {
                            if (_flightOwners.ContainsKey(id)
                                || !_overlay.TryLaunchPhysical(pair.Key, id, hand, targetPhysical, 0.6, 80))
                                throw new InvalidOperationException("Cannot launch the scheduled snowball.");
                            _flightOwners.Add(id, pair.Key);
                        });
                        if (pair.Value.Phase == SnowballActorPhase.Complete) CancelSender(pair.Key);
                    }
                    catch (Exception ex)
                    {
                        CrashLogWriter.Write("SnowballScene.Actor", ex);
                        CancelSender(pair.Key);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("SnowballScene.Advance", ex);
            Dispose();
        }
        finally { _advancing = false; }
        DrainPending();
    }

    private void AdvanceOutgoing(double seconds)
    {
        _overlay.AdvanceOutgoingFlights(seconds);
        if (_outgoing is not { HasWork: true } actor) return;
        try
        {
            // 긴 프레임도 발사 경계를 넘기기 전에 실제 투척 자세를 적용한다.
            while (seconds > 0 && actor.HasWork)
            {
                var step = Math.Min(seconds, 0.05);
                actor.Advance(step);
                seconds -= step;
            }
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("SnowballScene.Outgoing", ex);
            actor.Cancel();
            _overlay.CancelSenderFlights(Guid.Empty);
        }
    }

    internal void CancelReceiving()
    {
        _pending.Clear();
        foreach (var sender in _actors.Keys.ToArray()) CancelSender(sender);
    }

    internal void RemoveOfflineSenders(IReadOnlySet<Guid> online)
    {
        _overlay.Dispatcher.VerifyAccess();
        var senders = _actors.Keys.Concat(_pending.Select(item => item.SenderId)).Distinct().ToArray();
        foreach (var sender in senders)
            if (!online.Contains(sender)) CancelSender(sender);
    }

    internal void RemoveVisitor(Guid senderId)
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_actors.TryGetValue(senderId, out var actor) && actor is SnowballVisitorActor)
            CancelSender(senderId);
    }

    internal void CancelSender(Guid senderId)
    {
        _overlay.Dispatcher.VerifyAccess();
        RemovePending(senderId);
        if (_actors.Remove(senderId, out var actor))
        {
            try { actor.Dispose(); }
            catch (Exception ex) { CrashLogWriter.Write("SnowballScene.DisposeActor", ex); }
        }
        foreach (var pair in _flightOwners.Where(p => p.Value == senderId).ToArray())
            _flightOwners.Remove(pair.Key);
        _overlay.CancelSenderFlights(senderId);
    }

    private void OnOverlayClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_disposed) return;
        // Closed 이후에는 Flights getter를 호출하지 않는다.
        _disposed = true;
        _pending.Clear();
        _loop?.Dispose();
        _outgoing?.Dispose();
        _outgoing = null;
        _overlay.Closed -= OnOverlayClosed;
        foreach (var id in _actors.Keys.ToArray()) CancelSender(id);
        _flightOwners.Clear();
    }
}
