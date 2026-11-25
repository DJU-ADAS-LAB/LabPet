using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using LabPet.Client.Diagnostics;
using LabPet.Client.Views;

namespace LabPet.Client.Rendering;

/// <summary>모니터 장면의 공용 갱신 구독. 이벤트 수락 후 Wake를 호출하며 유휴 시 구독을 해제한다.</summary>
internal sealed class SnowballSceneLoop : IDisposable
{
    private readonly SnowballOverlayWindow _overlay;
    private readonly SnowballSceneController _scene;
    private readonly Func<Point?> _getTargetPhysical;
    private readonly Action<Guid> _onImpact;
    private readonly Action? _onFinished;
    private long _lastTimestamp;
    private bool _disposed;
    private bool _updating;
    internal bool IsRunning { get; private set; }

    internal SnowballSceneLoop(SnowballOverlayWindow overlay, SnowballSceneController scene,
        Func<Point?> getTargetPhysical, Action<Guid> onImpact, Action? onFinished = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(getTargetPhysical);
        ArgumentNullException.ThrowIfNull(onImpact);
        overlay.Dispatcher.VerifyAccess();
        _overlay = overlay;
        _scene = scene;
        _getTargetPhysical = getTargetPhysical;
        _onImpact = onImpact;
        _onFinished = onFinished;
        if (!_scene.TryAttachLoop(overlay, this))
            throw new InvalidOperationException("The scene is closed, belongs to another overlay, or already has a loop.");
        _overlay.Closed += OnClosed;
    }

    internal void Wake()
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_disposed || IsRunning || !_scene.HasWork) return;
        _lastTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        IsRunning = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || _updating) return;
        _overlay.Dispatcher.VerifyAccess();
        _updating = true;
        try
        {
            if (!_scene.HasWork) { Finish(); return; }
            var now = Stopwatch.GetTimestamp();
            var seconds = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            _lastTimestamp = now;
            var target = _getTargetPhysical();
            if (_disposed) return;
            if (target is not { } point)
            {
                _scene.Dispose();
                Finish();
                return;
            }
            _scene.Advance(seconds, point, _onImpact);
            if (!_scene.HasWork) Finish();
        }
        catch (Exception ex)
        {
            Stop();
            CrashLogWriter.Write("SnowballScene.Loop", ex);
            _scene.Dispose();
            Finish();
        }
        finally { _updating = false; }
    }

    private void Finish()
    {
        Stop();
        _onFinished?.Invoke();
    }

    private void Stop()
    {
        if (!IsRunning) return;
        CompositionTarget.Rendering -= OnRendering;
        IsRunning = false;
        _lastTimestamp = 0;
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Stop();
        _overlay.Closed -= OnClosed;
        _scene.DetachLoop(this);
    }
}
