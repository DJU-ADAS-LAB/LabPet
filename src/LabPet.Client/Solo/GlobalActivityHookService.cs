using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LabPet.Client.Solo;

/// <summary>
/// 키 내용/문자는 일절 수집하지 않고 단순 키 입력 횟수와 마우스 클릭 횟수만 집계하는 저부하 활동 감지기.
/// 훅 설치가 거부된 환경에서도 예외로 앱이 종료되지 않도록 안전 폴백한다.
/// </summary>
public sealed class GlobalActivityHookService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;

    private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // GC 수집 방지를 위해 델리게이트를 필드로 유지
    private readonly LowLevelHookProc _keyboardProc;
    private readonly LowLevelHookProc _mouseProc;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    private long _pendingKeystrokes;
    private long _pendingMouseClicks;
    private bool _isDisposed;

    /// <summary>키가 눌릴 때마다 발생 (치비 고양이 앞발 두드리기 모션 연동용).</summary>
    public event Action? KeyTyped;

    public bool IsActive => _keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;

    public GlobalActivityHookService()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Start()
    {
        if (_isDisposed || IsActive)
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var hMod = GetModuleHandle(module?.ModuleName);

            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, hMod, 0);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, hMod, 0);
        }
        catch
        {
            // 권한/보안 소프트웨어 제한 시 안전 폴백
            Stop();
        }
    }

    public void Stop()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    public (long Keystrokes, long MouseClicks) DrainPendingCounts()
    {
        var keys = Interlocked.Exchange(ref _pendingKeystrokes, 0);
        var clicks = Interlocked.Exchange(ref _pendingMouseClicks, 0);
        return (keys, clicks);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
        {
            Interlocked.Increment(ref _pendingKeystrokes);
            KeyTyped?.Invoke();
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmLButtonDown || wParam == (IntPtr)WmRButtonDown))
        {
            Interlocked.Increment(ref _pendingMouseClicks);
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop();
    }
}
