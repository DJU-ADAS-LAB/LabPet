using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Social;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.ViewModels;

public sealed partial class PeerListItemViewModel : ObservableObject
{
    public PeerSnapshot Snapshot { get; }
    public string DisplayTitle => SocialCoordinatorService.FormatDisplayName(Snapshot.OwnerName, Snapshot.PetName);
    public string PetName => Snapshot.PetName;
    public string SubInfo => Snapshot.TransportKind == PeerTransportKind.GcpRelay
        ? $"Lv.{Snapshot.Level} · 인터넷(GCP) · v{Snapshot.AppVersion}"
        : $"Lv.{Snapshot.Level} · {Snapshot.IpAddress} · v{Snapshot.AppVersion}";
    public bool IsFriend => Snapshot.IsFriend;
    public bool IsMusicActive => Snapshot.IsMusicActive;
    public bool IsSleeping => Snapshot.IsSleeping;
    public bool RequiresUpdate => Snapshot.RequiresUpdate;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSendFriendRequest), nameof(FriendRequestButtonText))]
    private bool _isSendingFriendRequest;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSendFriendRequest), nameof(FriendRequestButtonText))]
    private bool _isFriendRequestPending;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FriendRequestButtonText))]
    private bool _canRetryFriendRequest;

    public bool CanSendFriendRequest => !Snapshot.IsFriend && !Snapshot.RequiresUpdate && !IsSendingFriendRequest && !IsFriendRequestPending;
    public string FriendRequestButtonText => IsSendingFriendRequest
        ? "전송 중..."
        : IsFriendRequestPending
            ? "신청 완료"
            : CanRetryFriendRequest ? "다시 신청" : "친구 신청";

    public string UpdateBadgeText => Snapshot.Compatibility switch
    {
        ProtocolCompatibility.PeerOutdated => "업데이트 필요 (상대)",
        ProtocolCompatibility.LocalOutdated => "업데이트 필요 (내 앱)",
        _ => string.Empty
    };

    public string UpdateTooltip => Snapshot.Compatibility switch
    {
        ProtocolCompatibility.PeerOutdated => $"상대 피어 프로토콜(v{Snapshot.ProtocolVersion})이 낮아 연결이 차단되었습니다. 상대방 업데이트가 필요합니다.",
        ProtocolCompatibility.LocalOutdated => $"상대 피어 프로토콜(v{Snapshot.ProtocolVersion})이 더 최신입니다. 내 LabPet을 업데이트해 주세요.",
        _ => "프로토콜 호환"
    };

    public PeerListItemViewModel(
        PeerSnapshot snapshot,
        OutgoingFriendRequestState outgoingState = OutgoingFriendRequestState.None,
        bool canRetry = false)
    {
        Snapshot = snapshot;
        _isSendingFriendRequest = outgoingState == OutgoingFriendRequestState.Sending;
        _isFriendRequestPending = outgoingState == OutgoingFriendRequestState.Pending;
        _canRetryFriendRequest = canRetry;
    }
}

public sealed class FriendListItemViewModel
{
    public FriendEntry Entry { get; }
    public SnowballAction? Snowball { get; init; }
    public PetAppearance EffectiveAppearance { get; }
    public bool IsOnline { get; }
    public bool IsMusicActive { get; }
    public bool IsSleeping { get; }
    public bool ShowsSleepPose => IsSleeping || !IsOnline;
    public bool IsFollowingMusic { get; }
    public bool IsVisitingDesktop { get; }
    public bool RequiresUpdate { get; }
    public string UpdateBadgeText { get; }

    public string DisplayTitle => SocialCoordinatorService.FormatDisplayName(Entry.OwnerName, Entry.PetName);
    public string OwnerName => string.IsNullOrWhiteSpace(Entry.OwnerName) ? "주인 미설정" : Entry.OwnerName;
    public string PetName => Entry.PetName;
    public string PetNameSubtitle => $"펫: {Entry.PetName}";
    public string StatusText => RequiresUpdate
        ? UpdateBadgeText
        : IsOnline
            ? (IsSleeping, IsMusicActive) switch
            {
                (true, true) => "온라인 · zzz · 🎵 음악 반응 중",
                (true, false) => "온라인 · zzz",
                (false, true) => "온라인 · 🎵 음악 반응 중",
                (false, false) => "온라인"
            }
            : "오프라인";

    public int ClampedIntimacy => FriendEntry.NormalizeIntimacy(Entry.Intimacy);
    public bool IsIntimacyMax => ClampedIntimacy >= FriendEntry.MaxIntimacy;
    public string IntimacyText => IsIntimacyMax
        ? "친밀도 MAX"
        : $"친밀도 {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(ClampedIntimacy)}";
    public string IntimacyToolTip => IsIntimacyMax
        ? $"최대 친밀도 달성 ({LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(FriendEntry.MaxIntimacy)} / {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(FriendEntry.MaxIntimacy)})"
        : $"친밀도 {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(ClampedIntimacy)} / {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(FriendEntry.MaxIntimacy)}";
    public string MusicFollowButtonText => IsFollowingMusic
        ? "⏹  노래 따라하기 중지"
        : !IsOnline
            ? "🎵  오프라인"
            : RequiresUpdate
                ? "🎵  업데이트 필요"
                : IsMusicActive
                    ? "🎵  노래 따라하기"
                    : "🎵  음악 재생 중이 아님";
    public string VisitButtonText => IsVisitingDesktop ? "바탕화면에서 돌려보내기" : "내 바탕화면에 데려오기";
    public bool CanFollowMusic => IsOnline && !RequiresUpdate && (IsMusicActive || IsFollowingMusic);
    public bool CanInteract => IsOnline && !RequiresUpdate;

    public FriendListItemViewModel(
        FriendEntry entry,
        PeerSnapshot? livePeer,
        bool isFollowingMusic,
        bool isVisitingDesktop)
    {
        Entry = entry;
        EffectiveAppearance = (livePeer?.Appearance ?? entry.CachedAppearance ?? new PetAppearance()).Clone();
        IsOnline = livePeer is not null;
        IsMusicActive = livePeer?.IsMusicActive ?? false;
        IsSleeping = livePeer?.IsSleeping ?? false;
        IsFollowingMusic = isFollowingMusic;
        IsVisitingDesktop = isVisitingDesktop;
        RequiresUpdate = livePeer?.RequiresUpdate ?? false;
        UpdateBadgeText = livePeer?.Compatibility switch
        {
            ProtocolCompatibility.PeerOutdated => "업데이트 필요 (상대)",
            ProtocolCompatibility.LocalOutdated => "업데이트 필요 (내 앱)",
            _ => string.Empty
        };
    }
}

/// <summary>
/// LAN 주변 펫 탐색 · 친구 신청 · 말풍선 메시지 · 퀵 이모지 · 노래 따라하기 ViewModel.
/// </summary>
public sealed partial class SocialViewModel : ObservableObject
{
    private const string DefaultStatusBanner = "같은 로컬 네트워크(LAN) 및 인터넷(GCP)의 LabPet을 탐색 중입니다.";
    private const string MessageSendFailureStatus = "메시지 전송에 실패했습니다.";

    private readonly SocialCoordinatorService _social;
    private readonly SnowballInteractionService? _snowballs;
    private readonly HashSet<string> _retryableFailedPeerIds = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, bool>? _isVisitorActivePredicate;

    public event EventHandler<(FriendEntry Friend, PetAppearance Appearance)>? ToggleVisitDesktopRequested;

    public ObservableCollection<PeerListItemViewModel> NearbyPeers { get; } = [];
    public ObservableCollection<FriendListItemViewModel> Friends { get; } = [];

    [ObservableProperty] private FriendListItemViewModel? _selectedFriend;
    [ObservableProperty] private string _messageText = string.Empty;
    [ObservableProperty] private string _statusBanner = DefaultStatusBanner;
    [ObservableProperty] private bool _preferLocalMusicWhenActive = true;

    public bool HasSelectedFriend => SelectedFriend is not null;
    public string RemoteConnectionStatusText => _social.ConnectionSummary;

    public SocialViewModel(SocialCoordinatorService social, SnowballInteractionService? snowballs = null)
    {
        _social = social;
        _snowballs = snowballs;
        _preferLocalMusicWhenActive = _social.PreferLocalMusicWhenActive;
        _social.StateChanged += OnSocialStateChanged;
        _social.TransportErrorReported += OnTransportErrorReported;
        RefreshLists();
    }

    public void AttachVisitorStateChecker(Func<string, bool> isVisitorActivePredicate)
    {
        _isVisitorActivePredicate = isVisitorActivePredicate;
        RefreshLists();
    }

    partial void OnSelectedFriendChanged(FriendListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedFriend));
    }

    partial void OnPreferLocalMusicWhenActiveChanged(bool value)
    {
        _social.PreferLocalMusicWhenActive = value;
    }

    private void OnSocialStateChanged(object? sender, EventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(RefreshLists);
        }
        else
        {
            RefreshLists();
        }
    }

    private void OnTransportErrorReported(object? sender, TransportErrorEventArgs e)
    {
        void Apply()
        {
            OnPropertyChanged(nameof(RemoteConnectionStatusText));
            if (e.Operation is "Listen" or "GcpAuth" or "GcpConnect" or "GcpRegisterPresence")
            {
                StatusBanner = _social.LastTransportErrorMessage ?? e.Message;
            }
        }

        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    public void RefreshLists()
    {
        var peers = _social.DiscoveredPeers;
        var selectedPetId = SelectedFriend?.Entry.PetId;

        var friends = _social.Friends;
        var friendPetIds = new HashSet<string>(friends.Select(f => f.PetId), StringComparer.OrdinalIgnoreCase);

        // 이미 친구로 추가된 펫은 주변 펫 목록에서 제외
        NearbyPeers.Clear();
        foreach (var peer in peers)
        {
            if (peer.IsFriend || friendPetIds.Contains(peer.PetId))
            {
                _retryableFailedPeerIds.Remove(peer.PetId);
                continue;
            }

            var outgoingState = _social.GetOutgoingFriendRequestState(peer.PetId);
            var canRetry = outgoingState == OutgoingFriendRequestState.None && _retryableFailedPeerIds.Contains(peer.PetId);
            NearbyPeers.Add(new PeerListItemViewModel(peer, outgoingState, canRetry));
        }

        Friends.Clear();
        foreach (var friend in friends)
        {
            var live = peers.FirstOrDefault(p => string.Equals(p.PetId, friend.PetId, StringComparison.OrdinalIgnoreCase));
            var isFollowing = string.Equals(_social.FollowingHostPetId, friend.PetId, StringComparison.OrdinalIgnoreCase);
            var isVisiting = _isVisitorActivePredicate?.Invoke(friend.PetId) ?? false;
            Friends.Add(new FriendListItemViewModel(friend, live, isFollowing, isVisiting)
            {
                Snowball = _snowballs?.ForFriend(friend)
            });
        }

        if (selectedPetId is not null)
        {
            SelectedFriend = Friends.FirstOrDefault(f => string.Equals(f.Entry.PetId, selectedPetId, StringComparison.OrdinalIgnoreCase));
        }
        else if (SelectedFriend is null && Friends.Count > 0)
        {
            SelectedFriend = Friends[0];
        }

        OnPropertyChanged(nameof(RemoteConnectionStatusText));
    }

    [RelayCommand]
    private async Task SendFriendRequestAsync(PeerListItemViewModel? peer)
    {
        if (peer is null || !peer.CanSendFriendRequest)
        {
            return;
        }

        var routeLabel = peer.Snapshot.TransportKind == PeerTransportKind.GcpRelay ? "인터넷(GCP)" : "LAN";
        peer.IsSendingFriendRequest = true;
        StatusBanner = $"{peer.DisplayTitle}에게 [{routeLabel}] 경로로 친구 신청 전송 중...";

        try
        {
            var result = await _social.SendFriendRequestDetailedAsync(peer.Snapshot);
            if (result.Succeeded)
            {
                _retryableFailedPeerIds.Remove(peer.Snapshot.PetId);
                peer.CanRetryFriendRequest = false;
                peer.IsFriendRequestPending = true;
                var actualRoute = result.Route == PeerTransportKind.GcpRelay ? "인터넷(GCP)" : "LAN";
                StatusBanner = $"{peer.DisplayTitle}에게 [{actualRoute}] 친구 신청을 보냈습니다. 상대방 수락 대기 중...";
            }
            else
            {
                peer.IsFriendRequestPending = result.ErrorCode == FriendOperationErrorCodes.DuplicatePendingRequest;
                peer.CanRetryFriendRequest = result.IsTransient;
                if (result.IsTransient)
                {
                    _retryableFailedPeerIds.Add(peer.Snapshot.PetId);
                }
                else
                {
                    _retryableFailedPeerIds.Remove(peer.Snapshot.PetId);
                }

                StatusBanner = SocialCoordinatorService.FormatFriendOperationFailure(result, isAccept: false, peer.DisplayTitle);
            }
        }
        finally
        {
            peer.IsSendingFriendRequest = false;
        }
    }

    [RelayCommand]
    private async Task SendBubbleMessageAsync()
    {
        if (SelectedFriend is null || !SelectedFriend.CanInteract)
        {
            StatusBanner = "온라인 상태의 친구를 선택해 주세요.";
            return;
        }

        var trimmed = (MessageText ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var ok = await _social.SendBubbleMessageAsync(SelectedFriend.Entry, trimmed);
        if (ok)
        {
            MessageText = string.Empty;
            if (StatusBanner == MessageSendFailureStatus)
            {
                StatusBanner = DefaultStatusBanner;
            }
        }
        else
        {
            StatusBanner = MessageSendFailureStatus;
        }
    }

    [RelayCommand]
    private async Task SendQuickReactionAsync(string? emoji)
    {
        if (SelectedFriend is null || !SelectedFriend.CanInteract || string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        var ok = await _social.SendQuickReactionAsync(SelectedFriend.Entry, emoji);
        StatusBanner = ok
            ? $"{SelectedFriend.DisplayTitle}에게 {emoji} 반응을 보냈습니다."
            : "반응 전송에 실패했습니다.";
    }

    [RelayCommand]
    private async Task SendPetInteractionAsync()
    {
        if (SelectedFriend is not { CanInteract: true } friend)
        {
            return;
        }

        var result = await _social.SendPetInteractionAsync(friend.Entry);
        if (result != PetInteractionSendResult.Cooldown)
        {
            StatusBanner = result == PetInteractionSendResult.Sent
                ? $"{friend.DisplayTitle}에게 인사했습니다."
                : "인사하기 전송에 실패했습니다.";
        }
    }

    [RelayCommand]
    private async Task ToggleMusicFollowAsync(FriendListItemViewModel? friend)
    {
        var target = friend ?? SelectedFriend;
        if (target is null || !target.CanFollowMusic)
        {
            return;
        }

        var ok = await _social.ToggleMusicFollowAsync(target.Entry);
        if (!ok)
        {
            StatusBanner = "노래 따라하기 요청에 실패했습니다.";
        }
    }

    [RelayCommand]
    private void ToggleVisitDesktop(FriendListItemViewModel? friend)
    {
        var target = friend ?? SelectedFriend;
        if (target is null)
        {
            return;
        }

        ToggleVisitDesktopRequested?.Invoke(this, (target.Entry, target.EffectiveAppearance));
        RefreshLists();
    }

    public void Detach()
    {
        _social.StateChanged -= OnSocialStateChanged;
        _social.TransportErrorReported -= OnTransportErrorReported;
    }
}
