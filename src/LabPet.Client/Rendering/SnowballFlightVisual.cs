using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LabPet.Client.Rendering;

/// <summary>공용 오버레이의 로컬 DIP에서 비행과 명중 효과를 연결한다. 자체 타이머 없음.</summary>
internal sealed class SnowballFlightVisual : Canvas, IDisposable
{
    private readonly SnowballTrajectory _trajectory;
    private readonly Ellipse _ball;
    private SnowballImpactVisual? _impact;
    private bool _disposed;
    internal bool IsComplete => _disposed || (_impact?.IsComplete ?? false);
    internal Point Position => _trajectory.Position;

    internal SnowballFlightVisual(Point start, Point target, double durationSeconds, double arcHeight)
    {
        _trajectory = new SnowballTrajectory(start, target, durationSeconds, arcHeight);
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

    /// <summary>명중한 호출에서만 true. 소유자는 피격 반응·타임라인 통지를 한 번 처리한다.</summary>
    internal bool Advance(double deltaSeconds)
    {
        Dispatcher.VerifyAccess();
        if (IsComplete || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return false;
        if (_impact is { } impact)
        {
            impact.Advance(deltaSeconds);
            return false;
        }
        var remaining = _trajectory.RemainingSeconds;
        var hit = _trajectory.Advance(deltaSeconds);
        PlaceBall();
        if (!hit) return false;
        Children.Remove(_ball);
        _impact = new SnowballImpactVisual();
        SetLeft(_impact, Position.X - 50);
        SetTop(_impact, Position.Y - 50);
        Children.Add(_impact);
        _impact.Advance(Math.Max(0, deltaSeconds - remaining));
        return true;
    }

    private void PlaceBall()
    {
        SetLeft(_ball, Position.X - 7);
        SetTop(_ball, Position.Y - 7);
    }

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Children.Clear();
        _impact = null;
    }
}
