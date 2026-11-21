using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Audio;
using LabPet.Client.Solo;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 성장 화면에 표시되는 코인 획득 방법 안내 항목.
/// </summary>
public sealed record CoinEarningGuideItem(string Title, string ConditionText, string RewardBadge);

/// <summary>
/// 개인 성장 현황 · 실시간 오디오 모니터 · 뽀모도로 타이머 · 코인 게임 허브 ViewModel.
/// </summary>
public sealed partial class SoloHubViewModel : ObservableObject, IDisposable
{
    public IReadOnlyList<CoinEarningGuideItem> CoinEarningGuides { get; } =
    [
        new("키보드 입력", $"{SoloProgressionService.KeystrokesPerReward}타마다 자동 적립", $"+{SoloProgressionService.KeystrokeRewardCoins} 코인"),
        new("마우스 클릭", $"{SoloProgressionService.MouseClicksPerReward}회마다 자동 적립", $"+{SoloProgressionService.MouseClickRewardCoins} 코인"),
        new("함께한 시간", "PC 사용 1분마다 적립", $"+{SoloProgressionService.UptimeRewardCoinsPerMinute} 코인"),
        new("뽀모도로 완료", "집중 세션 1회 완료 시", $"+{SoloProgressionService.PomodoroRewardCoins} 코인"),
        new("친구와 교류", "메시지·쓰다듬기 / 빠른 반응", "+2 / +1 코인"),
        new("미니게임", "하이로우·가위바위보·타이핑·사다리", "승리·정산 보상")
    ];

    private readonly PomodoroTimerService _pomodoro;
    private readonly IAudioAnalyzer _audioAnalyzer;

    private PetProfile _profile = new();
    private bool _disposed;

    public event EventHandler<PetProfile>? ProfileChanged;

    public CoinGamesViewModel Games { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveSection), nameof(IsGrowthSectionActive))]
    private int _selectedMainTabIndex;

    public SoloHubSection ActiveSection
    {
        get => (SoloHubSection)Math.Clamp(SelectedMainTabIndex, 0, 2);
        set => SelectedMainTabIndex = (int)value;
    }

    public bool IsGrowthSectionActive => SelectedMainTabIndex == (int)SoloHubSection.GrowthAudio;

    [ObservableProperty] private string _petName = "모찌";
    [ObservableProperty] private int _level = 1;
    [ObservableProperty] private long _xp;
    [ObservableProperty] private long _requiredXp = 100;
    [ObservableProperty] private double _xpPercent;
    [ObservableProperty] private long _coins;
    [ObservableProperty] private long _totalKeystrokes;
    [ObservableProperty] private long _totalMouseClicks;
    [ObservableProperty] private long _totalUptimeMinutes;
    [ObservableProperty] private int _completedPomodoros;

    // 실시간 오디오 상태
    [ObservableProperty] private string _audioDeviceName = "확인 중...";
    [ObservableProperty] private string _audioStatusText = "대기 중";
    [ObservableProperty] private double _bassPercent;
    [ObservableProperty] private double _midPercent;
    [ObservableProperty] private double _treblePercent;

    // 뽀모도로 상태
    [ObservableProperty] private string _pomodoroPhaseText = "집중 시간";
    [ObservableProperty] private string _pomodoroTimeText = "25:00";
    [ObservableProperty] private string _pomodoroToggleText = "시작";
    [ObservableProperty] private double _pomodoroRingDashOffset;

    public SoloHubViewModel(
        PomodoroTimerService pomodoro,
        IAudioAnalyzer audioAnalyzer,
        CoinGamesViewModel games)
    {
        _pomodoro = pomodoro;
        _audioAnalyzer = audioAnalyzer;
        Games = games;

        _pomodoro.Tick += OnPomodoroTick;
        Games.ProfileChanged += OnGamesProfileChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pomodoro.Tick -= OnPomodoroTick;
        Games.ProfileChanged -= OnGamesProfileChanged;
    }

    public void BindProfile(PetProfile profile)
    {
        _profile = profile;
        Games.BindProfile(profile);
        RefreshProgressUi();
        RefreshPomodoroUi();
        RefreshAudioUi();
    }

    public void SelectGamesTab() => SelectedMainTabIndex = (int)SoloHubSection.Games;

    [RelayCommand]
    private void SelectSection(SoloHubSection section) => ActiveSection = section;

    public void RefreshProgressUi()
    {
        var p = _profile.SoloProgress;
        PetName = _profile.PetName;
        Level = p.Level;
        Xp = p.Xp;
        RequiredXp = SoloProgressionService.GetRequiredXpForLevel(p.Level);
        XpPercent = RequiredXp > 0 ? Math.Clamp((double)Xp / RequiredXp * 100.0, 0.0, 100.0) : 0.0;
        Coins = p.Coins;
        TotalKeystrokes = p.TotalKeystrokes;
        TotalMouseClicks = p.TotalMouseClicks;
        TotalUptimeMinutes = p.TotalUptimeMinutes;
        CompletedPomodoros = p.CompletedPomodoros;
    }

    public void RefreshAudioUi()
    {
        var snap = _audioAnalyzer.GetCurrentSnapshot();
        AudioDeviceName = snap.ActiveDeviceName;
        AudioStatusText = !snap.HasAudioDevice
            ? "재생 장치 없음 (안전 폴백)"
            : snap.IsMusicActive
                ? (snap.IsBeat ? "비트 감지 · 음악 반응 중" : "음악 반응 중")
                : "대기 중 (무음)";

        BassPercent = Math.Round(snap.Bass * 100.0);
        MidPercent = Math.Round(snap.Mid * 100.0);
        TreblePercent = Math.Round(snap.Treble * 100.0);
    }

    public void RefreshPomodoroUi()
    {
        PomodoroPhaseText = _pomodoro.CurrentPhase == PomodoroPhase.Focus ? "집중 시간" : "휴식 시간";
        var rem = _pomodoro.Remaining;
        PomodoroTimeText = $"{(int)rem.TotalMinutes:00}:{rem.Seconds:00}";
        PomodoroToggleText = _pomodoro.IsRunning ? "일시정지" : "시작";

        var totalSeconds = Math.Max(1.0, (_pomodoro.CurrentPhase == PomodoroPhase.Focus ? _pomodoro.FocusMinutes : _pomodoro.BreakMinutes) * 60.0);
        var ratio = Math.Clamp(rem.TotalSeconds / totalSeconds, 0.0, 1.0);
        const double ringCircumference = 56.55;
        PomodoroRingDashOffset = ringCircumference * (1.0 - ratio);
    }

    [RelayCommand]
    private void TogglePomodoro()
    {
        _pomodoro.StartOrPause();
        RefreshPomodoroUi();
    }

    [RelayCommand]
    private void ResetPomodoro()
    {
        _pomodoro.Reset();
        RefreshPomodoroUi();
    }

    [RelayCommand]
    private void SetPomodoroPreset(string? minutesText)
    {
        if (int.TryParse(minutesText, out var mins))
        {
            _pomodoro.ConfigureDurations(mins, Math.Max(1, mins / 5));
            RefreshPomodoroUi();
        }
    }

    private void OnPomodoroTick(object? sender, EventArgs e) => RefreshPomodoroUi();

    private void OnGamesProfileChanged(object? sender, PetProfile updated)
    {
        _profile = updated;
        RefreshProgressUi();
        ProfileChanged?.Invoke(this, updated);
    }
}
