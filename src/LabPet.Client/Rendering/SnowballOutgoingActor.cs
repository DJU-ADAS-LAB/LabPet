using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LabPet.Client.Interop;
using LabPet.Client.Views;

namespace LabPet.Client.Rendering;

/// <summary>내 펫 리그 하나와 대상별 물리 화면 좌표를 송신 시간표에 연결한다.</summary>
internal sealed class SnowballOutgoingActor(
    PetWindow pet, SnowballOutgoingQueue queue, SnowballOverlayWindow primary,
    Func<Rect> getMonitor, Func<Guid, VisitorPetWindow?> findVisitor,
    Func<Guid, SnowballTemporaryActor?> findTemporary,
    Func<Point, Rect?, SnowballFlightPlacement?> place) : IDisposable
{
    private SnowballOutgoingTimeline _timeline = new(queue);
    private readonly Dictionary<Guid, double> _offscreenDirections = [];
    private Ellipse? _held;
    private SnowballTemporaryActor? _preparingTarget;
    internal bool HasWork => _timeline.HasWork;

    internal void Advance(double seconds)
    {
        if (!_timeline.HasWork) return;
        if (!pet.TryGetSnowballTargetPhysical(out var source))
            throw new InvalidOperationException("내 펫의 눈덩이 투척 위치를 확인할 수 없습니다.");
        var monitor = getMonitor();
        var dpi = VisualTreeHelper.GetDpi(primary);
        var radius = 7 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
        var blocked = pet.IsSnowballThrowBlocked;

        Point Resolve(SnowballOutgoingRequest request, out ISnowballTarget? live)
        {
            var visitor = findVisitor(request.TargetPetId);
            if (visitor?.TryGetSnowballTargetPhysical(out var target) == true)
            {
                live = visitor;
                return target;
            }
            var temporary = findTemporary(request.TargetPetId);
            if (temporary?.TryGetSnowballTargetPhysical(out target) == true)
            {
                live = temporary;
                return target;
            }
            live = null;
            if (!_offscreenDirections.TryGetValue(request.TargetPetId, out var unit))
            {
                if (_offscreenDirections.Count >= SnowballOutgoingQueue.Capacity)
                    _offscreenDirections.Remove(_offscreenDirections.Keys.First());
                _offscreenDirections.Add(request.TargetPetId, unit = Random.Shared.NextDouble());
            }
            var edge = SnowballSpawnGeometry.PerimeterPoint(monitor, unit);
            var normal = new Vector(edge.X == monitor.Left ? -1 : edge.X == monitor.Right ? 1 : 0,
                edge.Y == monitor.Top ? -1 : edge.Y == monitor.Bottom ? 1 : 0);
            return edge + normal * (radius * 2 + 4);
        }

        Vector Aim(SnowballOutgoingRequest request)
        {
            var direction = Resolve(request, out var actual) - source;
            if (_timeline.IsPreparing && request.EventId == _timeline.ActiveEventId)
                SetPreparingTarget(actual as SnowballTemporaryActor);
            if (direction.LengthSquared > 1) direction.Normalize();
            return direction;
        }

        void Launch(SnowballOutgoingRequest request)
        {
            SetHeld(false);
            if (!pet.TryGetSnowballHandPhysical(out var hand))
                throw new InvalidOperationException("내 펫 손의 화면 위치를 확인할 수 없습니다.");
            var target = Resolve(request, out var live);
            var pendingHold = live as SnowballTemporaryActor;
            pendingHold?.HoldForCountershot();
            SetPreparingTarget(null);

            Rect? clipMonitor = live is null ? monitor : null;
            var expanded = Rect.Empty;
            var lastTarget = target;
            var enteredEscapeMonitor = clipMonitor is not null;
            var height = 80.0;
            if (clipMonitor is not null && target.Y >= monitor.Top)
                height = Math.Clamp(Math.Min(hand.Y, target.Y) - monitor.Top, 0, height);
            if (clipMonitor is not null) SetEscapeBounds(monitor);

            void SetEscapeBounds(Rect bounds)
            {
                clipMonitor = expanded = bounds;
                expanded.Inflate(radius, radius);
            }

            void EndTravel()
            {
                pendingHold?.ReleaseCountershot();
                pendingHold = null;
            }

            Point? FollowTarget()
            {
                // 이미 날아가는 눈덩이도 같은 펫이 정식 방문으로 바뀌면 인계받는다.
                if (findVisitor(request.TargetPetId) is { } visitor && visitor.TryGetSnowballTargetPhysical(out var current))
                {
                    live = visitor;
                    EndTravel();
                    return lastTarget = current;
                }
                return live?.TryGetSnowballTargetPhysical(out var position) == true ? lastTarget = position : null;
            }

            (Point Destination, double Seconds) Escape()
            {
                EndTravel();
                if (!Win32WindowHelper.TryGetMonitorBoundsAt(lastTarget, out var bounds)) bounds = monitor;
                SetEscapeBounds(bounds);
                var outside = SnowballSpawnGeometry.BeyondMonitor(bounds, hand, lastTarget, radius * 2 + 4);
                return (outside, Math.Clamp((outside - lastTarget).Length / 900, 0.20, 0.60));
            }

            bool HasEscaped(Point point)
            {
                if (clipMonitor is null) return false;
                var stillVisible = expanded.Contains(point);
                if (enteredEscapeMonitor) return !stillVisible;
                enteredEscapeMonitor = stillVisible;
                return false;
            }

            try
            {
                if (!primary.Flights.TryLaunchPhysical(request.EventId, hand, target, 0.6, height,
                    point => place(point, enteredEscapeMonitor ? clipMonitor : null),
                    live is not null ? FollowTarget : null, () => live?.TriggerSnowballHit(), HasEscaped,
                    live is not null ? Escape : null, EndTravel))
                    throw new InvalidOperationException("등록한 눈덩이를 발사할 수 없습니다.");
            }
            catch
            {
                EndTravel();
                throw;
            }
        }

        _timeline.Advance(seconds, blocked, Aim, (direction, pose, laugh, step) =>
        {
            pet.AdvanceOutgoingSnowballPose(direction, pose, laugh, step);
            SetHeld(!blocked && _timeline.IsPreparing);
        }, Launch, deferLaugh: pet.IsReactingToSnowball);

        if (!_timeline.HasWork)
        {
            _offscreenDirections.Clear();
            SetHeld(false);
            pet.ReleaseOutgoingSnowballPose();
        }
    }

    private void SetHeld(bool visible)
    {
        if (!visible || !pet.TryGetSnowballHandPhysical(out var hand)
            || !primary.TryScreenPhysicalToSceneDip(hand, out var local))
        {
            if (_held is not null) _held.Visibility = Visibility.Collapsed;
            return;
        }
        if (_held is null)
        {
            _held = new Ellipse { Width = 14, Height = 14, Fill = Brushes.Snow, IsHitTestVisible = false };
            primary.Scene.Children.Add(_held);
        }
        Canvas.SetLeft(_held, local.X - 7);
        Canvas.SetTop(_held, local.Y - 7);
        _held.Visibility = Visibility.Visible;
    }

    private void SetPreparingTarget(SnowballTemporaryActor? target)
    {
        if (ReferenceEquals(target, _preparingTarget)) return;
        _preparingTarget?.ReleaseCountershot();
        _preparingTarget = target;
        target?.HoldForCountershot();
    }

    public void Dispose()
    {
        if (_held is not null) primary.Scene.Children.Remove(_held);
        _held = null;
        _offscreenDirections.Clear();
        SetPreparingTarget(null);
        pet.ReleaseOutgoingSnowballPose();
    }

    internal void Cancel()
    {
        Dispose();
        queue.Clear();
        _timeline = new(queue);
    }
}
