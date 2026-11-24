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

    public int LastCompletedFocusMinutes { get; private set; } = 25;

    public event EventHandler<int>? FocusCompletedWithDuration;

    public void ConfigureDurations(int focusMinutes, int breakMinutes = 5)
    {
        FocusMinutes = Math.Clamp(focusMinutes, 1, 120);
        BreakMinutes = Math.Clamp(breakMinutes, 1, 30);
        if (!IsRunning)
        {
            CurrentPhase = PomodoroPhase.Focus;
            _activeSessionFocusMinutes = FocusMinutes;
            Remaining = TimeSpan.FromMinutes(FocusMinutes);
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }

    public void StartOrPause()
    {
        if (IsRunning)
        {
            _timer.Stop();
        }
        else
        {
            if (CurrentPhase == PomodoroPhase.Focus && Remaining >= TimeSpan.FromMinutes(FocusMinutes))
            {
                _activeSessionFocusMinutes = FocusMinutes;
            }
            _timer.Start();
        }

        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _timer.Stop();
        CurrentPhase = PomodoroPhase.Focus;
        _activeSessionFocusMinutes = FocusMinutes;
        Remaining = TimeSpan.FromMinutes(FocusMinutes);
        Tick?.Invoke(this, EventArgs.Empty);
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
            var completedFocusMinutes = Math.Clamp(_activeSessionFocusMinutes, 1, 120);
            LastCompletedFocusMinutes = completedFocusMinutes;
            CurrentPhase = PomodoroPhase.Break;
            Remaining = TimeSpan.FromMinutes(BreakMinutes);
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
