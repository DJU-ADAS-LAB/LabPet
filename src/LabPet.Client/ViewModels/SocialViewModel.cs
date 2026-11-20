using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Social;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.ViewModels;

public sealed class PeerListItemViewModel
{
    public PeerSnapshot Snapshot { get; }
    public string DisplayTitle => SocialCoordinatorService.FormatDisplayName(Snapshot.OwnerName, Snapshot.PetName);
    public string PetName => Snapshot.PetName;
    public string SubInfo => $"Lv.{Snapshot.Level} · {Snapshot.IpAddress} · v{Snapshot.AppVersion}";
    public bool IsFriend => Snapshot.IsFriend;
    public bool IsMusicActive => Snapshot.IsMusicActive;
    public bool RequiresUpdate => Snapshot.RequiresUpdate;
    public bool CanSendFriendRequest => !Snapshot.IsFriend && !Snapshot.RequiresUpdate;

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

    public PeerListItemViewModel(PeerSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}

public sealed class FriendListItemViewModel
{
    public FriendEntry Entry { get; }
    public bool IsOnline { get; }
    public bool IsMusicActive { get; }
    public bool IsFollowingMusic { get; }
    public bool RequiresUpdate { get; }
    public string UpdateBadgeText { get; }

    public string DisplayTitle => SocialCoordinatorService.FormatDisplayName(Entry.OwnerName, Entry.PetName);
    public string OwnerName => string.IsNullOrWhiteSpace(Entry.OwnerName) ? "주인 미설정" : Entry.OwnerName;
    public string PetName => Entry.PetName;
    public string PetNameSubtitle => $"펫: {Entry.PetName}";
    public string StatusText => RequiresUpdate
        ? UpdateBadgeText
        : IsOnline
            ? (IsMusicActive ? "온라인 · 🎵 음악 반응 중" : "온라인")
            : "오프라인";

    public string IntimacyText => $"친밀도 {Entry.Intimacy}";
    public string MusicFollowButtonText => IsFollowingMusic ? "따라하기 중지" : "노래 따라하기";
    public bool CanFollowMusic => IsOnline && !RequiresUpdate && (IsMusicActive || IsFollowingMusic);
    public bool CanInteract => IsOnline && !RequiresUpdate;

    public FriendListItemViewModel(
        FriendEntry entry,
        PeerSnapshot? livePeer,
        bool isFollowingMusic)
    {
        Entry = entry;
        IsOnline = livePeer is not null;
        IsMusicActive = livePeer?.IsMusicActive ?? false;
        IsFollowingMusic = isFollowingMusic;
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
    private readonly SocialCoordinatorService _social;

    public ObservableCollection<PeerListItemViewModel> NearbyPeers { get; } = [];
    public ObservableCollection<FriendListItemViewModel> Friends { get; } = [];

    [ObservableProperty] private FriendListItemViewModel? _selectedFriend;
    [ObservableProperty] private string _messageText = string.Empty;
    [ObservableProperty] private string _statusBanner = "같은 로컬 네트워크(LAN)의 LabPet을 자동 탐색 중입니다.";
    [ObservableProperty] private bool _preferLocalMusicWhenActive = true;

    public SocialViewModel(SocialCoordinatorService social)
    {
        _social = social;
        _preferLocalMusicWhenActive = _social.PreferLocalMusicWhenActive;
        _social.StateChanged += OnSocialStateChanged;
        RefreshLists();
    }

    partial void OnPreferLocalMusicWhenActiveChanged(bool value)
    {
        _social.PreferLocalMusicWhenActive = value;
    }

    private void OnSocialStateChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(RefreshLists);
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
                continue;
            }

            NearbyPeers.Add(new PeerListItemViewModel(peer));
        }

        Friends.Clear();
        foreach (var friend in friends)
        {
            var live = peers.FirstOrDefault(p => string.Equals(p.PetId, friend.PetId, StringComparison.OrdinalIgnoreCase));
            var isFollowing = string.Equals(_social.FollowingHostPetId, friend.PetId, StringComparison.OrdinalIgnoreCase);
            Friends.Add(new FriendListItemViewModel(friend, live, isFollowing));
        }

        if (selectedPetId is not null)
        {
            SelectedFriend = Friends.FirstOrDefault(f => string.Equals(f.Entry.PetId, selectedPetId, StringComparison.OrdinalIgnoreCase));
        }
        else if (SelectedFriend is null && Friends.Count > 0)
        {
            SelectedFriend = Friends[0];
        }
    }

    [RelayCommand]
    private async Task SendFriendRequestAsync(PeerListItemViewModel? peer)
    {
        if (peer is null || !peer.CanSendFriendRequest)
        {
            return;
        }

        var ok = await _social.SendFriendRequestAsync(peer.Snapshot);
        StatusBanner = ok
            ? $"{peer.DisplayTitle}에게 친구 신청을 보냈습니다."
            : $"{peer.DisplayTitle}에게 친구 신청을 보내지 못했습니다. (방화벽/네트워크 확인)";
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
            StatusBanner = $"{SelectedFriend.DisplayTitle}에게 메시지를 전송했습니다.";
        }
        else
        {
            StatusBanner = "메시지 전송에 실패했습니다.";
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
        if (SelectedFriend is null || !SelectedFriend.CanInteract)
        {
            return;
        }

        var ok = await _social.SendPetInteractionAsync(SelectedFriend.Entry);
        StatusBanner = ok
            ? $"{SelectedFriend.DisplayTitle}을(를) 쓰다듬어 주었습니다."
            : "쓰다듬기 전송에 실패했습니다.";
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

    public void Detach()
    {
        _social.StateChanged -= OnSocialStateChanged;
    }
}
