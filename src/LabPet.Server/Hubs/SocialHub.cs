using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using LabPet.Server.Options;
using LabPet.Server.Services;
using LabPet.Server.Storage;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LabPet.Server.Hubs;

/// <summary>
/// LabPet 인터넷(GCP) 실시간 중계 Hub.
/// 메시지 본문/암호문은 메모리 큐나 디스크에 보관하지 않고 온라인 수신자에게 즉시 전달한 뒤 폐기한다.
/// </summary>
[Authorize]
public sealed class SocialHub(
    ServerOptions options,
    IdentityRegistryStore registry,
    ConnectionPresenceManager presence,
    PendingFriendRequestStore pendingRequests,
    RateLimiterAndDedupService rateLimiter,
    ILogger<SocialHub> logger) : Hub
{
    private const int MaxPayloadJsonLength = 14 * 1024;
    private static readonly TimeSpan ControlTimestampTolerance = TimeSpan.FromMinutes(3);

    public override async Task OnConnectedAsync()
    {
        var petId = GetAuthenticatedPetId();
        var error = "UNAUTHENTICATED";
        if (string.IsNullOrEmpty(petId) || !presence.TryAddConnection(petId, Context.ConnectionId, out error))
        {
            logger.LogWarning("연결 거부: {Error}", error);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (petId, becameOffline) = presence.RemoveConnection(Context.ConnectionId);
        if (becameOffline && !string.IsNullOrEmpty(petId))
        {
            var otherConnections = presence.GetOtherOnlineConnectionIds(petId);
            if (otherConnections.Count > 0)
            {
                await Clients.Clients(otherConnections)
                    .SendAsync(SocialHubConstants.ReceiveFriendOfflineClientMethod, petId)
                    .ConfigureAwait(false);
            }
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteFriendStateDto>> RegisterPresenceAsync(RemotePresenceUpdate update)
    {
        var petId = RequireAuthenticatedPetId();
        registry.UpdateIdentityProfile(petId, update.FriendCode, update.OwnerName, update.PetName);
        if (update.KnownFriendPetIds is { Count: > 0 } knownIds)
        {
            registry.SyncDeclaredFriends(petId, knownIds);
        }

        presence.UpdatePresence(petId, update);
        await NotifyPresenceChangedAsync(petId, includeNonFriends: true).ConfigureAwait(false);
        return presence.GetVisibleOnlinePeers(petId, registry, options.MaxPublicDiscoveryPeers);
    }

    public async Task UpdatePresenceAsync(RemotePresenceUpdate update)
    {
        var petId = RequireAuthenticatedPetId();
        registry.UpdateIdentityProfile(petId, update.FriendCode, update.OwnerName, update.PetName);
        var (anyChanged, publicChanged) = presence.UpdatePresence(petId, update);
        if (anyChanged)
        {
            await NotifyPresenceChangedAsync(petId, includeNonFriends: publicChanged).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<RemoteFriendStateDto>> SyncKnownFriendsAsync(List<string> friendPetIds)
    {
        var petId = RequireAuthenticatedPetId();
        registry.SyncDeclaredFriends(petId, friendPetIds ?? []);
        await NotifyPresenceChangedAsync(petId, includeNonFriends: true).ConfigureAwait(false);
        return presence.GetVisibleOnlinePeers(petId, registry, options.MaxPublicDiscoveryPeers);
    }

    public async Task<RelayDeliveryResult> RelayControlPacketAsync(RelayControlEnvelopeRequest request)
    {
        var senderPetId = RequireAuthenticatedPetId();
        var sw = Stopwatch.StartNew();

        if (request is null || request.Envelope?.Header is null || string.IsNullOrWhiteSpace(request.TargetPetId)
            || request.TargetPetId.Length > 64
            || string.Equals(request.TargetPetId, senderPetId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("제어 패킷 중계 거부: Stage=Validation, ErrorCode=INVALID_REQUEST, ElapsedMs={ElapsedMs}", sw.ElapsedMilliseconds);
            return new RelayDeliveryResult(false, FriendOperationErrorCodes.InvalidRequest);
        }

        var envelope = request.Envelope;
        var header = envelope.Header;
        string? pairingRequestId = null;

        RelayDeliveryResult RejectAndLog(string stage, string errorCode)
        {
            if (IsPairingPacket(header.PacketType) || errorCode is not FriendOperationErrorCodes.RateLimited)
            {
                logger.LogWarning(
                    "제어 패킷 중계 거부: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage={Stage}, ErrorCode={ErrorCode}, ElapsedMs={ElapsedMs}",
                    pairingRequestId ?? "-",
                    header.MessageId,
                    header.PacketType,
                    stage,
                    errorCode,
                    sw.ElapsedMilliseconds);
            }

            return new RelayDeliveryResult(false, errorCode);
        }

        // 발신자 ID 위조 차단: 반드시 인증된 토큰의 PetId와 일치해야 함
        if (!string.Equals(header.SenderPetId, senderPetId, StringComparison.OrdinalIgnoreCase))
        {
            return RejectAndLog("Validation", FriendOperationErrorCodes.SenderMismatch);
        }

        if (ProtocolConstants.EvaluateCompatibility(header.ProtocolVersion) != ProtocolCompatibility.Compatible)
        {
            return RejectAndLog("Validation", FriendOperationErrorCodes.ProtocolMismatch);
        }

        if (!Guid.TryParseExact(header.MessageId, "N", out _))
        {
            return RejectAndLog("Validation", "INVALID_MESSAGE_ID");
        }

        var now = DateTimeOffset.UtcNow;
        if ((header.TimestampUtc - now).Duration() > ControlTimestampTolerance)
        {
            return RejectAndLog("Validation", FriendOperationErrorCodes.StaleTimestamp);
        }

        var payloadLen = envelope.PayloadJson?.Length ?? 0;
        if (payloadLen <= 0 || payloadLen > MaxPayloadJsonLength || payloadLen * 2 > options.MaxControlPacketBytes)
        {
            return RejectAndLog("Validation", "PAYLOAD_TOO_LARGE");
        }

        if (!rateLimiter.AllowControlPacket(senderPetId))
        {
            return RejectAndLog("Validation", FriendOperationErrorCodes.RateLimited);
        }

        if (!rateLimiter.TryMarkMessageSeen(senderPetId, header.MessageId))
        {
            return RejectAndLog("Validation", FriendOperationErrorCodes.DuplicateMessage);
        }

        var targetPetId = request.TargetPetId.Trim();
        var acceptValidation = PendingResponseValidation.Unknown;

        switch (header.PacketType)
        {
            case PacketType.FriendRequest:
            {
                if (envelope.IsEncrypted)
                {
                    return RejectAndLog("Validation", FriendOperationErrorCodes.InvalidPairingEnvelope);
                }

                if (registry.AreMutualFriends(senderPetId, targetPetId))
                {
                    return RejectAndLog("Validation", FriendOperationErrorCodes.AlreadyFriends);
                }

                var reqPayload = TryDeserialize<FriendRequestPayload>(envelope.PayloadJson!);
                if (reqPayload is null || !Guid.TryParseExact(reqPayload.RequestId, "N", out _))
                {
                    return RejectAndLog("Validation", FriendOperationErrorCodes.InvalidPairingRequest);
                }

                pairingRequestId = reqPayload.RequestId;
                pendingRequests.RecordRequest(pairingRequestId, senderPetId, targetPetId);
                logger.LogInformation(
                    "친구 신청 검증 완료: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=Validation, ElapsedMs={ElapsedMs}",
                    pairingRequestId,
                    header.MessageId,
                    header.PacketType,
                    sw.ElapsedMilliseconds);
                break;
            }

            case PacketType.FriendAccept:
            {
                if (envelope.IsEncrypted)
                {
                    return RejectAndLog("Validation", FriendOperationErrorCodes.InvalidPairingEnvelope);
                }

                var acceptPayload = TryDeserialize<FriendAcceptPayload>(envelope.PayloadJson!);
                if (acceptPayload is null || !Guid.TryParseExact(acceptPayload.RequestId, "N", out _))
                {
                    return RejectAndLog("Validation", FriendOperationErrorCodes.PairingRequestUnknown);
                }

                pairingRequestId = acceptPayload.RequestId;
                acceptValidation = pendingRequests.ValidateResponseTarget(acceptPayload.RequestId, senderPetId, targetPetId);
                switch (acceptValidation)
                {
                    case PendingResponseValidation.Expired:
                        return RejectAndLog("Validation", FriendOperationErrorCodes.PairingRequestExpired);
                    case PendingResponseValidation.Unknown:
                        return RejectAndLog("Validation", FriendOperationErrorCodes.PairingRequestUnknown);
                    case PendingResponseValidation.AlreadyApproved:
                        logger.LogInformation(
                            "친구 수락 멱등 재시도 허용: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=Validation, ElapsedMs={ElapsedMs}",
                            pairingRequestId,
                            header.MessageId,
                            header.PacketType,
                            sw.ElapsedMilliseconds);
                        break;
                    case PendingResponseValidation.Valid:
                        logger.LogInformation(
                            "친구 수락 검증 완료: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=Validation, ElapsedMs={ElapsedMs}",
                            pairingRequestId,
                            header.MessageId,
                            header.PacketType,
                            sw.ElapsedMilliseconds);
                        break;
                }

                break;
            }

            case PacketType.FriendReject:
            {
                if (envelope.IsEncrypted)
                {
                    return RejectAndLog("Validation", FriendOperationErrorCodes.InvalidPairingEnvelope);
                }

                var rejectPayload = TryDeserialize<FriendRejectPayload>(envelope.PayloadJson!);
                if (rejectPayload is null || !Guid.TryParseExact(rejectPayload.RequestId, "N", out _))
                {
                    return RejectAndLog("Validation", FriendOperationErrorCodes.PairingRequestUnknown);
                }

                pairingRequestId = rejectPayload.RequestId;
                var rejectValidation = pendingRequests.ValidateResponseTarget(rejectPayload.RequestId, senderPetId, targetPetId);
                switch (rejectValidation)
                {
                    case PendingResponseValidation.Expired:
                        return RejectAndLog("Validation", FriendOperationErrorCodes.PairingRequestExpired);
                    case PendingResponseValidation.Unknown:
                        return RejectAndLog("Validation", FriendOperationErrorCodes.PairingRequestUnknown);
                    case PendingResponseValidation.AlreadyApproved:
                        return RejectAndLog("Validation", FriendOperationErrorCodes.PairingAlreadyCompleted);
                }

                break;
            }

            case PacketType.BubbleMessage:
            case PacketType.QuickReaction:
            case PacketType.PetInteraction:
            case PacketType.MusicFollowOn:
            case PacketType.MusicFollowOff:
            case PacketType.GameEvent:
            case PacketType.PresenceSync:
                // 비친구 중계 및 비암호화 제어 패킷 원천 차단 (메시지 내용은 서버도 열람/저장하지 않음)
                if (!registry.AreMutualFriends(senderPetId, targetPetId))
                {
                    return RejectAndLog("Validation", "NOT_MUTUAL_FRIEND");
                }

                if (!envelope.IsEncrypted)
                {
                    return RejectAndLog("Validation", "ENCRYPTION_REQUIRED");
                }
                break;

            default:
                return RejectAndLog("Validation", "UNSUPPORTED_PACKET_TYPE");
        }

        // 수신자 오프라인 시 메시지 보관 없이 즉시 실패 반환
        var targetConnections = presence.GetConnectionIdsForPet(targetPetId);
        if (targetConnections.Count == 0)
        {
            if (header.PacketType == PacketType.FriendRequest && pairingRequestId is not null)
            {
                pendingRequests.Remove(pairingRequestId);
            }

            return RejectAndLog("RemoteDelivery", FriendOperationErrorCodes.RecipientOffline);
        }

        var delivery = new RelayControlEnvelopeDelivery(senderPetId, envelope);
        ControlPacketEnvelope? ackEnvelope = null;

        foreach (var connId in targetConnections)
        {
            try
            {
                using var ackTimeoutCts = new CancellationTokenSource(Math.Clamp(options.RelayAckTimeoutMs, 1000, 10_000));
                ackEnvelope = await Clients.Client(connId)
                    .InvokeAsync<ControlPacketEnvelope?>(SocialHubConstants.DeliverControlPacketClientMethod, delivery, ackTimeoutCts.Token)
                    .ConfigureAwait(false);
                if (ackEnvelope is not null)
                {
                    break;
                }
            }
            catch
            {
                // 다중 연결 중 하나가 방금 끊겼다면 다음 연결 시도
            }
        }

        if (ackEnvelope is null)
        {
            if (header.PacketType == PacketType.FriendRequest && pairingRequestId is not null)
            {
                pendingRequests.Remove(pairingRequestId);
            }

            return RejectAndLog("RemoteDelivery", FriendOperationErrorCodes.DeliveryFailedOrTimeout);
        }

        // 수신된 ACK 기본 헤더/메시지ID 교차 검증
        var ack = TryDeserialize<AckPayload>(ackEnvelope.PayloadJson);
        var ackHeaderValid = ackEnvelope.Header is { PacketType: PacketType.Ack } ackHeader
            && string.Equals(ackHeader.SenderPetId, targetPetId, StringComparison.OrdinalIgnoreCase)
            && ack is not null
            && string.Equals(ack.AckMessageId, header.MessageId, StringComparison.Ordinal);

        if (!ackHeaderValid || ack is null)
        {
            if (header.PacketType == PacketType.FriendRequest && pairingRequestId is not null)
            {
                pendingRequests.Remove(pairingRequestId);
            }

            return RejectAndLog("AckVerification", FriendOperationErrorCodes.DeliveryFailedOrTimeout);
        }

        if (header.PacketType == PacketType.FriendRequest && pairingRequestId is not null)
        {
            if (!ack.Accepted)
            {
                pendingRequests.Remove(pairingRequestId);
                logger.LogWarning(
                    "친구 신청 상대 거부 ACK 수신: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=RemoteAck, ErrorCode={ErrorCode}, ElapsedMs={ElapsedMs}",
                    pairingRequestId,
                    header.MessageId,
                    header.PacketType,
                    ack.ErrorCode ?? FriendOperationErrorCodes.PairingRequestRejected,
                    sw.ElapsedMilliseconds);
            }
            else
            {
                logger.LogInformation(
                    "친구 신청 수신 및 ACK 전달 완료: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=RemoteAck, ElapsedMs={ElapsedMs}",
                    pairingRequestId,
                    header.MessageId,
                    header.PacketType,
                    sw.ElapsedMilliseconds);
            }
        }
        // 수락 ACK 확인 시 서버 인증 테이블에도 친구 관계 영속 등록 및 양측 온라인 상태 동기화
        else if (header.PacketType == PacketType.FriendAccept && pairingRequestId is not null)
        {
            if (ack.Accepted)
            {
                var registryPersisted = registry.ApproveFriendship(senderPetId, targetPetId);
                pendingRequests.MarkApproved(pairingRequestId);
                logger.LogInformation(
                    "친구 수락 ACK 확인 및 서버 관계 등록: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=FriendshipRegistered, RegistryPersisted={RegistryPersisted}, ElapsedMs={ElapsedMs}",
                    pairingRequestId,
                    header.MessageId,
                    header.PacketType,
                    registryPersisted,
                    sw.ElapsedMilliseconds);

                try
                {
                    await NotifyPresenceChangedAsync(senderPetId, includeNonFriends: false).ConfigureAwait(false);
                    await NotifyPresenceChangedAsync(targetPetId, includeNonFriends: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "친구 승인 후 프레즌스 동기화 알림 실패: RequestId={RequestId}, MessageId={MessageId}, Stage=PresenceNotify, ElapsedMs={ElapsedMs}",
                        pairingRequestId,
                        header.MessageId,
                        sw.ElapsedMilliseconds);
                }
            }
            else
            {
                if (acceptValidation != PendingResponseValidation.AlreadyApproved
                    && ack.ErrorCode is FriendOperationErrorCodes.PairingRequestExpired or FriendOperationErrorCodes.PairingRequestUnknown)
                {
                    pendingRequests.Remove(pairingRequestId);
                }

                logger.LogWarning(
                    "친구 수락 ACK 거부: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=AckVerification, ErrorCode={ErrorCode}, ElapsedMs={ElapsedMs}",
                    pairingRequestId,
                    header.MessageId,
                    header.PacketType,
                    ack.ErrorCode ?? FriendOperationErrorCodes.PairingConfirmationFailed,
                    sw.ElapsedMilliseconds);
            }
        }
        else if (header.PacketType == PacketType.FriendReject && pairingRequestId is not null)
        {
            if (ack.Accepted)
            {
                pendingRequests.Remove(pairingRequestId);
                logger.LogInformation(
                    "친구 거절 ACK 완료: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=Rejected, ElapsedMs={ElapsedMs}",
                    pairingRequestId,
                    header.MessageId,
                    header.PacketType,
                    sw.ElapsedMilliseconds);
            }
            else
            {
                logger.LogWarning(
                    "친구 거절 ACK 거부: RequestId={RequestId}, MessageId={MessageId}, PacketType={PacketType}, Stage=AckVerification, ErrorCode={ErrorCode}, ElapsedMs={ElapsedMs}",
                    pairingRequestId,
                    header.MessageId,
                    header.PacketType,
                    ack.ErrorCode ?? FriendOperationErrorCodes.PairingRequestUnknown,
                    sw.ElapsedMilliseconds);
            }
        }
        else if (header.PacketType == PacketType.MusicFollowOn)
        {
            if (ack.Accepted)
            {
                presence.SetMusicSubscription(hostPetId: targetPetId, followerPetId: senderPetId, enable: true);
            }
        }
        else if (header.PacketType == PacketType.MusicFollowOff)
        {
            presence.SetMusicSubscription(hostPetId: targetPetId, followerPetId: senderPetId, enable: false);
        }

        return new RelayDeliveryResult(true, ack.Accepted ? null : ack.ErrorCode, ackEnvelope);
    }

    private static bool IsPairingPacket(PacketType type) => type is
        PacketType.FriendRequest or
        PacketType.FriendAccept or
        PacketType.FriendReject;

    public async Task RelayMusicFrameAsync(MusicFramePacket packet)
    {
        var senderPetId = RequireAuthenticatedPetId();
        if (packet is null
            || !string.Equals(packet.SenderPetId, senderPetId, StringComparison.OrdinalIgnoreCase)
            || ProtocolConstants.EvaluateCompatibility(packet.ProtocolVersion) != ProtocolCompatibility.Compatible
            || string.IsNullOrWhiteSpace(packet.SignatureBase64)
            || packet.SubscriptionId.Length > 64)
        {
            return;
        }

        if (!rateLimiter.AllowMusicFrame(senderPetId))
        {
            return;
        }

        // 원격 구독자가 1명 이상 있을 때만 릴레이하여 네트워크 이그레스 절감
        var subscriberConnIds = presence.GetMusicFollowerConnectionIds(senderPetId, registry);
        if (subscriberConnIds.Count == 0)
        {
            return;
        }

        await Clients.Clients(subscriberConnIds)
            .SendAsync(SocialHubConstants.ReceiveMusicFrameClientMethod, packet)
            .ConfigureAwait(false);
    }

    private async Task NotifyPresenceChangedAsync(string petId, bool includeNonFriends)
    {
        var fullDto = presence.BuildFriendDto(petId, includePrivateStates: true);
        if (fullDto is null)
        {
            return;
        }

        var (friendConnections, otherConnections) = presence.GetPresenceBroadcastTargets(petId, registry);
        if (friendConnections.Count > 0)
        {
            await Clients.Clients(friendConnections)
                .SendAsync(SocialHubConstants.ReceiveFriendPresenceChangedClientMethod, fullDto)
                .ConfigureAwait(false);
        }

        if (includeNonFriends && otherConnections.Count > 0)
        {
            var publicDto = presence.BuildFriendDto(petId, includePrivateStates: false);
            if (publicDto is not null)
            {
                await Clients.Clients(otherConnections)
                    .SendAsync(SocialHubConstants.ReceiveFriendPresenceChangedClientMethod, publicDto)
                    .ConfigureAwait(false);
            }
        }
    }

    private string? GetAuthenticatedPetId()
        => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);

    private string RequireAuthenticatedPetId()
        => GetAuthenticatedPetId() ?? throw new HubException("UNAUTHENTICATED");

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ProtocolJson.PacketOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
