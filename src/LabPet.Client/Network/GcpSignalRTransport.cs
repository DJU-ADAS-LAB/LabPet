using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabPet.Client.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LabPet.Client.Network;

/// <summary>
/// GCP SignalR 서버(LabPet.Server)와 통신하는 원격 소셜 전송 계층.
/// - 기기 ECDSA P-256 챌린지-응답 인증 및 자동 재연결
/// - 기존 PacketCrypto(AES-256-GCM) / PacketSigner(HMAC-SHA256) / P-256 ECDH 키 합의를 유지해 E2EE 통신 수행
/// - 30Hz 음악 프레임은 원격 구독자가 있을 때만 활성화하고 ~15Hz 이하로 스로틀링 전송
/// </summary>
public sealed class GcpSignalRTransport : ISocialTransport
{
    private sealed class BackoffRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
            => Delays[Math.Min((int)retryContext.PreviousRetryCount, Delays.Length - 1)];
    }

    private sealed class OutgoingPairing(string peerId) : IDisposable
    {
        public string PeerId { get; } = peerId;
        public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
        public string RequestFingerprint { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddMinutes(3);
        public string? ConfirmedPublicKey { get; private set; }
        public string? ConfirmationKey { get; private set; }

        public void Complete(string publicKey, string confirmationKey)
        {
            ConfirmedPublicKey = publicKey;
            ConfirmationKey = confirmationKey;
            Key.Dispose();
        }

        public void Dispose() => Key.Dispose();
    }

    private sealed class IncomingPairing(FriendRequestReceivedEventArgs request, string publicKey, string fingerprint) : IDisposable
    {
        public FriendRequestReceivedEventArgs Request { get; } = request;
        public string PublicKey { get; } = publicKey;
        public string RequestFingerprint { get; } = fingerprint;
        public ECDiffieHellman? ReplyKey { get; set; }
        public bool IsResponding { get; set; }
        public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddMinutes(3);
        public void Dispose() => ReplyKey?.Dispose();
    }

    private const int MinRemoteMusicPublishIntervalMs = 66;
    private static readonly TimeSpan InitialConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BackgroundRetryInterval = TimeSpan.FromSeconds(20);

    private readonly ServerConnectionOptions _options;
    private readonly DeviceIdentityStore _identityStore;
    private readonly Func<HttpMessageHandler>? _httpHandlerFactory;

    private readonly ConcurrentDictionary<string, PeerSnapshot> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _musicSubscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LanP2pTransport.MusicSequenceWindow> _musicReceiveWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ControlReplayWindow> _replayWindows = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, FriendEntry> _friendsByPetId = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _stateLock = new();
    private readonly object _pairingLock = new();
    private readonly Dictionary<string, OutgoingPairing> _outgoingPairings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingPairing> _incomingPairings = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private HubConnection? _hub;
    private Task? _reconnectLoopTask;

    private string _localPetId = string.Empty;
    private LocalPresenceState _localPresence = new(string.Empty, "모찌", new PetAppearance(), 1, false, "0.1.0");
    private ECDsa? _deviceKey;
    private string _devicePublicKeyBase64 = string.Empty;
    private string? _cachedAccessToken;
    private DateTimeOffset _tokenExpiresAtUtc;
    private Uri? _baseUri;

    private long _lastRemoteMusicSentTickMs;
    private int _musicPublishInFlight;

    public string TransportName => "GCP-SignalR";
    public bool IsRunning { get; private set; }
    public bool IsRemoteConnected => _hub?.State == HubConnectionState.Connected;
    public string ConnectionSummary => IsRemoteConnected ? "GCP 연결됨" : (_options.IsConfigured ? "GCP 대기 중" : "GCP 미설정");

    public event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged;
    public event EventHandler<FriendRequestReceivedEventArgs>? FriendRequestReceived;
    public event EventHandler<FriendAcceptedEventArgs>? FriendAccepted;
    public event EventHandler<FriendRejectedEventArgs>? FriendRejected;
    public event EventHandler<BubbleMessageReceivedEventArgs>? BubbleMessageReceived;
    public event EventHandler<QuickReactionReceivedEventArgs>? QuickReactionReceived;
    public event EventHandler<PetInteractionReceivedEventArgs>? PetInteractionReceived;
    public event EventHandler<MusicFrameReceivedEventArgs>? MusicFrameReceived;
    public event EventHandler<MusicFollowStateChangedEventArgs>? MusicFollowStateChanged;
    public event EventHandler<GameEventReceivedEventArgs>? GameEventReceived;
    public event EventHandler<TransportErrorEventArgs>? TransportError;

    public GcpSignalRTransport(
        ServerConnectionOptions options,
        DeviceIdentityStore identityStore,
        Func<HttpMessageHandler>? httpHandlerFactory = null)
    {
        _options = options;
        _identityStore = identityStore;
        _httpHandlerFactory = httpHandlerFactory;
    }

    public async Task StartAsync(
        PetProfile localProfile,
        IReadOnlyList<FriendEntry> friends,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured || !_options.TryGetBaseUri(out var baseUri) || baseUri is null)
        {
            return;
        }

        HubConnection hub;
        CancellationToken ct;
        lock (_stateLock)
        {
            if (IsRunning)
            {
                return;
            }

            _baseUri = baseUri;
            _localPetId = localProfile.PetId;
            _localPresence = new LocalPresenceState(
                localProfile.OwnerName,
                localProfile.PetName,
                localProfile.Appearance.Clone(),
                localProfile.SoloProgress.Level,
                IsMusicActive: false,
                AppVersion: typeof(GcpSignalRTransport).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                IsSleeping: false,
                FriendCode: localProfile.FriendCode);

            UpdateKnownFriends(friends);
            (_devicePublicKeyBase64, _deviceKey) = _identityStore.GetOrCreateKeyPair(_localPetId);
            _cachedAccessToken = null;
            _tokenExpiresAtUtc = DateTimeOffset.MinValue;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ct = _cts.Token;
            hub = BuildHubConnection(baseUri);
            _hub = hub;
            IsRunning = true;
        }

        // 초기 연결 시도는 짧은 타임아웃으로 수행하고, 서버 미응답 시 백그라운드 루프가 자동 재연결한다.
        await TryConnectOnceAsync(hub, ct).ConfigureAwait(false);
        _reconnectLoopTask = EnsureConnectedBackgroundLoopAsync(hub, ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        HubConnection? hub;
        Task? loopTask;
        CancellationTokenSource? cts;
        ECDsa? key;

        lock (_stateLock)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            cts = _cts;
            _cts = null;
            hub = _hub;
            _hub = null;
            loopTask = _reconnectLoopTask;
            _reconnectLoopTask = null;
            key = _deviceKey;
            _deviceKey = null;
            _cachedAccessToken = null;
            _tokenExpiresAtUtc = DateTimeOffset.MinValue;
            _peers.Clear();
            _musicSubscribers.Clear();
            _musicReceiveWindows.Clear();
            _replayWindows.Clear();
            lock (_pairingLock)
            {
                foreach (var p in _outgoingPairings.Values) p.Dispose();
                foreach (var p in _incomingPairings.Values) p.Dispose();
                _outgoingPairings.Clear();
                _incomingPairings.Clear();
            }
        }

        try { cts?.Cancel(); } catch { }

        if (hub is not null)
        {
            try { await hub.StopAsync(cancellationToken).ConfigureAwait(false); } catch { }
            try { await hub.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        if (loopTask is not null)
        {
            try { await loopTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); } catch { }
        }

        try { cts?.Dispose(); } catch { }
        key?.Dispose();
    }

    public async Task UpdateLocalPresenceAsync(LocalPresenceState presence, CancellationToken cancellationToken = default)
    {
        _localPresence = presence;
        var hub = _hub;
        if (!IsRunning || hub is null || hub.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await hub.InvokeAsync(SocialHubConstants.UpdatePresenceMethod, BuildPresenceUpdate(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs("GcpUpdatePresence", ex.Message, ex));
        }
    }

    public void UpdateKnownFriends(IReadOnlyList<FriendEntry> friends)
    {
        var map = new Dictionary<string, FriendEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in friends)
        {
            if (!string.IsNullOrWhiteSpace(f.PetId))
            {
                map[f.PetId] = f;
            }
        }

        _friendsByPetId = map;
        foreach (var id in _replayWindows.Keys)
        {
            if (!map.ContainsKey(id))
            {
                _replayWindows.TryRemove(id, out _);
            }
        }

        var changed = false;
        foreach (var existing in _peers.Values)
        {
            var isFriend = map.ContainsKey(existing.PetId);
            if (existing.IsFriend != isFriend)
            {
                _peers[existing.PetId] = new PeerSnapshot
                {
                    PetId = existing.PetId,
                    FriendCode = isFriend ? existing.FriendCode : string.Empty,
                    OwnerName = existing.OwnerName,
                    PetName = existing.PetName,
                    Appearance = existing.Appearance.Clone(),
                    Level = existing.Level,
                    IsMusicActive = isFriend && existing.IsMusicActive,
                    IsSleeping = isFriend && existing.IsSleeping,
                    IsFriend = isFriend,
                    TransportKind = existing.TransportKind,
                    IpAddress = existing.IpAddress,
                    TcpPort = existing.TcpPort,
                    ProtocolVersion = existing.ProtocolVersion,
                    AppVersion = existing.AppVersion,
                    Compatibility = existing.Compatibility,
                    LastSeenUtc = existing.LastSeenUtc
                };
                if (!isFriend)
                {
                    _musicSubscribers.TryRemove(existing.PetId, out _);
                }

                changed = true;
            }
        }

        if (changed)
        {
            RaisePeersChanged();
        }

        var hub = _hub;
        if (IsRunning && hub is { State: HubConnectionState.Connected })
        {
            var ids = map.Keys.ToList();
            _ = SyncKnownFriendsWithServerAsync(hub, ids);
        }
    }

    public IReadOnlyList<PeerSnapshot> GetDiscoveredPeers()
        => _peers.Values
            .OrderByDescending(p => _friendsByPetId.ContainsKey(p.PetId))
            .ThenBy(p => p.PetName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<bool> SendFriendRequestAsync(PeerSnapshot target, CancellationToken cancellationToken = default)
    {
        var hub = _hub;
        if (!IsRunning || hub is not { State: HubConnectionState.Connected }
            || target.RequiresUpdate || string.IsNullOrWhiteSpace(target.PetId))
        {
            return false;
        }

        var requestId = Guid.NewGuid().ToString("N");
        FriendRequestPayload payload;
        var presence = _localPresence;

        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (_friendsByPetId.ContainsKey(target.PetId) || _outgoingPairings.Count >= 32
                || _outgoingPairings.Values.Any(p => SamePetId(p.PeerId, target.PetId)))
            {
                return false;
            }

            var pending = new OutgoingPairing(target.PetId);
            payload = new FriendRequestPayload
            {
                RequestId = requestId,
                PublicKey = pending.PublicKey,
                RequesterOwnerName = presence.OwnerName,
                RequesterName = presence.PetName,
                Appearance = presence.Appearance.Clone(),
                Level = presence.Level,
                TcpPort = ProtocolConstants.TcpControlPort
            };
            pending.RequestFingerprint = Fingerprint(JsonSerializer.Serialize(payload, ProtocolJson.PacketOptions));
            _outgoingPairings.Add(requestId, pending);
        }

        var ack = await SendControlRelayAsync(target.PetId, PacketType.FriendRequest, payload, null, cancellationToken)
            .ConfigureAwait(false);
        if (ack is { Accepted: true })
        {
            return true;
        }

        lock (_pairingLock)
        {
            if (_outgoingPairings.Remove(requestId, out var pending))
            {
                pending.Dispose();
            }
        }

        return false;
    }

    public async Task<bool> RespondFriendRequestAsync(string requestId, bool accept, CancellationToken cancellationToken = default)
    {
        IncomingPairing pending;
        string? publicKey = null;
        (string ConfirmationKey, string FriendKey)? keys = null;

        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (!_incomingPairings.TryGetValue(requestId, out var found) || found.IsResponding
                || _friendsByPetId.ContainsKey(found.Request.RequesterPetId))
            {
                return false;
            }

            pending = found;
            if (accept)
            {
                pending.ReplyKey ??= ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                publicKey = Convert.ToBase64String(pending.ReplyKey.ExportSubjectPublicKeyInfo());
                keys = PacketCrypto.DerivePairingKeys(
                    pending.ReplyKey,
                    pending.PublicKey,
                    requestId,
                    pending.Request.RequesterPetId,
                    _localPetId,
                    pending.PublicKey,
                    publicKey,
                    pending.RequestFingerprint);
                if (keys is null)
                {
                    return false;
                }
            }

            pending.IsResponding = true;
        }

        var request = pending.Request;
        var presence = _localPresence;

        AckPayload? ack;
        if (keys is { } pairKeys)
        {
            var acceptPayload = new FriendAcceptPayload
            {
                RequestId = requestId,
                PublicKey = publicKey!,
                TcpPort = ProtocolConstants.TcpControlPort,
                AccepterOwnerName = presence.OwnerName,
                AccepterName = presence.PetName,
                Appearance = presence.Appearance.Clone()
            };
            ack = await SendControlRelayAsync(
                request.RequesterPetId, PacketType.FriendAccept, acceptPayload, null, cancellationToken, pairKeys.ConfirmationKey)
                .ConfigureAwait(false);
        }
        else
        {
            var rejectPayload = new FriendRejectPayload { RequestId = requestId, Reason = "rejected_by_user" };
            ack = await SendControlRelayAsync(
                request.RequesterPetId, PacketType.FriendReject, rejectPayload, null, cancellationToken)
                .ConfigureAwait(false);
        }

        lock (_pairingLock)
        {
            if (!_incomingPairings.TryGetValue(requestId, out var current) || !ReferenceEquals(current, pending))
            {
                return false;
            }

            pending.IsResponding = false;
            if (ack is not { Accepted: true })
            {
                return false;
            }

            _incomingPairings.Remove(requestId);
            pending.Dispose();
        }

        if (keys is { } confirmedKeys)
        {
            FriendAccepted?.Invoke(this, new FriendAcceptedEventArgs(
                request.RequesterPetId,
                request.RequesterOwnerName,
                request.RequesterName,
                request.Appearance,
                confirmedKeys.FriendKey,
                "GCP",
                ProtocolConstants.TcpControlPort));
        }

        return true;
    }

    public async Task<bool> SendBubbleMessageAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default)
    {
        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.Length > ProtocolConstants.MaxBubbleMessageLength)
        {
            trimmed = trimmed[..ProtocolConstants.MaxBubbleMessageLength];
        }

        return await SendEncryptedFriendPacketAsync(
            friend, PacketType.BubbleMessage, new BubbleMessagePayload { Text = trimmed }, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default)
        => SendEncryptedFriendPacketAsync(
            friend, PacketType.QuickReaction, new QuickReactionPayload { ReactionId = reactionId }, cancellationToken);

    public Task<bool> SendPetInteractionAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default)
        => SendEncryptedFriendPacketAsync(
            friend, PacketType.PetInteraction, new PetInteractionPayload { InteractionType = interactionType }, cancellationToken);

    public async Task<bool> SetMusicFollowAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default)
    {
        if (!enable)
        {
            _musicReceiveWindows.TryRemove(hostFriend.PetId, out var prev);
            return await SendEncryptedFriendPacketAsync(
                hostFriend,
                PacketType.MusicFollowOff,
                new MusicFollowPayload { SubscriptionId = prev?.SubscriptionId ?? string.Empty },
                cancellationToken).ConfigureAwait(false);
        }

        var session = new LanP2pTransport.MusicSequenceWindow(Guid.NewGuid().ToString("N"));
        _musicReceiveWindows[hostFriend.PetId] = session;
        var ok = await SendEncryptedFriendPacketAsync(
            hostFriend,
            PacketType.MusicFollowOn,
            new MusicFollowPayload { ListenerUdpPort = 0, SubscriptionId = session.SubscriptionId },
            cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            _musicReceiveWindows.TryRemove(new KeyValuePair<string, LanP2pTransport.MusicSequenceWindow>(hostFriend.PetId, session));
        }

        return ok;
    }

    public Task<bool> SendGameEventAsync(FriendEntry friend, GameEventPayload payload, CancellationToken cancellationToken = default)
        => SendEncryptedFriendPacketAsync(friend, PacketType.GameEvent, payload, cancellationToken);

    public async ValueTask PublishMusicFrameAsync(MusicFrameData frame, CancellationToken cancellationToken = default)
    {
        var hub = _hub;
        if (!IsRunning || hub is null || hub.State != HubConnectionState.Connected || _musicSubscribers.IsEmpty)
        {
            return;
        }

        // GCP 무료 이그레스 및 저사양 VM 보호를 위해 약 15Hz(66ms) 이하로 스로틀링
        var nowMs = Environment.TickCount64;
        var prevMs = Interlocked.Read(ref _lastRemoteMusicSentTickMs);
        if (nowMs - prevMs < MinRemoteMusicPublishIntervalMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _musicPublishInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _lastRemoteMusicSentTickMs, nowMs);

            foreach (var kv in _musicSubscribers)
            {
                if (!_friendsByPetId.TryGetValue(kv.Key, out var friend) || string.IsNullOrWhiteSpace(friend.PairSecretBase64))
                {
                    continue;
                }

                var packet = new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = _localPetId,
                    Sequence = frame.Sequence,
                    SubscriptionId = kv.Value,
                    Bass = frame.Bass,
                    Mid = frame.Mid,
                    Treble = frame.Treble,
                    IsBeat = frame.IsBeat
                };
                var canonical = PacketSigner.BuildMusicFrameCanonicalString(packet);
                packet.SignatureBase64 = PacketSigner.ComputeSignature(canonical, friend.PairSecretBase64);
                await hub.SendAsync(SocialHubConstants.RelayMusicFrameMethod, packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // 일시적 송신 실패는 다음 프레임에서 자연 복구
        }
        finally
        {
            Interlocked.Exchange(ref _musicPublishInFlight, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _tokenLock.Dispose();
    }

    private HubConnection BuildHubConnection(Uri baseUri)
    {
        var hubUri = new Uri(baseUri, SocialHubConstants.HubPath.TrimStart('/'));
        var builder = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.AccessTokenProvider = () => GetAccessTokenAsync(CancellationToken.None);
                if (_httpHandlerFactory is not null)
                {
                    options.HttpMessageHandlerFactory = _ => _httpHandlerFactory();
                }
            })
            .AddJsonProtocol(json =>
            {
                json.PayloadSerializerOptions.PropertyNamingPolicy = ProtocolJson.PacketOptions.PropertyNamingPolicy;
                json.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .WithAutomaticReconnect(new BackoffRetryPolicy());

        var hub = builder.Build();
        hub.ServerTimeout = TimeSpan.FromSeconds(35);
        hub.KeepAliveInterval = TimeSpan.FromSeconds(15);

        hub.On<RelayControlEnvelopeDelivery, ControlPacketEnvelope?>(
            SocialHubConstants.DeliverControlPacketClientMethod,
            HandleIncomingRelayDelivery);

        hub.On<MusicFramePacket>(
            SocialHubConstants.ReceiveMusicFrameClientMethod,
            HandleIncomingMusicFrame);

        hub.On<RemoteFriendStateDto>(
            SocialHubConstants.ReceiveFriendPresenceChangedClientMethod,
            dto => ApplyOnlinePeerDto(dto, raiseEvent: true));

        hub.On<string>(
            SocialHubConstants.ReceiveFriendOfflineClientMethod,
            petId =>
            {
                if (_peers.TryRemove(petId, out _))
                {
                    _musicSubscribers.TryRemove(petId, out _);
                    RaisePeersChanged();
                }
            });

        hub.Reconnected += async _ =>
        {
            var token = _cts?.Token ?? CancellationToken.None;
            await RegisterPresenceOnServerAsync(hub, token).ConfigureAwait(false);
        };

        hub.Closed += _ =>
        {
            _peers.Clear();
            _musicSubscribers.Clear();
            RaisePeersChanged();
            return Task.CompletedTask;
        };

        return hub;
    }

    private async Task<bool> TryConnectOnceAsync(HubConnection hub, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(InitialConnectTimeout);
            await hub.StartAsync(timeoutCts.Token).ConfigureAwait(false);
            await RegisterPresenceOnServerAsync(hub, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs("GcpConnect", ex.Message, ex));
            return false;
        }
    }

    private async Task EnsureConnectedBackgroundLoopAsync(HubConnection hub, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(BackgroundRetryInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                if (hub.State == HubConnectionState.Disconnected)
                {
                    await TryConnectOnceAsync(hub, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RegisterPresenceOnServerAsync(HubConnection hub, CancellationToken ct)
    {
        try
        {
            var list = await hub.InvokeAsync<List<RemoteFriendStateDto>>(
                SocialHubConstants.RegisterPresenceMethod, BuildPresenceUpdate(), ct).ConfigureAwait(false);
            ReplaceAllRemotePeers(list);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs("GcpRegisterPresence", ex.Message, ex));
        }
    }

    private async Task SyncKnownFriendsWithServerAsync(HubConnection hub, List<string> friendIds)
    {
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            var list = await hub.InvokeAsync<List<RemoteFriendStateDto>>(
                SocialHubConstants.SyncKnownFriendsMethod, friendIds, ct).ConfigureAwait(false);
            ReplaceAllRemotePeers(list);
        }
        catch
        {
            // 연결 끊김 시 Reconnected에서 자동 재동기화
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_cachedAccessToken)
                && DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2) < _tokenExpiresAtUtc)
            {
                return _cachedAccessToken;
            }

            if (_baseUri is null || _deviceKey is null || string.IsNullOrEmpty(_devicePublicKeyBase64))
            {
                return null;
            }

            using var client = _httpHandlerFactory is not null
                ? new HttpClient(_httpHandlerFactory(), disposeHandler: true) { BaseAddress = _baseUri }
                : new HttpClient { BaseAddress = _baseUri };
            client.Timeout = TimeSpan.FromSeconds(5);

            var challengeRes = await client.PostAsJsonAsync(
                SocialHubConstants.AuthChallengePath,
                new AuthChallengeRequest
                {
                    PetId = _localPetId,
                    DevicePublicKeyBase64 = _devicePublicKeyBase64
                },
                ProtocolJson.PacketOptions,
                ct).ConfigureAwait(false);

            if (!challengeRes.IsSuccessStatusCode)
            {
                return null;
            }

            var challenge = await challengeRes.Content.ReadFromJsonAsync<AuthChallengeResponse>(
                ProtocolJson.PacketOptions, ct).ConfigureAwait(false);
            if (challenge is null || string.IsNullOrEmpty(challenge.Nonce))
            {
                return null;
            }

            var canonical = RemoteAuthSigner.BuildChallengeCanonicalString(
                challenge.Nonce, _localPetId, _devicePublicKeyBase64, challenge.IssuedAtUtc);
            var signature = RemoteAuthSigner.SignChallenge(_deviceKey, canonical);

            var presence = _localPresence;
            var tokenRes = await client.PostAsJsonAsync(
                SocialHubConstants.AuthTokenPath,
                new AuthTokenRequest
                {
                    PetId = _localPetId,
                    FriendCode = presence.FriendCode,
                    OwnerName = presence.OwnerName,
                    PetName = presence.PetName,
                    DevicePublicKeyBase64 = _devicePublicKeyBase64,
                    Nonce = challenge.Nonce,
                    IssuedAtUtc = challenge.IssuedAtUtc,
                    SignatureBase64 = signature
                },
                ProtocolJson.PacketOptions,
                ct).ConfigureAwait(false);

            if (!tokenRes.IsSuccessStatusCode)
            {
                return null;
            }

            var auth = await tokenRes.Content.ReadFromJsonAsync<AuthTokenResponse>(
                ProtocolJson.PacketOptions, ct).ConfigureAwait(false);
            if (auth is null || string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                return null;
            }

            _cachedAccessToken = auth.AccessToken;
            _tokenExpiresAtUtc = auth.ExpiresAtUtc;
            return _cachedAccessToken;
        }
        catch
        {
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private Task<bool> SendEncryptedFriendPacketAsync<TPayload>(
        FriendEntry friend,
        PacketType packetType,
        TPayload payload,
        CancellationToken ct)
        => SendEncryptedFriendPacketCoreAsync(friend, packetType, payload, ct);

    private async Task<bool> SendEncryptedFriendPacketCoreAsync<TPayload>(
        FriendEntry friend,
        PacketType packetType,
        TPayload payload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(friend.PairSecretBase64))
        {
            return false;
        }

        var ack = await SendControlRelayAsync(friend.PetId, packetType, payload, friend.PairSecretBase64, ct)
            .ConfigureAwait(false);
        return ack is { Accepted: true };
    }

    private async Task<AckPayload?> SendControlRelayAsync<TPayload>(
        string targetPetId,
        PacketType packetType,
        TPayload payload,
        string? pairSecretBase64,
        CancellationToken ct,
        string? confirmationKey = null)
    {
        var hub = _hub;
        if (!IsRunning || hub is not { State: HubConnectionState.Connected })
        {
            return null;
        }

        try
        {
            var payloadJson = JsonSerializer.Serialize(payload, ProtocolJson.PacketOptions);
            var header = new PacketHeader
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                AppVersion = _localPresence.AppVersion,
                PacketType = packetType,
                SenderPetId = _localPetId,
                MessageId = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTimeOffset.UtcNow
            };

            ControlPacketEnvelope envelope;
            if (!string.IsNullOrWhiteSpace(pairSecretBase64))
            {
                var (ciphertext, nonce, tag) = PacketCrypto.EncryptControlPayload(header, payloadJson, pairSecretBase64);
                envelope = new ControlPacketEnvelope
                {
                    Header = header,
                    PayloadJson = ciphertext,
                    IsEncrypted = true,
                    NonceBase64 = nonce,
                    AuthTagBase64 = tag
                };
                var sigInput = PacketCrypto.BuildEncryptedPayloadSignatureInput(envelope);
                var canonical = PacketSigner.BuildControlCanonicalString(header, sigInput);
                header.SignatureBase64 = PacketSigner.ComputeSignature(canonical, pairSecretBase64);
            }
            else
            {
                envelope = new ControlPacketEnvelope
                {
                    Header = header,
                    PayloadJson = payloadJson,
                    IsEncrypted = false
                };
                if (confirmationKey is not null)
                {
                    SignPlaintext(envelope, confirmationKey);
                }
            }

            var result = await hub.InvokeAsync<RelayDeliveryResult>(
                SocialHubConstants.RelayControlPacketMethod,
                new RelayControlEnvelopeRequest(targetPetId, envelope),
                ct).ConfigureAwait(false);

            if (!result.Delivered || result.AckEnvelope is null)
            {
                return null;
            }

            return LanP2pTransport.ReadVerifiedAck(
                result.AckEnvelope,
                header.MessageId,
                targetPetId,
                confirmationKey ?? pairSecretBase64);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs($"GcpSend:{packetType}", ex.Message, ex));
            return null;
        }
    }

    private ControlPacketEnvelope? HandleIncomingRelayDelivery(RelayControlEnvelopeDelivery delivery)
    {
        if (delivery?.Envelope?.Header is null)
        {
            return null;
        }

        var ack = ProcessIncomingControlEnvelope(delivery.Envelope, out var ackKey);
        var ackEnvelope = new ControlPacketEnvelope
        {
            Header = new PacketHeader
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                AppVersion = _localPresence.AppVersion,
                PacketType = PacketType.Ack,
                SenderPetId = _localPetId
            },
            PayloadJson = JsonSerializer.Serialize(ack, ProtocolJson.PacketOptions)
        };
        if (ackKey is not null)
        {
            SignPlaintext(ackEnvelope, ackKey);
        }

        return ackEnvelope;
    }

    private AckPayload ProcessIncomingControlEnvelope(ControlPacketEnvelope envelope, out string? ackKey)
    {
        var header = envelope.Header;
        ackKey = null;

        if (ProtocolConstants.EvaluateCompatibility(header.ProtocolVersion) != ProtocolCompatibility.Compatible)
        {
            return new AckPayload { AckMessageId = header.MessageId, Accepted = false, ErrorCode = "PROTOCOL_MISMATCH" };
        }

        if (string.IsNullOrWhiteSpace(header.SenderPetId) || header.SenderPetId.Length > 64
            || SamePetId(header.SenderPetId, _localPetId) || !Guid.TryParseExact(header.MessageId, "N", out _))
        {
            return RejectAck(header.MessageId, "INVALID_HEADER");
        }

        var now = DateTimeOffset.UtcNow;
        if (!ControlReplayWindow.IsFresh(header.TimestampUtc, now))
        {
            return RejectAck(header.MessageId, "STALE_TIMESTAMP");
        }

        var requiresFriendAuth = header.PacketType is
            PacketType.BubbleMessage or
            PacketType.QuickReaction or
            PacketType.PetInteraction or
            PacketType.MusicFollowOn or
            PacketType.MusicFollowOff or
            PacketType.GameEvent or
            PacketType.PresenceSync;

        if (!requiresFriendAuth)
        {
            return DispatchControlPacket(envelope, envelope.PayloadJson, out ackKey);
        }

        if (!_friendsByPetId.TryGetValue(header.SenderPetId, out var friend))
        {
            return RejectAck(header.MessageId, "NOT_FRIEND");
        }

        if (!envelope.IsEncrypted)
        {
            return RejectAck(header.MessageId, "ENCRYPTION_REQUIRED");
        }

        string verifiedPairSecret;
        lock (friend)
        {
            verifiedPairSecret = friend.PairSecretBase64;
        }

        var signatureInput = PacketCrypto.BuildEncryptedPayloadSignatureInput(envelope);
        var canonical = PacketSigner.BuildControlCanonicalString(header, signatureInput);
        if (!PacketSigner.VerifySignature(canonical, header.SignatureBase64, verifiedPairSecret))
        {
            return RejectAck(header.MessageId, "INVALID_SIGNATURE");
        }

        if (!PacketCrypto.TryDecryptControlPayload(envelope, verifiedPairSecret, out var plaintextJson))
        {
            return RejectAck(header.MessageId, "DECRYPTION_FAILED");
        }

        ackKey = verifiedPairSecret;
        var replayWindow = _replayWindows.GetOrAdd(header.SenderPetId, _ => new ControlReplayWindow());
        return replayWindow.Execute(header, Fingerprint($"{(int)header.PacketType}|{plaintextJson}"), now, () =>
        {
            lock (friend)
            {
                friend.LastSeenAtUtc = now;
            }

            return DispatchControlPacket(envelope, plaintextJson, out _);
        });
    }

    private AckPayload DispatchControlPacket(ControlPacketEnvelope envelope, string effectivePayloadJson, out string? ackKey)
    {
        var header = envelope.Header;
        ackKey = null;

        switch (header.PacketType)
        {
            case PacketType.FriendRequest:
                if (envelope.IsEncrypted || !ReceiveFriendRequest(envelope))
                {
                    return RejectAck(header.MessageId, "PAIRING_REQUEST_REJECTED");
                }
                break;

            case PacketType.FriendAccept:
                if (envelope.IsEncrypted || !ReceiveFriendAcceptance(envelope, out ackKey))
                {
                    return RejectAck(header.MessageId, "PAIRING_CONFIRMATION_FAILED");
                }
                break;

            case PacketType.FriendReject:
                if (envelope.IsEncrypted || !ReceiveFriendRejection(envelope))
                {
                    return RejectAck(header.MessageId, "PAIRING_REQUEST_UNKNOWN");
                }
                break;

            case PacketType.BubbleMessage:
            {
                var p = JsonSerializer.Deserialize<BubbleMessagePayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (p is null || string.IsNullOrWhiteSpace(p.Text)) return RejectAck(header.MessageId, "INVALID_PAYLOAD");
                var safeText = p.Text.Length > ProtocolConstants.MaxBubbleMessageLength
                    ? p.Text[..ProtocolConstants.MaxBubbleMessageLength]
                    : p.Text;
                BubbleMessageReceived?.Invoke(this, new BubbleMessageReceivedEventArgs(
                    header.SenderPetId, header.MessageId, safeText, header.TimestampUtc));
                break;
            }

            case PacketType.QuickReaction:
            {
                var p = JsonSerializer.Deserialize<QuickReactionPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (p is null) return RejectAck(header.MessageId, "INVALID_PAYLOAD");
                QuickReactionReceived?.Invoke(this, new QuickReactionReceivedEventArgs(header.SenderPetId, p.ReactionId));
                break;
            }

            case PacketType.PetInteraction:
            {
                var p = JsonSerializer.Deserialize<PetInteractionPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                PetInteractionReceived?.Invoke(this, new PetInteractionReceivedEventArgs(
                    header.SenderPetId, p?.InteractionType ?? "pet"));
                break;
            }

            case PacketType.MusicFollowOn:
            {
                var p = JsonSerializer.Deserialize<MusicFollowPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (p is not null && Guid.TryParseExact(p.SubscriptionId, "N", out _))
                {
                    _musicSubscribers[header.SenderPetId] = p.SubscriptionId;
                    MusicFollowStateChanged?.Invoke(this, new MusicFollowStateChangedEventArgs(header.SenderPetId, true));
                    break;
                }
                return RejectAck(header.MessageId, "INVALID_SUBSCRIPTION");
            }

            case PacketType.MusicFollowOff:
            {
                var p = JsonSerializer.Deserialize<MusicFollowPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (_musicSubscribers.TryGetValue(header.SenderPetId, out var subId)
                    && (string.IsNullOrEmpty(p?.SubscriptionId) || subId == p.SubscriptionId)
                    && _musicSubscribers.TryRemove(header.SenderPetId, out _))
                {
                    MusicFollowStateChanged?.Invoke(this, new MusicFollowStateChangedEventArgs(header.SenderPetId, false));
                }
                break;
            }

            case PacketType.GameEvent:
            {
                var p = JsonSerializer.Deserialize<GameEventPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (p is null || string.IsNullOrWhiteSpace(p.GameType) || string.IsNullOrWhiteSpace(p.Action))
                {
                    return RejectAck(header.MessageId, "INVALID_PAYLOAD");
                }
                GameEventReceived?.Invoke(this, new GameEventReceivedEventArgs(header.SenderPetId, p));
                break;
            }

            case PacketType.PresenceSync:
                return ReceiveFriendPresence(header, effectivePayloadJson);

            default:
                return RejectAck(header.MessageId, "UNSUPPORTED_TYPE");
        }

        return new AckPayload { AckMessageId = header.MessageId, Accepted = true };
    }

    private AckPayload ReceiveFriendPresence(PacketHeader header, string json)
    {
        var presence = JsonSerializer.Deserialize<FriendPresencePayload>(json, ProtocolJson.PacketOptions);
        if (presence is null || !_friendsByPetId.TryGetValue(header.SenderPetId, out var friend)
            || presence.Appearance is null || string.IsNullOrWhiteSpace(presence.PetName))
        {
            return RejectAck(header.MessageId, "INVALID_PRESENCE");
        }

        var now = DateTimeOffset.UtcNow;
        lock (friend)
        {
            friend.LastSeenAtUtc = now;
            friend.OwnerName = presence.OwnerName ?? string.Empty;
            friend.PetName = presence.PetName;
            friend.CachedAppearance = presence.Appearance.Clone();
        }

        _peers[header.SenderPetId] = new PeerSnapshot
        {
            PetId = header.SenderPetId,
            OwnerName = presence.OwnerName ?? string.Empty,
            PetName = presence.PetName,
            Appearance = presence.Appearance.Clone(),
            Level = presence.Level,
            IsMusicActive = presence.IsMusicActive,
            IsSleeping = presence.IsSleeping,
            IsFriend = true,
            TransportKind = PeerTransportKind.GcpRelay,
            IpAddress = "GCP",
            TcpPort = ProtocolConstants.TcpControlPort,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = presence.AppVersion,
            Compatibility = ProtocolCompatibility.Compatible,
            LastSeenUtc = now
        };
        RaisePeersChanged();

        var local = _localPresence;
        return new AckPayload
        {
            AckMessageId = header.MessageId,
            Accepted = true,
            Presence = new FriendPresencePayload
            {
                OwnerName = local.OwnerName,
                PetName = local.PetName,
                Appearance = local.Appearance.Clone(),
                Level = local.Level,
                IsMusicActive = local.IsMusicActive,
                IsSleeping = local.IsSleeping,
                TcpPort = ProtocolConstants.TcpControlPort,
                AppVersion = local.AppVersion
            }
        };
    }

    private void HandleIncomingMusicFrame(MusicFramePacket packet)
    {
        if (packet is null || string.IsNullOrWhiteSpace(packet.SenderPetId)
            || ProtocolConstants.EvaluateCompatibility(packet.ProtocolVersion) != ProtocolCompatibility.Compatible
            || !_friendsByPetId.TryGetValue(packet.SenderPetId, out var friend))
        {
            return;
        }

        var canonical = PacketSigner.BuildMusicFrameCanonicalString(packet);
        if (!PacketSigner.VerifySignature(canonical, packet.SignatureBase64, friend.PairSecretBase64))
        {
            return;
        }

        if (!_musicReceiveWindows.TryGetValue(packet.SenderPetId, out var session) || !session.Accept(packet))
        {
            return;
        }

        MusicFrameReceived?.Invoke(this, new MusicFrameReceivedEventArgs(
            packet.SenderPetId,
            new MusicFrameData(packet.Bass, packet.Mid, packet.Treble, packet.IsBeat, packet.Sequence)));
    }

    private bool ReceiveFriendRequest(ControlPacketEnvelope envelope)
    {
        var header = envelope.Header;
        var payload = JsonSerializer.Deserialize<FriendRequestPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _)
            || !PacketCrypto.IsPairingPublicKey(payload.PublicKey))
        {
            return false;
        }

        var request = new FriendRequestReceivedEventArgs(
            payload.RequestId,
            header.SenderPetId,
            payload.RequesterOwnerName ?? string.Empty,
            payload.RequesterName,
            payload.Appearance ?? new PetAppearance(),
            payload.Level,
            "GCP",
            ProtocolConstants.TcpControlPort);

        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (_friendsByPetId.ContainsKey(header.SenderPetId) || _incomingPairings.Count >= 32
                || _incomingPairings.ContainsKey(payload.RequestId)
                || _incomingPairings.Values.Any(p => SamePetId(p.Request.RequesterPetId, header.SenderPetId)))
            {
                return false;
            }

            _incomingPairings.Add(payload.RequestId, new IncomingPairing(request, payload.PublicKey, Fingerprint(envelope.PayloadJson)));
        }

        FriendRequestReceived?.Invoke(this, request);
        return true;
    }

    private bool ReceiveFriendAcceptance(ControlPacketEnvelope envelope, out string? confirmationKey)
    {
        confirmationKey = null;
        var header = envelope.Header;
        var payload = JsonSerializer.Deserialize<FriendAcceptPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _))
        {
            return false;
        }

        string friendKey;
        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (!_outgoingPairings.TryGetValue(payload.RequestId, out var pending)
                || !SamePetId(pending.PeerId, header.SenderPetId))
            {
                return false;
            }

            if (pending.ConfirmationKey is { } completedKey)
            {
                if (pending.ConfirmedPublicKey != payload.PublicKey || !VerifyPlaintextSignature(envelope, completedKey))
                {
                    return false;
                }

                confirmationKey = completedKey;
                return true;
            }

            if (_friendsByPetId.ContainsKey(header.SenderPetId))
            {
                return false;
            }

            var keys = PacketCrypto.DerivePairingKeys(
                pending.Key, payload.PublicKey, payload.RequestId,
                _localPetId, header.SenderPetId, pending.PublicKey, payload.PublicKey,
                pending.RequestFingerprint);
            if (keys is not { } derived || !VerifyPlaintextSignature(envelope, derived.ConfirmationKey))
            {
                return false;
            }

            pending.Complete(payload.PublicKey, derived.ConfirmationKey);
            confirmationKey = derived.ConfirmationKey;
            friendKey = derived.FriendKey;
        }

        FriendAccepted?.Invoke(this, new FriendAcceptedEventArgs(
            header.SenderPetId,
            payload.AccepterOwnerName ?? string.Empty,
            payload.AccepterName,
            payload.Appearance ?? new PetAppearance(),
            friendKey,
            "GCP",
            ProtocolConstants.TcpControlPort));
        return true;
    }

    private bool ReceiveFriendRejection(ControlPacketEnvelope envelope)
    {
        var payload = JsonSerializer.Deserialize<FriendRejectPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _))
        {
            return false;
        }

        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (!_outgoingPairings.TryGetValue(payload.RequestId, out var pending)
                || !SamePetId(pending.PeerId, envelope.Header.SenderPetId) || pending.ConfirmationKey is not null)
            {
                return false;
            }

            _outgoingPairings.Remove(payload.RequestId);
            pending.Dispose();
        }

        FriendRejected?.Invoke(this, new FriendRejectedEventArgs(envelope.Header.SenderPetId, payload.Reason));
        return true;
    }

    private void ReplaceAllRemotePeers(IReadOnlyList<RemoteFriendStateDto> list)
    {
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in list)
        {
            if (string.IsNullOrWhiteSpace(dto.PetId) || SamePetId(dto.PetId, _localPetId))
            {
                continue;
            }

            activeIds.Add(dto.PetId);
            ApplyOnlinePeerDto(dto, raiseEvent: false);
        }

        foreach (var existingId in _peers.Keys)
        {
            if (!activeIds.Contains(existingId))
            {
                _peers.TryRemove(existingId, out _);
                _musicSubscribers.TryRemove(existingId, out _);
            }
        }

        RaisePeersChanged();
    }

    private void ApplyOnlinePeerDto(RemoteFriendStateDto dto, bool raiseEvent)
    {
        if (string.IsNullOrWhiteSpace(dto.PetId) || SamePetId(dto.PetId, _localPetId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var isFriend = _friendsByPetId.TryGetValue(dto.PetId, out var friend);
        if (isFriend && friend is not null)
        {
            lock (friend)
            {
                friend.LastSeenAtUtc = now;
                if (!string.IsNullOrWhiteSpace(dto.OwnerName)) friend.OwnerName = dto.OwnerName;
                if (!string.IsNullOrWhiteSpace(dto.PetName)) friend.PetName = dto.PetName;
                if (dto.Appearance is not null) friend.CachedAppearance = dto.Appearance.Clone();
            }
        }

        _peers[dto.PetId] = new PeerSnapshot
        {
            PetId = dto.PetId,
            FriendCode = isFriend ? (dto.FriendCode ?? string.Empty) : string.Empty,
            OwnerName = dto.OwnerName ?? string.Empty,
            PetName = dto.PetName,
            Appearance = (dto.Appearance ?? new PetAppearance()).Clone(),
            Level = dto.Level,
            IsMusicActive = isFriend && dto.IsMusicActive,
            IsSleeping = isFriend && dto.IsSleeping,
            IsFriend = isFriend,
            TransportKind = PeerTransportKind.GcpRelay,
            IpAddress = "GCP",
            TcpPort = ProtocolConstants.TcpControlPort,
            ProtocolVersion = dto.ProtocolVersion,
            AppVersion = dto.AppVersion,
            Compatibility = ProtocolConstants.EvaluateCompatibility(dto.ProtocolVersion),
            LastSeenUtc = now
        };

        if (raiseEvent)
        {
            RaisePeersChanged();
        }
    }

    private RemotePresenceUpdate BuildPresenceUpdate()
    {
        var local = _localPresence;
        return new RemotePresenceUpdate
        {
            FriendCode = local.FriendCode,
            OwnerName = local.OwnerName,
            PetName = local.PetName,
            Appearance = local.Appearance.Clone(),
            Level = local.Level,
            IsMusicActive = local.IsMusicActive,
            IsSleeping = local.IsSleeping,
            AppVersion = local.AppVersion,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            KnownFriendPetIds = _friendsByPetId.Keys.ToList()
        };
    }

    private void PrunePairings(DateTimeOffset now)
    {
        foreach (var item in _outgoingPairings.Where(i => i.Value.ExpiresAt <= now).ToArray())
        {
            _outgoingPairings.Remove(item.Key);
            item.Value.Dispose();
        }

        foreach (var item in _incomingPairings.Where(i => i.Value.ExpiresAt <= now && !i.Value.IsResponding).ToArray())
        {
            _incomingPairings.Remove(item.Key);
            item.Value.Dispose();
        }
    }

    private void RaisePeersChanged() => PeersChanged?.Invoke(this, GetDiscoveredPeers());

    private static void SignPlaintext(ControlPacketEnvelope envelope, string key)
        => envelope.Header.SignatureBase64 = PacketSigner.ComputeSignature(
            PacketSigner.BuildControlCanonicalString(envelope.Header, envelope.PayloadJson), key);

    private static bool VerifyPlaintextSignature(ControlPacketEnvelope envelope, string key)
        => PacketSigner.VerifySignature(
            PacketSigner.BuildControlCanonicalString(envelope.Header, envelope.PayloadJson),
            envelope.Header.SignatureBase64, key);

    private static AckPayload RejectAck(string messageId, string errorCode) => new()
    {
        AckMessageId = messageId,
        Accepted = false,
        ErrorCode = errorCode
    };

    private static bool SamePetId(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(string payload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
