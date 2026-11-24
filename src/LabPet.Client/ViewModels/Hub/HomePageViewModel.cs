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
    [ObservableProperty] private string _friendCode = string.Empty;
    [ObservableProperty] private ImageSource? _petPortrait;

    [ObservableProperty] private int _level = 1;
    [ObservableProperty] private long _xp;
    [ObservableProperty] private long _requiredXp = 100;
    [ObservableProperty] private double _xpPercent;
    [ObservableProperty] private long _coins;

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

    public string LevelText => $"Lv.{Level}";
    public string XpProgressText => $"{Xp} / {RequiredXp} XP";
    public string CoinsText => $"{Coins} 코인";

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
        FriendCode = profile.FriendCode;
        PetPortrait = _portraitRenderer.GetPortrait(profile.Appearance);

        Level = progress.Level;
        Xp = progress.Xp;
        RequiredXp = SoloProgressionService.GetRequiredXpForLevel(progress.Level);
        XpPercent = RequiredXp > 0 ? Math.Clamp((double)Xp / RequiredXp * 100.0, 0.0, 100.0) : 0.0;
        Coins = progress.Coins;

        TotalKeystrokes = progress.TotalKeystrokes;
        TotalMouseClicks = progress.TotalMouseClicks;
        TotalUptimeMinutes = progress.TotalUptimeMinutes;
        CompletedPomodoros = progress.CompletedPomodoros;

        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(XpProgressText));
        OnPropertyChanged(nameof(CoinsText));

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
    private Task OpenNetworkSettingsAsync() => _navigation.NavigateAsync(
        new NavigationRequest(HubPage.Settings, SubSection: "Network"));
}
