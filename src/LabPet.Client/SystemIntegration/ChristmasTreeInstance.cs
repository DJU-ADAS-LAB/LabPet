using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace LabPet.Client.SystemIntegration;

/// <summary>사용자·로그온 세션별 네이티브 트리 단일 실행과 기존 창 활성화.</summary>
internal sealed class ChristmasTreeInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _ready;
    private readonly MemoryMappedFile _window;
    private bool _disposed;
    public bool IsOwner { get; }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public ChristmasTreeInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("사용자 식별 정보를 확인하지 못했습니다.");
        var name = "Local\\LabPet.Christmas3D." + sid;
        _mutex = new Mutex(false, name + ".Mutex");
        try
        {
            try { IsOwner = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { IsOwner = true; }
            _ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Ready");
            try
            {
                _window = MemoryMappedFile.CreateOrOpen(name + ".Window", 8);
                if (IsOwner)
                {
                    _ready.Reset();
                    using var view = _window.CreateViewAccessor();
                    view.Write(0, 0L);
                }
            }
            catch
            {
                _ready.Dispose();
                throw;
            }
        }
        catch
        {
            if (IsOwner) _mutex.ReleaseMutex();
            _mutex.Dispose();
            throw;
        }
    }

    public void MarkReady()
    {
        if (!IsOwner) throw new InvalidOperationException();
        using var view = _window.CreateViewAccessor();
        view.Write(0, LabPetConsoleNative.GetConsoleWindow().ToInt64());
        _ready.Set();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public bool TryActivateExisting()
    {
        if (IsOwner || !_ready.WaitOne(5000)) return false;
        using var view = _window.CreateViewAccessor();
        var window = new IntPtr(view.ReadInt64(0));
        if (!IsWindow(window) || !IsWindowVisible(window)) return false;
        if (LabPetConsoleNative.IsIconic(window)) LabPetConsoleNative.ShowWindow(window, 9);
        if (GetForegroundWindow() == window) return true;
        SetForegroundWindow(window);
        // API 반환값뿐 아니라 실제 전경 창을 확인한다. 이미 활성화된 창도 성공이다.
        return GetForegroundWindow() == window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsOwner) _ready.Reset();
        _window.Dispose();
        _ready.Dispose();
        if (IsOwner) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
