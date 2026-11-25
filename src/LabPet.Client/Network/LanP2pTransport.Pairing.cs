using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.Network;

public sealed partial class LanP2pTransport
{
    private const int MaxPendingPairings = 32;
    private const int MaxExpiredRequestTombstones = 256;
    private static readonly TimeSpan ExpiredTombstoneLifetime = TimeSpan.FromMinutes(10);

    internal TimeSpan PairingLifetime { get; set; } = TimeSpan.FromMinutes(3);

    private readonly object _pairingLock = new();
    private readonly Dictionary<string, OutgoingPairing> _outgoingPairings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingPairing> _incomingPairings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _expiredOutgoingRequestIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _expiredIncomingRequestIds = new(StringComparer.Ordinal);

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
        // 응답 확인이 유실돼도 같은 키로 안전하게 재시도한다.
        public ECDiffieHellman? ReplyKey { get; set; }
        public bool IsResponding { get; set; }
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public void Dispose() => ReplyKey?.Dispose();
    }

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
            return _outgoingPairings.Values.Any(pair => pair.ConfirmationKey is null && SamePetId(pair.PeerId, peerPetId));
        }
    }

    public async Task<bool> SendFriendRequestAsync(
        PeerSnapshot target, CancellationToken cancellationToken = default)
        => (await SendFriendRequestDetailedAsync(target, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<FriendOperationResult> SendFriendRequestDetailedAsync(
        PeerSnapshot target, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.NetworkError, true, PeerTransportKind.Lan, detail: "LAN 통신이 실행 중이 아닙니다.");
        }

        if (target.RequiresUpdate)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.ProtocolMismatch, false, PeerTransportKind.Lan, detail: "상대방 또는 내 앱 버전이 호환되지 않습니다.");
        }

        if (_localPresence.IsFocusing)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.PomodoroFocusBlocked, false, PeerTransportKind.Lan, detail: FriendOperationErrorCodes.PomodoroFocusMessage);
        }

        if (string.IsNullOrWhiteSpace(target.IpAddress) || string.IsNullOrWhiteSpace(target.PetId) || !IsValidPort(target.TcpPort))
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.InvalidRequest, false, PeerTransportKind.Lan, detail: "대상 피어 주소가 올바르지 않습니다.");
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
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.AlreadyFriends, false, PeerTransportKind.Lan);
            }

            if (_outgoingPairings.Values.Any(pair => SamePetId(pair.PeerId, target.PetId)))
            {
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.DuplicatePendingRequest, false, PeerTransportKind.Lan);
            }

            if (_outgoingPairings.Count >= MaxPendingPairings)
            {
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.PendingCapacityFull, true, PeerTransportKind.Lan);
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
                TcpPort = _boundTcpPort
            };
            pending.RequestFingerprint = Fingerprint(JsonSerializer.Serialize(payload, ProtocolJson.PacketOptions));
            _outgoingPairings.Add(requestId, pending);
        }

        ControlSendOutcome outcome;
        try
        {
            outcome = await SendControlPacketWithOutcomeAsync(target.IpAddress, target.TcpPort, PacketType.FriendRequest,
                payload, null, cancellationToken, target.PetId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CleanupOutgoingPairing(requestId);
            return FriendOperationResult.Fail(FriendOperationStage.Sending, FriendOperationErrorCodes.NetworkError, true, PeerTransportKind.Lan, requestId, ex.Message);
        }

        if (outcome.VerifiedAck is { Accepted: true })
        {
            return FriendOperationResult.Ok(FriendOperationStage.Completed, PeerTransportKind.Lan, requestId);
        }

        CleanupOutgoingPairing(requestId);
        var errorCode = outcome.ErrorCode ?? FriendOperationErrorCodes.DeliveryFailedOrTimeout;
        var stage = errorCode is FriendOperationErrorCodes.RecipientOffline or FriendOperationErrorCodes.NetworkError or FriendOperationErrorCodes.Cancelled
            ? FriendOperationStage.Sending
            : errorCode == FriendOperationErrorCodes.InvalidAckSignature
                ? FriendOperationStage.AckVerification
                : FriendOperationStage.RemoteReceipt;

        return FriendOperationResult.Fail(stage, errorCode, outcome.IsTransient, PeerTransportKind.Lan, requestId, outcome.Detail);
    }

    public async Task<bool> RespondFriendRequestAsync(
        string requestId, bool accept, CancellationToken cancellationToken = default)
        => (await RespondFriendRequestDetailedAsync(requestId, accept, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<FriendOperationResult> RespondFriendRequestDetailedAsync(
        string requestId, bool accept, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.NetworkError, true, PeerTransportKind.Lan, requestId);
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
                return FriendOperationResult.Fail(FriendOperationStage.Validation, missingCode, false, PeerTransportKind.Lan, requestId);
            }

            if (found.IsResponding)
            {
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.RequestInProgress, false, PeerTransportKind.Lan, requestId);
            }

            if (_friendsByPetId.ContainsKey(found.Request.RequesterPetId))
            {
                _incomingPairings.Remove(requestId);
                found.Dispose();
                return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.AlreadyFriends, false, PeerTransportKind.Lan, requestId);
            }

            pending = found;
            if (accept)
            {
                pending.ReplyKey ??= ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                publicKey = Convert.ToBase64String(pending.ReplyKey.ExportSubjectPublicKeyInfo());
                keys = PacketCrypto.DerivePairingKeys(pending.ReplyKey, pending.PublicKey, requestId,
                    pending.Request.RequesterPetId, _localPetId, pending.PublicKey, publicKey,
                    pending.RequestFingerprint);
                if (keys is null)
                {
                    _incomingPairings.Remove(requestId);
                    pending.Dispose();
                    return FriendOperationResult.Fail(FriendOperationStage.Validation, FriendOperationErrorCodes.InvalidPairingRequest, false, PeerTransportKind.Lan, requestId);
                }
            }

            pending.IsResponding = true;
        }

        var request = pending.Request;
        var presence = _localPresence;
        ControlSendOutcome outcome;

        try
        {
            outcome = keys is { } pairKeys
                ? await SendControlPacketWithOutcomeAsync(request.RemoteIp, request.RemoteTcpPort, PacketType.FriendAccept,
                    new FriendAcceptPayload
                    {
                        RequestId = requestId, PublicKey = publicKey!, TcpPort = _boundTcpPort,
                        AccepterOwnerName = presence.OwnerName, AccepterName = presence.PetName, Appearance = presence.Appearance.Clone()
                    }, null, cancellationToken, request.RequesterPetId, pairKeys.ConfirmationKey).ConfigureAwait(false)
                : await SendControlPacketWithOutcomeAsync(request.RemoteIp, request.RemoteTcpPort, PacketType.FriendReject,
                    new FriendRejectPayload { RequestId = requestId, Reason = "rejected_by_user" },
                    null, cancellationToken, request.RequesterPetId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new ControlSendOutcome(null, FriendOperationErrorCodes.NetworkError, true, ex.Message);
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
                return FriendOperationResult.Fail(FriendOperationStage.AckVerification, lostCode, false, PeerTransportKind.Lan, requestId);
            }

            if (outcome.VerifiedAck is not { Accepted: true })
            {
                var errCode = outcome.ErrorCode ?? FriendOperationErrorCodes.DeliveryFailedOrTimeout;
                // 만료·존재하지 않음·서명 불일치 등 확정적 실패일 때만 임시 키와 요청을 폐기하고, 일시 오류는 동일 ReplyKey로 재시도 허용
                if (!outcome.IsTransient && errCode != FriendOperationErrorCodes.Cancelled)
                {
                    _incomingPairings.Remove(requestId);
                    pending.Dispose();
                }

                var failStage = errCode is FriendOperationErrorCodes.RecipientOffline or FriendOperationErrorCodes.NetworkError or FriendOperationErrorCodes.Cancelled
                    ? FriendOperationStage.Sending
                    : errCode is FriendOperationErrorCodes.InvalidAckSignature or FriendOperationErrorCodes.PairingConfirmationFailed
                        ? FriendOperationStage.AckVerification
                        : FriendOperationStage.RemoteReceipt;

                return FriendOperationResult.Fail(failStage, errCode, outcome.IsTransient, PeerTransportKind.Lan, requestId, outcome.Detail);
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
                request.RemoteIp,
                request.RemoteTcpPort,
                requestId,
                isLocalAccepter: true);
            FriendAccepted?.Invoke(this, acceptedArgs);
            return FriendOperationResult.Ok(FriendOperationStage.LocalPersistence, PeerTransportKind.Lan, requestId, acceptedArgs);
        }

        return FriendOperationResult.Ok(FriendOperationStage.Completed, PeerTransportKind.Lan, requestId);
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

    private bool ReceiveFriendRequest(ControlPacketEnvelope envelope, string remoteIp, out string errorCode)
    {
        errorCode = string.Empty;
        var header = envelope.Header;
        var payload = JsonSerializer.Deserialize<FriendRequestPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _) || !IsValidPort(payload.TcpPort)
            || string.IsNullOrWhiteSpace(remoteIp) || !PacketCrypto.IsPairingPublicKey(payload.PublicKey))
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
            remoteIp,
            payload.TcpPort,
            PeerTransportKind.Lan,
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
                || _incomingPairings.Values.Any(pair => SamePetId(pair.Request.RequesterPetId, header.SenderPetId)))
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

    private bool ReceiveFriendAcceptance(ControlPacketEnvelope envelope, string remoteIp, out string? confirmationKey, out string errorCode)
    {
        confirmationKey = null;
        errorCode = string.Empty;
        var header = envelope.Header;
        var payload = JsonSerializer.Deserialize<FriendAcceptPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _) || !IsValidPort(payload.TcpPort)
            || string.IsNullOrWhiteSpace(remoteIp))
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

            var keys = PacketCrypto.DerivePairingKeys(pending.Key, payload.PublicKey, payload.RequestId,
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
            remoteIp,
            payload.TcpPort,
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

    private void PrunePairings(DateTimeOffset now)
    {
        foreach (var item in _outgoingPairings.Where(item => item.Value.ExpiresAt <= now).ToArray())
        {
            _outgoingPairings.Remove(item.Key);
            item.Value.Dispose();
            RecordExpiredTombstone(_expiredOutgoingRequestIds, item.Key, now);
        }

        foreach (var item in _incomingPairings.Where(item => item.Value.ExpiresAt <= now && !item.Value.IsResponding).ToArray())
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

    private static bool SamePetId(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool IsValidPort(int port) => port is > 0 and <= 65535;
    private static string Fingerprint(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
