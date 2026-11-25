using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LabPet.Client.Interop;
using LabPet.Client.Views;

namespace LabPet.Client.Rendering;

/// <summary>이미 떠 있는 방문 펫의 리그에서 투척하고 팝업·입력 상태를 유지한다.</summary>
internal sealed class SnowballVisitorActor : ISnowballActor
{
    private readonly VisitorPetWindow _visitor;
    private readonly SnowballActorTimeline _timeline;
    private Ellipse? _held;
    private bool _disposed;
    private bool _resumeThrow;

    public SnowballOverlayWindow Overlay { get; }
    public SnowballActorPhase Phase => _timeline.Phase;
    public Rect OccupiedBounds => Win32WindowHelper.TryGetWindowPhysicalBounds(_visitor, out var bounds)
        ? bounds : Rect.Empty;

    internal SnowballVisitorActor(SnowballOverlayWindow overlay, VisitorPetWindow visitor, Guid eventId)
        : this(overlay, visitor, new SnowballActorTimeline(eventId, isVisitor: true))
    {
    }

    internal SnowballVisitorActor(SnowballOverlayWindow overlay, VisitorPetWindow visitor, SnowballActorTimeline timeline)
    {
        Overlay = overlay;
        _visitor = visitor;
        _timeline = timeline;
        _resumeThrow = timeline.ThrowElapsed > 0;
        if (!visitor.TryUseSnowballPose())
            throw new InvalidOperationException("방문 펫의 투척 자세를 시작할 수 없습니다.");
    }

    public bool TryEnqueue(Guid eventId) => !_disposed && _timeline.TryEnqueue(eventId);
    public void NotifyImpact(Guid eventId) => _timeline.NotifyImpact(eventId);

    public void Advance(double seconds, double lookX, double lookY, Action<Guid, Point> launch)
    {
        if (_disposed) return;
        if (_visitor.IsSnowballThrowBlocked)
        {
            HideHeld();
            if (!_visitor.AdvanceSnowballPose(SnowballActorPhase.Throwing, null, seconds, lookX, lookY))
                throw new InvalidOperationException("방문 펫의 대기 자세를 갱신할 수 없습니다.");
            return;
        }
        _timeline.Advance(seconds, id =>
        {
            HideHeld();
            if (!_visitor.TryGetSnowballHandPhysical(out var hand))
                throw new InvalidOperationException("방문 펫 손의 화면 위치를 확인할 수 없습니다.");
            launch(id, hand);
        }, (phase, before, step) =>
        {
            double? startFrom = phase == SnowballActorPhase.Throwing && (before == 0 || _resumeThrow) ? before : null;
            if (!_visitor.AdvanceSnowballPose(phase, startFrom, step, lookX, lookY))
                throw new InvalidOperationException("방문 펫의 투척 자세를 갱신할 수 없습니다.");
            _resumeThrow = false;

            if (phase == SnowballActorPhase.Throwing && _timeline.ThrowElapsed < SnowballThrowPose.ReleaseSeconds
                && _visitor.TryGetSnowballHandPhysical(out var hand)
                && Overlay.TryScreenPhysicalToSceneDip(hand, out var local))
            {
                if (_held is null)
                {
                    _held = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, IsHitTestVisible = false };
                    Overlay.Scene.Children.Add(_held);
                }
                Canvas.SetLeft(_held, local.X - _held.Width / 2);
                Canvas.SetTop(_held, local.Y - _held.Height / 2);
                _held.Visibility = Visibility.Visible;
            }
            else
            {
                HideHeld();
            }
        });
    }

    private void HideHeld()
    {
        if (_held is not null) _held.Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        Overlay.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _timeline.Cancel();
        if (_held is not null) Overlay.Scene.Children.Remove(_held);
        _visitor.ReleaseSnowballPose();
    }
}
