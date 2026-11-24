using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Navigation;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 통합 창 홈 대시보드 ViewModel.
/// 실제 프로필·성장·친구·뽀모도로·연결 상태 데이터만 요약 표시한다.
/// </summary>
public sealed partial class HomePageViewModel : ObservableObject, IDisposable
{
    private readonly IHubNavigationService _navigation;
    private readonly PetWindowViewModel _petVm;
    private readonly SocialCoordinatorService _social;
    private readonly PomodoroTimerService _pomodoro;
    private readonly CoinGamesViewModel _games;
    private readonly PetPortraitRenderer _portraitRenderer;
    private readonly DispatcherTimer _refreshTimer;

    private bool _isActive;
    private bool _disposed;

    [ObservableProperty] private string _petName = "모찌";
    [ObservableProperty] private string _ownerNameText = "주인 미설정";
    [ObservableProperty] private string _speciesDisplayName = "말랑 모찌 정령";
    [ObservableProperty] private ImageSource? _petPortrait;

    [ObservableProperty] private int _level = 1;
    [ObservableProperty] private long _coins;
    [ObservableProperty] private int _dailySocialCoinsEarned;

    [ObservableProperty] private long _totalKeystrokes;
    [ObservableProperty] private long _totalMouseClicks;
    [ObservableProperty] private long _totalUptimeMinutes;
    [ObservableProperty] private int _completedPomodoros;

    [ObservableProperty] private int _onlineFriendCount;
    [ObservableProperty] private int _totalFriendCount;
    [ObservableProperty] private string _friendSummaryText = "등록된 친구 없음";
    [ObservableProperty] private string _connectionSummaryText = string.Empty;

    [ObservableProperty] private string _pomodoroPhaseText = "집중 시간";
    [ObservableProperty] private string _pomodoroRemainingText = "25:00";
    [ObservableProperty] private string _pomodoroStatusText = "대기 중";
    [ObservableProperty] private bool _isPomodoroRunning;

    public bool IsMaxLevel => Level >= LabPet.Client.Solo.PetEconomyService.MaxLevel;
    public long LevelUpCost => LabPet.Client.Solo.PetEconomyService.GetLevelUpCost(Level);
    public long LevelUpShortfallCoins => IsMaxLevel ? 0L : Math.Max(0L, LevelUpCost - Coins);
    public double LevelUpAffordProgressPercent => IsMaxLevel
        ? 100.0
        : LevelUpCost <= 0
            ? 100.0
            : Math.Clamp((double)Coins / LevelUpCost * 100.0, 0.0, 100.0);

    public string LevelText => IsMaxLevel
        ? $"Lv.{LabPet.Client.Solo.PetEconomyService.MaxLevel} MAX"
        : $"Lv.{Level}";
    public string LevelUpTargetLabel => IsMaxLevel ? "최고 레벨 달성" : $"다음 Lv.{Level + 1} 성장 비용";
    public string LevelUpProgressText => IsMaxLevel
        ? "MAX LEVEL"
        : Coins >= LevelUpCost
            ? $"레벨 업 가능 ({LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(LevelUpCost)} 코인)"
            : $"{LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(LevelUpCost)} 코인 ({LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(LevelUpShortfallCoins)} 부족)";

    public string CoinsText => $"{LabPet.Client.Formatting.CompactNumberFormatter.Format(Coins)} 코인";
    public string CoinsExactTooltip => $"보유 코인: {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(Coins)} 코인";
    public string TotalKeystrokesText => LabPet.Client.Formatting.CompactNumberFormatter.Format(TotalKeystrokes);
    public string TotalKeystrokesTooltip => $"누적 키보드 입력: {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(TotalKeystrokes)}타";
    public string TotalMouseClicksText => LabPet.Client.Formatting.CompactNumberFormatter.Format(TotalMouseClicks);
    public string TotalMouseClicksTooltip => $"누적 마우스 클릭: {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(TotalMouseClicks)}회";
    public string TotalUptimeText => LabPet.Client.Formatting.CompactNumberFormatter.FormatUptimeDuration(TotalUptimeMinutes);
    public string TotalUptimeTooltip => $"누적 함께한 시간: {LabPet.Client.Formatting.CompactNumberFormatter.FormatExactUptimeTooltip(TotalUptimeMinutes)}";
    public string CompletedPomodorosText => LabPet.Client.Formatting.CompactNumberFormatter.Format(CompletedPomodoros);
    public string CompletedPomodorosTooltip => $"누적 뽀모도로: {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(CompletedPomodoros)}회";
    internal bool IsRefreshTimerRunning => _refreshTimer.IsEnabled;

    public HomePageViewModel(
        IHubNavigationService navigation,
        PetWindowViewModel petVm,
        SocialCoordinatorService social,
        PomodoroTimerService pomodoro,
        CoinGamesViewModel games,
        PetPortraitRenderer portraitRenderer)
    {
        _navigation = navigation;
        _petVm = petVm;
        _social = social;
        _pomodoro = pomodoro;
        _games = games;
        _portraitRenderer = portraitRenderer;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => Refresh();

        Refresh();
    }

    public void Activate()
    {
        if (_disposed)
        {
            return;
        }

        if (!_isActive)
        {
            _isActive = true;
            _petVm.ProfileUpdated += OnProfileUpdated;
            _petVm.ProfileProgressUpdated += OnProfileUpdated;
            _games.ProfileChanged += OnProfileUpdated;
            _games.Economy.EconomyStateChanged += OnEconomyStateChanged;
            _pomodoro.Tick += OnPomodoroTick;
            _social.StateChanged += OnSocialStateChanged;
        }

        Refresh();
        if (!_refreshTimer.IsEnabled)
        {
            _refreshTimer.Start();
        }
    }

    public void Deactivate()
    {
        if (_refreshTimer.IsEnabled)
        {
            _refreshTimer.Stop();
        }

        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        _petVm.ProfileUpdated -= OnProfileUpdated;
        _petVm.ProfileProgressUpdated -= OnProfileUpdated;
        _games.ProfileChanged -= OnProfileUpdated;
        _games.Economy.EconomyStateChanged -= OnEconomyStateChanged;
        _pomodoro.Tick -= OnPomodoroTick;
        _social.StateChanged -= OnSocialStateChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Deactivate();
    }

    public void Refresh()
    {
        var profile = _petVm.CurrentProfile;
        var progress = profile.SoloProgress;

        PetName = string.IsNullOrWhiteSpace(profile.PetName) ? "모찌" : profile.PetName;
        OwnerNameText = string.IsNullOrWhiteSpace(profile.OwnerName) ? "주인 미설정" : profile.OwnerName;
        SpeciesDisplayName = PetCustomizationCatalog.Species
            .FirstOrDefault(s => string.Equals(s.Id, profile.Appearance.SpeciesId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? profile.Appearance.SpeciesId;
        PetPortrait = _portraitRenderer.GetPortrait(profile.Appearance);

        lock (progress)
        {
            PetEconomyService.NormalizeProgress(profile);
            Level = progress.Level;
            Coins = progress.Coins;
            DailySocialCoinsEarned = _games.Economy.GetDailySocialCoinsEarned(profile);
            TotalKeystrokes = progress.TotalKeystrokes;
            TotalMouseClicks = progress.TotalMouseClicks;
            TotalUptimeMinutes = progress.TotalUptimeMinutes;
            CompletedPomodoros = progress.CompletedPomodoros;
        }

        OnPropertyChanged(nameof(IsMaxLevel));
        OnPropertyChanged(nameof(LevelUpCost));
        OnPropertyChanged(nameof(LevelUpShortfallCoins));
        OnPropertyChanged(nameof(LevelUpAffordProgressPercent));
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(LevelUpTargetLabel));
        OnPropertyChanged(nameof(LevelUpProgressText));
        OnPropertyChanged(nameof(CoinsText));
        OnPropertyChanged(nameof(CoinsExactTooltip));
        OnPropertyChanged(nameof(TotalKeystrokesText));
        OnPropertyChanged(nameof(TotalKeystrokesTooltip));
        OnPropertyChanged(nameof(TotalMouseClicksText));
        OnPropertyChanged(nameof(TotalMouseClicksTooltip));
        OnPropertyChanged(nameof(TotalUptimeText));
        OnPropertyChanged(nameof(TotalUptimeTooltip));
        OnPropertyChanged(nameof(CompletedPomodorosText));
        OnPropertyChanged(nameof(CompletedPomodorosTooltip));

        var friends = _social.Friends;
        var peers = _social.DiscoveredPeers;
        var onlineCount = 0;
        foreach (var friend in friends)
        {
            if (peers.Any(p => string.Equals(p.PetId, friend.PetId, StringComparison.OrdinalIgnoreCase) && !p.RequiresUpdate))
            {
                onlineCount++;
            }
        }

        TotalFriendCount = friends.Count;
        OnlineFriendCount = onlineCount;
        FriendSummaryText = TotalFriendCount == 0
            ? "등록된 친구 없음"
            : $"온라인 {OnlineFriendCount}명 · 전체 {TotalFriendCount}명";
        ConnectionSummaryText = _social.ConnectionSummary;

        PomodoroPhaseText = _pomodoro.CurrentPhase == PomodoroPhase.Focus ? "집중 시간" : "휴식 시간";
        var rem = _pomodoro.Remaining;
        PomodoroRemainingText = $"{(int)rem.TotalMinutes:00}:{rem.Seconds:00}";
        IsPomodoroRunning = _pomodoro.IsRunning;
        PomodoroStatusText = IsPomodoroRunning ? "타이머 동작 중" : "대기 중";
    }

    private void OnProfileUpdated(object? sender, PetProfile e) => Refresh();
    private void OnPomodoroTick(object? sender, EventArgs e) => Refresh();

    private void OnEconomyStateChanged(object? sender, EconomyChangedEventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(Refresh);
        }
        else
        {
            Refresh();
        }
    }

    private void OnSocialStateChanged(object? sender, EventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(Refresh);
        }
        else
        {
            Refresh();
        }
    }

    [RelayCommand]
    private Task OpenAppearanceAsync() => _navigation.NavigateAsync(new NavigationRequest(HubPage.Appearance));

    [RelayCommand]
    private Task StartFocusAsync() => _navigation.NavigateAsync(
        new NavigationRequest(HubPage.Pomodoro, Action: HubNavigationAction.StartPomodoroFocus));

    [RelayCommand]
    private Task OpenGamesAsync() => _navigation.NavigateAsync(new NavigationRequest(HubPage.Games));

    [RelayCommand]
    private Task OpenFriendsAsync() => _navigation.NavigateAsync(new NavigationRequest(HubPage.Friends));

    [RelayCommand]
    private Task OpenGrowthAsync() => _navigation.NavigateAsync(new NavigationRequest(HubPage.Growth));

    [RelayCommand]
    private Task OpenNearbyFriendsAsync() => _navigation.NavigateAsync(
        new NavigationRequest(HubPage.Friends, SubSection: "Nearby"));
}
