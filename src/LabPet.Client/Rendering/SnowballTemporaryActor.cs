using System.Windows;
using LabPet.Client.Views;

namespace LabPet.Client.Rendering;

/// <summary>공용 루프에서 갱신하는 발신자 한 명의 투척 연출.</summary>
internal interface ISnowballActor : IDisposable
{
    SnowballOverlayWindow Overlay { get; }
    SnowballActorPhase Phase { get; }
    Rect OccupiedBounds { get; }
    bool TryEnqueue(Guid eventId);
    void NotifyImpact(Guid eventId);
    void Advance(double seconds, double lookX, double lookY, Action<Guid, Point> launch);
}

/// <summary>임시 리그, 진입 위치, 투척 시간표를 연결한다.</summary>
internal sealed class SnowballTemporaryActor : ISnowballActor
{
    private readonly SnowballOverlayWindow _overlay;
    private readonly SnowballPetVisual _visual;
    private readonly SnowballSpawnGeometry _spawn;
    private readonly SnowballActorTimeline _timeline;
    private bool _disposed;

    public SnowballOverlayWindow Overlay => _overlay;
    public SnowballActorPhase Phase => _timeline.Phase;
    public Rect OccupiedBounds => _spawn.VisibleBounds;

    internal SnowballTemporaryActor(SnowballOverlayWindow overlay, SnowballPetVisual visual,
        SnowballSpawnGeometry spawn, Guid firstEventId)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(visual);
        overlay.Dispatcher.VerifyAccess();
        visual.Dispatcher.VerifyAccess();
        if (visual.Parent is not null || spawn.VisibleBounds.IsEmpty
            || spawn.VisibleBounds.Width <= 0 || spawn.VisibleBounds.Height <= 0)
            throw new ArgumentException("An unattached visual and valid spawn are required.");
        _timeline = new SnowballActorTimeline(firstEventId, false);
        _overlay = overlay;
        _visual = visual;
        _spawn = spawn;
        _overlay.Scene.Children.Add(visual);
        if (!_visual.TryPlace(overlay, spawn.HiddenBounds))
        {
            _overlay.Scene.Children.Remove(visual);
            throw new InvalidOperationException("The snowball overlay is not ready for placement.");
        }
    }

    public bool TryEnqueue(Guid eventId)
    {
        _overlay.Dispatcher.VerifyAccess();
        return !_disposed && _timeline.TryEnqueue(eventId);
    }

    public void NotifyImpact(Guid eventId)
    {
        _overlay.Dispatcher.VerifyAccess();
        if (!_disposed) _timeline.NotifyImpact(eventId);
    }

    /// <summary>launch는 물리 픽셀 손 위치를 받는다. 실패 시 예외를 상위 관리자가 기록하고 정리한다.</summary>
    public void Advance(double seconds, double lookX, double lookY, Action<Guid, Point> launch)
    {
        _overlay.Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(launch);
        if (_disposed || !double.IsFinite(lookX) || !double.IsFinite(lookY)) return;
        _timeline.Advance(seconds, id =>
        {
            if (!_visual.TryGetHandPhysical(out var hand))
                throw new InvalidOperationException("Cannot project the snowball release hand.");
            launch(id, hand);
        }, (phase, before, step) =>
        {
            _visual.Animator.SetSnowballLaughing(phase == SnowballActorPhase.Laughing);
            if (phase == SnowballActorPhase.Throwing && before == 0
                && !_visual.Animator.TryStartSnowballThrow())
                throw new InvalidOperationException("Cannot start the scheduled snowball throw.");
            _visual.Animator.SetSnowballPeek(_spawn.Inward.X, _spawn.Inward.Y,
                _timeline.VisibilityProgress);
            _visual.Advance(step, lookX, lookY);
            if (!_visual.TryPlace(_overlay, _spawn.BoundsAt(_timeline.VisibilityProgress)))
                throw new InvalidOperationException("Cannot place the active snowball actor.");
            _overlay.UpdateLayout();
            _visual.SetSnowballHeld(phase == SnowballActorPhase.Throwing
                && _timeline.ThrowElapsed < SnowballThrowPose.ReleaseSeconds);
        });
        if (_timeline.Phase == SnowballActorPhase.Complete) Dispose();
    }

    public void Dispose()
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _timeline.Cancel();
        _overlay.Scene.Children.Remove(_visual);
        _visual.Dispose();
    }
}
