using System.Windows;

namespace LabPet.Client.Rendering;

internal readonly record struct SnowballOutgoingRequest(Guid EventId, Guid TargetPetId, long SentAt, long QueuedAt);

/// <summary>전송 직전에 슬롯을 확보한다. 전송은 병렬로 완료할 수 있고 연출은 요청 순서를 유지한다.</summary>
internal sealed class SnowballOutgoingQueue(TimeProvider clock)
{
    internal const int Capacity = 256;
    private const int RecentEventCapacity = Capacity * 2;
    private readonly Dictionary<Guid, LinkedListNode<SnowballOutgoingRequest?>> _reservations = [];
    private readonly LinkedList<SnowballOutgoingRequest?> _admissions = new();
    private readonly Queue<SnowballOutgoingRequest> _pending = new();
    private readonly HashSet<Guid> _recent = [];
    private readonly Queue<Guid> _recentOrder = new();

    internal int Count => _pending.Count;
    internal bool TryReserve(Guid id)
    {
        if (id == Guid.Empty || _recent.Contains(id) || _reservations.ContainsKey(id)
            || _pending.Count + _admissions.Count >= Capacity) return false;
        _reservations.Add(id, _admissions.AddLast((SnowballOutgoingRequest?)null));
        return true;
    }

    internal bool Complete(Guid id, Guid targetPetId, long sentAt)
    {
        if (targetPetId == Guid.Empty || !_reservations.Remove(id, out var slot)) return false;
        _recent.Add(id);
        _recentOrder.Enqueue(id);
        if (_recentOrder.Count > RecentEventCapacity) _recent.Remove(_recentOrder.Dequeue());
        slot.Value = new(id, targetPetId, sentAt, clock.GetTimestamp());
        DrainAdmissions();
        return true;
    }

    internal void Release(Guid id)
    {
        if (!_reservations.Remove(id, out var slot)) return;
        _admissions.Remove(slot);
        DrainAdmissions();
    }

    private void DrainAdmissions()
    {
        while (_admissions.First?.Value is { } request)
        {
            _admissions.RemoveFirst();
            _pending.Enqueue(request);
        }
    }
    internal bool TryPeek(out SnowballOutgoingRequest request) => _pending.TryPeek(out request);
    internal bool TryTake(out SnowballOutgoingRequest request) => _pending.TryDequeue(out request);
    internal double WaitedSeconds(SnowballOutgoingRequest request) =>
        clock.GetElapsedTime(request.QueuedAt, clock.GetTimestamp()).TotalSeconds;

    internal void Clear()
    {
        _pending.Clear();
        _reservations.Clear();
        _admissions.Clear();
    }
}

/// <summary>한 리그의 자세를 순차 갱신하고, 각 발사 경계에서 원래 요청을 한 번만 전달한다.</summary>
internal sealed class SnowballOutgoingTimeline(SnowballOutgoingQueue queue)
{
    internal const double NormalSpeed = 1, FastSpeed = 1.5, FasterSpeed = 2, MaximumSpeed = 2.5;
    private const int FastThreshold = 2, FasterThreshold = 4, MaximumSpeedThreshold = 6;
    private const int BurstEnter = 9, BurstExit = 3, BurstSize = 3;
    private const double BurstWaitSeconds = 1.6, BurstStart = 0.19, SameDirectionDot = 0.5;
    private const double TransitionSeconds = 0.10, LaughSeconds = 0.55;
    private static readonly SnowballThrowPose ReadyPose = new(-16, -8, 0.45);
    private SnowballOutgoingRequest? _active;
    private SnowballThrowPose _lastPose, _startPose;
    private double _progress, _speed, _blendTime, _laughRemaining, _resumeTime;
    private int _burstRemaining;
    private bool _released, _needsLaugh, _suspended;
    internal bool IsBurstMode { get; private set; }
    internal double CurrentSpeed => _speed;
    internal bool HasWork => _active is not null || queue.Count > 0 || _needsLaugh || _laughRemaining > 0;
    internal bool IsPreparing => _active is not null && !_released && _progress < SnowballThrowPose.ReleaseSeconds;
    internal Guid? ActiveEventId => _active?.EventId;

    internal void Advance(double seconds, bool blocked, Func<SnowballOutgoingRequest, Vector> aim,
        Action<Vector?, SnowballThrowPose?, bool, double> updatePose, Action<SnowballOutgoingRequest> launch,
        bool deferLaugh = false)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        if (blocked)
        {
            _suspended = _active is not null;
            _resumeTime = 0;
            if (!_suspended) _lastPose = default;
            updatePose(null, null, false, seconds);
            return;
        }

        while (seconds > 0)
        {
            if (_active is not { } request)
            {
                if (!queue.TryTake(out var next))
                {
                    if (_needsLaugh)
                    {
                        if (deferLaugh)
                        {
                            updatePose(null, null, false, seconds);
                            return;
                        }
                        _needsLaugh = false;
                        _laughRemaining = LaughSeconds;
                    }
                    var laughing = _laughRemaining > 0;
                    var slice = laughing ? Math.Min(seconds, _laughRemaining) : seconds;
                    _laughRemaining -= laughing ? slice : 0;
                    updatePose(null, null, laughing, slice);
                    if (!laughing) return;
                    seconds -= slice;
                    continue;
                }

                Start(next, chainedBurst: false);
                request = next;
            }

            var direction = aim(request);
            if (_suspended)
            {
                var slice = Math.Min(seconds, TransitionSeconds - _resumeTime);
                _resumeTime += slice;
                updatePose(direction, SnowballThrowPose.Blend(default, _lastPose, _resumeTime / TransitionSeconds),
                    false, slice);
                if (_resumeTime >= TransitionSeconds)
                {
                    _resumeTime = 0;
                    _suspended = false;
                }
                seconds -= slice;
                continue;
            }

            var hasNext = queue.TryPeek(out var pending);
            var recoveryStart = SnowballThrowPose.PrepareSeconds + SnowballThrowPose.SwingSeconds;
            var continueBurst = _released && _progress <= recoveryStart && _burstRemaining > 0 && hasNext
                && Vector.Multiply(direction, aim(pending)) >= SameDirectionDot;
            var boundary = !_released ? SnowballThrowPose.ReleaseSeconds
                : continueBurst ? recoveryStart : SnowballThrowPose.DurationSeconds;
            var untilBoundary = Math.Max(0, (boundary - _progress) / _speed);
            var reached = seconds >= untilBoundary;
            var step = Math.Min(seconds, untilBoundary);
            _progress = reached ? Math.Max(_progress, boundary) : _progress + step * _speed;
            _blendTime += step;
            _lastPose = SnowballThrowPose.Blend(_startPose, Evaluate(_progress, hasNext), _blendTime / TransitionSeconds);
            if (step > 0) updatePose(direction, _lastPose, false, step);
            seconds -= step;
            if (!reached) return;

            if (!_released)
            {
                _released = true;
                launch(request);
                continue;
            }
            _active = null;
            _needsLaugh = true;
            if (continueBurst && queue.TryTake(out var following))
            {
                _burstRemaining--;
                Start(following, chainedBurst: true);
            }
        }
    }

    private void Start(SnowballOutgoingRequest request, bool chainedBurst)
    {
        if (!chainedBurst)
        {
            var pending = queue.Count;
            IsBurstMode = IsBurstMode ? pending > BurstExit
                : pending >= BurstEnter || (pending >= MaximumSpeedThreshold && queue.WaitedSeconds(request) >= BurstWaitSeconds);
            _speed = IsBurstMode ? MaximumSpeed : pending switch
            {
                >= MaximumSpeedThreshold => MaximumSpeed,
                >= FasterThreshold => FasterSpeed,
                >= FastThreshold => FastSpeed,
                _ => NormalSpeed
            };
            _burstRemaining = IsBurstMode ? BurstSize - 1 : 0;
        }
        _active = request;
        _progress = chainedBurst ? BurstStart : 0;
        _startPose = _lastPose;
        _blendTime = _laughRemaining = 0;
        _released = false;
    }

    private static SnowballThrowPose Evaluate(double progress, bool hasNext)
    {
        var recoveryStart = SnowballThrowPose.PrepareSeconds + SnowballThrowPose.SwingSeconds;
        var pose = SnowballThrowPose.Evaluate(progress);
        return hasNext && progress >= recoveryStart
            ? SnowballThrowPose.Blend(pose, ReadyPose, (progress - recoveryStart) / SnowballThrowPose.RecoverSeconds)
            : pose;
    }
}
