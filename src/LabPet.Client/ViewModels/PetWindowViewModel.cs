using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Social;
using LabPet.Client.Storage;
using LabPet.Client.Views;
using LabPet.Shared.Models;
using LabPet.Shared.Transport;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 펫 마우스 오버레이 팝업에 표시되는 미니멀 주변/친구 항목.
/// </summary>
public sealed partial class QuickOverlayEntry : ObservableObject
{
    private static readonly Brush OnlineDotBrush = CreateFrozenBrush("#FF34D399");
    private static readonly Brush SleepingDotBrush = CreateFrozenBrush("#FF818CF8");

    public string PetId { get; init; } = string.Empty;
    [ObservableProperty] private string _displayTitle = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFollowMusic), nameof(CanShowFriendRequestButton), nameof(CanSendFriendRequest))]
    private bool _isFriend;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFollowMusic), nameof(MusicTooltip), nameof(MusicButtonIcon))]
    private bool _isMusicActive;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSleepBadge), nameof(StatusDotBrush))]
    private bool _isSleeping;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFollowMusic), nameof(MusicTooltip), nameof(MusicButtonIcon))]
    private bool _isFollowingMusic;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanShowFriendRequestButton), nameof(CanSendFriendRequest))]
    private bool _requiresUpdate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSendFriendRequest), nameof(FriendRequestButtonText))]
    private bool _isSendingFriendRequest;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSendFriendRequest), nameof(FriendRequestButtonText))]
    private bool _isFriendRequestPending;
    [ObservableProperty] private bool _isComposingMessage;
    public bool CanFollowMusic => IsFriend && (IsMusicActive || IsFollowingMusic);
    public bool CanShowFriendRequestButton => !IsFriend && !RequiresUpdate;
    public bool CanSendFriendRequest => !IsFriend && !RequiresUpdate && !IsSendingFriendRequest && !IsFriendRequestPending;
    public string FriendRequestButtonText => IsSendingFriendRequest ? "전송 중..." : (IsFriendRequestPending ? "대기 중" : "+ 신청");
    public bool HasSleepBadge => IsSleeping;
    public Brush StatusDotBrush => IsSleeping ? SleepingDotBrush : OnlineDotBrush;
    public FriendEntry? Friend { get; set; }
    public PeerSnapshot? Peer { get; set; }

    public string MusicButtonIcon => IsFollowingMusic ? "⏹" : "🎵";

    public string MusicTooltip => IsFollowingMusic
        ? "노래 따라하기 중지"
        : (IsMusicActive ? "노래 따라하기 시작" : "음악 재생 중이 아님");

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 개별 표시 시간을 가진 말풍선. 같은 문구가 반복되어도 별개 항목으로 유지한다.
/// </summary>
public class BubbleLine(string text)
{
    public string Text { get; } = text;
}

public sealed class FriendMessageBubble(string text, string senderName, ImageSource portrait) : BubbleLine(text)
{
    public string SenderName { get; } = senderName;
    public ImageSource Portrait { get; } = portrait;
}

/// <summary>
/// 바탕화면 투명 펫 창 ViewModel.
/// </summary>
public sealed partial class PetWindowViewModel : ObservableObject
{
    private readonly IAppDataStore _store;
    private readonly SocialCoordinatorService _social;
    private readonly BubbleMessageStack _bubbleStack = new(MaxBubbleLines);

    public event EventHandler? OpenCustomizationRequested;
    public event EventHandler? OpenSoloHubRequested;
    public event EventHandler? OpenSocialRequested;
    public event EventHandler? OpenEnvironmentGuideRequested;
    public event EventHandler? LaunchChristmasTreeRequested;
    public event EventHandler? CheckUpdatesRequested;
    public event EventHandler? ApplyUpdateRestartRequested;
    public event EventHandler<bool>? StartupAutoRunToggled;
    public event EventHandler? ResetPositionRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? ClickThroughChanged;
    public event EventHandler<PetProfile>? ProfileUpdated;
    public event EventHandler<PetProfile>? ProfileProgressUpdated;
    public event EventHandler<double>? PetScaleChanged;
    public event EventHandler<double>? PetOpacityChanged;
    public event EventHandler<(FriendEntry Friend, PetAppearance Appearance)>? ToggleVisitFriendRequested;
    internal event EventHandler? VisitorStateChanged;

    private Func<string, bool>? _isVisitorActiveChecker;

    internal void AttachVisitorChecker(Func<string, bool> checker) => _isVisitorActiveChecker = checker;
    internal bool IsVisitorActive(string petId) => _isVisitorActiveChecker?.Invoke(petId) ?? false;
    internal void RequestToggleVisitFriend(FriendEntry friend, PetAppearance appearance)
        => ToggleVisitFriendRequested?.Invoke(this, (friend, appearance));
    internal void NotifyVisitorStateChanged() => VisitorStateChanged?.Invoke(this, EventArgs.Empty);

    public PetProfile CurrentProfile { get; private set; } = new();

    public ObservableCollection<QuickOverlayEntry> QuickOverlayEntries { get; } = [];

    [ObservableProperty] private string _petName = "모찌";
    [ObservableProperty] private string _friendCode = string.Empty;
    [ObservableProperty] private bool _isSleeping;
    [ObservableProperty] private bool _isClickThroughEnabled;
    [ObservableProperty] private bool _isStartupAutoRunEnabled;
    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAnyBubble))]
    private bool _isUpdateBubbleVisible;
    [ObservableProperty] private string _updateBubbleText = string.Empty;
    [ObservableProperty] private string _updateReadyMenuHeader = "새 버전 준비됨 · 재시작하여 적용";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAnyBubble))]
    private bool _isBubbleVisible;

    /// <summary>
    /// 말풍선에 동시에 쌓아 보여 줄 최대 메시지 수. 초과 시 가장 오래된 메시지부터 밀려난다.
    /// </summary>
    public const int MaxBubbleLines = 5;

    /// <summary>
    /// 말풍선에 쌓여 표시 중인 메시지 줄 목록(오래된 순 → 최신 순).
    /// 각 줄은 자신의 표시 시간이 끝나면 개별적으로 사라진다.
    /// </summary>
    public ObservableCollection<BubbleLine> BubbleLines => _bubbleStack.Items;
    private readonly Queue<PendingFriendRequest> _pendingFriendRequestQueue = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingFriendRequest), nameof(PendingFriendRequestText), nameof(HasAnyBubble), nameof(CanClickFriendRequestAction))]
    private PendingFriendRequest? _pendingFriendRequest;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanClickFriendRequestAction), nameof(AcceptButtonText))]
    private bool _isRespondingFriendRequest;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingFriendRequestStatus))]
    private string _pendingFriendRequestStatusText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AcceptButtonText))]
    private bool _canRetryPendingFriendRequest;
    [ObservableProperty] private string _trayTooltip = "LabPet";
    [ObservableProperty] private double _petScale = PetProfile.DefaultPetScale;
    [ObservableProperty] private double _petOpacity = 1.0;

    public string PetScalePercentText => $"{Math.Round(PetScale * 100)}%";
    public string PetOpacityPercentText => $"{Math.Round(PetOpacity * 100)}%";

    // 미니멀 오버레이 빠른 메시지 입력 상태
    [ObservableProperty] private QuickOverlayEntry? _quickMessageTarget;
    [ObservableProperty] private string _quickMessageText = string.Empty;

    public bool HasPendingFriendRequest => PendingFriendRequest is not null;
    public bool HasPendingFriendRequestStatus => !string.IsNullOrWhiteSpace(PendingFriendRequestStatusText);
    public bool CanClickFriendRequestAction => PendingFriendRequest is not null && !IsRespondingFriendRequest;
    public string AcceptButtonText => IsRespondingFriendRequest
        ? "처리 중..."
        : (CanRetryPendingFriendRequest ? "다시 시도" : "수락");
    public bool HasAnyBubble => IsUpdateBubbleVisible || HasPendingFriendRequest || IsBubbleVisible;
    public bool HasNoQuickOverlayEntries => QuickOverlayEntries.Count == 0;
    public string QuickOverlayHeaderText => $"주변 · 온라인 친구 ({QuickOverlayEntries.Count})";
    private int _quickMessageDraftRevision;

    public string PendingFriendRequestText => PendingFriendRequest is null
        ? string.Empty
        : $"{PendingFriendRequest.DisplayName} 친구 신청!";

    public string CurrentVersionText { get; } = BuildCurrentVersionText();

    private static string BuildCurrentVersionText()
    {
        var asm = typeof(PetWindowViewModel).Assembly;
        var infoAttr = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoAttr))
        {
            var clean = infoAttr.Split('+')[0];
            return $"현재 버전 v{clean}";
        }

        var ver = asm.GetName().Version?.ToString(3) ?? "0.1.0";
        return $"현재 버전 v{ver}";
    }

    private CancellationTokenSource? _appearanceSaveCts;
    private bool _suppressStartupToggleEvent;
    private bool _suppressSliderSideEffects;

    public PetWindowViewModel(IAppDataStore store, SocialCoordinatorService social)
    {
        _store = store;
        _social = social;
        _bubbleStack.Changed += (_, _) => IsBubbleVisible = _bubbleStack.HasItems;
    }

    public void ApplyProfile(PetProfile profile)
    {
        CurrentProfile = profile;
        PetName = profile.PetName;
        FriendCode = profile.FriendCode;

        _suppressSliderSideEffects = true;
        try
        {
            PetScale = PetProfile.NormalizePetScale(profile.PetScale);
            PetOpacity = PetDisplayLayoutHelper.NormalizeOpacity(
                profile.PetOpacity <= 0 ? PetDisplayLayoutHelper.MaxPetOpacity : profile.PetOpacity);
        }
        finally
        {
            _suppressSliderSideEffects = false;
        }

        var label = SocialCoordinatorService.FormatDisplayName(profile.OwnerName, profile.PetName);
        TrayTooltip = $"{label} · Lv.{profile.SoloProgress.Level}";
        ProfileUpdated?.Invoke(this, profile);
        PetScaleChanged?.Invoke(this, PetScale);
        PetOpacityChanged?.Invoke(this, PetOpacity);
    }

    partial void OnPetScaleChanged(double value)
    {
        OnPropertyChanged(nameof(PetScalePercentText));
        if (_suppressSliderSideEffects) return;

        var clamped = PetProfile.NormalizePetScale(value);
        CurrentProfile.PetScale = clamped;
        PetScaleChanged?.Invoke(this, clamped);
        ScheduleDebouncedProfileSave();
    }

    partial void OnPetOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(PetOpacityPercentText));
        if (_suppressSliderSideEffects) return;

        var clamped = PetDisplayLayoutHelper.NormalizeOpacity(value);
        CurrentProfile.PetOpacity = clamped;
        PetOpacityChanged?.Invoke(this, clamped);
        ScheduleDebouncedProfileSave();
    }

    private async void ScheduleDebouncedProfileSave()
    {
        _appearanceSaveCts?.Cancel();
        _appearanceSaveCts?.Dispose();
        _appearanceSaveCts = new CancellationTokenSource();
        var token = _appearanceSaveCts.Token;

        try
        {
            await Task.Delay(350, token);
            if (!token.IsCancellationRequested)
            {
                await _store.SaveProfileAsync(CurrentProfile, token);
            }
        }
        catch (TaskCanceledException)
        {
        }
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
                IsSleeping = live.IsSleeping,
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
                IsSleeping = p.IsSleeping,
                RequiresUpdate = p.RequiresUpdate,
                IsSendingFriendRequest = _social.IsOutgoingFriendRequestSending(p.PetId),
                IsFriendRequestPending = _social.IsOutgoingFriendRequestPending(p.PetId),
                Peer = p
            });
        }

        // 친구 상태가 바뀌어도 같은 행과 입력 컨트롤을 유지한다.
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var existing = QuickOverlayEntries.Skip(index)
                .FirstOrDefault(entry => string.Equals(entry.PetId, item.PetId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                QuickOverlayEntries.Insert(index, item);
                continue;
            }

            existing.DisplayTitle = item.DisplayTitle;
            existing.IsFriend = item.IsFriend;
            existing.IsMusicActive = item.IsMusicActive;
            existing.IsSleeping = item.IsSleeping;
            existing.IsFollowingMusic = item.IsFollowingMusic;
            existing.RequiresUpdate = item.RequiresUpdate;
            existing.IsSendingFriendRequest = item.IsSendingFriendRequest;
            existing.IsFriendRequestPending = item.IsFriendRequestPending;
            existing.Friend = item.Friend;
            existing.Peer = item.Peer;
            var oldIndex = QuickOverlayEntries.IndexOf(existing);
            if (oldIndex != index)
            {
                QuickOverlayEntries.Move(oldIndex, index);
            }
        }

        while (QuickOverlayEntries.Count > items.Count)
        {
            QuickOverlayEntries.RemoveAt(QuickOverlayEntries.Count - 1);
        }

        // 메시지 대상이 친구 목록에서 사라지면 입력창을 닫는다.
        if (QuickMessageTarget is { } target && (!target.IsFriend || !QuickOverlayEntries.Contains(target)))
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

    /// <summary>
    /// 펫 말풍선에 메시지 한 줄을 추가한다. 새 메시지가 와도 기존 줄을 즉시 지우지 않고
    /// 최대 <see cref="MaxBubbleLines"/>개까지 쌓으며, 각 메시지는 자신의 표시 시간이 지나면 사라진다.
    /// UI 스레드에서 호출해야 한다.
    /// </summary>
    public void ShowBubble(string message, int durationMs = 2600)
        => ShowBubble(new BubbleLine(message), durationMs);

    public void ShowBubble(BubbleLine line, int durationMs)
    {
        _bubbleStack.Add(line, durationMs);
    }

    public void DisposeBubbles() => _bubbleStack.Dispose();

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

    partial void OnQuickMessageTargetChanged(QuickOverlayEntry? oldValue, QuickOverlayEntry? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsComposingMessage = false;
        }

        QuickMessageText = string.Empty;
        if (newValue is not null)
        {
            newValue.IsComposingMessage = true;
        }
    }

    partial void OnQuickMessageTextChanged(string value) => _quickMessageDraftRevision++;

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
        UpdateBubbleText = $"새 버전 v{versionText} 업데이트할까요?";
        IsUpdateBubbleVisible = true;
    }

    [RelayCommand]
    private void DismissUpdateBubble() => IsUpdateBubbleVisible = false;

    [RelayCommand]
    private async Task SetPetScaleAsync(string? scaleText)
    {
        if (!double.TryParse(scaleText, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return;
        }

        var clamped = PetProfile.NormalizePetScale(parsed);
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
        if (QuickMessageTarget is not { Friend: { } friend })
        {
            return;
        }

        var trimmed = (QuickMessageText ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var revision = _quickMessageDraftRevision;
        var ok = await _social.SendBubbleMessageAsync(friend, trimmed);
        if (ok)
        {
            if (revision == _quickMessageDraftRevision)
            {
                QuickMessageText = string.Empty;
            }
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
        ShowBubble(ok ? $"{entry.DisplayTitle}을(를) 쓰다듬어 주었습니다." : "쓰다듬기 전송 실패", 2000);
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
    private void QuickToggleVisitFriend(QuickOverlayEntry? entry)
    {
        if (entry?.Friend is null)
        {
            return;
        }

        var appearance = (entry.Peer?.Appearance ?? entry.Friend.CachedAppearance ?? new PetAppearance()).Clone();
        ToggleVisitFriendRequested?.Invoke(this, (entry.Friend, appearance));
    }

    [RelayCommand]
    private async Task QuickSendFriendRequestAsync(QuickOverlayEntry? entry)
    {
        if (entry?.Peer is null || !entry.CanSendFriendRequest)
        {
            return;
        }

        entry.IsSendingFriendRequest = true;
        try
        {
            var result = await _social.SendFriendRequestDetailedAsync(entry.Peer);
            if (result.Succeeded)
            {
                entry.IsFriendRequestPending = true;
                var routeLabel = result.Route == PeerTransportKind.GcpRelay ? "인터넷(GCP)" : "LAN";
                ShowBubble($"{entry.DisplayTitle}에게 [{routeLabel}] 친구 신청 완료!", 2400);
            }
            else
            {
                entry.IsFriendRequestPending = result.ErrorCode == FriendOperationErrorCodes.DuplicatePendingRequest;
                ShowBubble(SocialCoordinatorService.FormatFriendOperationFailure(result, isAccept: false, entry.DisplayTitle), 4200);
            }
        }
        finally
        {
            entry.IsSendingFriendRequest = false;
            RefreshQuickOverlayEntries();
        }
    }

    public void EnqueueFriendRequest(PendingFriendRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        if (_social.Friends.Any(f => string.Equals(f.PetId, request.RequesterPetId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (PendingFriendRequest is not null)
        {
            if (string.Equals(PendingFriendRequest.RequestId, request.RequestId, StringComparison.Ordinal)
                || string.Equals(PendingFriendRequest.RequesterPetId, request.RequesterPetId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_pendingFriendRequestQueue.Any(q =>
                string.Equals(q.RequestId, request.RequestId, StringComparison.Ordinal)
                || string.Equals(q.RequesterPetId, request.RequesterPetId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _pendingFriendRequestQueue.Enqueue(request);
            return;
        }

        PendingFriendRequest = request;
        PendingFriendRequestStatusText = string.Empty;
        CanRetryPendingFriendRequest = false;
    }

    private void MoveToNextPendingFriendRequest()
    {
        var currentFriends = _social.Friends;
        while (_pendingFriendRequestQueue.Count > 0)
        {
            var next = _pendingFriendRequestQueue.Dequeue();
            if (next.IsExpired)
            {
                continue;
            }

            if (currentFriends.Any(f => string.Equals(f.PetId, next.RequesterPetId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            PendingFriendRequest = next;
            PendingFriendRequestStatusText = string.Empty;
            CanRetryPendingFriendRequest = false;
            return;
        }

        PendingFriendRequest = null;
        PendingFriendRequestStatusText = string.Empty;
        CanRetryPendingFriendRequest = false;
    }

    [RelayCommand]
    private void CheckUpdates() => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ApplyUpdateAndRestart() => ApplyUpdateRestartRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenEnvironmentGuide() => OpenEnvironmentGuideRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void LaunchChristmasTree() => LaunchChristmasTreeRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSocial() => OpenSocialRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task AcceptFriendRequestAsync()
    {
        var req = PendingFriendRequest;
        if (req is null || IsRespondingFriendRequest)
        {
            return;
        }

        IsRespondingFriendRequest = true;
        CanRetryPendingFriendRequest = false;
        PendingFriendRequestStatusText = "수락 처리 중...";

        FriendOperationResult result;
        try
        {
            result = await _social.RespondFriendRequestDetailedAsync(req, accept: true);
        }
        finally
        {
            IsRespondingFriendRequest = false;
        }

        if (!ReferenceEquals(PendingFriendRequest, req))
        {
            return;
        }

        if (result.Succeeded)
        {
            MoveToNextPendingFriendRequest();
            return;
        }

        var errorText = SocialCoordinatorService.FormatFriendOperationFailure(result, isAccept: true, req.DisplayName);
        if (result.IsTransient)
        {
            CanRetryPendingFriendRequest = true;
            PendingFriendRequestStatusText = "수락 실패 · 다시 시도 가능";
            ShowBubble(errorText, 4500);
            return;
        }

        // 만료되었거나 알 수 없는 신청, 이미 친구, 확정적 인증 오류는 재수락하지 않고 요청을 정리한 뒤 안내한다.
        MoveToNextPendingFriendRequest();
        ShowBubble(errorText, 4500);
    }

    [RelayCommand]
    private async Task RejectFriendRequestAsync()
    {
        var req = PendingFriendRequest;
        if (req is null || IsRespondingFriendRequest)
        {
            return;
        }

        IsRespondingFriendRequest = true;
        PendingFriendRequestStatusText = "거절 처리 중...";
        try
        {
            var result = await _social.RespondFriendRequestDetailedAsync(req, accept: false);
            if (!result.Succeeded && !FriendOperationErrorCodes.IsExpiredOrUnknown(result.ErrorCode))
            {
                ShowBubble(SocialCoordinatorService.FormatFriendOperationFailure(result, isAccept: false, req.DisplayName), 3200);
            }
        }
        finally
        {
            IsRespondingFriendRequest = false;
            if (ReferenceEquals(PendingFriendRequest, req))
            {
                MoveToNextPendingFriendRequest();
            }
        }
    }

    [RelayCommand]
    private void OpenSoloHub() => OpenSoloHubRequested?.Invoke(this, EventArgs.Empty);

    public async Task SaveCurrentProfileAsync()
    {
        await _store.SaveProfileAsync(CurrentProfile);
        var label = SocialCoordinatorService.FormatDisplayName(CurrentProfile.OwnerName, CurrentProfile.PetName);
        TrayTooltip = $"{label} · Lv.{CurrentProfile.SoloProgress.Level}";
        ProfileProgressUpdated?.Invoke(this, CurrentProfile);
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
