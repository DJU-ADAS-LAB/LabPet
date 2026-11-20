using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Audio;
using LabPet.Client.Rendering;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

public sealed partial class ShopItemViewModel : ObservableObject
{
    public string Category { get; }
    public CustomizationOption Option { get; }
    public string DisplayName => Option.DisplayName;
    public string Description => Option.Description;
    public int CoinPrice => Option.CoinPrice;

    [ObservableProperty]
    private bool _isUnlocked;

    public string ActionLabel => IsUnlocked ? "장착하기" : $"{CoinPrice} 코인 구매";

    public ShopItemViewModel(string category, CustomizationOption option, bool isUnlocked)
    {
        Category = category;
        Option = option;
        _isUnlocked = isUnlocked;
    }

    partial void OnIsUnlockedChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));
}

/// <summary>
/// 개인 성장 현황 · 실시간 오디오 모니터 · 뽀모도로 타이머 · 3D 상점/도감 ViewModel.
/// </summary>
public sealed partial class SoloHubViewModel : ObservableObject
{
    private readonly IAppDataStore _store;
    private readonly SoloProgressionService _progression;
    private readonly PomodoroTimerService _pomodoro;
    private readonly IAudioAnalyzer _audioAnalyzer;

    private PetProfile _profile = new();

    public event EventHandler<PetProfile>? ProfileChanged;

    public ObservableCollection<ShopItemViewModel> ShopItems { get; } = [];

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

    // 상점 안내 메시지
    [ObservableProperty] private string _shopStatusMessage = string.Empty;

    public SoloHubViewModel(
        IAppDataStore store,
        SoloProgressionService progression,
        PomodoroTimerService pomodoro,
        IAudioAnalyzer audioAnalyzer)
    {
        _store = store;
        _progression = progression;
        _pomodoro = pomodoro;
        _audioAnalyzer = audioAnalyzer;

        _pomodoro.Tick += (_, _) => RefreshPomodoroUi();
    }

    public void BindProfile(PetProfile profile)
    {
        _profile = profile;
        RefreshProgressUi();
        RebuildShopItems();
        RefreshPomodoroUi();
        RefreshAudioUi();
    }

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
                ? (snap.IsBeat ? "비트 감지! 리듬 반응 중" : "음악 반응 중")
                : "무음 대기 (자동 감쇠)";

        BassPercent = Math.Clamp(snap.Bass * 100.0, 0.0, 100.0);
        MidPercent = Math.Clamp(snap.Mid * 100.0, 0.0, 100.0);
        TreblePercent = Math.Clamp(snap.Treble * 100.0, 0.0, 100.0);
    }

    private void RefreshPomodoroUi()
    {
        PomodoroPhaseText = _pomodoro.CurrentPhase == PomodoroPhase.Focus ? "집중 세션" : "휴식 세션";
        PomodoroTimeText = $"{(int)_pomodoro.Remaining.TotalMinutes:00}:{_pomodoro.Remaining.Seconds:00}";
        PomodoroToggleText = _pomodoro.IsRunning ? "일시정지" : "시작";

        var totalSec = Math.Max(1.0, (_pomodoro.CurrentPhase == PomodoroPhase.Focus ? _pomodoro.FocusMinutes : _pomodoro.BreakMinutes) * 60.0);
        var remainRatio = Math.Clamp(_pomodoro.Remaining.TotalSeconds / totalSec, 0.0, 1.0);
        // 원형 링 둘레 정규화 길이(56.55 = 2*pi*9 stroke unit) 기준 진행 오프셋
        PomodoroRingDashOffset = 56.55 * (1.0 - remainRatio);
    }

    private void RebuildShopItems()
    {
        ShopItems.Clear();
        foreach (var (category, option) in PetCustomizationCatalog.GetShopItems())
        {
            var unlocked = PetCustomizationCatalog.IsUnlocked(option, _profile.SoloProgress);
            ShopItems.Add(new ShopItemViewModel(category, option, unlocked));
        }
    }

    [RelayCommand]
    private void TogglePomodoro() => _pomodoro.StartOrPause();

    [RelayCommand]
    private void ResetPomodoro() => _pomodoro.Reset();

    [RelayCommand]
    private void SetPomodoroPreset(string minutesText)
    {
        if (int.TryParse(minutesText, out var mins))
        {
            _pomodoro.ConfigureDurations(mins, breakMinutes: 5);
            RefreshPomodoroUi();
        }
    }

    [RelayCommand]
    private async Task PurchaseOrEquipAsync(ShopItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.IsUnlocked)
        {
            var (ok, msg) = _progression.TryPurchaseItem(_profile, item.Option);
            ShopStatusMessage = msg;
            if (!ok)
            {
                return;
            }

            item.IsUnlocked = true;
            RefreshProgressUi();
        }
        else
        {
            ApplyItemToAppearance(item);
            ShopStatusMessage = $"'{item.DisplayName}'을(를) 장착했습니다!";
        }

        await _store.SaveProfileAsync(_profile);
        ProfileChanged?.Invoke(this, _profile);
    }

    private void ApplyItemToAppearance(ShopItemViewModel item)
    {
        switch (item.Category)
        {
            case "머리 악세서리":
                _profile.Appearance.HeadAccessoryId = item.Option.Id;
                break;
            case "표정":
                _profile.Appearance.ExpressionId = item.Option.Id;
                break;
            case "3D 오라":
                _profile.Appearance.AuraEffectId = item.Option.Id;
                break;
            case "바디 컬러":
                _profile.Appearance.BodyColorHex = item.Option.Id;
                break;
            case "포인트 컬러":
                _profile.Appearance.AccentColorHex = item.Option.Id;
                break;
        }
    }
}
