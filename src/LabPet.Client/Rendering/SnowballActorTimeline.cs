namespace LabPet.Client.Rendering;

internal enum SnowballActorPhase { Entering, Throwing, WaitingForImpact, Laughing, Exiting, Complete }

/// <summary>
/// 발신자 한 명의 연출 시간표. 소유 UI 스레드에서 직렬 호출한다.
/// 네트워크 인증·중복 제거·쿨다운은 수신 서비스가 먼저 수행해야 한다.
/// 발사 콜백은 재진입하지 않아야 하며 실제 비행 완료 시 NotifyImpact를 호출한다.
/// </summary>
internal sealed class SnowballActorTimeline
{
    private const double EdgeSeconds = 0.4;
    private const double LaughSeconds = 0.9;
    private const double PendingLifetimeSeconds = 2;
    private readonly bool _visitor;
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = [];
    private readonly HashSet<Guid> _inFlight = [];
    private Guid _current;
    private Guid? _pending;
    private double _pendingAt;
    private double _clock;
    private double _elapsed;
    private double _entryStart;
    private bool _released;

    internal SnowballActorPhase Phase { get; private set; }
    internal int PendingCount => _pending.HasValue ? 1 : 0;
    internal int InFlightCount => _inFlight.Count;
    internal double ThrowElapsed => Phase == SnowballActorPhase.Throwing ? _elapsed : 0;
    internal SnowballThrowPose ThrowPose => Phase == SnowballActorPhase.Throwing
        ? SnowballThrowPose.Evaluate(_elapsed) : default;
    internal double VisibilityProgress => _visitor ? 1 : Phase switch
    {
        SnowballActorPhase.Entering => _entryStart + (1 - _entryStart) * Smooth(_elapsed / EdgeSeconds),
        SnowballActorPhase.Exiting => 1 - Smooth(_elapsed / EdgeSeconds),
        SnowballActorPhase.Complete => 0,
        _ => 1
    };
    internal double LaughProgress => Phase == SnowballActorPhase.Laughing ? _elapsed / LaughSeconds : 0;

    internal SnowballActorTimeline(Guid firstEventId, bool isVisitor)
    {
        if (firstEventId == Guid.Empty) throw new ArgumentException("Event ID is required.", nameof(firstEventId));
        _visitor = isVisitor;
        _current = firstEventId;
        Remember(firstEventId);
        Phase = isVisitor ? SnowballActorPhase.Throwing : SnowballActorPhase.Entering;
    }

    /// <summary>false일 때 수락된 연출이 아니므로 호출자는 성공으로 처리하지 않는다.</summary>
    internal bool TryEnqueue(Guid eventId)
    {
        ExpirePending();
        if (eventId == Guid.Empty || Phase == SnowballActorPhase.Complete
            || _seen.Contains(eventId) || _pending.HasValue) return false;
        Remember(eventId);
        _pending = eventId;
        _pendingAt = _clock;
        if (Phase == SnowballActorPhase.Exiting)
        {
            _entryStart = VisibilityProgress;
            SetPhase(SnowballActorPhase.Entering);
        }
        else if (Phase is SnowballActorPhase.WaitingForImpact or SnowballActorPhase.Laughing)
            StartPending();
        return true;
    }

    /// <summary>
    /// 대상 숨김·방문 해제·연결 종료 시 시간표를 종료한다. 반복 호출해도 안전하다.
    /// 소유자는 실제 비행 객체·파티클과 애니메이션 오버라이드도 함께 정리해야 한다.
    /// 일반적인 후속 투척에는 호출하지 않는다.
    /// </summary>
    internal void Cancel()
    {
        _pending = null;
        _inFlight.Clear();
        _seen.Clear();
        _seenOrder.Clear();
        _current = Guid.Empty;
        _released = false;
        SetPhase(SnowballActorPhase.Complete);
    }

    internal void NotifyImpact(Guid eventId) => _inFlight.Remove(eventId);

    // 단계별 관절 갱신은 발사 콜백보다 먼저 수행한다. 콜백에서 시간표로 재진입하지 않는다.
    internal void Advance(double deltaSeconds, Action<Guid> launch,
        Action<SnowballActorPhase, double, double>? advanceVisual = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0
            || !double.IsFinite(_clock + deltaSeconds) || Phase == SnowballActorPhase.Complete) return;
        _clock += deltaSeconds;
        ExpirePending();
        var remaining = deltaSeconds;
        while (remaining > 0 && Phase != SnowballActorPhase.Complete)
        {
            if (Phase == SnowballActorPhase.WaitingForImpact)
            {
                if (StartPending()) continue;
                if (_inFlight.Count > 0)
                {
                    advanceVisual?.Invoke(Phase, _elapsed, remaining);
                    return;
                }
                SetPhase(SnowballActorPhase.Laughing);
            }
            var duration = Phase switch
            {
                SnowballActorPhase.Entering or SnowballActorPhase.Exiting => EdgeSeconds,
                SnowballActorPhase.Throwing => SnowballThrowPose.DurationSeconds,
                _ => LaughSeconds
            };
            var boundary = Phase == SnowballActorPhase.Throwing && !_released
                ? SnowballThrowPose.ReleaseSeconds : duration;
            var step = Math.Min(remaining, boundary - _elapsed);
            var before = _elapsed;
            _elapsed += step;
            remaining -= step;
            advanceVisual?.Invoke(Phase, before, step);
            if (Phase == SnowballActorPhase.Throwing && !_released
                && SnowballThrowPose.CrossedRelease(before, _elapsed))
            {
                _released = true;
                _inFlight.Add(_current);
                launch(_current);
            }
            if (_elapsed < duration)
            {
                if (remaining > 0) continue;
                return;
            }
            switch (Phase)
            {
                case SnowballActorPhase.Entering:
                    if (!_released) SetPhase(SnowballActorPhase.Throwing);
                    else if (!StartPending()) SetPhase(SnowballActorPhase.WaitingForImpact);
                    break;
                case SnowballActorPhase.Throwing:
                    if (!StartPending()) SetPhase(SnowballActorPhase.WaitingForImpact);
                    break;
                case SnowballActorPhase.Laughing:
                    SetPhase(_visitor ? SnowballActorPhase.Complete : SnowballActorPhase.Exiting);
                    break;
                case SnowballActorPhase.Exiting:
                    SetPhase(SnowballActorPhase.Complete);
                    break;
            }
        }
    }

    private bool StartPending()
    {
        if (_pending is not { } next) return false;
        _current = next;
        _pending = null;
        _released = false;
        SetPhase(SnowballActorPhase.Throwing);
        return true;
    }

    private void ExpirePending()
    {
        if (_pending.HasValue && _clock - _pendingAt > PendingLifetimeSeconds) _pending = null;
    }

    private void SetPhase(SnowballActorPhase phase) { Phase = phase; _elapsed = 0; }
    private void Remember(Guid id)
    {
        _seen.Add(id);
        _seenOrder.Enqueue(id);
        if (_seenOrder.Count > 64) _seen.Remove(_seenOrder.Dequeue());
    }
    private static double Smooth(double t) => t * t * (3 - 2 * t);
}
