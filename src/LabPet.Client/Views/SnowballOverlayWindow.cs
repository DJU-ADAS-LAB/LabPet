using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LabPet.Client.Interop;

namespace LabPet.Client.Views;

/// <summary>공용 갱신 루프가 관리하는 모니터별 연출 창. 자체 타이머나 렌더링 구독은 없다.</summary>
internal sealed class SnowballOverlayWindow : Window, IDisposable
{
    private bool _closed;
    private HwndSource? _source;
    internal Canvas Scene { get; } = new() { ClipToBounds = true, IsHitTestVisible = false };

    internal SnowballOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        SizeToContent = SizeToContent.Manual;
        Width = Height = 1;
        Content = Scene;
    }

    /// <summary>호출자는 실제 모니터와 필요한 연출 영역을 물리 픽셀로 전달한다.</summary>
    internal bool TryShowWithinMonitor(Rect monitorPhysical, Rect scenePhysical)
    {
        Dispatcher.VerifyAccess();
        if (_closed || !Valid(monitorPhysical) || !Valid(scenePhysical)) return false;
        var clipped = Rect.Intersect(monitorPhysical, scenePhysical);
        if (clipped.IsEmpty || clipped.Width < 1 || clipped.Height < 1) return false;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (!Win32WindowHelper.IsValidWindowHandle(handle)) return false;
        Win32WindowHelper.ApplyNonActivatingToolWindowStyle(this);
        Win32WindowHelper.SetClickThrough(this, true);
        if (!Win32WindowHelper.TrySetOverlayPhysicalBounds(this, clipped)) return false;
        Show();
        // Show 및 DPI 변경 과정에서 WPF가 제안한 경계를 적용할 수 있으므로 재확인한다.
        if (Win32WindowHelper.TrySetOverlayPhysicalBounds(this, clipped)) return true;
        Hide();
        return false;
    }

    internal bool TryScreenPhysicalToSceneDip(Point screenPhysical, out Point localDip)
    {
        Dispatcher.VerifyAccess();
        localDip = default;
        if (_closed || !IsVisible || !double.IsFinite(screenPhysical.X)
            || !double.IsFinite(screenPhysical.Y)
            || _source is not { IsDisposed: false }
            || !Win32WindowHelper.IsValidWindowHandle(_source.Handle)) return false;
        var point = Scene.PointFromScreen(screenPhysical);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        localDip = point;
        return true;
    }

    /// <summary>손·대상의 물리 픽셀 좌표와 궤적 높이를 같은 장면의 DIP로 변환한다.</summary>
    internal bool TryLaunchPhysical(Guid senderId, Guid eventId, Point handPhysical,
        Point targetPhysical, double durationSeconds, double arcHeightPhysical)
    {
        Dispatcher.VerifyAccess();
        if (!double.IsFinite(arcHeightPhysical) || arcHeightPhysical < 0
            || !TryScreenPhysicalToSceneDip(handPhysical, out var handDip)
            || !TryScreenPhysicalToSceneDip(targetPhysical, out var targetDip)
            || !TryScreenPhysicalToSceneDip(
                new Point(handPhysical.X, handPhysical.Y - arcHeightPhysical), out var arcDip)) return false;
        var heightDip = Math.Abs(handDip.Y - arcDip.Y);
        return Flights.TryLaunch(senderId, eventId, handDip, targetDip, durationSeconds, heightDip);
    }

    /// <summary>이동한 대상의 물리 픽셀 좌표를 변환하고 실제 명중 이벤트를 반환한다.</summary>
    internal IReadOnlyList<Guid> AdvanceFlightsPhysical(double seconds, Point targetPhysical)
    {
        Dispatcher.VerifyAccess();
        if (_flights is null) return Array.Empty<Guid>();
        if (!TryScreenPhysicalToSceneDip(targetPhysical, out var targetDip))
            targetDip = new Point(double.NaN, double.NaN);
        return _flights.Advance(seconds, targetDip);
    }

    /// <summary>종료 후에도 안전하며 비행 계층을 새로 생성하지 않는 발신자별 정리.</summary>
    internal void CancelSenderFlights(Guid senderId)
    {
        Dispatcher.VerifyAccess();
        _flights?.CancelSender(senderId);
    }

    internal void AdvanceOutgoingFlights(double seconds) => _flights?.AdvancePhysical(seconds);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowHook);
    }

    private static IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        if (message == 0x0084) { handled = true; return new IntPtr(-1); }
        return IntPtr.Zero;
    }

    private LabPet.Client.Rendering.SnowballFlightLayer? _flights;

    // 상태 조회만으로 비행 계층을 생성하지 않는다. Count에는 명중 후 남은 입자도 포함된다.
    internal bool HasActiveFlights
    {
        get
        {
            Dispatcher.VerifyAccess();
            return !_closed && _flights is { Count: > 0 };
        }
    }

    internal LabPet.Client.Rendering.SnowballFlightLayer Flights
    {
        get
        {
            Dispatcher.VerifyAccess();
            ObjectDisposedException.ThrowIf(_closed, this);
            return _flights ??= new LabPet.Client.Rendering.SnowballFlightLayer(Scene);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _flights?.Dispose();
        _flights = null;
        if (_source is { IsDisposed: false }) _source.RemoveHook(WindowHook);
        _source = null;
        Scene.Children.Clear();
        Content = null;
        base.OnClosed(e);
    }

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (!_closed) Close();
    }

    private static bool Valid(Rect rect) => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0
        && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
        && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);
}
