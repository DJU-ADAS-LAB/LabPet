using LabPet.Client.Interop;
using Microsoft.Win32;

namespace LabPet.Client.Solo;

/// <summary>
/// 활동 보상(함께한 시간 등) 계산을 위한 OS 잠금·절전·유휴 상태 조회 인터페이스.
/// </summary>
public interface ISystemActivityStateProvider
{
    bool IsSessionLocked { get; }
    bool IsSystemSuspended { get; }
    TimeSpan MaxActiveIdleThreshold { get; }
    bool IsUserActiveForUptime(out TimeSpan idleDuration);
}

/// <summary>
/// Windows 세션 잠금, 절전 모드 전환, 마지막 입력 이후 유휴 시간을 감시하여 유효 활동 여부만 허용한다.
/// </summary>
public sealed class SystemActivityStateMonitor : ISystemActivityStateProvider, IDisposable
{
    public delegate bool IdleDurationProbe(out TimeSpan idleDuration);

    private readonly IdleDurationProbe _idleProbe;
    private volatile bool _isSessionLocked;
    private volatile bool _isSystemSuspended;
    private bool _disposed;

    public event EventHandler? ActivityClockResetRequested;

    public TimeSpan MaxActiveIdleThreshold { get; }
    public bool IsSessionLocked => _isSessionLocked;
    public bool IsSystemSuspended => _isSystemSuspended;

    public SystemActivityStateMonitor(
        IdleDurationProbe? idleProbe = null,
        TimeSpan? maxActiveIdleThreshold = null,
        bool hookSystemEvents = true)
    {
        _idleProbe = idleProbe ?? Win32WindowHelper.TryGetSystemIdleDuration;
        MaxActiveIdleThreshold = maxActiveIdleThreshold ?? TimeSpan.FromSeconds(60);

        if (hookSystemEvents)
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
    }

    public bool IsUserActiveForUptime(out TimeSpan idleDuration)
    {
        if (_isSessionLocked || _isSystemSuspended)
        {
            idleDuration = TimeSpan.Zero;
            return false;
        }

        if (!_idleProbe(out idleDuration))
        {
            return false;
        }

        return idleDuration >= TimeSpan.Zero && idleDuration < MaxActiveIdleThreshold;
    }

    public void SetSessionLockedForTest(bool isLocked)
    {
        var wasLocked = _isSessionLocked;
        _isSessionLocked = isLocked;
        if (wasLocked && !isLocked)
        {
            ActivityClockResetRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetSystemSuspendedForTest(bool isSuspended)
    {
        var wasSuspended = _isSystemSuspended;
        _isSystemSuspended = isSuspended;
        if (wasSuspended && !isSuspended)
        {
            ActivityClockResetRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _isSessionLocked = true;
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _isSessionLocked = false;
                ActivityClockResetRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _isSystemSuspended = true;
                break;

            case PowerModes.Resume:
                _isSystemSuspended = false;
                ActivityClockResetRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
