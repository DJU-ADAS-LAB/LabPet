using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Audio;
using LabPet.Client.Formatting;
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
        new("키보드 입력", $"{PetEconomyService.KeystrokesPerReward}타마다 자동 적립 (매크로·홀드 제외)", $"+{PetEconomyService.KeystrokeRewardCoins} 코인"),
        new("마우스 클릭", $"{PetEconomyService.MouseClicksPerReward}회마다 자동 적립 (오토클릭 제외)", $"+{PetEconomyService.MouseClickRewardCoins} 코인"),
        new("함께한 시간", $"활성 사용 {PetEconomyService.UptimeMinutesPerReward}분마다 (잠금·절전·유휴 제외)", $"+{PetEconomyService.UptimeRewardCoins} 코인"),
        new("뽀모도로 완료", $"집중 {PetEconomyService.PomodoroBaseMinutes}분당 +{PetEconomyService.PomodoroCoinsPer5Minutes} 코인 (15분 +6 / 25분 +10 / 50분 +20)", "집중 보상"),
        new("친구와 교류", $"메시지 · 인사하기(송/수신) · 일일 최대 {PetEconomyService.DailySocialCoinCap}코인", "+1 코인 (반응 0)"),
        new("미니게임", "하이로우 · 가위바위보 · 타이핑 · 사다리", "승리·정산 보상")
    ];

    private readonly PomodoroTimerService _pomodoro;
    private readonly IAudioAnalyzer _audioAnalyzer;
    private readonly PetEconomyService _economy;

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

    public bool IsGrowthSectionActive => SelectedMainTabIndex == (int)SoloHubSection.Games ? false : SelectedMainTabIndex == (int)SoloHubSection.GrowthAudio;

    [ObservableProperty] private string _petName = "모찌";

    [ObservableProperty, NotifyPropertyChangedFor(
        nameof(IsMaxLevel),
        nameof(LevelDisplayText),
        nameof(NextLevelLabelText),
        nameof(LevelUpCost),
        nameof(LevelUpCostExactText),
        nameof(LevelUpShortfallCoins),
        nameof(ProjectedRemainingCoins),
        nameof(CanLevelUp),
        nameof(LevelUpAffordProgressPercent),
        nameof(LevelUpHintText),
        nameof(LevelUpButtonText))]
    [NotifyCanExecuteChangedFor(nameof(LevelUpCommand))]
    private int _level = PetEconomyService.MinLevel;

    [ObservableProperty, NotifyPropertyChangedFor(
        nameof(ExactHeldCoinsText),
        nameof(CompactCoinsText),
        nameof(LevelUpShortfallCoins),
        nameof(ProjectedRemainingCoins),
        nameof(CanLevelUp),
        nameof(LevelUpAffordProgressPercent),
        nameof(LevelUpHintText),
        nameof(LevelUpButtonText))]
    [NotifyCanExecuteChangedFor(nameof(LevelUpCommand))]
    private long _coins;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DailySocialCapText))]
    private int _dailySocialCoinsEarned;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalKeystrokesCompactText), nameof(TotalKeystrokesExactTooltip))]
    private long _totalKeystrokes;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalMouseClicksCompactText), nameof(TotalMouseClicksExactTooltip))]
    private long _totalMouseClicks;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalUptimeDisplayText), nameof(TotalUptimeExactTooltip))]
    private long _totalUptimeMinutes;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CompletedPomodorosCompactText), nameof(CompletedPomodorosExactTooltip))]
    private int _completedPomodoros;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeystrokeProgressText))]
    private int _pendingKeystrokesForReward;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MouseClickProgressText))]
    private int _pendingMouseClicksForReward;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UptimeProgressText))]
    private int _pendingUptimeMinutesForReward;

    [ObservableProperty] private string _levelUpStatusText = string.Empty;

    public bool IsMaxLevel => Level >= PetEconomyService.MaxLevel;
    public string LevelDisplayText => IsMaxLevel ? $"Lv.{PetEconomyService.MaxLevel} MAX" : $"Lv.{Level}";
    public string NextLevelLabelText => IsMaxLevel ? "최고 레벨 달성" : $"다음 목표: Lv.{Level + 1}";
    public long LevelUpCost => PetEconomyService.GetLevelUpCost(Level);
    public string LevelUpCostExactText => IsMaxLevel ? "MAX LEVEL" : $"{CompactNumberFormatter.FormatExact(LevelUpCost)} 코인";
    public string ExactHeldCoinsText => $"{CompactNumberFormatter.FormatExact(Coins)} 코인";
    public string CompactCoinsText => CompactNumberFormatter.Format(Coins);
    public long LevelUpShortfallCoins => IsMaxLevel ? 0L : Math.Max(0L, LevelUpCost - Coins);
    public long ProjectedRemainingCoins => IsMaxLevel ? Coins : Math.Max(0L, Coins - LevelUpCost);
    public bool CanLevelUp => !IsMaxLevel && Coins >= LevelUpCost;

    public double LevelUpAffordProgressPercent => IsMaxLevel
        ? 100.0
        : LevelUpCost <= 0
            ? 100.0
            : Math.Clamp((double)Coins / LevelUpCost * 100.0, 0.0, 100.0);

    public string LevelUpHintText => IsMaxLevel
        ? $"최고 레벨 Lv.{PetEconomyService.MaxLevel}에 도달했습니다."
        : CanLevelUp
            ? $"레벨 업 후 예상 보유 코인: {CompactNumberFormatter.FormatExact(ProjectedRemainingCoins)} 코인"
            : $"{CompactNumberFormatter.FormatExact(LevelUpShortfallCoins)} 코인이 더 필요합니다.";

    public string LevelUpButtonText => IsMaxLevel
        ? "MAX LEVEL"
        : CanLevelUp
            ? $"레벨 업 (-{CompactNumberFormatter.FormatExact(LevelUpCost)} 코인)"
            : $"코인 부족 ({CompactNumberFormatter.FormatExact(LevelUpShortfallCoins)} 부족)";

    public string DailySocialCapText => $"오늘 소셜 코인 {DailySocialCoinsEarned} / {PetEconomyService.DailySocialCoinCap}";
    public string TotalKeystrokesCompactText => CompactNumberFormatter.Format(TotalKeystrokes);
    public string TotalKeystrokesExactTooltip => $"누적 키보드 입력: {CompactNumberFormatter.FormatExact(TotalKeystrokes)}타";
    public string TotalMouseClicksCompactText => CompactNumberFormatter.Format(TotalMouseClicks);
    public string TotalMouseClicksExactTooltip => $"누적 마우스 클릭: {CompactNumberFormatter.FormatExact(TotalMouseClicks)}회";
    public string TotalUptimeDisplayText => CompactNumberFormatter.FormatUptimeDuration(TotalUptimeMinutes);
    public string TotalUptimeExactTooltip => $"누적 함께한 시간: {CompactNumberFormatter.FormatExactUptimeTooltip(TotalUptimeMinutes)}";
    public string CompletedPomodorosCompactText => CompactNumberFormatter.Format(CompletedPomodoros);
    public string CompletedPomodorosExactTooltip => $"누적 뽀모도로 완료: {CompactNumberFormatter.FormatExact(CompletedPomodoros)}회";

    public string KeystrokeProgressText => $"다음 코인까지 {PendingKeystrokesForReward} / {PetEconomyService.KeystrokesPerReward}타";
    public string MouseClickProgressText => $"다음 코인까지 {PendingMouseClicksForReward} / {PetEconomyService.MouseClicksPerReward}회";
    public string UptimeProgressText => $"다음 코인까지 {PendingUptimeMinutesForReward} / {PetEconomyService.UptimeMinutesPerReward}분";

    // 실시간 오디오 상태
    [ObservableProperty] private string _audioDeviceName = "확인 중...";
    [ObservableProperty] private string _audioStatusText = "대기 중";
    [ObservableProperty] private double _bassPercent;
    [ObservableProperty] private double _midPercent;
    [ObservableProperty] private double _treblePercent;

    private static readonly Brush PomodoroFocusArcBrush = CreateFrozenBrush("#FFFF5D8F");
    private static readonly Brush PomodoroBreakArcBrush = CreateFrozenBrush("#FF10B981");

    // 뽀모도로 원형 다이얼 좌표 상수 (200x200 캔버스 기준)
    public const double PomodoroDialSize = 200.0;
    public const double PomodoroDialCenter = 100.0;
    public const double PomodoroDialRadius = 86.0;
    private const double PomodoroKnobDiameter = 20.0;

    // 뽀모도로 상태
    [ObservableProperty] private string _pomodoroPhaseText = "집중 시간";
    [ObservableProperty] private string _pomodoroTimeText = "25:00";
    [ObservableProperty] private string _pomodoroToggleText = "시작";
    [ObservableProperty] private double _pomodoroRingDashOffset;
    [ObservableProperty] private double _pomodoroDialProgressRatio = 25.0 / PomodoroTimerService.MaximumFocusMinutes;
    [ObservableProperty] private Geometry _pomodoroDialArcGeometry = Geometry.Empty;
    [ObservableProperty] private Brush _pomodoroDialArcBrush = PomodoroFocusArcBrush;
    [ObservableProperty] private double _pomodoroKnobLeft;
    [ObservableProperty] private double _pomodoroKnobTop;
    [ObservableProperty] private string _pomodoroRewardPreviewText = $"25분 설정 · 완료 시 +{PetEconomyService.GetPomodoroRewardCoins(25)} 코인";
    [ObservableProperty] private string _pomodoroFocusMinutesInputText = "25";

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public SoloHubViewModel(
        PomodoroTimerService pomodoro,
        IAudioAnalyzer audioAnalyzer,
        CoinGamesViewModel games,
        PetEconomyService? economy = null)
    {
        _pomodoro = pomodoro;
        _audioAnalyzer = audioAnalyzer;
        Games = games;
        _economy = economy ?? games.Economy;

        _pomodoro.Tick += OnPomodoroTick;
        Games.ProfileChanged += OnGamesProfileChanged;
        _economy.EconomyStateChanged += OnEconomyStateChanged;
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
        _economy.EconomyStateChanged -= OnEconomyStateChanged;
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

    [RelayCommand(CanExecute = nameof(CanLevelUp))]
    private void LevelUp()
    {
        if (_economy.TryLevelUp(_profile, out var result))
        {
            LevelUpStatusText = result.NewLevel >= PetEconomyService.MaxLevel
                ? $"Lv.{result.NewLevel} MAX LEVEL 달성! (-{CompactNumberFormatter.FormatExact(result.CostCoins)} 코인)"
                : $"Lv.{result.NewLevel} 레벨 업 완료! (-{CompactNumberFormatter.FormatExact(result.CostCoins)} 코인)";
            RefreshProgressUi();
            ProfileChanged?.Invoke(this, _profile);
        }
        else if (result.FailureReason == LevelUpFailureReason.MaxLevelReached)
        {
            LevelUpStatusText = $"최고 레벨(Lv.{PetEconomyService.MaxLevel})에 도달했습니다.";
        }
        else
        {
            LevelUpStatusText = $"코인이 {CompactNumberFormatter.FormatExact(result.ShortfallCoins)} 부족합니다.";
        }
    }

    public void RefreshProgressUi()
    {
        PetProfileSnapshot snapshot;
        lock (_profile.SoloProgress)
        {
            PetEconomyService.NormalizeProgress(_profile);
            var p = _profile.SoloProgress;
            snapshot = new PetProfileSnapshot(
                _profile.PetName,
                p.Level,
                p.Coins,
                _economy.GetDailySocialCoinsEarned(_profile),
                p.TotalKeystrokes,
                p.TotalMouseClicks,
                p.TotalUptimeMinutes,
                p.CompletedPomodoros,
                p.PendingKeystrokesForReward,
                p.PendingMouseClicksForReward,
                p.PendingUptimeMinutesForReward);
        }

        PetName = snapshot.PetName;
        Level = snapshot.Level;
        Coins = snapshot.Coins;
        DailySocialCoinsEarned = snapshot.DailySocialCoinsEarned;
        TotalKeystrokes = snapshot.TotalKeystrokes;
        TotalMouseClicks = snapshot.TotalMouseClicks;
        TotalUptimeMinutes = snapshot.TotalUptimeMinutes;
        CompletedPomodoros = snapshot.CompletedPomodoros;
        PendingKeystrokesForReward = snapshot.PendingKeystrokesForReward;
        PendingMouseClicksForReward = snapshot.PendingMouseClicksForReward;
        PendingUptimeMinutesForReward = snapshot.PendingUptimeMinutesForReward;
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
        OnPropertyChanged(nameof(CanConfigurePomodoro));
        OnPropertyChanged(nameof(IsPomodoroDurationLocked));
        OnPropertyChanged(nameof(IsPomodoroFocusActive));
        OnPropertyChanged(nameof(PomodoroFocusMinutes));
        OnPropertyChanged(nameof(PomodoroLockHintText));
        PomodoroFocusMinutesInputText = _pomodoro.FocusMinutes.ToString();
        SetPomodoroPresetCommand.NotifyCanExecuteChanged();
        DecrementPomodoroMinutesCommand.NotifyCanExecuteChanged();
        IncrementPomodoroMinutesCommand.NotifyCanExecuteChanged();
        PomodoroPhaseText = _pomodoro.CurrentPhase == PomodoroPhase.Focus ? "집중 시간" : "휴식 시간";
        var rem = _pomodoro.Remaining;
        PomodoroTimeText = $"{(int)rem.TotalMinutes:00}:{rem.Seconds:00}";
        PomodoroToggleText = _pomodoro.IsRunning ? "일시정지" : "시작";
        var rewardCoins = PetEconomyService.GetPomodoroRewardCoins(_pomodoro.FocusMinutes);
        PomodoroRewardPreviewText = _pomodoro.IsDurationLocked
            ? $"{_pomodoro.FocusMinutes}분 설정 · 완료 시 +{rewardCoins} 코인 · 종료 전까지 시간 고정"
            : $"{_pomodoro.FocusMinutes}분 설정 · 완료 시 +{rewardCoins} 코인 (5분당 +{PetEconomyService.PomodoroCoinsPer5Minutes} 코인)";

        var totalSeconds = Math.Max(1.0, (_pomodoro.CurrentPhase == PomodoroPhase.Focus ? _pomodoro.FocusMinutes : _pomodoro.BreakMinutes) * 60.0);
        var ratio = Math.Clamp(rem.TotalSeconds / totalSeconds, 0.0, 1.0);
        const double ringCircumference = 56.55;
        PomodoroRingDashOffset = ringCircumference * (1.0 - ratio);

        var dialRatio = PomodoroCircularDialMath.ComputeDisplayProgressRatio(_pomodoro);
        PomodoroDialProgressRatio = dialRatio;
        PomodoroDialArcBrush = _pomodoro.CurrentPhase == PomodoroPhase.Break ? PomodoroBreakArcBrush : PomodoroFocusArcBrush;
        PomodoroDialArcGeometry = PomodoroCircularDialMath.BuildArcGeometry(
            PomodoroDialCenter, PomodoroDialCenter, PomodoroDialRadius, dialRatio);
        var knobCenter = PomodoroCircularDialMath.ComputeKnobCenter(
            PomodoroDialCenter, PomodoroDialCenter, PomodoroDialRadius, dialRatio);
        PomodoroKnobLeft = knobCenter.X - PomodoroKnobDiameter * 0.5;
        PomodoroKnobTop = knobCenter.Y - PomodoroKnobDiameter * 0.5;
    }

    [RelayCommand]
    private void TogglePomodoro()
    {
        if (CanConfigurePomodoro)
        {
            CommitPomodoroFocusMinutesInput();
        }

        _pomodoro.StartOrPause();
        RefreshPomodoroUi();
    }

    [RelayCommand]
    private void ResetPomodoro()
    {
        _pomodoro.Reset();
        RefreshPomodoroUi();
    }

    public bool CanConfigurePomodoro => !_pomodoro.IsDurationLocked;
    public bool IsPomodoroDurationLocked => _pomodoro.IsDurationLocked;
    public bool IsPomodoroFocusActive => _pomodoro.IsFocusActive;
    public string PomodoroLockHintText => _pomodoro.IsDurationLocked
        ? "종료 전까지 시간을 바꿀 수 없어요"
        : "원형을 돌려 1~60분 설정";

    public int PomodoroFocusMinutes
    {
        get => _pomodoro.FocusMinutes;
        set
        {
            if (!CanConfigurePomodoro)
            {
                OnPropertyChanged(nameof(PomodoroFocusMinutes));
                PomodoroFocusMinutesInputText = _pomodoro.FocusMinutes.ToString();
                return;
            }

            var minutes = Math.Clamp(value, PomodoroTimerService.MinimumFocusMinutes, PomodoroTimerService.MaximumFocusMinutes);
            _pomodoro.ConfigureDurations(minutes, Math.Max(1, minutes / 5));
            RefreshPomodoroUi();
        }
    }

    public void CommitPomodoroFocusMinutesInput()
    {
        if (!CanConfigurePomodoro)
        {
            PomodoroFocusMinutesInputText = _pomodoro.FocusMinutes.ToString();
            return;
        }

        if (int.TryParse((PomodoroFocusMinutesInputText ?? string.Empty).Trim(), out var parsed))
        {
            PomodoroFocusMinutes = parsed;
        }
        else
        {
            PomodoroFocusMinutesInputText = _pomodoro.FocusMinutes.ToString();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigurePomodoro))]
    private void DecrementPomodoroMinutes()
    {
        if (CanConfigurePomodoro)
        {
            PomodoroFocusMinutes = Math.Max(PomodoroTimerService.MinimumFocusMinutes, PomodoroFocusMinutes - 1);
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigurePomodoro))]
    private void IncrementPomodoroMinutes()
    {
        if (CanConfigurePomodoro)
        {
            PomodoroFocusMinutes = Math.Min(PomodoroTimerService.MaximumFocusMinutes, PomodoroFocusMinutes + 1);
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigurePomodoro))]
    private void SetPomodoroPreset(string? minutesText)
    {
        if (CanConfigurePomodoro && int.TryParse(minutesText, out var mins))
        {
            PomodoroFocusMinutes = mins;
        }
    }

    private void OnPomodoroTick(object? sender, EventArgs e) => RefreshPomodoroUi();

    private void OnGamesProfileChanged(object? sender, PetProfile updated)
    {
        _profile = updated;
        RefreshProgressUi();
        ProfileChanged?.Invoke(this, updated);
    }

    private void OnEconomyStateChanged(object? sender, EconomyChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshProgressUi);
            return;
        }

        RefreshProgressUi();
    }

    private readonly record struct PetProfileSnapshot(
        string PetName,
        int Level,
        long Coins,
        int DailySocialCoinsEarned,
        long TotalKeystrokes,
        long TotalMouseClicks,
        long TotalUptimeMinutes,
        int CompletedPomodoros,
        int PendingKeystrokesForReward,
        int PendingMouseClicksForReward,
        int PendingUptimeMinutesForReward);
}
