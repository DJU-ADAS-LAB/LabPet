using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Social;
using LabPet.Client.Storage;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 펫 마우스 오버레이 팝업에 표시되는 미니멀 주변/친구 항목.
/// </summary>
public sealed class QuickOverlayEntry
{
    public string PetId { get; init; } = string.Empty;
    public string DisplayTitle { get; init; } = string.Empty;
    public bool IsFriend { get; init; }
    public bool IsMusicActive { get; init; }
    public bool IsFollowingMusic { get; init; }
    public bool CanFollowMusic => IsFriend && (IsMusicActive || IsFollowingMusic);
    public bool CanSendFriendRequest => !IsFriend && !RequiresUpdate;
    public bool RequiresUpdate { get; init; }
    public FriendEntry? Friend { get; init; }
    public PeerSnapshot? Peer { get; init; }

    public string MusicTooltip => IsFollowingMusic
        ? "노래 따라하기 중지"
        : (IsMusicActive ? "노래 따라하기" : "음악 재생 중이 아님");
}

/// <summary>
/// 바탕화면 투명 펫 창 ViewModel.
/// </summary>
public sealed partial class PetWindowViewModel : ObservableObject
{
    private readonly IAppDataStore _store;
    private readonly SocialCoordinatorService _social;

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
    public event EventHandler<double>? PetScaleChanged;
    public event EventHandler<(PendingFriendRequest Request, bool Accept)>? FriendRequestResponded;

    public PetProfile CurrentProfile { get; private set; } = new();

    public ObservableCollection<QuickOverlayEntry> QuickOverlayEntries { get; } = [];

    [ObservableProperty] private string _petName = "모찌";
    [ObservableProperty] private string _friendCode = string.Empty;
    [ObservableProperty] private bool _isClickThroughEnabled;
    [ObservableProperty] private bool _isStartupAutoRunEnabled;
    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private string _updateReadyMenuHeader = "새 버전 준비됨 · 재시작하여 적용";
    [ObservableProperty] private string _bubbleText = string.Empty;
    [ObservableProperty] private bool _isBubbleVisible;
    [ObservableProperty] private PendingFriendRequest? _pendingFriendRequest;
    [ObservableProperty] private string _trayTooltip = "LabPet";
    [ObservableProperty] private double _petScale = 1.0;

    // 미니멀 오버레이 빠른 메시지 입력 상태
    [ObservableProperty] private QuickOverlayEntry? _quickMessageTarget;
    [ObservableProperty] private string _quickMessageText = string.Empty;

    public bool HasPendingFriendRequest => PendingFriendRequest is not null;
    public bool HasQuickMessageTarget => QuickMessageTarget is not null;
    public bool HasNoQuickOverlayEntries => QuickOverlayEntries.Count == 0;
    public string QuickOverlayHeaderText => $"주변 · 온라인 친구 ({QuickOverlayEntries.Count})";
    public string QuickMessagePlaceholder => QuickMessageTarget is null
        ? string.Empty
        : $"{QuickMessageTarget.DisplayTitle}에게 메시지...";

    public string PendingFriendRequestText => PendingFriendRequest is null
        ? string.Empty
        : $"{PendingFriendRequest.DisplayName} 친구 신청!";

    private CancellationTokenSource? _bubbleCts;
    private bool _suppressStartupToggleEvent;

    public PetWindowViewModel(IAppDataStore store, SocialCoordinatorService social)
    {
        _store = store;
        _social = social;
    }

    public void ApplyProfile(PetProfile profile)
    {
        CurrentProfile = profile;
        PetName = profile.PetName;
        FriendCode = profile.FriendCode;
        PetScale = Math.Clamp(profile.PetScale <= 0 ? 1.0 : profile.PetScale, 0.65, 1.45);
        var label = SocialCoordinatorService.FormatDisplayName(profile.OwnerName, profile.PetName);
        TrayTooltip = $"{label} · Lv.{profile.SoloProgress.Level}";
        ProfileUpdated?.Invoke(this, profile);
        PetScaleChanged?.Invoke(this, PetScale);
    }

    public void RefreshQuickOverlayEntries()
    {
        var peers = _social.DiscoveredPeers;
        var friends = _social.Friends;
        var followingId = _social.FollowingHostPetId;

        var items = new List<QuickOverlayEntry>();

        // 1. 온라인 친구 우선 표시
        foreach (var f in friends)
        {
            var live = peers.FirstOrDefault(p => string.Equals(p.PetId, f.PetId, StringComparison.OrdinalIgnoreCase));
            if (live is null || live.RequiresUpdate)
            {
                continue;
            }

            items.Add(new QuickOverlayEntry
            {
                PetId = f.PetId,
                DisplayTitle = SocialCoordinatorService.FormatDisplayName(
                    string.IsNullOrWhiteSpace(live.OwnerName) ? f.OwnerName : live.OwnerName,
                    string.IsNullOrWhiteSpace(live.PetName) ? f.PetName : live.PetName),
                IsFriend = true,
                IsMusicActive = live.IsMusicActive,
                IsFollowingMusic = string.Equals(followingId, f.PetId, StringComparison.OrdinalIgnoreCase),
                Friend = f,
                Peer = live
            });
        }

        // 2. 아직 친구가 아닌 주변 온라인 LAN 피어 (최대 총 5명까지 미니멀 표시)
        foreach (var p in peers)
        {
            if (items.Count >= 5)
            {
                break;
            }

            if (p.IsFriend || items.Any(x => string.Equals(x.PetId, p.PetId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            items.Add(new QuickOverlayEntry
            {
                PetId = p.PetId,
                DisplayTitle = SocialCoordinatorService.FormatDisplayName(p.OwnerName, p.PetName),
                IsFriend = false,
                IsMusicActive = p.IsMusicActive,
                RequiresUpdate = p.RequiresUpdate,
                Peer = p
            });
        }

        QuickOverlayEntries.Clear();
        foreach (var item in items)
        {
            QuickOverlayEntries.Add(item);
        }

        // 메시지 대상이 오프라인이 되면 입력창 닫기
        if (QuickMessageTarget is not null && !items.Any(i => string.Equals(i.PetId, QuickMessageTarget.PetId, StringComparison.OrdinalIgnoreCase)))
        {
            QuickMessageTarget = null;
        }

        OnPropertyChanged(nameof(HasNoQuickOverlayEntries));
        OnPropertyChanged(nameof(QuickOverlayHeaderText));
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
        }
    }

    partial void OnIsClickThroughEnabledChanged(bool value)
    {
        ClickThroughChanged?.Invoke(this, value);
        ShowBubble(value ? "클릭 투과 집중 모드 ON (트레이에서 해제)" : "클릭 투과 집중 모드 OFF", 2400);
    }

    partial void OnPendingFriendRequestChanged(PendingFriendRequest? value)
    {
        OnPropertyChanged(nameof(HasPendingFriendRequest));
        OnPropertyChanged(nameof(PendingFriendRequestText));
    }

    partial void OnQuickMessageTargetChanged(QuickOverlayEntry? value)
    {
        OnPropertyChanged(nameof(HasQuickMessageTarget));
        OnPropertyChanged(nameof(QuickMessagePlaceholder));
    }

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
    private async Task SetPetScaleAsync(string? scaleText)
    {
        if (!double.TryParse(scaleText, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return;
        }

        var clamped = Math.Clamp(parsed, 0.65, 1.45);
        PetScale = clamped;
        CurrentProfile.PetScale = clamped;
        PetScaleChanged?.Invoke(this, clamped);
        await _store.SaveProfileAsync(CurrentProfile);
        ShowBubble($"펫 크기 {Math.Round(clamped * 100)}%", 1600);
    }

    [RelayCommand]
    private void OpenQuickMessage(QuickOverlayEntry? entry)
    {
        if (entry is null || !entry.IsFriend)
        {
            return;
        }

        QuickMessageTarget = string.Equals(QuickMessageTarget?.PetId, entry.PetId, StringComparison.OrdinalIgnoreCase)
            ? null
            : entry;
    }

    [RelayCommand]
    private async Task SendQuickMessageAsync()
    {
        if (QuickMessageTarget?.Friend is null)
        {
            return;
        }

        var trimmed = (QuickMessageText ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var targetName = QuickMessageTarget.DisplayTitle;
        var ok = await _social.SendBubbleMessageAsync(QuickMessageTarget.Friend, trimmed);
        if (ok)
        {
            QuickMessageText = string.Empty;
            QuickMessageTarget = null;
            ShowBubble($"{targetName}에게 전송됨 (+2 코인)", 2000);
        }
        else
        {
            ShowBubble("메시지 전송 실패", 2200);
        }
    }

    [RelayCommand]
    private async Task QuickPetFriendAsync(QuickOverlayEntry? entry)
    {
        if (entry?.Friend is null)
        {
            return;
        }

        var ok = await _social.SendPetInteractionAsync(entry.Friend);
        ShowBubble(ok ? $"{entry.DisplayTitle} 쓰다듬기 완료! (+2 코인)" : "쓰다듬기 전송 실패", 2000);
    }

    [RelayCommand]
    private async Task QuickToggleMusicFollowAsync(QuickOverlayEntry? entry)
    {
        if (entry?.Friend is null || !entry.CanFollowMusic)
        {
            return;
        }

        var ok = await _social.ToggleMusicFollowAsync(entry.Friend);
        if (ok)
        {
            RefreshQuickOverlayEntries();
        }
        else
        {
            ShowBubble("노래 따라하기 요청 실패", 2000);
        }
    }

    [RelayCommand]
    private async Task QuickSendFriendRequestAsync(QuickOverlayEntry? entry)
    {
        if (entry?.Peer is null || !entry.CanSendFriendRequest)
        {
            return;
        }

        var ok = await _social.SendFriendRequestAsync(entry.Peer);
        ShowBubble(ok ? $"{entry.DisplayTitle}에게 친구 신청!" : "친구 신청 전송 실패", 2200);
    }

    [RelayCommand]
    private void CheckUpdates() => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ApplyUpdateAndRestart() => ApplyUpdateRestartRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenEnvironmentGuide() => OpenEnvironmentGuideRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSocial() => OpenSocialRequested?.Invoke(this, EventArgs.Empty);

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
    private void OpenSoloHub() => OpenSoloHubRequested?.Invoke(this, EventArgs.Empty);

    public async Task SaveCurrentProfileAsync()
    {
        await _store.SaveProfileAsync(CurrentProfile);
        var label = SocialCoordinatorService.FormatDisplayName(CurrentProfile.OwnerName, CurrentProfile.PetName);
        TrayTooltip = $"{label} · Lv.{CurrentProfile.SoloProgress.Level}";
    }

    [RelayCommand]
    private void ToggleClickThrough() => IsClickThroughEnabled = !IsClickThroughEnabled;

    [RelayCommand]
    private void OpenCustomization() => OpenCustomizationRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ResetPosition() => ResetPositionRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ExitApplication() => ExitRequested?.Invoke(this, EventArgs.Empty);
}
