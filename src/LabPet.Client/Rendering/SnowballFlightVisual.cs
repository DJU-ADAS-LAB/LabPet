using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LabPet.Client.Rendering;

internal readonly record struct SnowballFlightPlacement(Canvas Canvas, Point Local);

/// <summary>공용 오버레이의 로컬 DIP에서 비행과 명중 효과를 연결한다. 자체 타이머 없음.</summary>
internal sealed class SnowballFlightVisual : Canvas, IDisposable
{
    private readonly SnowballTrajectory _trajectory;
    private readonly Ellipse _ball;
    private readonly Func<Point, SnowballFlightPlacement?>? _placePhysical;
    private readonly Func<Point, bool>? _escaped;
    private SnowballImpactVisual? _impact;
    private bool _disposed;
    private bool _finishedWithoutImpact;
    private bool _extendedForMovingTarget;
    internal bool IsComplete => _disposed || _finishedWithoutImpact || (_impact?.IsComplete ?? false);
    internal bool IsPhysical => _placePhysical is not null;
    internal bool IsFlying => !IsComplete && !_trajectory.IsComplete;
    internal Point Position => _trajectory.Position;

    internal SnowballFlightVisual(Point start, Point target, double durationSeconds, double arcHeight,
        Func<Point, SnowballFlightPlacement?>? placePhysical = null, Func<Point, bool>? escaped = null)
    {
        _trajectory = new SnowballTrajectory(start, target, durationSeconds, arcHeight);
        _placePhysical = placePhysical;
        _escaped = escaped;
        IsHitTestVisible = false;
        Focusable = false;
        _ball = new Ellipse
        {
            Width = 14, Height = 14,
            Fill = Brushes.Snow,
            Stroke = Brushes.LightBlue,
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        Children.Add(_ball);
        PlaceBall();
    }

    internal bool TryRetarget(Point target)
    {
        Dispatcher.VerifyAccess();
        return !_disposed && _impact is null && _trajectory.TryRetarget(target);
    }

    internal void TrackPhysicalTarget(Point target)
    {
        if (!IsFlying || TryRetarget(target) || _extendedForMovingTarget
            || (target - _trajectory.Destination).Length <= 12) return;
        _extendedForMovingTarget = true;
        _trajectory.Redirect(target, 0.12);
    }

    internal void RedirectOutside(Point target, double seconds)
    {
        if (IsFlying) _trajectory.Redirect(target, seconds);
    }

    /// <summary>명중한 호출에서만 true. 소유자는 피격 반응·타임라인 통지를 한 번 처리한다.</summary>
    internal bool Advance(double deltaSeconds, bool canImpact = true)
    {
        Dispatcher.VerifyAccess();
        if (IsComplete || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return false;
        if (_impact is { } impact)
        {
            if (_placePhysical is not null && PlaceBall() is { } effectCenter)
            {
                SetLeft(impact, effectCenter.X - 50);
                SetTop(impact, effectCenter.Y - 50);
            }
            impact.Advance(deltaSeconds);
            return false;
        }
        var remaining = _trajectory.RemainingSeconds;
        var hit = _trajectory.Advance(deltaSeconds);
        if (_escaped?.Invoke(Position) == true || (hit && !canImpact))
        {
            _finishedWithoutImpact = true;
            return false;
        }
        var local = PlaceBall();
        if (!hit) return false;
        if (local is not { } impactCenter)
        {
            _finishedWithoutImpact = true;
            return false;
        }
        Children.Remove(_ball);
        _impact = new SnowballImpactVisual();
        SetLeft(_impact, impactCenter.X - 50);
        SetTop(_impact, impactCenter.Y - 50);
        Children.Add(_impact);
        _impact.Advance(Math.Max(0, deltaSeconds - remaining));
        return true;
    }

    private Point? PlaceBall()
    {
        var local = Position;
        if (_placePhysical is not null)
        {
            var placement = _placePhysical(Position);
            var parent = Parent as Canvas;
            if (!ReferenceEquals(parent, placement?.Canvas))
            {
                parent?.Children.Remove(this);
                placement?.Canvas.Children.Add(this);
            }
            if (placement is not { } placed) return null;
            local = placed.Local;
        }
        SetLeft(_ball, local.X - 7);
        SetTop(_ball, local.Y - 7);
        return local;
    }

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        if (Parent is Canvas canvas) canvas.Children.Remove(this);
        Children.Clear();
        _impact = null;
    }
}
