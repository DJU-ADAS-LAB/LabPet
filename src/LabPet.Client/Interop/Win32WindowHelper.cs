using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LabPet.Client.Interop;

/// <summary>
/// 투명 펫 창의 클릭 투과(WS_EX_TRANSPARENT) 및 전역 마우스 좌표 조회를 담당하는 Win32 헬퍼.
/// </summary>
public static partial class Win32WindowHelper
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public int Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    public static bool TryGetNearestWorkArea(Window window, out Rect bounds)
    {
        const uint monitorDefaultToNearest = 2;
        bounds = Rect.Empty;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        var monitor = MonitorFromWindow(handle, monitorDefaultToNearest);
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }
        var work = info.Work;
        bounds = new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        return true;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// WPF Popup(WS_EX_NOACTIVATE) 내부 TextBox가 비활성 상태에서도 키보드 입력을 받을 수 있도록
    /// 실제 Popup HWND를 포그라운드로 활성화한다.
    /// </summary>
    public static void ActivatePopupForKeyboardInput(UIElement element)
    {
        if (PresentationSource.FromVisual(element) is HwndSource source && source.Handle != IntPtr.Zero)
        {
            _ = SetForegroundWindow(source.Handle);
        }
    }

    /// <summary>
    /// 창을 Alt+Tab 목록에서 숨기는 ToolWindow 스타일을 적용한다.
    /// </summary>
    public static void ApplyToolWindowStyle(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        _ = SetWindowLong(hwnd, GwlExStyle, exStyle | WsExToolWindow);
    }

    /// <summary>
    /// 마우스 클릭이 뒤쪽 작업 창으로 통과하는 클릭 투과 집중 모드를 켜거나 끈다.
    /// </summary>
    public static void SetClickThrough(Window window, bool enable)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        var updatedStyle = enable
            ? (exStyle | WsExTransparent)
            : (exStyle & ~WsExTransparent);

        _ = SetWindowLong(hwnd, GwlExStyle, updatedStyle);
    }

    /// <summary>
    /// 현재 전역 마우스 커서 위치(물리 픽셀)를 반환한다.
    /// </summary>
    public static bool TryGetGlobalCursorPosition(out Point physicalPoint)
    {
        if (GetCursorPos(out var pt))
        {
            physicalPoint = new Point(pt.X, pt.Y);
            return true;
        }

        physicalPoint = default;
        return false;
    }
}
