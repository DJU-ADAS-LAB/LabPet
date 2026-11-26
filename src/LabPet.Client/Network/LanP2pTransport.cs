using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.Network;

/// <summary>
/// 외부 서버 없이 로컬 네트워크(LAN)만으로 동작하는 서버리스 P2P 소셜 전송 계층.
/// - UDP 45678: 물리 LAN 서브넷 브로드캐스트 자동 탐색 (3초 송신 / 10초 오프라인 판정)
/// - TCP 45680: 친구 신청/수락, HMAC-SHA256 인증 메시지/반응/쓰다듬기, 노래 따라하기 구독 제어
/// - UDP 45679: 구독 중인 인증된 친구 IP 목록에만 30Hz 경량 오디오 프레임 유니캐스트
/// </summary>
public sealed partial class LanP2pTransport : ISocialTransport
{
    internal const int MaxConcurrentControlConnections = 16;
    internal static readonly TimeSpan InboundControlTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan OutboundControlTimeout = TimeSpan.FromSeconds(3);
    private sealed record MusicSubscriber(string PetId, IPEndPoint UdpEndPoint, string PairSecretBase64, string SubscriptionId);

    internal sealed class MusicSequenceWindow(string subscriptionId)
    {
        private uint? _sequence;
        public string SubscriptionId { get; } = subscriptionId;

        public bool Accept(MusicFramePacket packet)
        {
            if (packet.SubscriptionId != SubscriptionId ||
                (_sequence is { } previous && unchecked((int)(packet.Sequence - previous)) <= 0))
            {
                return false;
            }
            _sequence = packet.Sequence;
            return true;
        }
    }

    private readonly int _udpDiscoveryPort;
    private readonly int _udpMusicPort;
    private readonly int _tcpControlPort;
    private readonly LanDosProtectionOptions _dosOptions;

    private readonly ConcurrentDictionary<string, PeerSnapshot> _peers = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, FriendEntry> _friendsByPetId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MusicSubscriber> _musicSubscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MusicSequenceWindow> _musicReceiveWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ControlReplayWindow> _replayWindows = new(StringComparer.OrdinalIgnoreCase);

    private readonly BoundedSlidingRateLimiter _discoveryLimiter;
    private readonly BoundedSlidingRateLimiter _musicLimiter;
    private readonly BoundedSlidingRateLimiter _tcpRequestLimiter;
    private readonly BoundedSlidingRateLimiter _featureLimiter;
    private readonly SemaphoreSlim _totalTcpSlots;
    private readonly SemaphoreSlim _unauthenticatedTcpSlots;
    private readonly ConcurrentDictionary<string, int> _activeTcpByIp = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _peersChangedLock = new();
    private Timer? _peersChangedTimer;
    private long _lastPeersChangedSentMs;
    private bool _peersChangedPending;

    private long _droppedOversizedDiscoveryPackets;
    private long _droppedRateLimitedDiscoveryPackets;
    private long _droppedOversizedMusicPackets;
    private long _droppedRateLimitedMusicPackets;
    private long _droppedTcpConnectionsByLimit;

    internal long DroppedOversizedDiscoveryPackets => Interlocked.Read(ref _droppedOversizedDiscoveryPackets);
    internal long DroppedRateLimitedDiscoveryPackets => Interlocked.Read(ref _droppedRateLimitedDiscoveryPackets);
    internal long DroppedOversizedMusicPackets => Interlocked.Read(ref _droppedOversizedMusicPackets);
    internal long DroppedRateLimitedMusicPackets => Interlocked.Read(ref _droppedRateLimitedMusicPackets);
    internal long DroppedTcpConnectionsByLimit => Interlocked.Read(ref _droppedTcpConnectionsByLimit);
    internal int ActiveTcpConnectionCount => _activeTcpByIp.Values.Sum();

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private AsyncOperationLifetime _background = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _discoveryUdp;
    private UdpClient? _musicUdp;
    private TcpListener? _tcpListener;
    private Task[] _transportTasks = [];

    private string _localPetId = string.Empty;
    private LocalPresenceState _localPresence = new(string.Empty, "모찌", new PetAppearance(), 1, false, "0.1.0");
    private int _boundTcpPort;

    public string TransportName => "LAN-P2P";
    public bool IsRunning { get; private set; }

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

    public LanP2pTransport(
        int udpDiscoveryPort = ProtocolConstants.UdpDiscoveryPort,
        int udpMusicPort = ProtocolConstants.UdpMusicFramePort,
        int tcpControlPort = ProtocolConstants.TcpControlPort,
        LanDosProtectionOptions? dosOptions = null)
    {
        _udpDiscoveryPort = udpDiscoveryPort;
        _udpMusicPort = udpMusicPort;
        _tcpControlPort = tcpControlPort;
        _dosOptions = dosOptions ?? new LanDosProtectionOptions();

        var maxBuckets = Math.Clamp(_dosOptions.MaxTrackedRateBuckets, 16, 16_384);
        _discoveryLimiter = new BoundedSlidingRateLimiter(maxBuckets);
        _musicLimiter = new BoundedSlidingRateLimiter(maxBuckets);
        _tcpRequestLimiter = new BoundedSlidingRateLimiter(maxBuckets);
        _featureLimiter = new BoundedSlidingRateLimiter(maxBuckets);

        var maxTotalTcp = Math.Clamp(_dosOptions.MaxTotalControlConnections, 2, 128);
        var reservedFriend = Math.Clamp(_dosOptions.ReservedFriendControlConnections, 1, maxTotalTcp - 1);
        _totalTcpSlots = new SemaphoreSlim(maxTotalTcp, maxTotalTcp);
        _unauthenticatedTcpSlots = new SemaphoreSlim(maxTotalTcp - reservedFriend, maxTotalTcp - reservedFriend);
    }

    public int BoundTcpPort => _boundTcpPort;

    public async Task StartAsync(PetProfile localProfile, IReadOnlyList<FriendEntry> friends, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(localProfile, friends, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private Task StartCoreAsync(PetProfile localProfile, IReadOnlyList<FriendEntry> friends, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            _localPetId = localProfile.PetId;
            _localPresence = new LocalPresenceState(
                localProfile.OwnerName,
                localProfile.PetName,
                localProfile.Appearance.Clone(),
                localProfile.SoloProgress.Level,
                IsMusicActive: false,
                AppVersion: typeof(LanP2pTransport).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

            UpdateKnownFriends(friends);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 방화벽의 고정 포트를 사용한다. 테스트에서만 명시적으로 포트 0을 지정한다.
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, _tcpControlPort) { ExclusiveAddressUse = true };
                _tcpListener.Start(MaxConcurrentControlConnections * 2);
                _boundTcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
                _discoveryUdp = CreateReusableUdpClient(_udpDiscoveryPort, enableBroadcast: true);
                _musicUdp = CreateReusableUdpClient(_udpMusicPort, enableBroadcast: false);
            }
            catch (SocketException ex)
            {
                _tcpListener?.Stop();
                _tcpListener = null;
                _discoveryUdp?.Dispose();
                _musicUdp?.Dispose();
                _discoveryUdp = null;
                _musicUdp = null;
                _boundTcpPort = 0;
                _cts.Dispose();
                _cts = null;
                TransportError?.Invoke(this, new TransportErrorEventArgs("Listen",
                    $"LAN 통신을 시작하지 못했습니다. TCP {_tcpControlPort} 및 UDP {_udpDiscoveryPort}, {_udpMusicPort} 포트를 확인해 주세요.", ex));
                return Task.CompletedTask;
            }

            _background = new AsyncOperationLifetime();
            IsRunning = true;
            var ct = _cts.Token;
            _transportTasks =
            [
                DiscoveryBroadcastLoopAsync(ct), DiscoveryReceiveLoopAsync(ct),
                MusicFrameReceiveLoopAsync(ct), TcpAcceptLoopAsync(_tcpListener, ct)
            ];
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        Task[] tasks;
        Task background;
        lock (_stateLock)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            background = _background.DrainAsync();
            try { _cts?.Cancel(); } catch { }
            try { _tcpListener?.Stop(); } catch { }
            try { _discoveryUdp?.Dispose(); } catch { }
            try { _musicUdp?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }

            _tcpListener = null;
            _discoveryUdp = null;
            _musicUdp = null;
            _cts = null;
            _boundTcpPort = 0;
            tasks = _transportTasks;
            _transportTasks = [];
            _musicSubscribers.Clear();
            _musicReceiveWindows.Clear();
            _replayWindows.Clear();
            _discoveryLimiter.Clear();
            _musicLimiter.Clear();
            _tcpRequestLimiter.Clear();
            _featureLimiter.Clear();
            lock (_peersChangedLock)
            {
                _peersChangedPending = false;
                _peersChangedTimer?.Dispose();
                _peersChangedTimer = null;
            }
            lock (_pairingLock)
            {
                foreach (var pair in _outgoingPairings.Values) pair.Dispose();
                foreach (var pair in _incomingPairings.Values) pair.Dispose();
                _outgoingPairings.Clear();
                _incomingPairings.Clear();
            }
        }

        await Task.WhenAll(tasks.Append(background)).ConfigureAwait(false);
    }

    public Task UpdateLocalPresenceAsync(LocalPresenceState presence, CancellationToken cancellationToken = default)
    {
        var previous = _localPresence;
        _localPresence = presence;
        if (presence.IsFocusing)
        {
            _musicSubscribers.Clear();
        }

        if (IsRunning)
        {
            var publicChanged = previous.Level != presence.Level
                || !string.Equals(previous.OwnerName, presence.OwnerName, StringComparison.Ordinal)
                || !string.Equals(previous.PetName, presence.PetName, StringComparison.Ordinal);
            var privateChanged = previous.IsSleeping != presence.IsSleeping
                || previous.IsMusicActive != presence.IsMusicActive
                || previous.IsFocusing != presence.IsFocusing;

            if (publicChanged || privateChanged)
            {
                var operation = BeginBackgroundWork(out var ct);
                if (operation is not null)
                {
                    if (publicChanged)
                    {
                        _ = BroadcastPresenceAsync(operation, ct);
                    }
                    else
                    {
                        operation.Dispose();
                    }

                    PushPresenceToOnlineFriends(ct);
                }
            }
        }

        return Task.CompletedTask;
    }

    private IDisposable? BeginBackgroundWork(out CancellationToken token)
    {
        lock (_stateLock)
        {
            token = _cts?.Token ?? new CancellationToken(canceled: true);
            return IsRunning ? _background.TryBegin() : null;
        }
    }

    private async Task BroadcastPresenceAsync(IDisposable operation, CancellationToken ct)
    {
        using (operation)
        {
            try
            {
                await SendHelloBroadcastOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("LAN 즉시 프레즌스 송신 실패: {0}", ex.Message);
            }
        }
    }

    public void UpdateKnownFriends(IReadOnlyList<FriendEntry> friends)
    {
        var friendsByPetId = new Dictionary<string, FriendEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in friends)
        {
            if (!string.IsNullOrWhiteSpace(f.PetId))
            {
                friendsByPetId[f.PetId] = f;
            }
        }
        _friendsByPetId = friendsByPetId;
        foreach (var petId in _replayWindows.Keys)
        {
            if (!friendsByPetId.ContainsKey(petId))
            {
                _replayWindows.TryRemove(petId, out _);
                _musicSubscribers.TryRemove(petId, out _);
                _musicReceiveWindows.TryRemove(petId, out _);
            }
        }

        // 탐색 후보를 친구의 인증된 프레즌스로 승격하지 않는다.
        var changed = false;
        foreach (var peer in _peers.Values)
        {
            var isFriend = friendsByPetId.ContainsKey(peer.PetId);
            if (peer.IsFriend != isFriend)
            {
                changed |= _peers.TryRemove(new KeyValuePair<string, PeerSnapshot>(peer.PetId, peer));
            }
        }

        if (changed)
        {
            RaisePeersChanged();
        }
    }

    public Task<bool> RemoveFriendAsync(string friendPetId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(friendPetId))
        {
            return Task.FromResult(false);
        }

        var cleanId = friendPetId.Trim();
        var next = new Dictionary<string, FriendEntry>(_friendsByPetId, StringComparer.OrdinalIgnoreCase);
        var removed = next.Remove(cleanId);
        _friendsByPetId = next;

        _replayWindows.TryRemove(cleanId, out _);
        _musicSubscribers.TryRemove(cleanId, out _);
        _musicReceiveWindows.TryRemove(cleanId, out _);
        _presenceInFlight.TryRemove(cleanId, out _);
        _presenceResendPending.TryRemove(cleanId, out _);
        ClearPairingsForPeer(cleanId);

        var peerRemoved = _peers.TryRemove(cleanId, out _);
        if (removed || peerRemoved)
        {
            RaisePeersChanged();
        }

        return Task.FromResult(removed || peerRemoved);
    }

    public void SetPendingRemovedFriends(IReadOnlyCollection<string> removedPetIds) { }

    public IReadOnlyList<PeerSnapshot> GetDiscoveredPeers()
        => _peers.Values.Where(peer => peer.IsFriend == _friendsByPetId.ContainsKey(peer.PetId))
            .OrderByDescending(p => p.IsFriend).ThenBy(p => p.PetName, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<bool> SendBubbleMessageAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default)
        => (await SendBubbleMessageDetailedAsync(friend, message, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<TransportSendResult> SendBubbleMessageDetailedAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default)
    {
        if (_localPresence.IsFocusing)
        {
            return TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked);
        }

        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return TransportSendResult.Fail();
        }

        if (trimmed.Length > ProtocolConstants.MaxBubbleMessageLength)
        {
            trimmed = trimmed[..ProtocolConstants.MaxBubbleMessageLength];
        }

        return await SendFriendControlPacketDetailedAsync(
            friend,
            PacketType.BubbleMessage,
            new BubbleMessagePayload { Text = trimmed },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default)
        => (await SendQuickReactionDetailedAsync(friend, reactionId, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendQuickReactionDetailedAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default)
    {
        if (_localPresence.IsFocusing)
        {
            return Task.FromResult(TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked));
        }

        return SendFriendControlPacketDetailedAsync(
            friend,
            PacketType.QuickReaction,
            new QuickReactionPayload { ReactionId = reactionId },
            cancellationToken);
    }

    public SnowballPeerStatus GetSnowballPeerStatus(string petId)
    {
        var peer = GetDiscoveredPeers().FirstOrDefault(p =>
            string.Equals(p.PetId, petId, StringComparison.OrdinalIgnoreCase));
        if (peer is null) return SnowballPeerStatus.Offline;
        return !peer.RequiresUpdate && PetInteractionPayload.SupportsSnowball(peer.AppVersion)
            ? SnowballPeerStatus.Available : SnowballPeerStatus.Unsupported;
    }

    public async Task<bool> SendSnowballAsync(FriendEntry friend, Guid eventId, CancellationToken cancellationToken = default)
        => (await SendSnowballDetailedAsync(friend, eventId, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendSnowballDetailedAsync(FriendEntry friend, Guid eventId, CancellationToken cancellationToken = default)
    {
        if (_localPresence.IsFocusing)
        {
            return Task.FromResult(TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked));
        }

        if (eventId == Guid.Empty || GetSnowballPeerStatus(friend.PetId) != SnowballPeerStatus.Available)
            return Task.FromResult(TransportSendResult.Fail());
        return SendFriendControlPacketDetailedAsync(friend, PacketType.PetInteraction,
            new PetInteractionPayload { InteractionType = PetInteractionPayload.SnowballType, EventId = eventId.ToString("N") },
            cancellationToken);
    }

    public async Task<bool> SendPetInteractionAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default)
        => (await SendPetInteractionDetailedAsync(friend, interactionType, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendPetInteractionDetailedAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default)
    {
        if (_localPresence.IsFocusing)
        {
            return Task.FromResult(TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked));
        }

        return SendFriendControlPacketDetailedAsync(
            friend,
            PacketType.PetInteraction,
            new PetInteractionPayload { InteractionType = interactionType },
            cancellationToken);
    }

    public async Task<bool> SetMusicFollowAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default)
        => (await SetMusicFollowDetailedAsync(hostFriend, enable, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<TransportSendResult> SetMusicFollowDetailedAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default)
    {
        if (!enable)
        {
            _musicReceiveWindows.TryRemove(hostFriend.PetId, out var previous);
            return await SendFriendControlPacketDetailedAsync(hostFriend, PacketType.MusicFollowOff,
                new MusicFollowPayload { SubscriptionId = previous?.SubscriptionId ?? string.Empty },
                cancellationToken).ConfigureAwait(false);
        }

        if (_localPresence.IsFocusing)
        {
            return TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked);
        }

        var session = new MusicSequenceWindow(Guid.NewGuid().ToString("N"));
        _musicReceiveWindows[hostFriend.PetId] = session;
        var localPort = (_musicUdp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? _udpMusicPort;
        var result = await SendFriendControlPacketDetailedAsync(hostFriend, PacketType.MusicFollowOn,
            new MusicFollowPayload { ListenerUdpPort = localPort, SubscriptionId = session.SubscriptionId },
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _musicReceiveWindows.TryRemove(new KeyValuePair<string, MusicSequenceWindow>(hostFriend.PetId, session));
        }
        return result;
    }

    public async Task<bool> SendGameEventAsync(FriendEntry friend, GameEventPayload payload, CancellationToken cancellationToken = default)
        => (await SendGameEventDetailedAsync(friend, payload, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendGameEventDetailedAsync(FriendEntry friend, GameEventPayload payload, CancellationToken cancellationToken = default)
    {
        if (_localPresence.IsFocusing && payload?.Action is not ("cancel" or "decline"))
        {
            return Task.FromResult(TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked));
        }

        return SendFriendControlPacketDetailedAsync(
            friend,
            PacketType.GameEvent,
            payload!,
            cancellationToken);
    }

    private async Task<TransportSendResult> SendFriendControlPacketDetailedAsync<TPayload>(
        FriendEntry friend,
        PacketType packetType,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        if (!TryResolveFriendEndpoint(friend, out var primaryIp, out var primaryPort))
        {
            return TransportSendResult.Fail(FriendOperationErrorCodes.RecipientOffline);
        }

        var messageId = Guid.NewGuid().ToString("N");
        var outcome = await SendControlPacketWithOutcomeAsync(
            primaryIp,
            primaryPort,
            packetType,
            payload,
            friend.PairSecretBase64,
            cancellationToken, friend.PetId, messageId: messageId).ConfigureAwait(false);

        if (outcome.VerifiedAck is { Accepted: true })
        {
            return TransportSendResult.Ok;
        }

        // 상대가 서명된 ACK로 명시적으로 거절한 경우(예: 뽀모도로 집중 중) 보조 IP로 재시도하지 않는다.
        if (outcome.VerifiedAck is { Accepted: false } rejectedAck)
        {
            return TransportSendResult.Fail(rejectedAck.ErrorCode ?? outcome.ErrorCode);
        }

        // 다중 어댑터 환경에서 브로드캐스트 IP와 최근 TCP 연결 IP가 다를 때 보조 IP로 1회 폴백 재시도
        string? fallbackIp = null;
        var fallbackPort = primaryPort;
        if (_friendsByPetId.TryGetValue(friend.PetId, out var known))
        {
            lock (known)
            {
                fallbackIp = known.LastKnownIp;
                if (known.LastKnownTcpPort > 0)
                {
                    fallbackPort = known.LastKnownTcpPort;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(fallbackIp))
        {
            fallbackIp = friend.LastKnownIp;
        }

        if (!string.IsNullOrWhiteSpace(fallbackIp) &&
            !string.Equals(fallbackIp, primaryIp, StringComparison.OrdinalIgnoreCase))
        {
            var retryOutcome = await SendControlPacketWithOutcomeAsync(
                fallbackIp,
                fallbackPort,
                packetType,
                payload,
                friend.PairSecretBase64,
                cancellationToken, friend.PetId, messageId: messageId).ConfigureAwait(false);
            return retryOutcome.VerifiedAck is { Accepted: true }
                ? TransportSendResult.Ok
                : TransportSendResult.Fail(retryOutcome.VerifiedAck?.ErrorCode ?? retryOutcome.ErrorCode);
        }

        return TransportSendResult.Fail(outcome.ErrorCode);
    }

    public async ValueTask PublishMusicFrameAsync(MusicFrameData frame, CancellationToken cancellationToken = default)
    {
        var udp = _musicUdp;
        if (!IsRunning || udp is null || _musicSubscribers.IsEmpty || _localPresence.IsFocusing)
        {
            return;
        }

        foreach (var sub in _musicSubscribers.Values)
        {
            try
            {
                var packet = new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = _localPetId,
                    Sequence = frame.Sequence,
                    SubscriptionId = sub.SubscriptionId,
                    Bass = frame.Bass,
                    Mid = frame.Mid,
                    Treble = frame.Treble,
                    IsBeat = frame.IsBeat
                };

                var canonical = PacketSigner.BuildMusicFrameCanonicalString(packet);
                packet.SignatureBase64 = PacketSigner.ComputeSignature(canonical, sub.PairSecretBase64);

                var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, ProtocolJson.PacketOptions);
                await udp.SendAsync(bytes, sub.UdpEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 개별 구독자 UDP 송신 오류는 다른 구독자에 영향을 주지 않음
            }
        }
    }

    private async Task DiscoveryBroadcastLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(ProtocolConstants.DiscoveryIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendHelloBroadcastOnceAsync(ct).ConfigureAwait(false);
                PruneStalePeers();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                TransportError?.Invoke(this, new TransportErrorEventArgs("DiscoveryBroadcast", ex.Message, ex));
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendHelloBroadcastOnceAsync(CancellationToken ct)
    {
        var udp = _discoveryUdp;
        if (udp is null)
        {
            return;
        }

        var presence = _localPresence;
        var hello = new DiscoveryHelloPacket
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = presence.AppVersion,
            PetId = _localPetId,
            OwnerName = presence.OwnerName,
            PetName = presence.PetName,
            Appearance = presence.Appearance.Clone(),
            Level = presence.Level,
            IsMusicActive = false,
            IsSleeping = false,
            IsFocusing = false,
            TcpPort = _boundTcpPort,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(hello, ProtocolJson.PacketOptions);
        var broadcastTargets = LanNetworkHelper.GetPhysicalBroadcastAddresses();

        foreach (var broadcastIp in broadcastTargets)
        {
            try
            {
                await udp.SendAsync(payloadBytes, new IPEndPoint(broadcastIp, _udpDiscoveryPort), ct).ConfigureAwait(false);
            }
            catch
            {
                // 특정 인터페이스 송신 실패 시 나머지 인터페이스 계속 진행
            }
        }
    }

    private async Task DiscoveryReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _discoveryUdp;
        if (udp is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (result.Buffer.Length <= 0 || result.Buffer.Length > _dosOptions.MaxDiscoveryUdpBytes)
                {
                    Interlocked.Increment(ref _droppedOversizedDiscoveryPackets);
                    continue;
                }

                var address = result.RemoteEndPoint.Address;
                var remoteIp = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
                if (!_discoveryLimiter.Allow("ip|" + remoteIp, 1000L, _dosOptions.DiscoveryPerIpPerSecond)
                    || !_discoveryLimiter.Allow("global", 1000L, _dosOptions.DiscoveryGlobalPerSecond))
                {
                    Interlocked.Increment(ref _droppedRateLimitedDiscoveryPackets);
                    continue;
                }

                var hello = JsonSerializer.Deserialize<DiscoveryHelloPacket>(result.Buffer, ProtocolJson.PacketOptions);
                if (hello is not null)
                {
                    HandleDiscoveryHello(hello, remoteIp, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // 잘못된 UDP 패킷은 무시
            }
        }
    }

    private async Task MusicFrameReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _musicUdp;
        if (udp is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (result.Buffer.Length <= 0 || result.Buffer.Length > _dosOptions.MaxMusicUdpBytes)
                {
                    Interlocked.Increment(ref _droppedOversizedMusicPackets);
                    continue;
                }

                var address = result.RemoteEndPoint.Address;
                var remoteIp = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
                if (!_musicLimiter.Allow("ip|" + remoteIp, 1000L, _dosOptions.MusicPerIpPerSecond)
                    || !_musicLimiter.Allow("global", 1000L, _dosOptions.MusicGlobalPerSecond))
                {
                    Interlocked.Increment(ref _droppedRateLimitedMusicPackets);
                    continue;
                }

                var packet = JsonSerializer.Deserialize<MusicFramePacket>(result.Buffer, ProtocolJson.PacketOptions);
                if (packet is null
                    || string.IsNullOrWhiteSpace(packet.SenderPetId)
                    || packet.SenderPetId.Length > 64
                    || packet.SubscriptionId.Length > 64
                    || string.IsNullOrWhiteSpace(packet.SignatureBase64)
                    || packet.SignatureBase64.Length > 88
                    || !float.IsFinite(packet.Bass)
                    || !float.IsFinite(packet.Mid)
                    || !float.IsFinite(packet.Treble))
                {
                    continue;
                }

                if (ProtocolConstants.EvaluateCompatibility(packet.ProtocolVersion) != ProtocolCompatibility.Compatible)
                {
                    continue;
                }

                // 인증된 친구 패킷만 발신자별 빈도·HMAC 검증 후 허용 (30Hz 정상 동기화 유지)
                if (!_friendsByPetId.TryGetValue(packet.SenderPetId, out var friend))
                {
                    continue;
                }

                if (!_musicLimiter.Allow("friend|" + packet.SenderPetId, 1000L, _dosOptions.MusicPerFriendPerSecond))
                {
                    Interlocked.Increment(ref _droppedRateLimitedMusicPackets);
                    continue;
                }

                var canonical = PacketSigner.BuildMusicFrameCanonicalString(packet);
                if (!PacketSigner.VerifySignature(canonical, packet.SignatureBase64, friend.PairSecretBase64))
                {
                    continue;
                }
                if (!_musicReceiveWindows.TryGetValue(packet.SenderPetId, out var session) || !session.Accept(packet))
                {
                    continue;
                }

                MusicFrameReceived?.Invoke(this, new MusicFrameReceivedEventArgs(
                    packet.SenderPetId,
                    new MusicFrameData(packet.Bass, packet.Mid, packet.Treble, packet.IsBeat, packet.Sequence)));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // 잘못된 오디오 프레임 무시
            }
        }
    }

    private bool TryResolveFriendEndpoint(FriendEntry friend, out string ip, out int port)
    {
        // 최신 탐색 캐시가 있으면 우선 사용 (DHCP IP 변경 자동 반영)
        if (_peers.TryGetValue(friend.PetId, out var livePeer) && livePeer.IsFriend && !livePeer.RequiresUpdate
            && !string.IsNullOrWhiteSpace(livePeer.IpAddress))
        {
            ip = livePeer.IpAddress;
            port = livePeer.TcpPort;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(friend.LastKnownIp))
        {
            ip = friend.LastKnownIp;
            port = friend.LastKnownTcpPort > 0 ? friend.LastKnownTcpPort : _tcpControlPort;
            return true;
        }

        ip = string.Empty;
        port = 0;
        return false;
    }

    internal int PruneStalePeers()
    {
        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
        }

        _discoveryLimiter.Prune(60_000L);
        _musicLimiter.Prune(10_000L);
        _tcpRequestLimiter.Prune(10_000L);
        _featureLimiter.Prune(60_000L);

        var now = DateTimeOffset.UtcNow;
        var friendCutoff = now - TimeSpan.FromSeconds(ProtocolConstants.PeerOfflineTimeoutSeconds);
        var unauthTtl = Math.Clamp(_dosOptions.UnauthenticatedPeerTtlSeconds, 1, ProtocolConstants.PeerOfflineTimeoutSeconds);
        var unauthCutoff = now - TimeSpan.FromSeconds(unauthTtl);
        var removedCount = 0;

        foreach (var kv in _peers)
        {
            var cutoff = kv.Value.IsFriend ? friendCutoff : unauthCutoff;
            if (kv.Value.LastSeenUtc < cutoff)
            {
                if (_peers.TryRemove(kv.Key, out _))
                {
                    _musicSubscribers.TryRemove(kv.Key, out _);
                    removedCount++;
                }
            }
        }

        if (removedCount > 0)
        {
            RaisePeersChanged();
        }

        return removedCount;
    }

    private void RaisePeersChanged()
    {
        var minIntervalMs = _dosOptions.PeersChangedMinIntervalMs;
        if (minIntervalMs <= 0)
        {
            PeersChanged?.Invoke(this, GetDiscoveredPeers());
            return;
        }

        var raiseNow = false;
        lock (_peersChangedLock)
        {
            var nowMs = Environment.TickCount64;
            var elapsed = nowMs - _lastPeersChangedSentMs;
            if (elapsed >= minIntervalMs || _lastPeersChangedSentMs == 0)
            {
                _lastPeersChangedSentMs = nowMs;
                _peersChangedPending = false;
                _peersChangedTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                raiseNow = true;
            }
            else if (!_peersChangedPending)
            {
                _peersChangedPending = true;
                var delayMs = (int)Math.Clamp(minIntervalMs - elapsed, 1, minIntervalMs);
                _peersChangedTimer ??= new Timer(OnPeersChangedTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
                _peersChangedTimer.Change(delayMs, Timeout.Infinite);
            }
        }

        if (raiseNow)
        {
            PeersChanged?.Invoke(this, GetDiscoveredPeers());
        }
    }

    private void OnPeersChangedTimerCallback(object? _)
    {
        lock (_peersChangedLock)
        {
            if (!_peersChangedPending || !IsRunning)
            {
                _peersChangedPending = false;
                return;
            }

            _peersChangedPending = false;
            _lastPeersChangedSentMs = Environment.TickCount64;
        }

        PeersChanged?.Invoke(this, GetDiscoveredPeers());
    }

    private static AckPayload RejectAck(string messageId, string errorCode) => new()
    {
        AckMessageId = messageId,
        Accepted = false,
        ErrorCode = errorCode
    };

    private static UdpClient CreateReusableUdpClient(int port, bool enableBroadcast)
    {
        var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.ExclusiveAddressUse = false;
        udp.EnableBroadcast = enableBroadcast;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return udp;
    }


}
