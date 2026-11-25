using System.Collections.Concurrent;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.Network;

/// <summary>
/// LAN P2P(<see cref="LanP2pTransport"/>)와 GCP SignalR(<see cref="GcpSignalRTransport"/>)을 통합하는 하이브리드 라우터.
/// - 같은 LAN의 온라인 친구 → LAN P2P 우선
/// - 외부 네트워크 친구 또는 LAN 실패 시 → GCP SignalR 자동 전환
/// - GCP 미연결/불가 시에도 LAN P2P 정상 유지
/// - 이중 경로 수신 시 바운디드 중복 방지 캐시로 동일 이벤트 1회만 상위 전달
/// </summary>
public sealed class HybridTransportRouter : ISocialTransport
{
    private enum RouteKind { Lan, Gcp }

    private sealed class BoundedEventDedup(int capacity, TimeSpan ttl)
    {
        private readonly object _lock = new();
        private readonly Queue<(string Key, DateTimeOffset ExpiresAt)> _order = new();
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public bool TryAccept(string key)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_lock)
            {
                while (_order.Count > 0 && (_order.Peek().ExpiresAt <= now || _order.Count >= capacity))
                {
                    var (old, _) = _order.Dequeue();
                    _keys.Remove(old);
                }

                if (!_keys.Add(key))
                {
                    return false;
                }

                _order.Enqueue((key, now + ttl));
                return true;
            }
        }
    }

    private readonly ConcurrentDictionary<string, long> _petSentAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _petReceivedAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PetInteractionInterval = TimeSpan.FromSeconds(1);

    private readonly LanP2pTransport _lan;
    private readonly GcpSignalRTransport _gcp;

    private readonly ConcurrentDictionary<string, RouteKind> _incomingRequestRoutes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RouteKind> _musicFollowRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly BoundedEventDedup _messageDedup = new(1024, TimeSpan.FromMinutes(3));
    private readonly BoundedEventDedup _pairingRequestDedup = new(256, TimeSpan.FromMinutes(3));
    private readonly BoundedEventDedup _pairingOutcomeDedup = new(256, TimeSpan.FromSeconds(30));
    private readonly BoundedEventDedup _gameEventDedup = new(512, TimeSpan.FromMilliseconds(1200));

    internal LanP2pTransport LanTransport => _lan;
    internal GcpSignalRTransport GcpTransport => _gcp;

    public string TransportName => "Hybrid(LAN+GCP)";
    public bool IsRunning => _lan.IsRunning || _gcp.IsRunning;
    public bool IsRemoteConnected => _gcp.IsRemoteConnected;

    public string ConnectionSummary => (_lan.IsRunning, _gcp.IsRemoteConnected) switch
    {
        (true, true) => "LAN P2P + 인터넷(GCP)",
        (true, false) => "LAN P2P (GCP 미연결)",
        (false, true) => "인터넷(GCP) 전용",
        _ => "네트워크 대기 중"
    };

    public event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged;
    public event EventHandler<FriendRequestReceivedEventArgs>? FriendRequestReceived;
    public event EventHandler<FriendAcceptedEventArgs>? FriendAccepted;
    public event EventHandler<FriendRejectedEventArgs>? FriendRejected;
    public event EventHandler<BubbleMessageReceivedEventArgs>? BubbleMessageReceived;
    public event EventHandler<QuickReactionReceivedEventArgs>? QuickReactionReceived;
    public event EventHandler<PetInteractionReceivedEventArgs>? PetInteractionReceived;
    public event EventHandler<SnowballReceivedEventArgs>? SnowballReceived;
    public event EventHandler<MusicFrameReceivedEventArgs>? MusicFrameReceived;
    public event EventHandler<MusicFollowStateChangedEventArgs>? MusicFollowStateChanged;
    public event EventHandler<GameEventReceivedEventArgs>? GameEventReceived;
    public event EventHandler<TransportErrorEventArgs>? TransportError;

    public HybridTransportRouter(LanP2pTransport lan, GcpSignalRTransport gcp)
    {
        _lan = lan;
        _gcp = gcp;
        WireEvents(_lan, RouteKind.Lan);
        WireEvents(_gcp, RouteKind.Gcp);
    }

    public async Task StartAsync(
        PetProfile localProfile,
        IReadOnlyList<FriendEntry> friends,
        CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(
            _lan.StartAsync(localProfile, friends, cancellationToken),
            _gcp.StartAsync(localProfile, friends, cancellationToken)).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(
            _lan.StopAsync(cancellationToken),
            _gcp.StopAsync(cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateLocalPresenceAsync(LocalPresenceState presence, CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(
            _lan.UpdateLocalPresenceAsync(presence, cancellationToken),
            _gcp.UpdateLocalPresenceAsync(presence, cancellationToken)).ConfigureAwait(false);
    }

    public void UpdateKnownFriends(IReadOnlyList<FriendEntry> friends)
    {
        _lan.UpdateKnownFriends(friends);
        _gcp.UpdateKnownFriends(friends);
    }

    public IReadOnlyList<PeerSnapshot> GetDiscoveredPeers()
    {
        var merged = new Dictionary<string, PeerSnapshot>(StringComparer.OrdinalIgnoreCase);

        // 같은 LAN에 있는 피어를 항상 우선 배치
        foreach (var lanPeer in _lan.GetDiscoveredPeers())
        {
            merged[lanPeer.PetId] = lanPeer;
        }

        foreach (var gcpPeer in _gcp.GetDiscoveredPeers())
        {
            merged.TryAdd(gcpPeer.PetId, gcpPeer);
        }

        return merged.Values
            .OrderByDescending(p => p.IsFriend)
            .ThenBy(p => p.PetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool HasPendingOutgoingRequest(string peerPetId)
        => _lan.HasPendingOutgoingRequest(peerPetId) || _gcp.HasPendingOutgoingRequest(peerPetId);

    public async Task<bool> SendFriendRequestAsync(PeerSnapshot target, CancellationToken cancellationToken = default)
        => (await SendFriendRequestDetailedAsync(target, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<FriendOperationResult> SendFriendRequestDetailedAsync(PeerSnapshot target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target.PetId))
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.InvalidRequest, false);
        }

        if (_lan.IsFriend(target.PetId) || _gcp.IsFriend(target.PetId))
        {
            var friendRoute = target.TransportKind == PeerTransportKind.GcpRelay && _gcp.IsFriend(target.PetId)
                ? PeerTransportKind.GcpRelay
                : PeerTransportKind.Lan;
            return FriendOperationResult.Fail(
                FriendOperationStage.Validation,
                FriendOperationErrorCodes.AlreadyFriends,
                false,
                friendRoute);
        }

        // LAN/GCP 양 경로 간 동일 상대 중복 발신 차단
        if (_lan.HasPendingOutgoingRequest(target.PetId) || _gcp.HasPendingOutgoingRequest(target.PetId))
        {
            var pendingRoute = _lan.HasPendingOutgoingRequest(target.PetId)
                ? PeerTransportKind.Lan
                : PeerTransportKind.GcpRelay;
            return FriendOperationResult.Fail(
                FriendOperationStage.Validation,
                FriendOperationErrorCodes.DuplicatePendingRequest,
                false,
                pendingRoute);
        }

        if (target.TransportKind == PeerTransportKind.GcpRelay)
        {
            var gcpResult = await _gcp.SendFriendRequestDetailedAsync(target, cancellationToken).ConfigureAwait(false);
            if (gcpResult.Succeeded || !gcpResult.IsTransient)
            {
                return gcpResult;
            }

            var lanPeer = _lan.GetDiscoveredPeers().FirstOrDefault(p =>
                string.Equals(p.PetId, target.PetId, StringComparison.OrdinalIgnoreCase) && !p.RequiresUpdate);
            if (lanPeer is not null)
            {
                var lanFallback = await _lan.SendFriendRequestDetailedAsync(lanPeer, cancellationToken).ConfigureAwait(false);
                return lanFallback.Succeeded ? lanFallback : gcpResult;
            }

            return gcpResult;
        }

        var lanResult = await _lan.SendFriendRequestDetailedAsync(target, cancellationToken).ConfigureAwait(false);
        if (lanResult.Succeeded)
        {
            return lanResult;
        }

        // 확정적 거부(ALREADY_FRIENDS, DUPLICATE_PENDING_REQUEST, PROTOCOL_MISMATCH 등)는 GCP로 불필요하게 재시도하지 않는다.
        if (!lanResult.IsTransient)
        {
            return lanResult;
        }

        if (_gcp.IsRemoteConnected)
        {
            return await _gcp.SendFriendRequestDetailedAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return lanResult;
    }

    public async Task<bool> RespondFriendRequestAsync(string requestId, bool accept, CancellationToken cancellationToken = default)
        => (await RespondFriendRequestDetailedAsync(requestId, accept, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<FriendOperationResult> RespondFriendRequestDetailedAsync(string requestId, bool accept, CancellationToken cancellationToken = default)
    {
        if (_incomingRequestRoutes.TryGetValue(requestId, out var route))
        {
            var result = route == RouteKind.Gcp
                ? await _gcp.RespondFriendRequestDetailedAsync(requestId, accept, cancellationToken).ConfigureAwait(false)
                : await _lan.RespondFriendRequestDetailedAsync(requestId, accept, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded || (!result.IsTransient && result.ErrorCode is not (FriendOperationErrorCodes.Cancelled or FriendOperationErrorCodes.RequestInProgress)))
            {
                _incomingRequestRoutes.TryRemove(requestId, out _);
            }

            return result;
        }

        var lanResult = await _lan.RespondFriendRequestDetailedAsync(requestId, accept, cancellationToken).ConfigureAwait(false);
        if (lanResult.Succeeded || (lanResult.ErrorCode != FriendOperationErrorCodes.PairingRequestUnknown && !lanResult.IsTransient))
        {
            return lanResult;
        }

        if (_gcp.IsRemoteConnected)
        {
            return await _gcp.RespondFriendRequestDetailedAsync(requestId, accept, cancellationToken).ConfigureAwait(false);
        }

        return lanResult;
    }

    public async Task<bool> SendBubbleMessageAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default)
        => (await SendBubbleMessageDetailedAsync(friend, message, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendBubbleMessageDetailedAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default)
        => SendRoutedDetailedAsync(friend, t => t.SendBubbleMessageDetailedAsync(friend, message, cancellationToken));

    public async Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default)
        => (await SendQuickReactionDetailedAsync(friend, reactionId, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendQuickReactionDetailedAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default)
        => SendRoutedDetailedAsync(friend, t => t.SendQuickReactionDetailedAsync(friend, reactionId, cancellationToken));

    public SnowballPeerStatus GetSnowballPeerStatus(string petId)
    {
        var lan = _lan.GetSnowballPeerStatus(petId);
        var gcp = _gcp.GetSnowballPeerStatus(petId);
        if (lan == SnowballPeerStatus.Available || gcp == SnowballPeerStatus.Available)
            return SnowballPeerStatus.Available;
        return lan == SnowballPeerStatus.Unsupported || gcp == SnowballPeerStatus.Unsupported
            ? SnowballPeerStatus.Unsupported : SnowballPeerStatus.Offline;
    }

    public async Task<bool> SendSnowballAsync(FriendEntry friend, Guid eventId, CancellationToken cancellationToken = default)
        => (await SendSnowballDetailedAsync(friend, eventId, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendSnowballDetailedAsync(FriendEntry friend, Guid eventId, CancellationToken cancellationToken = default)
        => SendRoutedDetailedAsync(friend, transport => transport.SendSnowballDetailedAsync(friend, eventId, cancellationToken));

    public async Task<bool> SendPetInteractionAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default)
        => (await SendPetInteractionDetailedAsync(friend, interactionType, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendPetInteractionDetailedAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default)
        => interactionType == PetInteractionPayload.PetType && TryAcceptPetInteraction(_petSentAt, friend.PetId)
            ? SendRoutedDetailedAsync(friend, t => t.SendPetInteractionDetailedAsync(friend, interactionType, cancellationToken))
            : Task.FromResult(TransportSendResult.Fail());

    [System.Reflection.Obfuscation(Exclude = false)]
    private static bool TryAcceptPetInteraction(ConcurrentDictionary<string, long> timestamps, string petId)
    {
        while (true)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!timestamps.TryGetValue(petId, out var previous))
            {
                if (timestamps.TryAdd(petId, now)) return true;
            }
            else
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(previous, now) < PetInteractionInterval)
                    return false;
                if (timestamps.TryUpdate(petId, now, previous)) return true;
            }
        }
    }

    public async Task<bool> SendGameEventAsync(FriendEntry friend, GameEventPayload payload, CancellationToken cancellationToken = default)
        => (await SendGameEventDetailedAsync(friend, payload, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendGameEventDetailedAsync(FriendEntry friend, GameEventPayload payload, CancellationToken cancellationToken = default)
        => SendRoutedDetailedAsync(friend, t => t.SendGameEventDetailedAsync(friend, payload, cancellationToken));

    public async Task<bool> SetMusicFollowAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default)
        => (await SetMusicFollowDetailedAsync(hostFriend, enable, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<TransportSendResult> SetMusicFollowDetailedAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default)
    {
        if (!enable)
        {
            if (_musicFollowRoutes.TryRemove(hostFriend.PetId, out var activeRoute))
            {
                return activeRoute == RouteKind.Lan
                    ? await _lan.SetMusicFollowDetailedAsync(hostFriend, false, cancellationToken).ConfigureAwait(false)
                    : await _gcp.SetMusicFollowDetailedAsync(hostFriend, false, cancellationToken).ConfigureAwait(false);
            }

            var lanOff = await _lan.SetMusicFollowDetailedAsync(hostFriend, false, cancellationToken).ConfigureAwait(false);
            if (lanOff.Succeeded)
            {
                return lanOff;
            }

            return _gcp.IsRemoteConnected
                ? await _gcp.SetMusicFollowDetailedAsync(hostFriend, false, cancellationToken).ConfigureAwait(false)
                : lanOff;
        }

        if (IsLanPeerOnline(hostFriend.PetId))
        {
            var lanResult = await _lan.SetMusicFollowDetailedAsync(hostFriend, true, cancellationToken).ConfigureAwait(false);
            if (lanResult.Succeeded)
            {
                _musicFollowRoutes[hostFriend.PetId] = RouteKind.Lan;
                return lanResult;
            }

            if (lanResult.IsPomodoroFocusBlocked)
            {
                return lanResult;
            }
        }

        if (_gcp.IsRemoteConnected)
        {
            var gcpResult = await _gcp.SetMusicFollowDetailedAsync(hostFriend, true, cancellationToken).ConfigureAwait(false);
            if (gcpResult.Succeeded)
            {
                _musicFollowRoutes[hostFriend.PetId] = RouteKind.Gcp;
                return gcpResult;
            }

            if (gcpResult.IsPomodoroFocusBlocked)
            {
                return gcpResult;
            }
        }

        if (!IsLanPeerOnline(hostFriend.PetId))
        {
            var fallbackLan = await _lan.SetMusicFollowDetailedAsync(hostFriend, true, cancellationToken).ConfigureAwait(false);
            if (fallbackLan.Succeeded)
            {
                _musicFollowRoutes[hostFriend.PetId] = RouteKind.Lan;
                return fallbackLan;
            }

            return fallbackLan;
        }

        return TransportSendResult.Fail();
    }

    public async ValueTask PublishMusicFrameAsync(MusicFrameData frame, CancellationToken cancellationToken = default)
    {
        await _lan.PublishMusicFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        await _gcp.PublishMusicFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        UnwireEvents(_lan);
        UnwireEvents(_gcp);
        await StopAsync().ConfigureAwait(false);
        await _lan.DisposeAsync().ConfigureAwait(false);
        await _gcp.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<TransportSendResult> SendRoutedDetailedAsync(FriendEntry friend, Func<ISocialTransport, Task<TransportSendResult>> sendFunc)
    {
        if (IsLanPeerOnline(friend.PetId))
        {
            var lanResult = await sendFunc(_lan).ConfigureAwait(false);
            if (lanResult.Succeeded || lanResult.IsPomodoroFocusBlocked)
            {
                return lanResult;
            }

            if (_gcp.IsRemoteConnected)
            {
                return await sendFunc(_gcp).ConfigureAwait(false);
            }

            return lanResult;
        }

        if (_gcp.IsRemoteConnected)
        {
            var gcpResult = await sendFunc(_gcp).ConfigureAwait(false);
            if (gcpResult.Succeeded || gcpResult.IsPomodoroFocusBlocked)
            {
                return gcpResult;
            }
        }

        return await sendFunc(_lan).ConfigureAwait(false);
    }

    private bool IsLanPeerOnline(string petId)
        => _lan.GetDiscoveredPeers().Any(p =>
            string.Equals(p.PetId, petId, StringComparison.OrdinalIgnoreCase) && !p.RequiresUpdate);

    private void WireEvents(ISocialTransport source, RouteKind route)
    {
        source.PeersChanged += OnSourcePeersChanged;
        if (route == RouteKind.Lan)
        {
            source.FriendRequestReceived += OnLanFriendRequestReceived;
        }
        else
        {
            source.FriendRequestReceived += OnGcpFriendRequestReceived;
        }

        source.FriendAccepted += OnSourceFriendAccepted;
        source.FriendRejected += OnSourceFriendRejected;
        source.BubbleMessageReceived += OnSourceBubbleMessageReceived;
        source.QuickReactionReceived += OnSourceQuickReactionReceived;
        source.PetInteractionReceived += OnSourcePetInteractionReceived;
        source.SnowballReceived += OnSourceSnowballReceived;
        source.MusicFrameReceived += OnSourceMusicFrameReceived;
        source.MusicFollowStateChanged += OnSourceMusicFollowStateChanged;
        source.GameEventReceived += OnSourceGameEventReceived;
        source.TransportError += OnSourceTransportError;
    }

    private void UnwireEvents(ISocialTransport source)
    {
        source.PeersChanged -= OnSourcePeersChanged;
        source.FriendRequestReceived -= OnLanFriendRequestReceived;
        source.FriendRequestReceived -= OnGcpFriendRequestReceived;
        source.FriendAccepted -= OnSourceFriendAccepted;
        source.FriendRejected -= OnSourceFriendRejected;
        source.BubbleMessageReceived -= OnSourceBubbleMessageReceived;
        source.QuickReactionReceived -= OnSourceQuickReactionReceived;
        source.PetInteractionReceived -= OnSourcePetInteractionReceived;
        source.SnowballReceived -= OnSourceSnowballReceived;
        source.MusicFrameReceived -= OnSourceMusicFrameReceived;
        source.MusicFollowStateChanged -= OnSourceMusicFollowStateChanged;
        source.GameEventReceived -= OnSourceGameEventReceived;
        source.TransportError -= OnSourceTransportError;
    }

    private void OnSourcePeersChanged(object? sender, IReadOnlyList<PeerSnapshot> _)
        => PeersChanged?.Invoke(this, GetDiscoveredPeers());

    private void OnLanFriendRequestReceived(object? sender, FriendRequestReceivedEventArgs e)
    {
        _incomingRequestRoutes[e.RequestId] = RouteKind.Lan;
        if (_pairingRequestDedup.TryAccept(e.RequestId))
        {
            FriendRequestReceived?.Invoke(this, e);
        }
    }

    private void OnGcpFriendRequestReceived(object? sender, FriendRequestReceivedEventArgs e)
    {
        _incomingRequestRoutes[e.RequestId] = RouteKind.Gcp;
        if (_pairingRequestDedup.TryAccept(e.RequestId))
        {
            FriendRequestReceived?.Invoke(this, e);
        }
    }

    private void OnSourceFriendAccepted(object? sender, FriendAcceptedEventArgs e)
    {
        var reqKey = string.IsNullOrEmpty(e.RequestId) ? e.PairSecretBase64 : e.RequestId;
        if (_pairingOutcomeDedup.TryAccept($"acc|{e.AccepterPetId}|{reqKey}|{e.IsLocalAccepter}"))
        {
            FriendAccepted?.Invoke(this, e);
        }
    }

    private void OnSourceFriendRejected(object? sender, FriendRejectedEventArgs e)
    {
        var reqKey = string.IsNullOrEmpty(e.RequestId) ? (e.Reason ?? string.Empty) : e.RequestId;
        if (_pairingOutcomeDedup.TryAccept($"rej|{e.ResponderPetId}|{reqKey}"))
        {
            FriendRejected?.Invoke(this, e);
        }
    }

    private void OnSourceBubbleMessageReceived(object? sender, BubbleMessageReceivedEventArgs e)
    {
        if (_messageDedup.TryAccept($"msg|{e.SenderPetId}|{e.MessageId}"))
        {
            BubbleMessageReceived?.Invoke(this, e);
        }
    }

    private void OnSourceQuickReactionReceived(object? sender, QuickReactionReceivedEventArgs e)
        => QuickReactionReceived?.Invoke(this, e);

    private void OnSourceSnowballReceived(object? sender, SnowballReceivedEventArgs e) => SnowballReceived?.Invoke(this, e);

    [System.Reflection.Obfuscation(Exclude = false)]
    private void OnSourcePetInteractionReceived(object? sender, PetInteractionReceivedEventArgs e)
    {
        if (e.InteractionType == PetInteractionPayload.PetType && TryAcceptPetInteraction(_petReceivedAt, e.SenderPetId))
            PetInteractionReceived?.Invoke(this, e);
    }

    private void OnSourceMusicFrameReceived(object? sender, MusicFrameReceivedEventArgs e)
        => MusicFrameReceived?.Invoke(this, e);

    private void OnSourceMusicFollowStateChanged(object? sender, MusicFollowStateChangedEventArgs e)
        => MusicFollowStateChanged?.Invoke(this, e);

    private void OnSourceGameEventReceived(object? sender, GameEventReceivedEventArgs e)
    {
        // 초대 단계는 응답 유실 시 같은 세션의 패킷을 다시 받아 응답해야 한다.
        if (e.Payload.InviteExpiresAtUtc is not null)
        {
            GameEventReceived?.Invoke(this, e);
            return;
        }

        var key = $"game|{e.SenderPetId}|{e.Payload.GameType}|{e.Payload.SessionId}|{e.Payload.Action}|{e.Payload.WagerCoins}|{e.Payload.Data}";
        if (_gameEventDedup.TryAccept(key))
        {
            GameEventReceived?.Invoke(this, e);
        }
    }

    private void OnSourceTransportError(object? sender, TransportErrorEventArgs e)
        => TransportError?.Invoke(this, e);
}
