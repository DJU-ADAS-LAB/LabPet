using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LabPet.Client.Interop;

/// <summary>
/// 투명 펫 창의 클릭 투과(WS_EX_TRANSPARENT) 및 전역 마우스 좌표 조회를 담당하는 Win32 헬퍼.
/// </summary>
public static partial class Win32WindowHelper
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopMost = new(-1);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    internal static bool IsValidWindowHandle(IntPtr handle) => handle != IntPtr.Zero && IsWindow(handle);

    public static bool TryGetNearestWorkArea(Window window, out Rect bounds)
    {
        const uint monitorDefaultToNearest = 2;
        bounds = Rect.Empty;
        var handle = new WindowInteropHelper(window).Handle;
        if (!IsValidWindowHandle(handle))
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
        var width = work.Right - work.Left;
        var height = work.Bottom - work.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bounds = new Rect(work.Left, work.Top, width, height);
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hWnd, uint command);

    internal static bool IsWindowAbove(IntPtr higher, IntPtr lower)
    {
        const uint previousWindow = 3;
        const int maxWalkSteps = 4096;
        if (!IsValidWindowHandle(higher) || !IsValidWindowHandle(lower) || higher == lower)
        {
            return false;
        }

        var steps = 0;
        for (var window = GetWindow(lower, previousWindow);
             window != IntPtr.Zero && steps < maxWalkSteps;
             window = GetWindow(window, previousWindow), steps++)
        {
            if (window == higher)
            {
                return true;
            }
        }

        return false;
    }

    internal static void PlacePopupBehind(IntPtr popup, IntPtr higher)
    {
        if (!IsValidWindowHandle(popup) || !IsValidWindowHandle(higher) || popup == higher)
        {
            return;
        }

        if (!SetWindowPos(popup, higher, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder))
        {
            System.Diagnostics.Debug.WriteLine($"방문 펫 팝업 표시 순서 적용 실패 ({Marshal.GetLastPInvokeError()})");
        }
    }

    internal static void MovePopupPhysical(Visual visual, Point origin)
    {
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y)
            || PresentationSource.FromVisual(visual) is not HwndSource { IsDisposed: false, Handle: var handle }
            || !IsValidWindowHandle(handle)
            || !GetWindowRect(handle, out var bounds))
        {
            return;
        }

        var x = (int)Math.Round(origin.X);
        var y = (int)Math.Round(origin.Y);
        if ((bounds.Left != x || bounds.Top != y)
            && !SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
        {
            System.Diagnostics.Debug.WriteLine($"팝업 화면 좌표 적용 실패 ({Marshal.GetLastPInvokeError()})");
        }
    }

    internal static void SetPopupClickThrough(Visual visual)
    {
        if (PresentationSource.FromVisual(visual) is HwndSource { IsDisposed: false, Handle: var handle }
            && IsValidWindowHandle(handle))
        {
            var style = GetWindowLong(handle, GwlExStyle);
            _ = SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExNoActivate);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref NativeLastInputInfo plii);

    /// <summary>
    /// OS 전역 키보드·마우스 마지막 입력 이후 경과 시간을 조회한다.
    /// </summary>
    public static bool TryGetSystemIdleDuration(out TimeSpan idleDuration)
    {
        var info = new NativeLastInputInfo
        {
            Size = (uint)Marshal.SizeOf<NativeLastInputInfo>()
        };
        if (!GetLastInputInfo(ref info))
        {
            idleDuration = TimeSpan.Zero;
            return false;
        }

        idleDuration = ComputeIdleDurationFromTickDiff(unchecked((uint)Environment.TickCount), info.Time);
        return true;
    }

    internal static TimeSpan ComputeIdleDurationFromTickDiff(uint currentTickCount, uint lastInputTickCount)
    {
        var elapsedMilliseconds = unchecked(currentTickCount - lastInputTickCount);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    /// <summary>
    /// 창을 Alt+Tab 목록에서 숨기고 포커스를 빼앗지 않는 Non-Activating ToolWindow 스타일을 적용한다.
    /// WS_EX_TRANSPARENT는 부여하지 않으므로 펫 본체가 클릭 투과 중이어도 버튼 클릭을 수신한다.
    /// </summary>
    public static void ApplyNonActivatingToolWindowStyle(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        _ = SetWindowLong(hwnd, GwlExStyle, (exStyle | WsExToolWindow | WsExNoActivate) & ~WsExTransparent);
    }

    /// <summary>
    /// 혼합 DPI 다중 모니터 환경에서도 왜곡 없이 창을 지정한 물리 픽셀 좌표로 이동한다.
    /// </summary>
    public static bool MoveWindowPhysical(Window window, int physicalX, int physicalY)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!IsValidWindowHandle(hwnd))
        {
            return false;
        }

        return SetWindowPos(hwnd, HwndTopMost, physicalX, physicalY, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>
    /// 창의 현재 화면 물리 픽셀 경계를 조회한다.
    /// </summary>
    public static bool TryGetWindowPhysicalBounds(Window window, out Rect bounds)
    {
        bounds = Rect.Empty;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!IsValidWindowHandle(hwnd) || !GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 0 || height < 0)
        {
            return false;
        }

        bounds = new Rect(rect.Left, rect.Top, width, height);
        return true;
    }

    /// <summary>
    /// WPF Popup(WS_EX_NOACTIVATE) 내부 TextBox가 비활성 상태에서도 키보드 입력을 받을 수 있도록
    /// 실제 Popup HWND를 포그라운드로 활성화한다.
    /// </summary>
    public static void ActivatePopupForKeyboardInput(UIElement element)
    {
        if (PresentationSource.FromVisual(element) is HwndSource { IsDisposed: false, Handle: var handle }
            && IsValidWindowHandle(handle))
        {
            _ = SetForegroundWindow(handle);
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
