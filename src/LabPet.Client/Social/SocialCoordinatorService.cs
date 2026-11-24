using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.Social;

public enum OutgoingFriendRequestState
{
    None = 0,
    Sending = 1,
    Pending = 2
}

public sealed record PendingFriendRequest(
    string RequestId,
    string RequesterPetId,
    string RequesterOwnerName,
    string RequesterName,
    PetAppearance Appearance,
    int Level,
    string RemoteIp,
    int RemoteTcpPort,
    PeerTransportKind TransportKind = PeerTransportKind.Lan,
    DateTimeOffset ExpiresAtUtc = default)
{
    public string DisplayName => SocialCoordinatorService.FormatDisplayName(RequesterOwnerName, RequesterName);
    public bool IsExpired => ExpiresAtUtc != default && DateTimeOffset.UtcNow >= ExpiresAtUtc;
}

public sealed record ReceivedFriendMessage(
    string SenderPetId,
    string SenderName,
    string Text,
    PetAppearance Appearance,
    string MessageId = "");

internal sealed record FriendStatusNotice(string FriendPetId, string Text, int DurationMs, bool AnimateHost = true);

public enum PetInteractionSendResult
{
    Unavailable,
    Sent,
    Cooldown
}

/// <summary>
/// ISocialTransport, friends.json 저장소, 보너스 코인, 노래 따라하기(팔로워 타임아웃·우선순위)를 조정하는 소셜 코디네이터.
/// ViewModel과 View는 구체 전송 구현체가 아닌 ISocialTransport/SocialCoordinatorService만 의존한다.
/// </summary>
public sealed class SocialCoordinatorService : IAsyncDisposable
{
    private readonly ISocialTransport _transport;
    private readonly IAppDataStore _store;
    private readonly SoloProgressionService _progression;

    private readonly AsyncOperationLifetime _operations = new();
    private readonly Lazy<Task> _shutdown;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _musicLock = new();
    private readonly object _friendStateLock = new();
    private readonly object _pairingWorkflowLock = new();
    private readonly SemaphoreSlim _friendSaveLock = new(1, 1);

    private readonly Dictionary<string, OutgoingFriendRequestState> _outgoingRequestStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _respondingRequestIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FriendEntry> _pendingUnsavedAccepts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTransportNotifyAt = new(StringComparer.OrdinalIgnoreCase);

    private volatile PetProfile _localProfile = new();
    private volatile FriendEntry[] _friends = [];
    private uint _musicFrameSequence;
    private bool _isLocalSleeping;
    private long _nextPetInteractionTicks;
    private Task _lastRemoteAcceptPersistTask = Task.CompletedTask;

    // 노래 따라하기 팔로워 상태
    private string? _followingHostPetId;
    private MusicFrameData _lastFollowedFrame;
    private long _lastFollowedPacketTicks;
    private long _lastFollowedActiveSoundTicks;

    /// <summary>내 음악이 동시에 재생 중일 때 팔로우 대신 로컬 음악을 우선할지 여부.</summary>
    public bool PreferLocalMusicWhenActive { get; set; } = true;

    public bool IsLocalSleeping => _isLocalSleeping;
    public bool IsRemoteConnected => _transport.IsRemoteConnected;
    public string ConnectionSummary => _transport.ConnectionSummary;
    public string? LastTransportErrorMessage { get; private set; }
    public string? FollowingHostPetId => _followingHostPetId;
    public IReadOnlyList<FriendEntry> Friends => Array.ConvertAll(_friends, static friend => friend.Clone());
    public IReadOnlyList<PeerSnapshot> DiscoveredPeers => _transport.GetDiscoveredPeers();
    internal Task LastRemoteAcceptPersistTask => _lastRemoteAcceptPersistTask;

    public event EventHandler? StateChanged;
    public event EventHandler<PendingFriendRequest>? FriendRequestArrived;
    public event EventHandler<ReceivedFriendMessage>? FriendMessageReceived;
    internal event EventHandler<FriendStatusNotice>? FriendStatusNotification;
    public event EventHandler<string>? SocialNotification;
    public event EventHandler<TransportErrorEventArgs>? TransportErrorReported;
    public event EventHandler? PetReactionTriggered;
    public event EventHandler<string>? NewerAppVersionDetected;
    public event EventHandler<GameEventReceivedEventArgs>? GameEventReceived;

    public SocialCoordinatorService(
        ISocialTransport transport,
        IAppDataStore store,
        SoloProgressionService progression)
    {
        _transport = transport;
        _store = store;
        _progression = progression;
        _shutdown = new Lazy<Task>(ShutdownAsync);

        _transport.PeersChanged += OnPeersChanged;
        _transport.FriendRequestReceived += OnFriendRequestReceived;
        _transport.FriendAccepted += OnFriendAccepted;
        _transport.FriendRejected += OnFriendRejected;
        _transport.BubbleMessageReceived += OnBubbleMessageReceived;
        _transport.QuickReactionReceived += OnQuickReactionReceived;
        _transport.PetInteractionReceived += OnPetInteractionReceived;
        _transport.MusicFrameReceived += OnMusicFrameReceived;
        _transport.GameEventReceived += OnGameEventReceived;
        _transport.TransportError += OnTransportError;
    }

    public async Task StartAsync(PetProfile localProfile, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return;
        }

        _localProfile = localProfile;
        FriendsStore friendsStore;
        try
        {
            friendsStore = await _store.LoadFriendsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("친구 목록 로드 실패: {0}", ex);
            SocialNotification?.Invoke(this, "친구 데이터를 열거나 보호하지 못했습니다. 기존 파일을 보존하고 LAN 연결을 중단했습니다.");
            return;
        }
        _friends = [.. friendsStore.Friends];
        await _transport.StartAsync(_localProfile, _friends, cancellationToken).ConfigureAwait(false);
        if (friendsStore.UnreadableProtectedKeys > 0)
        {
            SocialNotification?.Invoke(this, "일부 친구 키를 열 수 없습니다. 원래 키를 저장한 Windows 계정에서 복구해 주세요.");
        }
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

    public async Task UpdateLocalPresenceAsync(
        PetProfile profile,
        bool isMusicActive,
        bool? isSleeping = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return;
        }

        _localProfile = profile;
        if (isSleeping is { } sleepState)
        {
            _isLocalSleeping = sleepState;
        }

        var version = typeof(SocialCoordinatorService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        await _transport.UpdateLocalPresenceAsync(
            new LocalPresenceState(
                profile.OwnerName,
                profile.PetName,
                profile.Appearance.Clone(),
                profile.SoloProgress.Level,
                isMusicActive,
                version,
                _isLocalSleeping,
                profile.FriendCode),
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
    /// 곡 간 전환·잠시 일시정지 중에는 팔로우를 끊지 않고 유지하며,
    /// 호스트 패킷이 15초 이상 끊기거나 3분 이상 완전 무음일 때만 로컬 모드로 자동 복귀한다.
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
            else if (PreferLocalMusicWhenActive && isLocalMusicActive && sinceSoundMs > 1200.0)
            {
                // 호스트가 조용한 동안 내 PC에서 음악이 재생 중이면 로컬 오디오 사용 (팔로우 연결 자체는 유지)
                frame = default;
                return false;
            }
            else
            {
                // 일시적으로 패킷이 지연(350ms 초과)되거나 호스트가 잠시 일시정지한 동안에는 마지막 피크값 고정 대신 0(대기 호흡)으로 부드럽게 처리
                frame = sincePacketMs > 350.0 ? default : _lastFollowedFrame;
                return true;
            }
        }

        if (timedOutHost is not null)
        {
            var hostFriend = FindFriend(timedOutHost);
            if (hostFriend is not null)
            {
                _ = ObserveIncomingAsync(() => _transport.SetMusicFollowAsync(hostFriend, enable: false));
            }

            var hostName = FindFriendName(timedOutHost);
            SocialNotification?.Invoke(this, $"{hostName}의 음악이 멈춰 로컬 모드로 복귀했어요.");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return false;
    }

    public OutgoingFriendRequestState GetOutgoingFriendRequestState(string petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
        {
            return OutgoingFriendRequestState.None;
        }

        lock (_pairingWorkflowLock)
        {
            if (!_outgoingRequestStates.TryGetValue(petId, out var state))
            {
                return OutgoingFriendRequestState.None;
            }

            if (state == OutgoingFriendRequestState.Pending && !_transport.HasPendingOutgoingRequest(petId))
            {
                _outgoingRequestStates.Remove(petId);
                return OutgoingFriendRequestState.None;
            }

            return state;
        }
    }

    public async Task<bool> SendFriendRequestAsync(PeerSnapshot peer)
        => (await SendFriendRequestDetailedAsync(peer).ConfigureAwait(false)).Succeeded;

    public async Task<FriendOperationResult> SendFriendRequestDetailedAsync(
        PeerSnapshot peer,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Sending, FriendOperationErrorCodes.Cancelled, isTransient: false);
        }

        if (peer is null || string.IsNullOrWhiteSpace(peer.PetId))
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.InvalidRequest, false);
        }

        if (peer.RequiresUpdate)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.ProtocolMismatch, false, peer.TransportKind);
        }

        lock (_friendStateLock)
        {
            if (FindSharedFriend(peer.PetId) is not null)
            {
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.AlreadyFriends, false, peer.TransportKind);
            }
        }

        lock (_pairingWorkflowLock)
        {
            if (_outgoingRequestStates.TryGetValue(peer.PetId, out var existingState))
            {
                if (existingState == OutgoingFriendRequestState.Pending && !_transport.HasPendingOutgoingRequest(peer.PetId))
                {
                    _outgoingRequestStates.Remove(peer.PetId);
                }
                else
                {
                    var errCode = existingState == OutgoingFriendRequestState.Sending
                        ? FriendOperationErrorCodes.RequestInProgress
                        : FriendOperationErrorCodes.DuplicatePendingRequest;
                    return FriendOperationResult.Fail(FriendOperationStage.Validation, errCode, false, peer.TransportKind);
                }
            }

            _outgoingRequestStates[peer.PetId] = OutgoingFriendRequestState.Sending;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);

        var sw = Stopwatch.StartNew();
        FriendOperationResult result;
        try
        {
            result = await _transport.SendFriendRequestDetailedAsync(peer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = FriendOperationResult.Fail(FriendOperationStage.Sending, FriendOperationErrorCodes.Cancelled, false, peer.TransportKind);
        }
        catch (Exception ex)
        {
            result = FriendOperationResult.Fail(FriendOperationStage.Sending, FriendOperationErrorCodes.NetworkError, true, peer.TransportKind, detail: ex.Message);
        }

        lock (_pairingWorkflowLock)
        {
            if (result.Succeeded)
            {
                _outgoingRequestStates[peer.PetId] = OutgoingFriendRequestState.Pending;
            }
            else if (result.ErrorCode == FriendOperationErrorCodes.DuplicatePendingRequest)
            {
                _outgoingRequestStates[peer.PetId] = OutgoingFriendRequestState.Pending;
            }
            else
            {
                _outgoingRequestStates.Remove(peer.PetId);
            }
        }

        if (result.Succeeded)
        {
            Trace.TraceInformation(
                "친구 신청 전송 완료: RequestId={0}, Route={1}, Stage={2}, ElapsedMs={3}",
                result.RequestId ?? "-",
                result.Route?.ToString() ?? peer.TransportKind.ToString(),
                result.Stage,
                sw.ElapsedMilliseconds);
        }
        else
        {
            Trace.TraceWarning(
                "친구 신청 실패: RequestId={0}, Route={1}, Stage={2}, ErrorCode={3}, Transient={4}, ElapsedMs={5}",
                result.RequestId ?? "-",
                result.Route?.ToString() ?? peer.TransportKind.ToString(),
                result.Stage,
                result.ErrorCode ?? "-",
                result.IsTransient,
                sw.ElapsedMilliseconds);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task<bool> SendGameEventAsync(FriendEntry friend, GameEventPayload payload)
    {
        using var operation = _operations.TryBegin();
        return operation is not null && await _transport.SendGameEventAsync(
            FindFriend(friend.PetId) ?? friend, payload).ConfigureAwait(false);
    }

    public string GetFriendDisplayName(string petId) => FindFriendName(petId);

    public async Task<bool> RespondFriendRequestAsync(PendingFriendRequest request, bool accept)
        => (await RespondFriendRequestDetailedAsync(request, accept).ConfigureAwait(false)).Succeeded;

    public async Task<FriendOperationResult> RespondFriendRequestDetailedAsync(
        PendingFriendRequest request,
        bool accept,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Sending, FriendOperationErrorCodes.Cancelled, isTransient: false);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.InvalidRequest, false);
        }

        lock (_pairingWorkflowLock)
        {
            if (!_respondingRequestIds.Add(request.RequestId))
            {
                return FriendOperationResult.Fail(
                    FriendOperationStage.Validation,
                    FriendOperationErrorCodes.RequestInProgress,
                    false,
                    request.TransportKind,
                    request.RequestId,
                    "이미 친구 신청 응답을 처리 중입니다.");
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            FriendEntry? unsavedEntry = null;
            lock (_pairingWorkflowLock)
            {
                _pendingUnsavedAccepts.TryGetValue(request.RequestId, out unsavedEntry);
            }

            if (accept && unsavedEntry is not null)
            {
                var savedRetry = await PersistLocalAcceptedFriendAsync(unsavedEntry).ConfigureAwait(false);
                if (!savedRetry)
                {
                    Trace.TraceError(
                        "친구 수락 저장 재시도 실패: RequestId={0}, Route={1}, Stage=LocalPersistence, ErrorCode={2}, ElapsedMs={3}",
                        request.RequestId,
                        request.TransportKind,
                        FriendOperationErrorCodes.LocalStorageFailed,
                        sw.ElapsedMilliseconds);
                    return FriendOperationResult.Fail(
                        FriendOperationStage.LocalPersistence,
                        FriendOperationErrorCodes.LocalStorageFailed,
                        true,
                        request.TransportKind,
                        request.RequestId,
                        "친구 목록 파일 저장에 실패했습니다.");
                }

                lock (_pairingWorkflowLock)
                {
                    _pendingUnsavedAccepts.Remove(request.RequestId);
                }

                var retryDisplay = FormatDisplayName(unsavedEntry.OwnerName, unsavedEntry.PetName);
                SocialNotification?.Invoke(this, $"{retryDisplay}(이)가 친구 신청을 수락했어요!");
                PetReactionTriggered?.Invoke(this, EventArgs.Empty);
                return FriendOperationResult.Ok(FriendOperationStage.Completed, request.TransportKind, request.RequestId);
            }

            if (accept)
            {
                lock (_friendStateLock)
                {
                    if (FindSharedFriend(request.RequesterPetId) is not null)
                    {
                        return FriendOperationResult.Fail(
                            FriendOperationStage.Validation,
                            FriendOperationErrorCodes.AlreadyFriends,
                            false,
                            request.TransportKind,
                            request.RequestId);
                    }
                }
            }
            else
            {
                lock (_pairingWorkflowLock)
                {
                    _pendingUnsavedAccepts.Remove(request.RequestId);
                }
            }

            var transportResult = await _transport.RespondFriendRequestDetailedAsync(
                request.RequestId, accept, cancellationToken).ConfigureAwait(false);

            if (!transportResult.Succeeded)
            {
                Trace.TraceWarning(
                    "친구 요청 응답 실패: RequestId={0}, Route={1}, Stage={2}, ErrorCode={3}, Transient={4}, ElapsedMs={5}",
                    request.RequestId,
                    transportResult.Route?.ToString() ?? request.TransportKind.ToString(),
                    transportResult.Stage,
                    transportResult.ErrorCode ?? "-",
                    transportResult.IsTransient,
                    sw.ElapsedMilliseconds);
                return transportResult;
            }

            if (!accept)
            {
                Trace.TraceInformation(
                    "친구 신청 거절 완료: RequestId={0}, Route={1}, ElapsedMs={2}",
                    request.RequestId,
                    transportResult.Route?.ToString() ?? request.TransportKind.ToString(),
                    sw.ElapsedMilliseconds);
                return FriendOperationResult.Ok(
                    FriendOperationStage.Completed,
                    transportResult.Route ?? request.TransportKind,
                    request.RequestId);
            }

            var accepted = transportResult.AcceptedFriend;
            if (accepted is null)
            {
                return FriendOperationResult.Fail(
                    FriendOperationStage.AckVerification,
                    FriendOperationErrorCodes.PairingConfirmationFailed,
                    false,
                    transportResult.Route ?? request.TransportKind,
                    request.RequestId);
            }

            var entry = new FriendEntry
            {
                PetId = accepted.AccepterPetId,
                OwnerName = accepted.AccepterOwnerName,
                PetName = accepted.AccepterName,
                LastKnownIp = accepted.RemoteIp,
                LastKnownTcpPort = accepted.RemoteTcpPort,
                PairSecretBase64 = accepted.PairSecretBase64,
                CachedAppearance = accepted.Appearance.Clone(),
                Intimacy = 1,
                PairedAtUtc = DateTimeOffset.UtcNow,
                LastSeenAtUtc = DateTimeOffset.UtcNow
            };

            var saved = await PersistLocalAcceptedFriendAsync(entry).ConfigureAwait(false);
            if (!saved)
            {
                lock (_pairingWorkflowLock)
                {
                    _pendingUnsavedAccepts[request.RequestId] = entry;
                }

                Trace.TraceError(
                    "친구 수락 후 로컬 저장 실패로 메모리 롤백: RequestId={0}, Route={1}, Stage=LocalPersistence, ErrorCode={2}, ElapsedMs={3}",
                    request.RequestId,
                    transportResult.Route?.ToString() ?? request.TransportKind.ToString(),
                    FriendOperationErrorCodes.LocalStorageFailed,
                    sw.ElapsedMilliseconds);
                return FriendOperationResult.Fail(
                    FriendOperationStage.LocalPersistence,
                    FriendOperationErrorCodes.LocalStorageFailed,
                    true,
                    transportResult.Route ?? request.TransportKind,
                    request.RequestId,
                    "친구 목록 파일 저장에 실패했습니다.");
            }

            Trace.TraceInformation(
                "친구 수락 및 로컬 저장 완료: RequestId={0}, Route={1}, Stage=Completed, ElapsedMs={2}",
                request.RequestId,
                transportResult.Route?.ToString() ?? request.TransportKind.ToString(),
                sw.ElapsedMilliseconds);

            var display = FormatDisplayName(entry.OwnerName, entry.PetName);
            SocialNotification?.Invoke(this, $"{display}(이)가 친구 신청을 수락했어요!");
            PetReactionTriggered?.Invoke(this, EventArgs.Empty);

            return FriendOperationResult.Ok(
                FriendOperationStage.Completed,
                transportResult.Route ?? request.TransportKind,
                request.RequestId,
                accepted);
        }
        finally
        {
            lock (_pairingWorkflowLock)
            {
                _respondingRequestIds.Remove(request.RequestId);
            }
        }
    }

    public static string FormatFriendOperationFailure(
        FriendOperationResult result,
        bool isAccept,
        string? targetDisplayName = null)
    {
        var routePrefix = result.Route switch
        {
            PeerTransportKind.Lan => "[LAN] ",
            PeerTransportKind.GcpRelay => "[GCP] ",
            _ => string.Empty
        };

        var target = string.IsNullOrWhiteSpace(targetDisplayName) ? "상대방" : targetDisplayName;
        var action = isAccept ? "친구 수락" : "친구 신청";

        return result.ErrorCode switch
        {
            FriendOperationErrorCodes.RecipientOffline
                => $"{routePrefix}{target}이(가) 오프라인이거나 응답할 수 없어 {action}에 실패했습니다.",
            FriendOperationErrorCodes.PairingRequestExpired
                => isAccept
                    ? $"{routePrefix}친구 신청 유효 시간이 만료되었습니다. {target}에게 다시 신청해 달라고 안내해 주세요."
                    : $"{routePrefix}친구 신청 유효 시간이 만료되었습니다. 다시 신청해 주세요.",
            FriendOperationErrorCodes.PairingRequestUnknown
                => isAccept
                    ? $"{routePrefix}만료되었거나 찾을 수 없는 친구 신청입니다. {target}에게 새로 신청해 달라고 안내해 주세요."
                    : $"{routePrefix}유효하지 않은 친구 신청입니다. 다시 시도해 주세요.",
            FriendOperationErrorCodes.DeliveryFailedOrTimeout
                => $"{routePrefix}상대방 응답(ACK)이 시간 내에 도착하지 않았습니다. 연결 확인 후 다시 시도해 주세요.",
            FriendOperationErrorCodes.RateLimited
                => $"{routePrefix}요청이 너무 빠릅니다. 잠시 후 다시 시도해 주세요.",
            FriendOperationErrorCodes.GcpNotConnected
                => "[GCP] 중계 서버에 연결되지 않았습니다. 인터넷 상태를 확인한 후 다시 시도해 주세요.",
            FriendOperationErrorCodes.PublicKeyMismatch or FriendOperationErrorCodes.InvalidSignature or FriendOperationErrorCodes.InvalidOrExpiredChallenge or FriendOperationErrorCodes.AuthFailed or FriendOperationErrorCodes.SenderMismatch
                => $"{routePrefix}기기 인증 오류({result.ErrorCode})로 {action}을(를) 완료할 수 없습니다.",
            FriendOperationErrorCodes.InvalidAckSignature or FriendOperationErrorCodes.PairingConfirmationFailed
                => $"{routePrefix}보안 서명 검증 오류({result.ErrorCode})로 {action}을(를) 완료하지 못했습니다.",
            FriendOperationErrorCodes.NetworkError
                => $"{routePrefix}네트워크 오류로 {action}을(를) 완료하지 못했습니다. 연결 및 방화벽을 확인해 주세요.",
            FriendOperationErrorCodes.LocalStorageFailed
                => "로컬 친구 목록(friends.json) 저장에 실패해 등록을 보류했습니다. 다시 시도해 주세요.",
            FriendOperationErrorCodes.AlreadyFriends
                => "이미 친구로 등록된 펫입니다.",
            FriendOperationErrorCodes.DuplicatePendingRequest
                => $"{target}에게 이미 대기 중인 친구 신청이 있습니다.",
            FriendOperationErrorCodes.RequestInProgress
                => "이미 친구 요청을 처리 중입니다.",
            FriendOperationErrorCodes.ProtocolMismatch
                => "서로의 LabPet 버전이 달라 업데이트가 필요합니다.",
            FriendOperationErrorCodes.PairingAlreadyCompleted
                => "이미 처리 완료된 친구 신청입니다.",
            FriendOperationErrorCodes.PairingRequestRejected
                => $"{routePrefix}상대방이 현재 친구 신청을 받을 수 없습니다.",
            _ => $"{routePrefix}{action}에 실패했습니다. ({result.ErrorCode ?? "UNKNOWN"})"
        };
    }

    public async Task<bool> SendBubbleMessageAsync(FriendEntry friend, string text)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return false;
        }

        var ok = await _transport.SendBubbleMessageAsync(FindFriend(friend.PetId) ?? friend, text).ConfigureAwait(false);
        if (ok)
        {
            await RewardSocialBonusAsync(friend.PetId, SocialRewardKind.FriendMessage).ConfigureAwait(false);
        }

        return ok;
    }

    public async Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return false;
        }

        var ok = await _transport.SendQuickReactionAsync(FindFriend(friend.PetId) ?? friend, reactionId).ConfigureAwait(false);
        if (ok)
        {
            await RewardSocialBonusAsync(friend.PetId, SocialRewardKind.QuickReaction).ConfigureAwait(false);
        }

        return ok;
    }

    internal bool TryReservePetInteraction()
    {
        var next = Volatile.Read(ref _nextPetInteractionTicks);
        var now = _clock.Elapsed.Ticks;
        return now >= next
            && Interlocked.CompareExchange(ref _nextPetInteractionTicks, now + TimeSpan.TicksPerSecond, next) == next;
    }

    public async Task<PetInteractionSendResult> SendPetInteractionAsync(FriendEntry friend)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return PetInteractionSendResult.Unavailable;
        }

        if (!TryReservePetInteraction())
        {
            return PetInteractionSendResult.Cooldown;
        }

        var target = FindFriend(friend.PetId) ?? friend;
        if (!await _transport.SendPetInteractionAsync(target, "pet").ConfigureAwait(false))
        {
            return PetInteractionSendResult.Unavailable;
        }

        await RewardSocialBonusAsync(target.PetId, SocialRewardKind.FriendGreeting).ConfigureAwait(false);
        var name = FormatDisplayName(target.OwnerName, target.PetName);
        FriendStatusNotification?.Invoke(this, new FriendStatusNotice(target.PetId, $"{name}에게 인사했습니다.", 2000, AnimateHost: false));
        return PetInteractionSendResult.Sent;
    }

    public async Task<bool> ToggleMusicFollowAsync(FriendEntry hostFriend)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return false;
        }

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
        var routeLabel = e.Route?.ToString() ?? "-";
        var errCode = e.ErrorCode ?? "-";
        Trace.TraceWarning("소셜 전송 오류: Operation={0}, Route={1}, ErrorCode={2}, Message={3}", e.Operation, routeLabel, errCode, e.Message);

        var formatted = FormatTransportErrorMessage(e);
        LastTransportErrorMessage = formatted;
        TransportErrorReported?.Invoke(this, e);

        if (e.Operation is "Listen" or "GcpAuth" or "GcpConnect" or "GcpRegisterPresence")
        {
            var dedupKey = $"{e.Operation}|{errCode}";
            var now = DateTimeOffset.UtcNow;
            if (!_lastTransportNotifyAt.TryGetValue(dedupKey, out var prev) || now - prev >= TimeSpan.FromSeconds(15))
            {
                _lastTransportNotifyAt[dedupKey] = now;
                SocialNotification?.Invoke(this, formatted);
            }
        }
    }

    private static string FormatTransportErrorMessage(TransportErrorEventArgs e)
    {
        if (e.Operation == "Listen")
        {
            return e.Message;
        }

        if (e.Operation == "GcpAuth")
        {
            return string.IsNullOrWhiteSpace(e.ErrorCode)
                ? $"[GCP] 기기 인증 오류: {e.Message}"
                : $"[GCP] 기기 인증 오류 ({e.ErrorCode}): 서버와 연결할 수 없습니다.";
        }

        if (e.Operation is "GcpConnect" or "GcpRegisterPresence")
        {
            return FriendOperationErrorCodes.IsAuthError(e.ErrorCode)
                ? $"[GCP] 기기 인증 오류 ({e.ErrorCode}): 재연결 대기 중"
                : "[GCP] 중계 서버 연결 오류: 네트워크 상태를 확인하는 중입니다.";
        }

        var prefix = e.Route == PeerTransportKind.Lan ? "[LAN] " : (e.Route == PeerTransportKind.GcpRelay ? "[GCP] " : string.Empty);
        return string.IsNullOrWhiteSpace(e.ErrorCode)
            ? $"{prefix}통신 오류: {e.Message}"
            : $"{prefix}통신 오류 ({e.ErrorCode})";
    }

    private void OnGameEventReceived(object? sender, GameEventReceivedEventArgs e)
    {
        GameEventReceived?.Invoke(this, e);
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
            e.RequestId,
            e.RequesterPetId,
            e.RequesterOwnerName,
            e.RequesterName,
            e.Appearance,
            e.Level,
            e.RemoteIp,
            e.RemoteTcpPort,
            e.TransportKind,
            e.ExpiresAtUtc));
    }

    private void OnFriendAccepted(object? sender, FriendAcceptedEventArgs e)
    {
        // 수락자 본인 측은 RespondFriendRequestDetailedAsync에서 직접 저장 완료까지 대기하므로 여기서는 신청자 측만 처리한다.
        if (e.IsLocalAccepter)
        {
            return;
        }

        lock (_pairingWorkflowLock)
        {
            _outgoingRequestStates.Remove(e.AccepterPetId);
        }

        _lastRemoteAcceptPersistTask = ObserveIncomingAsync(() => HandleRemoteFriendAcceptedAsync(e));
    }

    private async Task HandleRemoteFriendAcceptedAsync(FriendAcceptedEventArgs e)
    {
        var entry = new FriendEntry
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
        };

        var added = AddFriendToMemoryLocked(entry);
        if (!added)
        {
            return;
        }

        var saved = await TrySaveSocialStateAsync().ConfigureAwait(false);
        if (!saved)
        {
            RemoveFriendFromMemoryLocked(entry.PetId);
            Trace.TraceError("신청자 측 친구 수락 저장 실패로 메모리 등록 롤백: RequestId={0}, ErrorCode={1}", e.RequestId, FriendOperationErrorCodes.LocalStorageFailed);
            StateChanged?.Invoke(this, EventArgs.Empty);
            SocialNotification?.Invoke(this, $"{FormatDisplayName(entry.OwnerName, entry.PetName)} 친구 정보 저장에 실패했습니다.");
            return;
        }

        Trace.TraceInformation("신청자 측 친구 등록 및 저장 완료: RequestId={0}, Stage=Completed", e.RequestId);
        StateChanged?.Invoke(this, EventArgs.Empty);
        var display = FormatDisplayName(e.AccepterOwnerName, e.AccepterName);
        SocialNotification?.Invoke(this, $"{display}(이)가 친구 신청을 수락했어요!");
        PetReactionTriggered?.Invoke(this, EventArgs.Empty);
    }

    private void OnFriendRejected(object? sender, FriendRejectedEventArgs e)
    {
        lock (_pairingWorkflowLock)
        {
            _outgoingRequestStates.Remove(e.ResponderPetId);
        }

        Trace.TraceInformation("친구 신청 거절 수신: RequestId={0}, Reason={1}", e.RequestId, e.Reason ?? "-");
        StateChanged?.Invoke(this, EventArgs.Empty);
        SocialNotification?.Invoke(this, "친구 신청이 거절되었어요.");
    }

    private void OnBubbleMessageReceived(object? sender, BubbleMessageReceivedEventArgs e)
        => _ = ObserveIncomingAsync(async () =>
        {
            var name = await RewardSocialBonusAsync(e.SenderPetId, SocialRewardKind.FriendMessage).ConfigureAwait(false);
            var peer = DiscoveredPeers.FirstOrDefault(peer =>
                string.Equals(peer.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase));
            var appearance = peer?.Appearance?.Clone() ?? FindFriend(e.SenderPetId)?.CachedAppearance ?? new PetAppearance();

            FriendMessageReceived?.Invoke(this, new ReceivedFriendMessage(e.SenderPetId, name, e.Text, appearance, e.MessageId));
            PetReactionTriggered?.Invoke(this, EventArgs.Empty);
        });

    private void OnQuickReactionReceived(object? sender, QuickReactionReceivedEventArgs e)
        => _ = ObserveIncomingAsync(async () =>
        {
            var name = await RewardSocialBonusAsync(e.SenderPetId, SocialRewardKind.QuickReaction).ConfigureAwait(false);
            FriendStatusNotification?.Invoke(this, new FriendStatusNotice(e.SenderPetId, $"{name}의 반응: {e.ReactionId}", 2600));
        });

    private void OnPetInteractionReceived(object? sender, PetInteractionReceivedEventArgs e)
        => _ = ObserveIncomingAsync(async () =>
        {
            var name = await RewardSocialBonusAsync(e.SenderPetId, SocialRewardKind.FriendGreeting).ConfigureAwait(false);
            FriendStatusNotification?.Invoke(this, new FriendStatusNotice(e.SenderPetId, $"{name}(이)가 인사했어요!", 2600));
        });

    private async Task ObserveIncomingAsync(Func<Task> run)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return;
        }

        try
        {
            await run().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceError("백그라운드 소셜 작업 실패: {0}", ex);
        }
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

    private bool AddFriendToMemoryLocked(FriendEntry entry)
    {
        // 수락 패킷 응답/수신 직후 인증 목록에 반영한다.
        lock (_friendStateLock)
        {
            if (FindSharedFriend(entry.PetId) is not null)
            {
                return false;
            }

            _friends = [.. _friends, entry];
            _transport.UpdateKnownFriends(_friends);
            return true;
        }
    }

    private void RemoveFriendFromMemoryLocked(string petId)
    {
        lock (_friendStateLock)
        {
            var next = _friends.Where(f => !string.Equals(f.PetId, petId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (next.Length != _friends.Length)
            {
                _friends = next;
                _transport.UpdateKnownFriends(_friends);
            }
        }
    }

    private async Task<bool> PersistLocalAcceptedFriendAsync(FriendEntry entry)
    {
        AddFriendToMemoryLocked(entry);
        var saved = await TrySaveSocialStateAsync().ConfigureAwait(false);
        if (!saved)
        {
            RemoveFriendFromMemoryLocked(entry.PetId);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task<string> RewardSocialBonusAsync(string petId, SocialRewardKind rewardKind)
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
                if (friend.Intimacy < FriendEntry.MaxIntimacy)
                {
                    friend.Intimacy = FriendEntry.NormalizeIntimacy(friend.Intimacy + 1);
                }
                else
                {
                    friend.Intimacy = FriendEntry.MaxIntimacy;
                }

                name = FormatDisplayName(friend.OwnerName, friend.PetName);
            }

            profile = _localProfile;
        }

        _progression.Economy.TryGrantSocialCoinReward(profile, petId, rewardKind);

        await SaveSocialStateAsync(profile).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return name;
    }

    private async Task SaveSocialStateAsync(PetProfile? profile = null)
    {
        if (!await TrySaveSocialStateAsync(profile).ConfigureAwait(false))
        {
            SocialNotification?.Invoke(this, "친구 정보 또는 보상을 저장하지 못했어요.");
        }
    }

    private async Task<bool> TrySaveSocialStateAsync(PetProfile? profile = null)
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

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("소셜 상태 저장 실패: {0}", ex);
            return false;
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

    public ValueTask DisposeAsync() => new(_shutdown.Value);

    private async Task ShutdownAsync()
    {
        var drain = _operations.DrainAsync();
        _transport.PeersChanged -= OnPeersChanged;
        _transport.FriendRequestReceived -= OnFriendRequestReceived;
        _transport.FriendAccepted -= OnFriendAccepted;
        _transport.FriendRejected -= OnFriendRejected;
        _transport.BubbleMessageReceived -= OnBubbleMessageReceived;
        _transport.QuickReactionReceived -= OnQuickReactionReceived;
        _transport.PetInteractionReceived -= OnPetInteractionReceived;
        _transport.MusicFrameReceived -= OnMusicFrameReceived;
        _transport.GameEventReceived -= OnGameEventReceived;
        _transport.TransportError -= OnTransportError;
        await drain.ConfigureAwait(false);
        await _transport.StopAsync().ConfigureAwait(false);
    }
}
