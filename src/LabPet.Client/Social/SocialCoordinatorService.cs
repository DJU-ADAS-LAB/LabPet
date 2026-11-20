using System.Diagnostics;
using System.IO;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.Social;

public sealed record PendingFriendRequest(
    string RequesterPetId,
    string RequesterOwnerName,
    string RequesterName,
    PetAppearance Appearance,
    int Level,
    string RemoteIp,
    int RemoteTcpPort)
{
    public string DisplayName => SocialCoordinatorService.FormatDisplayName(RequesterOwnerName, RequesterName);
}

public sealed record ReceivedFriendMessage(string SenderName, string Text, PetAppearance Appearance);

/// <summary>
/// ISocialTransport, friends.json 저장소, 보너스 코인, 노래 따라하기(팔로워 타임아웃·우선순위)를 조정하는 소셜 코디네이터.
/// ViewModel과 View는 구체 전송 구현체가 아닌 ISocialTransport/SocialCoordinatorService만 의존한다.
/// </summary>
public sealed class SocialCoordinatorService : IAsyncDisposable
{
    private readonly ISocialTransport _transport;
    private readonly IAppDataStore _store;
    private readonly SoloProgressionService _progression;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _musicLock = new();
    private readonly object _friendStateLock = new();
    private readonly SemaphoreSlim _friendSaveLock = new(1, 1);

    private volatile PetProfile _localProfile = new();
    private volatile FriendEntry[] _friends = [];
    private uint _musicFrameSequence;

    // 노래 따라하기 팔로워 상태
    private string? _followingHostPetId;
    private MusicFrameData _lastFollowedFrame;
    private long _lastFollowedPacketTicks;
    private long _lastFollowedActiveSoundTicks;

    /// <summary>내 음악이 동시에 재생 중일 때 팔로우 대신 로컬 음악을 우선할지 여부.</summary>
    public bool PreferLocalMusicWhenActive { get; set; } = true;

    public string? FollowingHostPetId => _followingHostPetId;
    public IReadOnlyList<FriendEntry> Friends => Array.ConvertAll(_friends, static friend => friend.Clone());
    public IReadOnlyList<PeerSnapshot> DiscoveredPeers => _transport.GetDiscoveredPeers();

    public event EventHandler? StateChanged;
    public event EventHandler<PendingFriendRequest>? FriendRequestArrived;
    public event EventHandler<ReceivedFriendMessage>? FriendMessageReceived;
    public event EventHandler<string>? SocialNotification;
    public event EventHandler? PetReactionTriggered;
    public event EventHandler<string>? NewerAppVersionDetected;

    public SocialCoordinatorService(
        ISocialTransport transport,
        IAppDataStore store,
        SoloProgressionService progression)
    {
        _transport = transport;
        _store = store;
        _progression = progression;

        _transport.PeersChanged += OnPeersChanged;
        _transport.FriendRequestReceived += OnFriendRequestReceived;
        _transport.FriendAccepted += OnFriendAccepted;
        _transport.FriendRejected += OnFriendRejected;
        _transport.BubbleMessageReceived += OnBubbleMessageReceived;
        _transport.QuickReactionReceived += OnQuickReactionReceived;
        _transport.PetInteractionReceived += OnPetInteractionReceived;
        _transport.MusicFrameReceived += OnMusicFrameReceived;
        _transport.TransportError += OnTransportError;
    }

    public async Task StartAsync(PetProfile localProfile, CancellationToken cancellationToken = default)
    {
        _localProfile = localProfile;
        var friendsStore = await _store.LoadFriendsAsync(cancellationToken).ConfigureAwait(false);
        _friends = [.. friendsStore.Friends];
        await _transport.StartAsync(_localProfile, _friends, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string FormatDisplayName(string? ownerName, string? petName)
    {
        var owner = (ownerName ?? string.Empty).Trim();
        var pet = (petName ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(pet))
        {
            return $"{owner} ({pet})";
        }

        return !string.IsNullOrEmpty(owner) ? owner : (string.IsNullOrEmpty(pet) ? "이름 없음" : pet);
    }

    public async Task UpdateLocalPresenceAsync(PetProfile profile, bool isMusicActive, CancellationToken cancellationToken = default)
    {
        _localProfile = profile;
        var version = typeof(SocialCoordinatorService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        await _transport.UpdateLocalPresenceAsync(
            new LocalPresenceState(
                profile.OwnerName,
                profile.PetName,
                profile.Appearance.Clone(),
                profile.SoloProgress.Level,
                isMusicActive,
                version),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>호스트로서 구독 중인 친구들에게만 30Hz 경량 오디오 프레임을 전송한다.</summary>
    public ValueTask PublishHostMusicFrameAsync(float bass, float mid, float treble, bool isBeat)
    {
        var seq = Interlocked.Increment(ref _musicFrameSequence);
        return _transport.PublishMusicFrameAsync(new MusicFrameData(bass, mid, treble, isBeat, seq));
    }

    /// <summary>
    /// 팔로워 모드일 때 수신된 호스트 오디오 프레임을 반환한다.
    /// 1.5초 이상 미수신 또는 호스트 2초 이상 무음 시 자동으로 로컬 모드로 복귀한다.
    /// </summary>
    public bool TryGetFollowedMusicFrame(bool isLocalMusicActive, out MusicFrameData frame)
    {
        string? timedOutHost = null;
        lock (_musicLock)
        {
            if (_followingHostPetId is null)
            {
                frame = default;
                return false;
            }

            var nowTicks = _clock.ElapsedTicks;
            var sincePacketMs = (nowTicks - _lastFollowedPacketTicks) * 1000.0 / Stopwatch.Frequency;
            var sinceSoundMs = (nowTicks - _lastFollowedActiveSoundTicks) * 1000.0 / Stopwatch.Frequency;

            if (sincePacketMs > ProtocolConstants.MusicFrameTimeoutMs || sinceSoundMs > ProtocolConstants.MusicHostSilenceTimeoutMs)
            {
                timedOutHost = _followingHostPetId;
                _followingHostPetId = null;
                frame = default;
            }
            else if (PreferLocalMusicWhenActive && isLocalMusicActive)
            {
                // 내 음악 우선 옵션이 켜져 있으면 로컬 오디오 사용
                frame = default;
                return false;
            }
            else
            {
                frame = _lastFollowedFrame;
                return true;
            }
        }

        if (timedOutHost is not null)
        {
            var hostName = FindFriendName(timedOutHost);
            SocialNotification?.Invoke(this, $"{hostName}의 음악이 멈춰 로컬 모드로 복귀했어요.");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return false;
    }

    public Task<bool> SendFriendRequestAsync(PeerSnapshot peer)
        => _transport.SendFriendRequestAsync(peer);

    private string? _lastTransportError;

    public async Task<bool> RespondFriendRequestAsync(PendingFriendRequest request, bool accept)
    {
        _lastTransportError = null;
        string? secret = accept ? PacketSigner.GeneratePairSecretBase64() : null;
        var sent = await _transport.RespondFriendRequestAsync(
            request.RequesterPetId,
            request.RemoteIp,
            request.RemoteTcpPort,
            accept,
            secret).ConfigureAwait(false);

        if (accept && sent && secret is not null)
        {
            await UpsertFriendAsync(new FriendEntry
            {
                PetId = request.RequesterPetId,
                OwnerName = request.RequesterOwnerName,
                PetName = request.RequesterName,
                LastKnownIp = request.RemoteIp,
                LastKnownTcpPort = request.RemoteTcpPort,
                PairSecretBase64 = secret,
                CachedAppearance = request.Appearance.Clone(),
                Intimacy = 1,
                PairedAtUtc = DateTimeOffset.UtcNow,
                LastSeenAtUtc = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);

            SocialNotification?.Invoke(this, $"{request.DisplayName}(와)과 친구가 되었어요!");
            PetReactionTriggered?.Invoke(this, EventArgs.Empty);
        }
        else if (accept && !sent)
        {
            var reason = string.IsNullOrWhiteSpace(_lastTransportError)
                ? "상대 PC 방화벽(TCP 45680) 또는 네트워크를 확인해 주세요."
                : $"연결 실패({request.RemoteIp}:{request.RemoteTcpPort}) · 상대 PC 방화벽(TCP 45680)을 확인해 주세요.";
            SocialNotification?.Invoke(this, $"{request.DisplayName} 수락 전송 실패: {reason}");
        }

        return sent;
    }

    public async Task<bool> SendBubbleMessageAsync(FriendEntry friend, string text)
    {
        var ok = await _transport.SendBubbleMessageAsync(FindFriend(friend.PetId) ?? friend, text).ConfigureAwait(false);
        if (ok)
        {
            await RewardSocialBonusAsync(friend.PetId, coins: 2).ConfigureAwait(false);
        }

        return ok;
    }

    public async Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId)
    {
        var ok = await _transport.SendQuickReactionAsync(FindFriend(friend.PetId) ?? friend, reactionId).ConfigureAwait(false);
        if (ok)
        {
            await RewardSocialBonusAsync(friend.PetId, coins: 1).ConfigureAwait(false);
        }

        return ok;
    }

    public async Task<bool> SendPetInteractionAsync(FriendEntry friend)
    {
        var ok = await _transport.SendPetInteractionAsync(FindFriend(friend.PetId) ?? friend, "pet").ConfigureAwait(false);
        if (ok)
        {
            await RewardSocialBonusAsync(friend.PetId, coins: 2).ConfigureAwait(false);
        }

        return ok;
    }

    public async Task<bool> ToggleMusicFollowAsync(FriendEntry hostFriend)
    {
        hostFriend = FindFriend(hostFriend.PetId) ?? hostFriend;
        var previousHostId = FollowingHostPetId;
        var currentlyFollowingThis = string.Equals(previousHostId, hostFriend.PetId, StringComparison.OrdinalIgnoreCase);
        if (currentlyFollowingThis)
        {
            await _transport.SetMusicFollowAsync(hostFriend, enable: false).ConfigureAwait(false);
            lock (_musicLock)
            {
                _followingHostPetId = null;
            }

            SocialNotification?.Invoke(this, $"{hostFriend.PetName} 노래 따라하기를 종료했어요.");
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // 기존 팔로우 호스트가 있으면 먼저 해제
        if (previousHostId is not null)
        {
            var prev = FindFriend(previousHostId);
            if (prev is not null)
            {
                _ = await _transport.SetMusicFollowAsync(prev, enable: false).ConfigureAwait(false);
            }
        }

        var ok = await _transport.SetMusicFollowAsync(hostFriend, enable: true).ConfigureAwait(false);
        if (ok)
        {
            var nowTicks = _clock.ElapsedTicks;
            lock (_musicLock)
            {
                _followingHostPetId = hostFriend.PetId;
                _lastFollowedPacketTicks = nowTicks;
                _lastFollowedActiveSoundTicks = nowTicks;
            }

            SocialNotification?.Invoke(this, $"{hostFriend.PetName}의 음악을 따라 추는 중!");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return ok;
    }

    private void OnTransportError(object? sender, TransportErrorEventArgs e)
    {
        _lastTransportError = e.Message;
    }

    private void OnPeersChanged(object? sender, IReadOnlyList<PeerSnapshot> peers)
    {
        var localVersion = typeof(SocialCoordinatorService).Assembly.GetName().Version;
        if (localVersion is not null)
        {
            var normalizedLocal = new Version(localVersion.Major, localVersion.Minor, Math.Max(0, localVersion.Build));
            foreach (var peer in peers)
            {
                if (Version.TryParse(peer.AppVersion, out var peerVer) && peerVer > normalizedLocal)
                {
                    NewerAppVersionDetected?.Invoke(this, peer.AppVersion);
                    break;
                }
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFriendRequestReceived(object? sender, FriendRequestReceivedEventArgs e)
    {
        FriendRequestArrived?.Invoke(this, new PendingFriendRequest(
            e.RequesterPetId,
            e.RequesterOwnerName,
            e.RequesterName,
            e.Appearance,
            e.Level,
            e.RemoteIp,
            e.RemoteTcpPort));
    }

    private async void OnFriendAccepted(object? sender, FriendAcceptedEventArgs e)
    {
        await UpsertFriendAsync(new FriendEntry
        {
            PetId = e.AccepterPetId,
            OwnerName = e.AccepterOwnerName,
            PetName = e.AccepterName,
            LastKnownIp = e.RemoteIp,
            LastKnownTcpPort = e.RemoteTcpPort,
            PairSecretBase64 = e.PairSecretBase64,
            CachedAppearance = e.Appearance.Clone(),
            Intimacy = 1,
            PairedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow
        }).ConfigureAwait(false);

        var display = FormatDisplayName(e.AccepterOwnerName, e.AccepterName);
        SocialNotification?.Invoke(this, $"{display}(이)가 친구 신청을 수락했어요!");
        PetReactionTriggered?.Invoke(this, EventArgs.Empty);
    }

    private void OnFriendRejected(object? sender, FriendRejectedEventArgs e)
    {
        SocialNotification?.Invoke(this, "친구 신청이 거절되었어요.");
    }

    private async void OnBubbleMessageReceived(object? sender, BubbleMessageReceivedEventArgs e)
    {
        var name = await RewardSocialBonusAsync(e.SenderPetId, coins: 2).ConfigureAwait(false);
        var peer = DiscoveredPeers.FirstOrDefault(peer =>
            string.Equals(peer.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase));
        var appearance = peer?.Appearance?.Clone() ?? FindFriend(e.SenderPetId)?.CachedAppearance ?? new PetAppearance();

        FriendMessageReceived?.Invoke(this, new ReceivedFriendMessage(name, e.Text, appearance));
        PetReactionTriggered?.Invoke(this, EventArgs.Empty);
    }

    private async void OnQuickReactionReceived(object? sender, QuickReactionReceivedEventArgs e)
    {
        var name = await RewardSocialBonusAsync(e.SenderPetId, coins: 1).ConfigureAwait(false);

        SocialNotification?.Invoke(this, $"{name}의 반응: {e.ReactionId}");
        PetReactionTriggered?.Invoke(this, EventArgs.Empty);
    }

    private async void OnPetInteractionReceived(object? sender, PetInteractionReceivedEventArgs e)
    {
        var name = await RewardSocialBonusAsync(e.SenderPetId, coins: 2).ConfigureAwait(false);

        SocialNotification?.Invoke(this, $"{name}(이)가 쓰다듬어 줬어요!");
        PetReactionTriggered?.Invoke(this, EventArgs.Empty);
    }

    private void OnMusicFrameReceived(object? sender, MusicFrameReceivedEventArgs e)
    {
        lock (_musicLock)
        {
            if (!string.Equals(_followingHostPetId, e.HostPetId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var nowTicks = _clock.ElapsedTicks;
            _lastFollowedFrame = e.Frame;
            _lastFollowedPacketTicks = nowTicks;

            var avgEnergy = (e.Frame.Bass + e.Frame.Mid + e.Frame.Treble) / 3.0f;
            if (avgEnergy >= 0.03f || e.Frame.IsBeat)
            {
                _lastFollowedActiveSoundTicks = nowTicks;
            }
        }
    }

    private async Task UpsertFriendAsync(FriendEntry entry)
    {
        // 수락 패킷에 응답하기 전에 인증 목록에 등록한다.
        lock (_friendStateLock)
        {
            var existing = FindSharedFriend(entry.PetId);
            if (existing is null)
            {
                _friends = [.. _friends, entry];
            }
            else
            {
                lock (existing)
                {
                    existing.OwnerName = entry.OwnerName;
                    existing.PetName = entry.PetName;
                    existing.LastKnownIp = entry.LastKnownIp;
                    existing.LastKnownTcpPort = entry.LastKnownTcpPort;
                    existing.PairSecretBase64 = entry.PairSecretBase64;
                    existing.CachedAppearance = entry.CachedAppearance;
                    existing.LastSeenAtUtc = entry.LastSeenAtUtc;
                }
            }

            _transport.UpdateKnownFriends(_friends);
        }

        await SaveSocialStateAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> RewardSocialBonusAsync(string petId, long coins)
    {
        string name;
        PetProfile profile;
        lock (_friendStateLock)
        {
            var friend = FindSharedFriend(petId);
            if (friend is null)
            {
                return "친구";
            }

            lock (friend)
            {
                friend.Intimacy++;
                name = FormatDisplayName(friend.OwnerName, friend.PetName);
            }

            profile = _localProfile;
            _progression.GrantRewards(profile, xp: 3, coins: coins);
        }

        await SaveSocialStateAsync(profile).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return name;
    }

    private async Task SaveSocialStateAsync(PetProfile? profile = null)
    {
        await _friendSaveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // 저장 순서가 돌아왔을 때 최신 목록을 복사한다.
            await _store.SaveFriendsAsync(new FriendsStore { Friends = [.. Friends] }).ConfigureAwait(false);
            if (profile is not null)
            {
                await _store.SaveProfileAsync(profile).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("소셜 상태 저장 실패: {0}", ex);
            SocialNotification?.Invoke(this, "친구 정보 또는 보상을 저장하지 못했어요.");
        }
        finally
        {
            _friendSaveLock.Release();
        }
    }

    private FriendEntry? FindSharedFriend(string petId)
        => Array.Find(_friends, f => string.Equals(f.PetId, petId, StringComparison.OrdinalIgnoreCase));

    private FriendEntry? FindFriend(string petId)
        => FindSharedFriend(petId)?.Clone();

    private string FindFriendName(string petId)
    {
        var f = FindFriend(petId);
        return f is null ? "친구" : FormatDisplayName(f.OwnerName, f.PetName);
    }

    public async ValueTask DisposeAsync()
    {
        _transport.PeersChanged -= OnPeersChanged;
        _transport.FriendRequestReceived -= OnFriendRequestReceived;
        _transport.FriendAccepted -= OnFriendAccepted;
        _transport.FriendRejected -= OnFriendRejected;
        _transport.BubbleMessageReceived -= OnBubbleMessageReceived;
        _transport.QuickReactionReceived -= OnQuickReactionReceived;
        _transport.PetInteractionReceived -= OnPetInteractionReceived;
        _transport.MusicFrameReceived -= OnMusicFrameReceived;
        _transport.TransportError -= OnTransportError;
        await _transport.StopAsync().ConfigureAwait(false);
    }
}
