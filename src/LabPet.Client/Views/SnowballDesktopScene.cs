using System.Windows;
using System.Windows.Media;
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
    private SnowballOverlayWindow? _overlay;
    private SnowballSceneController? _scene;
    private Rect _monitor;
    private bool _disposed;

    internal SnowballDesktopScene(PetWindow pet, IPetMeshFactory factory, Func<string, VisitorPetWindow?> findVisitor)
    {
        _pet = pet;
        _factory = factory;
        _findVisitor = findVisitor;
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
            if (monitor != _monitor) Reset();
            if (_scene is null && !TryOpen(monitor)) return false;
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

    internal void RemoveVisitor(string petId)
    {
        if (Guid.TryParse(petId, out var id)) _scene?.RemoveVisitor(id);
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
        _ = new SnowballSceneLoop(_overlay, _scene, GetTarget, _ => _pet.TryTriggerSnowballHit(), Reset);
        return true;
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

    private Point? GetTarget() =>
        Win32WindowHelper.TryGetNearestMonitorBounds(_pet, out var monitor) && monitor == _monitor
        && _pet.TryGetSnowballTargetPhysical(out var target) ? target : null;

    private void OnPetVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_pet.IsVisible) Reset();
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
    }

    public void Dispose()
    {
        _pet.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _pet.IsVisibleChanged -= OnPetVisibilityChanged;
        _pet.Closing -= OnPetClosing;
        Reset();
    }
}
