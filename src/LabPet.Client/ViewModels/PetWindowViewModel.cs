using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Social;
using LabPet.Client.Storage;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 바탕화면 투명 펫 창 ViewModel.
/// </summary>
public sealed partial class PetWindowViewModel : ObservableObject
{
    private readonly IAppDataStore _store;

    public event EventHandler? OpenCustomizationRequested;
    public event EventHandler? OpenSoloHubRequested;
    public event EventHandler? OpenSocialRequested;
    public event EventHandler? OpenEnvironmentGuideRequested;
    public event EventHandler? CheckUpdatesRequested;
    public event EventHandler? ApplyUpdateRestartRequested;
    public event EventHandler<bool>? StartupAutoRunToggled;
    public event EventHandler? ResetPositionRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? ClickThroughChanged;
    public event EventHandler<PetProfile>? ProfileUpdated;
    public event EventHandler<(PendingFriendRequest Request, bool Accept)>? FriendRequestResponded;

    public PetProfile CurrentProfile { get; private set; } = new();

    [ObservableProperty]
    private string _petName = "모찌";

    [ObservableProperty]
    private string _friendCode = string.Empty;

    [ObservableProperty]
    private bool _isClickThroughEnabled;

    [ObservableProperty]
    private bool _isStartupAutoRunEnabled;

    [ObservableProperty]
    private bool _isUpdateReady;

    [ObservableProperty]
    private string _updateReadyMenuHeader = "새 버전 준비됨 · 재시작하여 적용";

    [ObservableProperty]
    private string _bubbleText = string.Empty;

    [ObservableProperty]
    private bool _isBubbleVisible;

    [ObservableProperty]
    private PendingFriendRequest? _pendingFriendRequest;

    public bool HasPendingFriendRequest => PendingFriendRequest is not null;

    public string PendingFriendRequestText => PendingFriendRequest is null
        ? string.Empty
        : $"{PendingFriendRequest.RequesterName}(이)가 친구 신청!";

    [ObservableProperty]
    private string _trayTooltip = "LabPet";

    private CancellationTokenSource? _bubbleCts;

    public PetWindowViewModel(IAppDataStore store)
    {
        _store = store;
    }

    public void ApplyProfile(PetProfile profile)
    {
        CurrentProfile = profile;
        PetName = profile.PetName;
        FriendCode = profile.FriendCode;
        TrayTooltip = $"LabPet · {profile.PetName} (Lv.{profile.SoloProgress.Level})";
        ProfileUpdated?.Invoke(this, profile);
    }

    public async Task SaveWindowPositionAsync(double left, double top)
    {
        CurrentProfile.LastWindowLeft = left;
        CurrentProfile.LastWindowTop = top;
        await _store.SaveProfileAsync(CurrentProfile);
    }

    public async void ShowBubble(string message, int durationMs = 2600)
    {
        _bubbleCts?.Cancel();
        _bubbleCts?.Dispose();
        _bubbleCts = new CancellationTokenSource();
        var token = _bubbleCts.Token;

        BubbleText = message;
        IsBubbleVisible = true;

        try
        {
            await Task.Delay(durationMs, token);
            if (!token.IsCancellationRequested)
            {
                IsBubbleVisible = false;
            }
        }
        catch (TaskCanceledException)
        {
            // 새 말풍선이 이전 타이머를 대체함
        }
    }

    partial void OnIsClickThroughEnabledChanged(bool value)
    {
        ClickThroughChanged?.Invoke(this, value);
        ShowBubble(value ? "클릭 투과 집중 모드 ON (트레이에서 해제)" : "클릭 투과 집중 모드 OFF", 2400);
    }

    [RelayCommand]
    private void ToggleClickThrough()
    {
        IsClickThroughEnabled = !IsClickThroughEnabled;
    }

    [RelayCommand]
    private void OpenCustomization()
    {
        OpenCustomizationRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnPendingFriendRequestChanged(PendingFriendRequest? value)
    {
        OnPropertyChanged(nameof(HasPendingFriendRequest));
        OnPropertyChanged(nameof(PendingFriendRequestText));
    }

    private bool _suppressStartupToggleEvent;

    partial void OnIsStartupAutoRunEnabledChanged(bool value)
    {
        if (_suppressStartupToggleEvent)
        {
            return;
        }

        StartupAutoRunToggled?.Invoke(this, value);
    }

    public void InitializeStartupState(bool isEnabled)
    {
        _suppressStartupToggleEvent = true;
        try
        {
            IsStartupAutoRunEnabled = isEnabled;
        }
        finally
        {
            _suppressStartupToggleEvent = false;
        }
    }

    public void NotifyUpdateReady(string versionText)
    {
        IsUpdateReady = true;
        UpdateReadyMenuHeader = $"새 버전 v{versionText} 준비됨 · 재시작하여 적용";
        ShowBubble($"새 버전 v{versionText} 준비됨! 트레이에서 재시작하세요.", 5000);
    }

    [RelayCommand]
    private void CheckUpdates() => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ApplyUpdateAndRestart() => ApplyUpdateRestartRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenEnvironmentGuide() => OpenEnvironmentGuideRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSocial()
    {
        OpenSocialRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AcceptFriendRequest()
    {
        if (PendingFriendRequest is null) return;
        var req = PendingFriendRequest;
        PendingFriendRequest = null;
        FriendRequestResponded?.Invoke(this, (req, true));
    }

    [RelayCommand]
    private void RejectFriendRequest()
    {
        if (PendingFriendRequest is null) return;
        var req = PendingFriendRequest;
        PendingFriendRequest = null;
        FriendRequestResponded?.Invoke(this, (req, false));
    }

    [RelayCommand]
    private void OpenSoloHub()
    {
        OpenSoloHubRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveCurrentProfileAsync()
    {
        await _store.SaveProfileAsync(CurrentProfile);
        TrayTooltip = $"LabPet · {CurrentProfile.PetName} (Lv.{CurrentProfile.SoloProgress.Level})";
    }

    [RelayCommand]
    private void ResetPosition()
    {
        ResetPositionRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ExitApplication()
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
