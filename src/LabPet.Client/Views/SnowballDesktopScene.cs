using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LabPet.Client.Diagnostics;
using LabPet.Client.Interop;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;

namespace LabPet.Client.Views;

/// <summary>인증된 수신 이벤트를 현재 데스크톱 펫과 모니터의 공용 장면에 적용한다.</summary>
internal sealed class SnowballDesktopScene : IDisposable
{
    private readonly PetWindow _pet;
    private readonly IPetMeshFactory _factory;
    private readonly Func<string, VisitorPetWindow?> _findVisitor;
    private readonly SnowballOutgoingQueue? _outgoingQueue;
    private readonly Dictionary<Rect, SnowballOverlayWindow> _otherOverlays = [];
    private SnowballOverlayWindow? _overlay;
    private SnowballSceneController? _scene;
    private Rect _monitor;
    private bool _disposed;
    private bool _wakeQueued;

    internal SnowballDesktopScene(PetWindow pet, IPetMeshFactory factory, Func<string, VisitorPetWindow?> findVisitor,
        SnowballOutgoingQueue? outgoingQueue = null)
    {
        _pet = pet;
        _factory = factory;
        _findVisitor = findVisitor;
        _outgoingQueue = outgoingQueue;
        pet.IsVisibleChanged += OnPetVisibilityChanged;
        pet.Closing += OnPetClosing;
    }

    internal bool TryReceive(Guid senderId, Guid eventId, PetAppearance appearance)
    {
        _pet.Dispatcher.VerifyAccess();
        if (_disposed || senderId == Guid.Empty || eventId == Guid.Empty || !_pet.IsAvailableForMessages
            || _pet.Dispatcher.HasShutdownStarted || !_pet.TryGetSnowballTargetPhysical(out _)
            || !Win32WindowHelper.TryGetNearestMonitorBounds(_pet, out var monitor)) return false;
        try
        {
            if (!TryUseMonitor(monitor)) return false;
            var scene = _scene!;
            var snapshot = appearance.Clone();
            var accepted = scene.TryReceive(senderId, eventId, id => CreateActor(senderId, id, snapshot));
            if (ReferenceEquals(_scene, scene) && !scene.HasWork) Reset();
            return accepted;
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("SnowballDesktopScene.Receive", ex);
            if (_scene?.HasWork != true) Reset();
            return false;
        }
    }

    internal void WakeOutgoing()
    {
        _pet.Dispatcher.VerifyAccess();
        if (_disposed || _wakeQueued || _pet.Dispatcher.HasShutdownStarted) return;
        _wakeQueued = true;
        _pet.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _wakeQueued = false;
            if (_disposed || _outgoingQueue is not { Count: > 0 }) return;
            try
            {
                if (_pet.TryGetSnowballTargetPhysical(out _) && Win32WindowHelper.TryGetNearestMonitorBounds(_pet, out var monitor)
                    && TryUseMonitor(monitor))
                {
                    _scene!.WakeOutgoing();
                    return;
                }
            }
            catch (Exception ex)
            {
                CrashLogWriter.Write("SnowballDesktopScene.Outgoing", ex);
            }
            _outgoingQueue.Clear();
            if (_scene?.HasWork != true) Reset();
        }));
    }

    internal void RemoveVisitor(string petId)
    {
        if (Guid.TryParse(petId, out var id)) _scene?.RemoveVisitor(id);
    }

    internal void PromoteVisitor(string petId, VisitorPetWindow visitor)
    {
        if (Guid.TryParse(petId, out var id)) _scene?.PromoteVisitor(id, visitor);
    }

    internal void RemoveOfflineSenders(IEnumerable<string> onlinePetIds)
    {
        _pet.Dispatcher.VerifyAccess();
        if (_scene is not { } scene) return;
        var online = new HashSet<Guid>();
        foreach (var petId in onlinePetIds)
            if (Guid.TryParse(petId, out var id)) online.Add(id);
        scene.RemoveOfflineSenders(online);
        if (!scene.HasWork) Reset();
    }

    private bool TryOpen(Rect monitor)
    {
        _overlay = new SnowballOverlayWindow();
        if (!_overlay.TryShowWithinMonitor(monitor, monitor))
        {
            Reset();
            return false;
        }
        _monitor = monitor;
        _scene = new SnowballSceneController(_overlay);
        if (_outgoingQueue is { } queue)
            _scene.AttachOutgoing(new SnowballOutgoingActor(_pet, queue, _overlay, () => _monitor,
                id => _findVisitor(id.ToString("D")), _scene.FindTemporary, PlaceFlight));
        _ = new SnowballSceneLoop(_overlay, _scene, GetTarget, _ => _pet.TryTriggerSnowballHit(),
            () =>
            {
                Reset();
                // 장면 종료와 같은 프레임에 전송이 완료된 경우 다시 시작한다.
                if (_outgoingQueue is { Count: > 0 }) WakeOutgoing();
            });
        return true;
    }

    private bool TryUseMonitor(Rect monitor)
    {
        if (_scene is null) return TryOpen(monitor);
        if (monitor == _monitor) return true;
        _scene.CancelReceiving();
        if (!_overlay!.TryShowWithinMonitor(monitor, monitor)) return false;
        if (_otherOverlays.Remove(monitor, out var duplicate)) duplicate.Dispose();
        _monitor = monitor;
        return true;
    }

    private SnowballFlightPlacement? PlaceFlight(Point point, Rect? lockedMonitor)
    {
        var probe = lockedMonitor is { } clip
            ? new Point(clip.Left + clip.Width / 2, clip.Top + clip.Height / 2) : point;
        if (!Win32WindowHelper.TryGetMonitorBoundsAt(probe, out var monitor)
            || (lockedMonitor is { } locked && locked != monitor)) return null;
        var overlay = _overlay!;
        if (monitor != _monitor && !_otherOverlays.TryGetValue(monitor, out overlay!))
        {
            overlay = new SnowballOverlayWindow();
            if (!overlay.TryShowWithinMonitor(monitor, monitor))
            {
                overlay.Dispose();
                return null;
            }
            _otherOverlays.Add(monitor, overlay);
        }
        return overlay.TryScreenPhysicalToSceneDip(point, out var local) ? new(overlay.Scene, local) : null;
    }

    private ISnowballActor CreateActor(Guid senderId, Guid eventId, PetAppearance appearance)
    {
        if (_findVisitor(senderId.ToString("D")) is { IsAvailableForMessages: true } visitor)
            return new SnowballVisitorActor(_overlay!, visitor, eventId);

        var scale = _pet.SnowballPetScale;
        var size = PetDisplayLayoutHelper.ComputeViewportSize(scale);
        var dpi = VisualTreeHelper.GetDpi(_overlay!);
        var sizePhysical = new Size(size.Width * dpi.DpiScaleX, size.Height * dpi.DpiScaleY);
        if (!_scene!.TrySelectSpawn(_monitor, sizePhysical, out var spawn))
            throw new InvalidOperationException("임시 펫의 등장 위치를 확보할 수 없습니다.");
        var visual = new SnowballPetVisual(_factory, appearance, scale);
        try
        {
            return new SnowballTemporaryActor(_overlay!, visual, spawn, eventId);
        }
        catch
        {
            visual.Dispose();
            throw;
        }
    }

    private Point? GetTarget()
    {
        if (!_pet.TryGetSnowballTargetPhysical(out var target)
            || !Win32WindowHelper.TryGetNearestMonitorBounds(_pet, out var monitor)
            || !TryUseMonitor(monitor)) return null;
        if (_otherOverlays.Count > 0)
        {
            foreach (var pair in _otherOverlays.ToArray())
            {
                if (pair.Value.Scene.Children.Count > 0) continue;
                _otherOverlays.Remove(pair.Key);
                pair.Value.Dispose();
            }
        }
        return target;
    }

    private void OnPetVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_pet.IsVisible)
        {
            _outgoingQueue?.Clear();
            Reset();
        }
    }

    private void OnPetClosing(object? sender, System.ComponentModel.CancelEventArgs e) => Dispose();

    private void Reset()
    {
        var overlay = _overlay;
        var scene = _scene;
        _overlay = null;
        _scene = null;
        _monitor = Rect.Empty;
        scene?.Dispose();
        overlay?.Dispose();
        foreach (var spare in _otherOverlays.Values) spare.Dispose();
        _otherOverlays.Clear();
    }

    public void Dispose()
    {
        _pet.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _pet.IsVisibleChanged -= OnPetVisibilityChanged;
        _pet.Closing -= OnPetClosing;
        _outgoingQueue?.Clear();
        Reset();
    }
}
