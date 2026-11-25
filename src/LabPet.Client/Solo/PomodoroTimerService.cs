using System.Windows.Threading;

namespace LabPet.Client.Solo;

public enum PomodoroPhase
{
    Focus = 0,
    Break = 1
}

/// <summary>
/// 내장 뽀모도로 타이머 서비스. 집중 완료 시 보상을 트리거한다.
/// </summary>
public sealed class PomodoroTimerService
{
    private readonly DispatcherTimer _timer;

    public PomodoroPhase CurrentPhase { get; private set; } = PomodoroPhase.Focus;
    public int FocusMinutes { get; private set; } = 25;
    public int BreakMinutes { get; private set; } = 5;
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(25);
    public bool IsRunning => _timer.IsEnabled;

    public event EventHandler? Tick;
    public event EventHandler? FocusCompleted;
    public event EventHandler? BreakCompleted;

    public PomodoroTimerService()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
    }

    private int _activeSessionFocusMinutes = 25;
    private bool _isFocusSessionActive;

    public int LastCompletedFocusMinutes { get; private set; } = 25;

    public event EventHandler<int>? FocusCompletedWithDuration;
    public event EventHandler<bool>? FocusStateChanged;

    public const int MinimumFocusMinutes = 1;
    public const int MaximumFocusMinutes = 60;
    public bool IsDurationLocked { get; private set; }
    public bool IsFocusActive => _isFocusSessionActive;

    public void ConfigureDurations(int focusMinutes, int breakMinutes = 5)
    {
        _timer.Dispatcher.VerifyAccess();
        if (IsRunning || IsDurationLocked) return;
        FocusMinutes = Math.Clamp(focusMinutes, MinimumFocusMinutes, MaximumFocusMinutes);
        BreakMinutes = Math.Clamp(breakMinutes, 1, 30);
        CurrentPhase = PomodoroPhase.Focus;
        _activeSessionFocusMinutes = FocusMinutes;
        Remaining = TimeSpan.FromMinutes(FocusMinutes);
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void StartOrPause()
    {
        _timer.Dispatcher.VerifyAccess();
        var wasFocusActive = _isFocusSessionActive;
        if (IsRunning)
        {
            _timer.Stop();
        }
        else
        {
            if (CurrentPhase == PomodoroPhase.Focus)
            {
                if (!IsDurationLocked)
                {
                    _activeSessionFocusMinutes = FocusMinutes;
                }

                _isFocusSessionActive = true;
            }

            IsDurationLocked = true;
            _timer.Start();
        }

        if (wasFocusActive != _isFocusSessionActive)
        {
            FocusStateChanged?.Invoke(this, _isFocusSessionActive);
        }

        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _timer.Dispatcher.VerifyAccess();
        var wasFocusActive = _isFocusSessionActive;
        _timer.Stop();
        IsDurationLocked = false;
        _isFocusSessionActive = false;
        CurrentPhase = PomodoroPhase.Focus;
        _activeSessionFocusMinutes = FocusMinutes;
        Remaining = TimeSpan.FromMinutes(FocusMinutes);
        if (wasFocusActive)
        {
            FocusStateChanged?.Invoke(this, false);
        }

        Tick?.Invoke(this, EventArgs.Empty);
    }

    internal void CompleteCurrentPhaseForTest()
    {
        _timer.Dispatcher.VerifyAccess();
        Remaining = TimeSpan.FromSeconds(1);
        OnTimerTick(this, EventArgs.Empty);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (Remaining > TimeSpan.FromSeconds(1))
        {
            Remaining -= TimeSpan.FromSeconds(1);
            Tick?.Invoke(this, EventArgs.Empty);
            return;
        }

        _timer.Stop();
        if (CurrentPhase == PomodoroPhase.Focus)
        {
            var wasFocusActive = _isFocusSessionActive;
            var completedFocusMinutes = Math.Clamp(_activeSessionFocusMinutes, MinimumFocusMinutes, MaximumFocusMinutes);
            LastCompletedFocusMinutes = completedFocusMinutes;
            _isFocusSessionActive = false;
            CurrentPhase = PomodoroPhase.Break;
            Remaining = TimeSpan.FromMinutes(BreakMinutes);
            if (wasFocusActive)
            {
                FocusStateChanged?.Invoke(this, false);
            }

            FocusCompletedWithDuration?.Invoke(this, completedFocusMinutes);
            FocusCompleted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CurrentPhase = PomodoroPhase.Focus;
            _activeSessionFocusMinutes = FocusMinutes;
            Remaining = TimeSpan.FromMinutes(FocusMinutes);
            BreakCompleted?.Invoke(this, EventArgs.Empty);
        }

        Tick?.Invoke(this, EventArgs.Empty);
    }
}
