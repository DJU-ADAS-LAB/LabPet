using System.Windows;

namespace LabPet.Client.Rendering;

/// <summary>모니터 오버레이 로컬 DIP 좌표의 비행 계산. 창이나 타이머를 생성하지 않는다.</summary>
internal sealed class SnowballTrajectory
{
    private Point _start;
    private Point _target;
    private Vector _startVelocity;
    private Vector _endVelocity;
    private double _segmentDuration;
    private double _segmentElapsed;

    public Point Position { get; private set; }
    public Vector Velocity { get; private set; }
    internal Point Destination => _target;
    public double RemainingSeconds => Math.Max(0, _segmentDuration - _segmentElapsed);
    public bool IsComplete => RemainingSeconds == 0;

    public SnowballTrajectory(Point start, Point target, double durationSeconds, double arcHeight)
    {
        if (!IsValid(start) || !IsValid(target))
            throw new ArgumentOutOfRangeException(nameof(start));
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0.5 || durationSeconds > 0.9)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (!double.IsFinite(arcHeight) || arcHeight < 0 || arcHeight > 10000)
            throw new ArgumentOutOfRangeException(nameof(arcHeight));
        _start = Position = start;
        _target = target;
        _segmentDuration = durationSeconds;
        var linear = (target - start) / durationSeconds;
        _startVelocity = Velocity = linear + new Vector(0, -4 * arcHeight / durationSeconds);
        _endVelocity = linear + new Vector(0, 4 * arcHeight / durationSeconds);
    }

    /// <summary>현재 위치와 속도를 보존한다. 마지막 80ms에는 급격한 유도 보정을 하지 않는다.</summary>
    public bool TryRetarget(Point target)
    {
        if (!IsValid(target) || RemainingSeconds <= 0.08 || target == _target) return false;
        Redirect(target, RemainingSeconds);
        return true;
    }

    /// <summary>유효 대상을 잃으면 현재 위치·속도를 유지한 새 비행 구간으로 이어 간다.</summary>
    internal void Redirect(Point target, double seconds)
    {
        _start = Position;
        _startVelocity = Velocity;
        _target = target;
        _segmentDuration = seconds;
        _segmentElapsed = 0;
    }

    /// <summary>공용 갱신 루프에서 호출. 완료 이후 재호출해도 명중을 다시 반환하지 않는다.</summary>
    public bool Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0 || IsComplete) return false;
        _segmentElapsed = Math.Min(_segmentDuration, _segmentElapsed + deltaSeconds);
        var t = _segmentElapsed / _segmentDuration;
        var t2 = t * t;
        var t3 = t2 * t;
        var displacement = _target - _start;
        var offset = displacement * (-2 * t3 + 3 * t2)
            + _startVelocity * (_segmentDuration * (t3 - 2 * t2 + t))
            + _endVelocity * (_segmentDuration * (t3 - t2));
        Position = _start + offset;
        Velocity = displacement * ((-6 * t2 + 6 * t) / _segmentDuration)
            + _startVelocity * (3 * t2 - 4 * t + 1)
            + _endVelocity * (3 * t2 - 2 * t);
        if (!IsComplete) return false;
        Position = _target;
        Velocity = _endVelocity;
        return true;
    }

    private static bool IsValid(Point point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y)
        && Math.Abs(point.X) <= 10000000 && Math.Abs(point.Y) <= 10000000;
}
