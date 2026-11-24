using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Navigation;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 좌측 사이드바 단일 메뉴 항목 메타데이터.
/// </summary>
public sealed partial class HubNavigationItemViewModel : ObservableObject
{
    public HubPage Page { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string IconResourceKey { get; }

    [ObservableProperty]
    private bool _isSelected;

    public HubNavigationItemViewModel(HubPage page, string title, string subtitle, string iconResourceKey)
    {
        Page = page;
        Title = title;
        Subtitle = subtitle;
        IconResourceKey = iconResourceKey;
    }
}

/// <summary>
/// 통합 관리 창(HubWindow) 셸 ViewModel.
/// 사이드바 선택 상태, 펫 요약 프로필(초상·이름·레벨·코인), 접힘 폭 및 초대 배너 연동 상태를 관리한다.
/// </summary>
public sealed partial class HubViewModel : ObservableObject, IDisposable
{
    public const double ExpandedSidebarWidth = 196.0;
    public const double CollapsedSidebarWidth = 56.0;

    private readonly IHubNavigationService _navigation;
    private readonly PetWindowViewModel _petViewModel;
    private readonly PetPortraitRenderer _portraitRenderer;
    private bool _disposed;

    public CoinGamesViewModel Games { get; }

    public IReadOnlyList<HubNavigationItemViewModel> PrimaryNavItems { get; }
    public IReadOnlyList<HubNavigationItemViewModel> FooterNavItems { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentPageTitle), nameof(CurrentPageSubtitle))]
    private HubPage _selectedPage = HubPage.Home;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SidebarWidth), nameof(IsSidebarExpanded), nameof(SidebarToggleToolTip))]
    private bool _isSidebarCollapsed;

    [ObservableProperty] private string _petName = "모찌";
    [ObservableProperty] private string _ownerName = string.Empty;
    [ObservableProperty] private int _level = 1;
    [ObservableProperty] private long _coins;
    [ObservableProperty] private ImageSource? _petPortrait;

    public double SidebarWidth => IsSidebarCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;
    public bool IsSidebarExpanded => !IsSidebarCollapsed;
    public string SidebarToggleToolTip => IsSidebarCollapsed ? "사이드바 펼치기" : "사이드바 접기";

    public string CurrentPageTitle => FindNavItem(SelectedPage)?.Title ?? "홈";
    public string CurrentPageSubtitle => FindNavItem(SelectedPage)?.Subtitle ?? string.Empty;
    public string LevelBadgeText => $"Lv.{Level}";
    public string CoinsBadgeText => $"{Coins} 코인";

    public HubViewModel(
        IHubNavigationService navigation,
        PetWindowViewModel petViewModel,
        CoinGamesViewModel games,
        PetPortraitRenderer portraitRenderer)
    {
        _navigation = navigation;
        _petViewModel = petViewModel;
        Games = games;
        _portraitRenderer = portraitRenderer;

        PrimaryNavItems =
        [
            new(HubPage.Home, "홈", "펫 상태 요약 · 빠른 실행", "IconHomeGeometry"),
            new(HubPage.Appearance, "외형 편집", "캐릭터 · 색상 · 표정 · 액세서리 · 오라", "IconPaletteGeometry"),
            new(HubPage.Friends, "친구", "내 친구 목록 · 주변 펫 · 노래 따라하기", "IconUsersGeometry"),
            new(HubPage.Growth, "성장", "레벨 · 활동 기록 · 오디오 반응", "IconTrendUpGeometry"),
            new(HubPage.Pomodoro, "뽀모도로", "집중 및 휴식 타이머 · 완료 보상", "IconClockGeometry"),
            new(HubPage.Games, "게임", "하이로우 · 가위바위보 · 타이핑 · 사다리", "IconGamepadGeometry"),
            new(HubPage.Seasonal, "시즌 콘텐츠", "계절 · 특별 테마 콘텐츠", "IconTreeGeometry")
        ];

        FooterNavItems =
        [
            new(HubPage.Settings, "설정", "일반 · 펫 표시 · 네트워크 및 방화벽 · 데이터", "IconSettingsGeometry"),
            new(HubPage.About, "정보", "설치 버전 · 업데이트 확인 · 오픈소스 라이선스", "IconInfoGeometry")
        ];

        _petViewModel.ProfileUpdated += OnPetProfileUpdated;
        Games.ProfileChanged += OnGamesProfileChanged;
        _navigation.Navigated += OnNavigated;

        RefreshPetSummary(_petViewModel.CurrentProfile);
        SyncSelectedPage(_navigation.CurrentPage);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _petViewModel.ProfileUpdated -= OnPetProfileUpdated;
        Games.ProfileChanged -= OnGamesProfileChanged;
        _navigation.Navigated -= OnNavigated;
    }

    public void RefreshPetSummary(PetProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        PetName = string.IsNullOrWhiteSpace(profile.PetName) ? "모찌" : profile.PetName;
        OwnerName = profile.OwnerName ?? string.Empty;
        Level = profile.SoloProgress.Level;
        Coins = profile.SoloProgress.Coins;
        PetPortrait = _portraitRenderer.GetPortrait(profile.Appearance);
        OnPropertyChanged(nameof(LevelBadgeText));
        OnPropertyChanged(nameof(CoinsBadgeText));
    }

    public void SyncSelectedPage(HubPage page)
    {
        SelectedPage = page;
        foreach (var item in PrimaryNavItems)
        {
            item.IsSelected = item.Page == page;
        }

        foreach (var item in FooterNavItems)
        {
            item.IsSelected = item.Page == page;
        }
    }

    [RelayCommand]
    private async Task NavigateToItemAsync(HubNavigationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var moved = await _navigation.NavigateAsync(new NavigationRequest(item.Page));
        if (!moved)
        {
            SyncSelectedPage(_navigation.CurrentPage);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    private void OnPetProfileUpdated(object? sender, PetProfile profile) => RefreshPetSummary(profile);
    private void OnGamesProfileChanged(object? sender, PetProfile profile) => RefreshPetSummary(profile);
    private void OnNavigated(object? sender, NavigationRequest request) => SyncSelectedPage(request.Page);

    private HubNavigationItemViewModel? FindNavItem(HubPage page)
    {
        foreach (var item in PrimaryNavItems)
        {
            if (item.Page == page)
            {
                return item;
            }
        }

        foreach (var item in FooterNavItems)
        {
            if (item.Page == page)
            {
                return item;
            }
        }

        return null;
    }
}
