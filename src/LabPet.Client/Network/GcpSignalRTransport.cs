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

    private sealed class OutgoingPairing(string peerId, DateTimeOffset expiresAt) : IDisposable
    {
        public string PeerId { get; } = peerId;
        public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
        public string RequestFingerprint { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
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

    private sealed class IncomingPairing(FriendRequestReceivedEventArgs request, string publicKey, string fingerprint, DateTimeOffset expiresAt) : IDisposable
    {
        public FriendRequestReceivedEventArgs Request { get; } = request;
        public string PublicKey { get; } = publicKey;
        public string RequestFingerprint { get; } = fingerprint;
        public ECDiffieHellman? ReplyKey { get; set; }
        public bool IsResponding { get; set; }
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public void Dispose() => ReplyKey?.Dispose();
    }

    private const int MinRemoteMusicPublishIntervalMs = 66;
    private const int MaxPendingPairings = 32;
    private const int MaxExpiredRequestTombstones = 256;
    private static readonly TimeSpan ExpiredTombstoneLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InitialConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BackgroundRetryInterval = TimeSpan.FromSeconds(20);

    internal TimeSpan PairingLifetime { get; set; } = TimeSpan.FromMinutes(3);
    internal TimeSpan RelayInvokeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private readonly ServerConnectionOptions _options;
    private readonly DeviceIdentityStore _identityStore;
    private readonly Func<HttpMessageHandler>? _httpHandlerFactory;

    private readonly ConcurrentDictionary<string, PeerSnapshot> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _peerPushSequence = new(StringComparer.OrdinalIgnoreCase);
    private long _presencePushSequence;
    private readonly ConcurrentDictionary<string, string> _musicSubscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LanP2pTransport.MusicSequenceWindow> _musicReceiveWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ControlReplayWindow> _replayWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingRemovedFriends = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, FriendEntry> _friendsByPetId = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private AsyncOperationLifetime _background = new();
    private readonly object _stateLock = new();
    private readonly object _pairingLock = new();
    private readonly Dictionary<string, OutgoingPairing> _outgoingPairings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingPairing> _incomingPairings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _expiredOutgoingRequestIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _expiredIncomingRequestIds = new(StringComparer.Ordinal);
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
    private volatile string? _lastAuthErrorCode;
    private volatile string? _lastAuthErrorMessage;
    private volatile string? _lastServerBootId;

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
    public event EventHandler<SnowballReceivedEventArgs>? SnowballReceived;
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

    private async Task StartCoreAsync(
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
            _background = new AsyncOperationLifetime();
            IsRunning = true;
        }

        // 초기 연결 시도는 짧은 타임아웃으로 수행하고, 서버 미응답 시 백그라운드 루프가 자동 재연결한다.
        await TryConnectOnceAsync(hub, ct).ConfigureAwait(false);
        _reconnectLoopTask = EnsureConnectedBackgroundLoopAsync(hub, ct);
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
        HubConnection? hub;
        Task? loopTask;
        CancellationTokenSource? cts;
        ECDsa? key;
        Task background;

        lock (_stateLock)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            background = _background.DrainAsync();
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
            _lastAuthErrorCode = null;
            _lastAuthErrorMessage = null;
            _lastServerBootId = null;
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
                _expiredOutgoingRequestIds.Clear();
                _expiredIncomingRequestIds.Clear();
            }
        }

        try
        {
            cts?.Cancel();
            if (hub is not null)
            {
                try { await hub.StopAsync().ConfigureAwait(false); }
                finally { await hub.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(loopTask ?? Task.CompletedTask, background).ConfigureAwait(false);
            }
            finally
            {
                cts?.Dispose();
                key?.Dispose();
            }
        }
    }

    public async Task UpdateLocalPresenceAsync(LocalPresenceState presence, CancellationToken cancellationToken = default)
    {
        _localPresence = presence;
        if (presence.IsFocusing)
        {
            _musicSubscribers.Clear();
        }

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
                _pendingRemovedFriends.TryRemove(f.PetId, out _);
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
                    IsFocusing = isFriend && existing.IsFocusing,
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
                    _musicReceiveWindows.TryRemove(existing.PetId, out _);
                }

                changed = true;
            }
        }

        if (changed)
        {
            RaisePeersChanged();
        }

        HubConnection? hub;
        CancellationToken token;
        IDisposable? operation;
        lock (_stateLock)
        {
            hub = _hub;
            token = _cts?.Token ?? new CancellationToken(canceled: true);
            operation = IsRunning && hub is { State: HubConnectionState.Connected } ? _background.TryBegin() : null;
        }
        if (hub is not null && operation is not null)
        {
            _ = SyncKnownFriendsWithServerAsync(hub, map.Keys.ToList(), operation, token);
        }
    }

    public void SetPendingRemovedFriends(IReadOnlyCollection<string> removedPetIds)
    {
        _pendingRemovedFriends.Clear();
        if (removedPetIds is null)
        {
            return;
        }

        foreach (var id in removedPetIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && !_friendsByPetId.ContainsKey(id))
            {
                _pendingRemovedFriends[id.Trim()] = 0;
            }
        }
    }

    public async Task<bool> RemoveFriendAsync(string friendPetId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(friendPetId))
        {
            return false;
        }

        var cleanId = friendPetId.Trim();
        _pendingRemovedFriends[cleanId] = 0;
        ApplyLocalFriendRemovalCleanup(cleanId);

        var hub = _hub;
        if (!IsRunning || hub is not { State: HubConnectionState.Connected })
        {
            return true;
        }

        try
        {
            await hub.InvokeAsync(SocialHubConstants.RemoveFriendMethod, cleanId, cancellationToken).ConfigureAwait(false);
            _pendingRemovedFriends.TryRemove(cleanId, out _);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs(
                "GcpRemoveFriend",
                SanitizeSensitiveText(ex.Message),
                ex,
                FriendOperationErrorCodes.NetworkError,
                PeerTransportKind.GcpRelay));
            return false;
        }
    }

    private void ApplyLocalFriendRemovalCleanup(string petId)
    {
        _musicSubscribers.TryRemove(petId, out _);
        _musicReceiveWindows.TryRemove(petId, out _);
        _replayWindows.TryRemove(petId, out _);

        lock (_pairingLock)
        {
            foreach (var kv in _outgoingPairings.Where(kv => SamePetId(kv.Value.PeerId, petId)).ToArray())
            {
                _outgoingPairings.Remove(kv.Key);
                kv.Value.Dispose();
            }

            foreach (var kv in _incomingPairings.Where(kv => SamePetId(kv.Value.Request.RequesterPetId, petId) && !kv.Value.IsResponding).ToArray())
            {
                _incomingPairings.Remove(kv.Key);
                kv.Value.Dispose();
            }
        }

        if (_peers.TryGetValue(petId, out var existing) && existing.IsFriend)
        {
            _peers[petId] = new PeerSnapshot
            {
                PetId = existing.PetId,
                FriendCode = string.Empty,
                OwnerName = existing.OwnerName,
                PetName = existing.PetName,
                Appearance = existing.Appearance.Clone(),
                Level = existing.Level,
                IsMusicActive = false,
                IsSleeping = false,
                IsFocusing = false,
                IsFriend = false,
                TransportKind = existing.TransportKind,
                IpAddress = existing.IpAddress,
                TcpPort = existing.TcpPort,
                ProtocolVersion = existing.ProtocolVersion,
                AppVersion = existing.AppVersion,
                Compatibility = existing.Compatibility,
                LastSeenUtc = existing.LastSeenUtc
            };
            RaisePeersChanged();
        }
    }

    public IReadOnlyList<PeerSnapshot> GetDiscoveredPeers()
        => _peers.Values
            .OrderByDescending(p => _friendsByPetId.ContainsKey(p.PetId))
            .ThenBy(p => p.PetName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal bool IsFriend(string peerPetId)
        => !string.IsNullOrWhiteSpace(peerPetId) && _friendsByPetId.ContainsKey(peerPetId);

    public bool HasPendingOutgoingRequest(string peerPetId)
    {
        if (string.IsNullOrWhiteSpace(peerPetId) || _friendsByPetId.ContainsKey(peerPetId))
        {
            return false;
        }

        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            return _outgoingPairings.Values.Any(p => p.ConfirmationKey is null && SamePetId(p.PeerId, peerPetId));
        }
    }

    public async Task<bool> SendFriendRequestAsync(PeerSnapshot target, CancellationToken cancellationToken = default)
        => (await SendFriendRequestDetailedAsync(target, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<FriendOperationResult> SendFriendRequestDetailedAsync(PeerSnapshot target, CancellationToken cancellationToken = default)
    {
        var hub = _hub;
        if (!IsRunning || hub is not { State: HubConnectionState.Connected })
        {
            var errCode = _lastAuthErrorCode ?? FriendOperationErrorCodes.GcpNotConnected;
            var detail = _lastAuthErrorMessage ?? "GCP 중계 서버에 연결되어 있지 않습니다.";
            return FriendOperationResult.Fail(
                FriendOperationStage.Validation,
                errCode,
                FriendOperationErrorCodes.IsTransient(errCode),
                PeerTransportKind.GcpRelay,
                detail: detail);
        }

        if (target.RequiresUpdate)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.ProtocolMismatch, false, PeerTransportKind.GcpRelay);
        }

        if (_localPresence.IsFocusing)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.PomodoroFocusBlocked, false, PeerTransportKind.GcpRelay, detail: FriendOperationErrorCodes.PomodoroFocusMessage);
        }

        if (string.IsNullOrWhiteSpace(target.PetId))
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.InvalidRequest, false, PeerTransportKind.GcpRelay);
        }

        var requestId = Guid.NewGuid().ToString("N");
        FriendRequestPayload payload;
        var presence = _localPresence;

        lock (_pairingLock)
        {
            var now = DateTimeOffset.UtcNow;
            PrunePairings(now);
            if (_friendsByPetId.ContainsKey(target.PetId))
            {
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.AlreadyFriends, false, PeerTransportKind.GcpRelay);
            }

            if (_outgoingPairings.Values.Any(p => p.ConfirmationKey is null && SamePetId(p.PeerId, target.PetId)))
            {
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.DuplicatePendingRequest, false, PeerTransportKind.GcpRelay);
            }

            if (_outgoingPairings.Count >= MaxPendingPairings)
            {
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.PendingCapacityFull, true, PeerTransportKind.GcpRelay);
            }

            var pending = new OutgoingPairing(target.PetId, now + PairingLifetime);
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

        LanP2pTransport.ControlSendOutcome outcome;
        try
        {
            outcome = await SendControlRelayWithOutcomeAsync(target.PetId, PacketType.FriendRequest, payload, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CleanupOutgoingPairing(requestId);
            return FriendOperationResult.Fail(FriendOperationStage.Sending, FriendOperationErrorCodes.NetworkError, true, PeerTransportKind.GcpRelay, requestId, ex.Message);
        }

        if (outcome.VerifiedAck is { Accepted: true })
        {
            return FriendOperationResult.Ok(FriendOperationStage.Completed, PeerTransportKind.GcpRelay, requestId);
        }

        CleanupOutgoingPairing(requestId);
        var errorCode = outcome.ErrorCode ?? FriendOperationErrorCodes.DeliveryFailedOrTimeout;
        var stage = errorCode is FriendOperationErrorCodes.NetworkError or FriendOperationErrorCodes.GcpNotConnected or FriendOperationErrorCodes.Cancelled
            ? FriendOperationStage.Sending
            : errorCode == FriendOperationErrorCodes.InvalidAckSignature
                ? FriendOperationStage.AckVerification
                : FriendOperationStage.RemoteReceipt;

        return FriendOperationResult.Fail(stage, errorCode, outcome.IsTransient, PeerTransportKind.GcpRelay, requestId, outcome.Detail);
    }

    public async Task<bool> RespondFriendRequestAsync(string requestId, bool accept, CancellationToken cancellationToken = default)
        => (await RespondFriendRequestDetailedAsync(requestId, accept, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<FriendOperationResult> RespondFriendRequestDetailedAsync(string requestId, bool accept, CancellationToken cancellationToken = default)
    {
        var hub = _hub;
        if (!IsRunning || hub is not { State: HubConnectionState.Connected })
        {
            var errCode = _lastAuthErrorCode ?? FriendOperationErrorCodes.GcpNotConnected;
            return FriendOperationResult.Fail(
                FriendOperationStage.Validation,
                errCode,
                FriendOperationErrorCodes.IsTransient(errCode),
                PeerTransportKind.GcpRelay,
                requestId,
                _lastAuthErrorMessage);
        }

        IncomingPairing pending;
        string? publicKey = null;
        (string ConfirmationKey, string FriendKey)? keys = null;

        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (!_incomingPairings.TryGetValue(requestId, out var found))
            {
                var missingCode = _expiredIncomingRequestIds.ContainsKey(requestId)
                    ? FriendOperationErrorCodes.PairingRequestExpired
                    : FriendOperationErrorCodes.PairingRequestUnknown;
                return FriendOperationResult.Fail(FriendOperationStage.Validation, missingCode, false, PeerTransportKind.GcpRelay, requestId);
            }

            if (found.IsResponding)
            {
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.RequestInProgress, false, PeerTransportKind.GcpRelay, requestId);
            }

            if (_friendsByPetId.ContainsKey(found.Request.RequesterPetId))
            {
                _incomingPairings.Remove(requestId);
                found.Dispose();
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.AlreadyFriends, false, PeerTransportKind.GcpRelay, requestId);
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
                    _incomingPairings.Remove(requestId);
                    pending.Dispose();
                    return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.InvalidPairingRequest, false, PeerTransportKind.GcpRelay, requestId);
                }
            }

            pending.IsResponding = true;
        }

        var request = pending.Request;
        var presence = _localPresence;

        LanP2pTransport.ControlSendOutcome outcome;
        try
        {
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
                outcome = await SendControlRelayWithOutcomeAsync(
                    request.RequesterPetId, PacketType.FriendAccept, acceptPayload, null, cancellationToken, pairKeys.ConfirmationKey)
                    .ConfigureAwait(false);
            }
            else
            {
                var rejectPayload = new FriendRejectPayload { RequestId = requestId, Reason = "rejected_by_user" };
                outcome = await SendControlRelayWithOutcomeAsync(
                    request.RequesterPetId, PacketType.FriendReject, rejectPayload, null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            outcome = new LanP2pTransport.ControlSendOutcome(null, FriendOperationErrorCodes.NetworkError, true, ex.Message);
        }
        finally
        {
            lock (_pairingLock)
            {
                pending.IsResponding = false;
            }
        }

        lock (_pairingLock)
        {
            if (!_incomingPairings.TryGetValue(requestId, out var current) || !ReferenceEquals(current, pending))
            {
                var lostCode = _expiredIncomingRequestIds.ContainsKey(requestId)
                    ? FriendOperationErrorCodes.PairingRequestExpired
                    : FriendOperationErrorCodes.PairingRequestUnknown;
                return FriendOperationResult.Fail(FriendOperationStage.AckVerification, lostCode, false, PeerTransportKind.GcpRelay, requestId);
            }

            if (outcome.VerifiedAck is not { Accepted: true })
            {
                var errCode = outcome.ErrorCode ?? FriendOperationErrorCodes.DeliveryFailedOrTimeout;
                if (!outcome.IsTransient && errCode != FriendOperationErrorCodes.Cancelled)
                {
                    _incomingPairings.Remove(requestId);
                    pending.Dispose();
                }

                var stage = errCode is FriendOperationErrorCodes.NetworkError or FriendOperationErrorCodes.GcpNotConnected or FriendOperationErrorCodes.Cancelled
                    ? FriendOperationStage.Sending
                    : errCode is FriendOperationErrorCodes.InvalidAckSignature or FriendOperationErrorCodes.PairingConfirmationFailed
                        ? FriendOperationStage.AckVerification
                        : FriendOperationStage.RemoteReceipt;

                return FriendOperationResult.Fail(stage, errCode, outcome.IsTransient, PeerTransportKind.GcpRelay, requestId, outcome.Detail);
            }

            _incomingPairings.Remove(requestId);
            pending.Dispose();
        }

        if (keys is { } confirmedKeys)
        {
            var acceptedArgs = new FriendAcceptedEventArgs(
                request.RequesterPetId,
                request.RequesterOwnerName,
                request.RequesterName,
                request.Appearance,
                confirmedKeys.FriendKey,
                "GCP",
                ProtocolConstants.TcpControlPort,
                requestId,
                isLocalAccepter: true);
            FriendAccepted?.Invoke(this, acceptedArgs);
            return FriendOperationResult.Ok(FriendOperationStage.LocalPersistence, PeerTransportKind.GcpRelay, requestId, acceptedArgs);
        }

        return FriendOperationResult.Ok(FriendOperationStage.Completed, PeerTransportKind.GcpRelay, requestId);
    }

    private void CleanupOutgoingPairing(string requestId)
    {
        lock (_pairingLock)
        {
            if (_outgoingPairings.Remove(requestId, out var pending))
            {
                pending.Dispose();
            }
        }
    }

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

        return await SendEncryptedFriendPacketDetailedAsync(
            friend, PacketType.BubbleMessage, new BubbleMessagePayload { Text = trimmed }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default)
        => (await SendQuickReactionDetailedAsync(friend, reactionId, cancellationToken).ConfigureAwait(false)).Succeeded;

    public Task<TransportSendResult> SendQuickReactionDetailedAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default)
    {
        if (_localPresence.IsFocusing)
        {
            return Task.FromResult(TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked));
        }

        return SendEncryptedFriendPacketDetailedAsync(
            friend, PacketType.QuickReaction, new QuickReactionPayload { ReactionId = reactionId }, cancellationToken);
    }

    public SnowballPeerStatus GetSnowballPeerStatus(string petId)
    {
        if (!IsRemoteConnected) return SnowballPeerStatus.Offline;
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
        return SendEncryptedFriendPacketDetailedAsync(friend, PacketType.PetInteraction,
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

        return SendEncryptedFriendPacketDetailedAsync(
            friend, PacketType.PetInteraction, new PetInteractionPayload { InteractionType = interactionType }, cancellationToken);
    }

    public async Task<bool> SetMusicFollowAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default)
        => (await SetMusicFollowDetailedAsync(hostFriend, enable, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<TransportSendResult> SetMusicFollowDetailedAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default)
    {
        if (!enable)
        {
            _musicReceiveWindows.TryRemove(hostFriend.PetId, out var prev);
            return await SendEncryptedFriendPacketDetailedAsync(
                hostFriend,
                PacketType.MusicFollowOff,
                new MusicFollowPayload { SubscriptionId = prev?.SubscriptionId ?? string.Empty },
                cancellationToken).ConfigureAwait(false);
        }

        if (_localPresence.IsFocusing)
        {
            return TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked);
        }

        var session = new LanP2pTransport.MusicSequenceWindow(Guid.NewGuid().ToString("N"));
        _musicReceiveWindows[hostFriend.PetId] = session;
        var result = await SendEncryptedFriendPacketDetailedAsync(
            hostFriend,
            PacketType.MusicFollowOn,
            new MusicFollowPayload { ListenerUdpPort = 0, SubscriptionId = session.SubscriptionId },
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _musicReceiveWindows.TryRemove(new KeyValuePair<string, LanP2pTransport.MusicSequenceWindow>(hostFriend.PetId, session));
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

        return SendEncryptedFriendPacketDetailedAsync(friend, PacketType.GameEvent, payload!, cancellationToken);
    }

    public async ValueTask PublishMusicFrameAsync(MusicFrameData frame, CancellationToken cancellationToken = default)
    {
        var hub = _hub;
        if (!IsRunning || hub is null || hub.State != HubConnectionState.Connected || _musicSubscribers.IsEmpty || _localPresence.IsFocusing)
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
                    TargetPetId = kv.Key,
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
                options.AccessTokenProvider = () => GetAccessTokenAsync(_cts?.Token ?? CancellationToken.None);
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
            dto =>
            {
                if (!string.IsNullOrWhiteSpace(dto?.PetId))
                {
                    _peerPushSequence[dto.PetId] = Interlocked.Increment(ref _presencePushSequence);
                }

                ApplyOnlinePeerDto(dto!, raiseEvent: true);
            });

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

        hub.On<string>(
            SocialHubConstants.ReceiveFriendRemovedClientMethod,
            petId =>
            {
                if (!string.IsNullOrWhiteSpace(petId))
                {
                    ApplyLocalFriendRemovalCleanup(petId.Trim());
                }
            });

        hub.Reconnecting += _ =>
        {
            _peers.Clear();
            _musicSubscribers.Clear();
            RaisePeersChanged();
            return Task.CompletedTask;
        };

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
            if (IsUnauthorizedException(ex))
            {
                InvalidateCachedToken();
            }

            var errCode = _lastAuthErrorCode ?? FriendOperationErrorCodes.GcpNotConnected;
            var detail = SanitizeSensitiveText(_lastAuthErrorMessage ?? ex.Message);
            TransportError?.Invoke(this, new TransportErrorEventArgs("GcpConnect", detail, ex, errCode, PeerTransportKind.GcpRelay));
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
                    continue;
                }

                if (hub.State == HubConnectionState.Connected && !string.IsNullOrEmpty(_cachedAccessToken))
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now >= _tokenExpiresAtUtc)
                    {
                        InvalidateCachedToken();
                        try { await hub.StopAsync(ct).ConfigureAwait(false); } catch { }
                        await TryConnectOnceAsync(hub, ct).ConfigureAwait(false);
                    }
                    else if (now + TimeSpan.FromMinutes(2) >= _tokenExpiresAtUtc)
                    {
                        await GetAccessTokenAsync(ct, forceRefresh: true).ConfigureAwait(false);
                    }
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
            await RefreshServerBootInstanceAsync(ct).ConfigureAwait(false);
            var update = BuildPresenceUpdate();
            var list = await hub.InvokeAsync<List<RemoteFriendStateDto>>(
                SocialHubConstants.RegisterPresenceMethod, update, ct).ConfigureAwait(false);
            foreach (var syncedRemovedId in update.RemovedFriendPetIds)
            {
                _pendingRemovedFriends.TryRemove(syncedRemovedId, out _);
            }
            ReplaceAllRemotePeers(list);
        }
        catch (Exception ex)
        {
            if (IsUnauthorizedException(ex))
            {
                InvalidateCachedToken();
            }

            TransportError?.Invoke(this, new TransportErrorEventArgs(
                "GcpRegisterPresence", SanitizeSensitiveText(ex.Message), ex, FriendOperationErrorCodes.NetworkError, PeerTransportKind.GcpRelay));
        }
    }

    private async Task RefreshServerBootInstanceAsync(CancellationToken ct)
    {
        if (_baseUri is null)
        {
            return;
        }

        try
        {
            using var client = _httpHandlerFactory is not null
                ? new HttpClient(_httpHandlerFactory(), disposeHandler: true) { BaseAddress = _baseUri }
                : new HttpClient { BaseAddress = _baseUri };
            client.Timeout = TimeSpan.FromSeconds(2);

            var res = await client.GetAsync(SocialHubConstants.HealthPath, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return;
            }

            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("bootId", out var bootProp) || bootProp.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var newBootId = bootProp.GetString();
            if (string.IsNullOrWhiteSpace(newBootId))
            {
                return;
            }

            var prev = _lastServerBootId;
            _lastServerBootId = newBootId;
            if (prev is not null && !string.Equals(prev, newBootId, StringComparison.Ordinal))
            {
                // 서버 재시작으로 인메모리 대기 요청이 초기화되었으므로 미완료 발신 요청을 정리해 재신청이 가능하게 한다.
                lock (_pairingLock)
                {
                    foreach (var kv in _outgoingPairings.Where(kv => kv.Value.ConfirmationKey is null).ToArray())
                    {
                        _outgoingPairings.Remove(kv.Key);
                        kv.Value.Dispose();
                    }
                }
            }
        }
        catch
        {
            // /health 일시 조회 실패는 프레즌스 등록 흐름을 막지 않음
        }
    }

    private async Task SyncKnownFriendsWithServerAsync(HubConnection hub, List<string> friendIds, IDisposable operation, CancellationToken ct)
    {
        using var work = operation;
        try
        {
            var seqBeforeSync = Interlocked.Read(ref _presencePushSequence);
            var list = await hub.InvokeAsync<List<RemoteFriendStateDto>>(
                SocialHubConstants.SyncKnownFriendsMethod, friendIds, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) { ReplaceAllRemotePeers(list, seqBeforeSync); }
        }
        catch
        {
            // 연결 끊김 시 Reconnected에서 자동 재동기화
        }
    }

    private Task<string?> GetAccessTokenAsync(CancellationToken ct)
        => GetAccessTokenAsync(ct, forceRefresh: false);

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh
                && !string.IsNullOrEmpty(_cachedAccessToken)
                && DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2) < _tokenExpiresAtUtc)
            {
                return _cachedAccessToken;
            }

            if (_baseUri is null || _deviceKey is null || string.IsNullOrEmpty(_devicePublicKeyBase64))
            {
                _lastAuthErrorCode = FriendOperationErrorCodes.AuthFailed;
                _lastAuthErrorMessage = "기기 인증 키가 준비되지 않았습니다.";
                TransportError?.Invoke(this, new TransportErrorEventArgs(
                    "GcpAuth", _lastAuthErrorMessage, null, _lastAuthErrorCode, PeerTransportKind.GcpRelay));
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
                InvalidateCachedToken();
                var errCode = await TryReadServerErrorCodeAsync(challengeRes, ct).ConfigureAwait(false)
                    ?? FriendOperationErrorCodes.AuthFailed;
                _lastAuthErrorCode = errCode;
                _lastAuthErrorMessage = $"GCP 인증 챌린지 요청 실패 ({(int)challengeRes.StatusCode} / {errCode})";
                TransportError?.Invoke(this, new TransportErrorEventArgs(
                    "GcpAuth", _lastAuthErrorMessage, null, errCode, PeerTransportKind.GcpRelay));
                return null;
            }

            var challenge = await challengeRes.Content.ReadFromJsonAsync<AuthChallengeResponse>(
                ProtocolJson.PacketOptions, ct).ConfigureAwait(false);
            if (challenge is null || string.IsNullOrEmpty(challenge.Nonce))
            {
                InvalidateCachedToken();
                _lastAuthErrorCode = FriendOperationErrorCodes.AuthFailed;
                _lastAuthErrorMessage = "GCP 서버 인증 챌린지 응답이 비어 있습니다.";
                TransportError?.Invoke(this, new TransportErrorEventArgs(
                    "GcpAuth", _lastAuthErrorMessage, null, _lastAuthErrorCode, PeerTransportKind.GcpRelay));
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
                InvalidateCachedToken();
                var errCode = await TryReadServerErrorCodeAsync(tokenRes, ct).ConfigureAwait(false)
                    ?? FriendOperationErrorCodes.AuthFailed;
                _lastAuthErrorCode = errCode;
                _lastAuthErrorMessage = $"GCP 기기 인증 토큰 발급 거부 ({(int)tokenRes.StatusCode} / {errCode})";
                TransportError?.Invoke(this, new TransportErrorEventArgs(
                    "GcpAuth", _lastAuthErrorMessage, null, errCode, PeerTransportKind.GcpRelay));
                return null;
            }

            var auth = await tokenRes.Content.ReadFromJsonAsync<AuthTokenResponse>(
                ProtocolJson.PacketOptions, ct).ConfigureAwait(false);
            if (auth is null || string.IsNullOrWhiteSpace(auth.AccessToken) || auth.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                InvalidateCachedToken();
                _lastAuthErrorCode = FriendOperationErrorCodes.AuthFailed;
                _lastAuthErrorMessage = "GCP 서버에서 유효하지 않은 액세스 토큰을 반환했습니다.";
                TransportError?.Invoke(this, new TransportErrorEventArgs(
                    "GcpAuth", _lastAuthErrorMessage, null, _lastAuthErrorCode, PeerTransportKind.GcpRelay));
                return null;
            }

            _cachedAccessToken = auth.AccessToken;
            _tokenExpiresAtUtc = auth.ExpiresAtUtc;
            _lastAuthErrorCode = null;
            _lastAuthErrorMessage = null;
            return _cachedAccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (IsUnauthorizedException(ex))
            {
                InvalidateCachedToken();
            }

            var safeMsg = SanitizeSensitiveText(ex.Message);
            _lastAuthErrorCode = FriendOperationErrorCodes.NetworkError;
            _lastAuthErrorMessage = safeMsg;
            TransportError?.Invoke(this, new TransportErrorEventArgs(
                "GcpAuth", $"GCP 인증 서버 통신 오류: {safeMsg}", ex, FriendOperationErrorCodes.NetworkError, PeerTransportKind.GcpRelay));
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void InvalidateCachedToken()
    {
        _cachedAccessToken = null;
        _tokenExpiresAtUtc = DateTimeOffset.MinValue;
    }

    private static bool IsUnauthorizedException(Exception ex)
        => ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
           || (ex.Message?.Contains("401", StringComparison.Ordinal) == true
               && ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase));

    internal static string SanitizeSensitiveText(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var masked = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(?i)\bBearer\s+[A-Za-z0-9\-_=+/\.]+",
            "Bearer [REDACTED]");
        return System.Text.RegularExpressions.Regex.Replace(
            masked,
            @"(?i)(access_token\s*[=:]\s*)[^\s&""']+",
            "$1[REDACTED]");
    }

    private static async Task<string?> TryReadServerErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
            {
                return errProp.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private async Task<TransportSendResult> SendEncryptedFriendPacketDetailedAsync<TPayload>(
        FriendEntry friend,
        PacketType packetType,
        TPayload payload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(friend.PairSecretBase64))
        {
            return TransportSendResult.Fail();
        }

        var outcome = await SendControlRelayWithOutcomeAsync(friend.PetId, packetType, payload, friend.PairSecretBase64, ct)
            .ConfigureAwait(false);
        return outcome.VerifiedAck is { Accepted: true }
            ? TransportSendResult.Ok
            : TransportSendResult.Fail(outcome.VerifiedAck?.ErrorCode ?? outcome.ErrorCode);
    }

    private async Task<AckPayload?> SendControlRelayAsync<TPayload>(
        string targetPetId,
        PacketType packetType,
        TPayload payload,
        string? pairSecretBase64,
        CancellationToken ct,
        string? confirmationKey = null)
    {
        var outcome = await SendControlRelayWithOutcomeAsync(
            targetPetId, packetType, payload, pairSecretBase64, ct, confirmationKey).ConfigureAwait(false);
        return outcome.VerifiedAck;
    }

    private async Task<LanP2pTransport.ControlSendOutcome> SendControlRelayWithOutcomeAsync<TPayload>(
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
            var errCode = _lastAuthErrorCode ?? FriendOperationErrorCodes.GcpNotConnected;
            var detail = _lastAuthErrorMessage ?? "GCP 중계 서버에 연결되어 있지 않습니다.";
            return new LanP2pTransport.ControlSendOutcome(null, errCode, FriendOperationErrorCodes.IsTransient(errCode), detail);
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

            using var invokeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            invokeTimeoutCts.CancelAfter(RelayInvokeTimeout);

            var result = await hub.InvokeAsync<RelayDeliveryResult>(
                SocialHubConstants.RelayControlPacketMethod,
                new RelayControlEnvelopeRequest(targetPetId, envelope),
                invokeTimeoutCts.Token).ConfigureAwait(false);

            if (result is null)
            {
                return new LanP2pTransport.ControlSendOutcome(null, FriendOperationErrorCodes.DeliveryFailedOrTimeout, true, "GCP 중계 응답이 비어 있습니다.");
            }

            if (!result.Delivered)
            {
                var errCode = string.IsNullOrWhiteSpace(result.ErrorCode)
                    ? FriendOperationErrorCodes.DeliveryFailedOrTimeout
                    : result.ErrorCode;
                var isTransient = FriendOperationErrorCodes.IsTransient(errCode);
                TransportError?.Invoke(this, new TransportErrorEventArgs(
                    $"GcpSend:{packetType}",
                    $"GCP 중계 실패 ({errCode})",
                    null,
                    errCode,
                    PeerTransportKind.GcpRelay));
                return new LanP2pTransport.ControlSendOutcome(null, errCode, isTransient, errCode);
            }

            if (result.AckEnvelope is null)
            {
                return new LanP2pTransport.ControlSendOutcome(null, FriendOperationErrorCodes.DeliveryFailedOrTimeout, true, "수신자 ACK 응답이 누락되었습니다.");
            }

            return LanP2pTransport.InspectAckEnvelope(
                result.AckEnvelope,
                header.MessageId,
                targetPetId,
                confirmationKey ?? pairSecretBase64,
                allowUnsignedRejection: packetType == PacketType.FriendAccept);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new LanP2pTransport.ControlSendOutcome(null, FriendOperationErrorCodes.Cancelled, false, "요청이 취소되었습니다.");
        }
        catch (OperationCanceledException ex)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs(
                $"GcpSend:{packetType}",
                SanitizeSensitiveText(ex.Message),
                ex,
                FriendOperationErrorCodes.DeliveryFailedOrTimeout,
                PeerTransportKind.GcpRelay));
            return new LanP2pTransport.ControlSendOutcome(null, FriendOperationErrorCodes.DeliveryFailedOrTimeout, true, "GCP 중계 응답 시간이 초과되었습니다.");
        }
        catch (Exception ex)
        {
            if (IsUnauthorizedException(ex))
            {
                InvalidateCachedToken();
            }

            var safeMsg = SanitizeSensitiveText(ex.Message);
            TransportError?.Invoke(this, new TransportErrorEventArgs(
                $"GcpSend:{packetType}",
                safeMsg,
                ex,
                FriendOperationErrorCodes.NetworkError,
                PeerTransportKind.GcpRelay));
            return new LanP2pTransport.ControlSendOutcome(null, FriendOperationErrorCodes.NetworkError, true, safeMsg);
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
                if (envelope.IsEncrypted)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.InvalidPairingEnvelope);
                }

                if (_localPresence.IsFocusing)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.PomodoroFocusBlocked);
                }

                if (!ReceiveFriendRequest(envelope, out var requestError))
                {
                    return RejectAck(header.MessageId, requestError);
                }
                break;

            case PacketType.FriendAccept:
                if (envelope.IsEncrypted)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.InvalidPairingEnvelope);
                }

                if (!ReceiveFriendAcceptance(envelope, out ackKey, out var acceptError))
                {
                    return RejectAck(header.MessageId, acceptError);
                }
                break;

            case PacketType.FriendReject:
                if (envelope.IsEncrypted)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.InvalidPairingEnvelope);
                }

                if (!ReceiveFriendRejection(envelope, out var rejectError))
                {
                    return RejectAck(header.MessageId, rejectError);
                }
                break;

            case PacketType.BubbleMessage:
            {
                if (_localPresence.IsFocusing)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.PomodoroFocusBlocked);
                }

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
                if (_localPresence.IsFocusing)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.PomodoroFocusBlocked);
                }

                var p = JsonSerializer.Deserialize<QuickReactionPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (p is null) return RejectAck(header.MessageId, "INVALID_PAYLOAD");
                QuickReactionReceived?.Invoke(this, new QuickReactionReceivedEventArgs(header.SenderPetId, p.ReactionId));
                break;
            }

            case PacketType.PetInteraction:
            {
                if (_localPresence.IsFocusing)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.PomodoroFocusBlocked);
                }

                var p = JsonSerializer.Deserialize<PetInteractionPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (p?.InteractionType == PetInteractionPayload.PetType)
                {
                    PetInteractionReceived?.Invoke(this, new PetInteractionReceivedEventArgs(header.SenderPetId, p.InteractionType));
                    break;
                }

                if (p?.InteractionType != PetInteractionPayload.SnowballType
                    || !Guid.TryParseExact(p.EventId, "N", out var eventId) || eventId == Guid.Empty
                    || !PetInteractionPayload.SupportsSnowball(header.AppVersion))
                    return RejectAck(header.MessageId, "INVALID_INTERACTION");

                SnowballReceived?.Invoke(this, new SnowballReceivedEventArgs(header.SenderPetId, eventId, header.TimestampUtc));
                break;
            }

            case PacketType.MusicFollowOn:
            {
                if (_localPresence.IsFocusing)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.PomodoroFocusBlocked);
                }

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

                if (_localPresence.IsFocusing)
                {
                    if (p.Action is "cancel" or "decline")
                    {
                        break;
                    }

                    return RejectAck(header.MessageId, FriendOperationErrorCodes.PomodoroFocusBlocked);
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
            IsFocusing = presence.IsFocusing,
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
                IsFocusing = local.IsFocusing,
                TcpPort = ProtocolConstants.TcpControlPort,
                AppVersion = local.AppVersion
            }
        };
    }

    private void HandleIncomingMusicFrame(MusicFramePacket packet)
    {
        if (packet is null
            || string.IsNullOrWhiteSpace(packet.SenderPetId)
            || packet.SenderPetId.Length > 64
            || packet.SubscriptionId.Length > 64
            || string.IsNullOrWhiteSpace(packet.SignatureBase64)
            || packet.SignatureBase64.Length > 88
            || !float.IsFinite(packet.Bass)
            || !float.IsFinite(packet.Mid)
            || !float.IsFinite(packet.Treble)
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

    private bool ReceiveFriendRequest(ControlPacketEnvelope envelope, out string errorCode)
    {
        errorCode = string.Empty;
        var header = envelope.Header;
        var payload = JsonSerializer.Deserialize<FriendRequestPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _)
            || !PacketCrypto.IsPairingPublicKey(payload.PublicKey))
        {
            errorCode = FriendOperationErrorCodes.InvalidPairingRequest;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + PairingLifetime;
        var request = new FriendRequestReceivedEventArgs(
            payload.RequestId,
            header.SenderPetId,
            payload.RequesterOwnerName ?? string.Empty,
            payload.RequesterName,
            payload.Appearance ?? new PetAppearance(),
            payload.Level,
            "GCP",
            ProtocolConstants.TcpControlPort,
            PeerTransportKind.GcpRelay,
            expiresAt);

        lock (_pairingLock)
        {
            PrunePairings(now);
            if (_friendsByPetId.ContainsKey(header.SenderPetId))
            {
                errorCode = FriendOperationErrorCodes.AlreadyFriends;
                return false;
            }

            if (_incomingPairings.ContainsKey(payload.RequestId)
                || _incomingPairings.Values.Any(p => SamePetId(p.Request.RequesterPetId, header.SenderPetId)))
            {
                errorCode = FriendOperationErrorCodes.DuplicatePendingRequest;
                return false;
            }

            if (_incomingPairings.Count >= MaxPendingPairings)
            {
                errorCode = FriendOperationErrorCodes.PendingCapacityFull;
                return false;
            }

            _incomingPairings.Add(payload.RequestId, new IncomingPairing(request, payload.PublicKey, Fingerprint(envelope.PayloadJson), expiresAt));
        }

        FriendRequestReceived?.Invoke(this, request);
        return true;
    }

    private bool ReceiveFriendAcceptance(ControlPacketEnvelope envelope, out string? confirmationKey, out string errorCode)
    {
        confirmationKey = null;
        errorCode = string.Empty;
        var header = envelope.Header;
        var payload = JsonSerializer.Deserialize<FriendAcceptPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _))
        {
            errorCode = FriendOperationErrorCodes.InvalidPairingRequest;
            return false;
        }

        string friendKey;
        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (!_outgoingPairings.TryGetValue(payload.RequestId, out var pending))
            {
                errorCode = _expiredOutgoingRequestIds.ContainsKey(payload.RequestId)
                    ? FriendOperationErrorCodes.PairingRequestExpired
                    : FriendOperationErrorCodes.PairingRequestUnknown;
                return false;
            }

            if (!SamePetId(pending.PeerId, header.SenderPetId))
            {
                errorCode = FriendOperationErrorCodes.PairingRequestUnknown;
                return false;
            }

            if (pending.ConfirmationKey is { } completedKey)
            {
                if (pending.ConfirmedPublicKey != payload.PublicKey || !VerifyPlaintextSignature(envelope, completedKey))
                {
                    errorCode = FriendOperationErrorCodes.PairingConfirmationFailed;
                    return false;
                }

                confirmationKey = completedKey;
                return true;
            }

            if (_friendsByPetId.ContainsKey(header.SenderPetId))
            {
                errorCode = FriendOperationErrorCodes.AlreadyFriends;
                return false;
            }

            var keys = PacketCrypto.DerivePairingKeys(
                pending.Key, payload.PublicKey, payload.RequestId,
                _localPetId, header.SenderPetId, pending.PublicKey, payload.PublicKey,
                pending.RequestFingerprint);
            if (keys is not { } derived || !VerifyPlaintextSignature(envelope, derived.ConfirmationKey))
            {
                errorCode = FriendOperationErrorCodes.PairingConfirmationFailed;
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
            ProtocolConstants.TcpControlPort,
            payload.RequestId,
            isLocalAccepter: false));
        return true;
    }

    private bool ReceiveFriendRejection(ControlPacketEnvelope envelope, out string errorCode)
    {
        errorCode = string.Empty;
        var payload = JsonSerializer.Deserialize<FriendRejectPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _))
        {
            errorCode = FriendOperationErrorCodes.InvalidPairingRequest;
            return false;
        }

        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (!_outgoingPairings.TryGetValue(payload.RequestId, out var pending))
            {
                errorCode = _expiredOutgoingRequestIds.ContainsKey(payload.RequestId)
                    ? FriendOperationErrorCodes.PairingRequestExpired
                    : FriendOperationErrorCodes.PairingRequestUnknown;
                return false;
            }

            if (!SamePetId(pending.PeerId, envelope.Header.SenderPetId))
            {
                errorCode = FriendOperationErrorCodes.PairingRequestUnknown;
                return false;
            }

            if (pending.ConfirmationKey is not null)
            {
                errorCode = FriendOperationErrorCodes.PairingAlreadyCompleted;
                return false;
            }

            _outgoingPairings.Remove(payload.RequestId);
            pending.Dispose();
        }

        FriendRejected?.Invoke(this, new FriendRejectedEventArgs(envelope.Header.SenderPetId, payload.Reason, payload.RequestId));
        return true;
    }

    private void ReplaceAllRemotePeers(IReadOnlyList<RemoteFriendStateDto> list, long? preservePushesAfterSequence = null)
    {
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in list)
        {
            if (string.IsNullOrWhiteSpace(dto.PetId) || SamePetId(dto.PetId, _localPetId))
            {
                continue;
            }

            activeIds.Add(dto.PetId);
            if (preservePushesAfterSequence is { } minSeq
                && _peerPushSequence.TryGetValue(dto.PetId, out var pushedSeq)
                && pushedSeq > minSeq)
            {
                continue;
            }

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
            IsFocusing = isFriend && dto.IsFocusing,
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
            IsFocusing = local.IsFocusing,
            AppVersion = local.AppVersion,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            KnownFriendPetIds = _friendsByPetId.Keys.ToList(),
            RemovedFriendPetIds = _pendingRemovedFriends.Keys.Take(200).ToList()
        };
    }

    private void PrunePairings(DateTimeOffset now)
    {
        foreach (var item in _outgoingPairings.Where(i => i.Value.ExpiresAt <= now).ToArray())
        {
            _outgoingPairings.Remove(item.Key);
            item.Value.Dispose();
            RecordExpiredTombstone(_expiredOutgoingRequestIds, item.Key, now);
        }

        foreach (var item in _incomingPairings.Where(i => i.Value.ExpiresAt <= now && !i.Value.IsResponding).ToArray())
        {
            _incomingPairings.Remove(item.Key);
            item.Value.Dispose();
            RecordExpiredTombstone(_expiredIncomingRequestIds, item.Key, now);
        }

        PruneExpiredTombstones(_expiredOutgoingRequestIds, now);
        PruneExpiredTombstones(_expiredIncomingRequestIds, now);
    }

    private static void RecordExpiredTombstone(Dictionary<string, DateTimeOffset> map, string requestId, DateTimeOffset now)
    {
        if (map.Count >= MaxExpiredRequestTombstones)
        {
            foreach (var oldKey in map.OrderBy(kv => kv.Value).Take(map.Count - MaxExpiredRequestTombstones + 16).Select(kv => kv.Key).ToArray())
            {
                map.Remove(oldKey);
            }
        }

        map[requestId] = now + ExpiredTombstoneLifetime;
    }

    private static void PruneExpiredTombstones(Dictionary<string, DateTimeOffset> map, DateTimeOffset now)
    {
        foreach (var kv in map.Where(kv => kv.Value <= now).ToArray())
        {
            map.Remove(kv.Key);
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
