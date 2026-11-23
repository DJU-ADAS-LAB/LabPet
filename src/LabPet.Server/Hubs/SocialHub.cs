using System.Security.Claims;
using System.Text.Json;
using LabPet.Server.Options;
using LabPet.Server.Services;
using LabPet.Server.Storage;
using LabPet.Shared.Protocol;
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

        if (request is null || request.Envelope?.Header is null || string.IsNullOrWhiteSpace(request.TargetPetId)
            || request.TargetPetId.Length > 64
            || string.Equals(request.TargetPetId, senderPetId, StringComparison.OrdinalIgnoreCase))
        {
            return new RelayDeliveryResult(false, "INVALID_REQUEST");
        }

        var envelope = request.Envelope;
        var header = envelope.Header;

        // 발신자 ID 위조 차단: 반드시 인증된 토큰의 PetId와 일치해야 함
        if (!string.Equals(header.SenderPetId, senderPetId, StringComparison.OrdinalIgnoreCase))
        {
            return new RelayDeliveryResult(false, "SENDER_MISMATCH");
        }

        if (ProtocolConstants.EvaluateCompatibility(header.ProtocolVersion) != ProtocolCompatibility.Compatible)
        {
            return new RelayDeliveryResult(false, "PROTOCOL_MISMATCH");
        }

        if (!Guid.TryParseExact(header.MessageId, "N", out _))
        {
            return new RelayDeliveryResult(false, "INVALID_MESSAGE_ID");
        }

        var now = DateTimeOffset.UtcNow;
        if ((header.TimestampUtc - now).Duration() > ControlTimestampTolerance)
        {
            return new RelayDeliveryResult(false, "STALE_TIMESTAMP");
        }

        var payloadLen = envelope.PayloadJson?.Length ?? 0;
        if (payloadLen <= 0 || payloadLen > MaxPayloadJsonLength || payloadLen * 2 > options.MaxControlPacketBytes)
        {
            return new RelayDeliveryResult(false, "PAYLOAD_TOO_LARGE");
        }

        if (!rateLimiter.AllowControlPacket(senderPetId))
        {
            return new RelayDeliveryResult(false, "RATE_LIMITED");
        }

        if (!rateLimiter.TryMarkMessageSeen(senderPetId, header.MessageId))
        {
            return new RelayDeliveryResult(false, "DUPLICATE_MESSAGE");
        }

        var targetPetId = request.TargetPetId.Trim();
        string? pairingRequestId = null;

        switch (header.PacketType)
        {
            case PacketType.FriendRequest:
            {
                if (envelope.IsEncrypted)
                {
                    return new RelayDeliveryResult(false, "INVALID_PAIRING_ENVELOPE");
                }

                if (registry.AreMutualFriends(senderPetId, targetPetId))
                {
                    return new RelayDeliveryResult(false, "ALREADY_FRIENDS");
                }

                var reqPayload = TryDeserialize<FriendRequestPayload>(envelope.PayloadJson!);
                if (reqPayload is null || !Guid.TryParseExact(reqPayload.RequestId, "N", out _))
                {
                    return new RelayDeliveryResult(false, "INVALID_PAIRING_REQUEST");
                }

                pairingRequestId = reqPayload.RequestId;
                pendingRequests.RecordRequest(pairingRequestId, senderPetId, targetPetId);
                break;
            }

            case PacketType.FriendAccept:
            {
                if (envelope.IsEncrypted)
                {
                    return new RelayDeliveryResult(false, "INVALID_PAIRING_ENVELOPE");
                }

                var acceptPayload = TryDeserialize<FriendAcceptPayload>(envelope.PayloadJson!);
                if (acceptPayload is null || !Guid.TryParseExact(acceptPayload.RequestId, "N", out _)
                    || !pendingRequests.IsValidResponseTarget(acceptPayload.RequestId, senderPetId, targetPetId))
                {
                    return new RelayDeliveryResult(false, "PAIRING_REQUEST_UNKNOWN");
                }

                pairingRequestId = acceptPayload.RequestId;
                break;
            }

            case PacketType.FriendReject:
            {
                if (envelope.IsEncrypted)
                {
                    return new RelayDeliveryResult(false, "INVALID_PAIRING_ENVELOPE");
                }

                var rejectPayload = TryDeserialize<FriendRejectPayload>(envelope.PayloadJson!);
                if (rejectPayload is null || !Guid.TryParseExact(rejectPayload.RequestId, "N", out _)
                    || !pendingRequests.IsValidResponseTarget(rejectPayload.RequestId, senderPetId, targetPetId))
                {
                    return new RelayDeliveryResult(false, "PAIRING_REQUEST_UNKNOWN");
                }

                pairingRequestId = rejectPayload.RequestId;
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
                    return new RelayDeliveryResult(false, "NOT_MUTUAL_FRIEND");
                }

                if (!envelope.IsEncrypted)
                {
                    return new RelayDeliveryResult(false, "ENCRYPTION_REQUIRED");
                }
                break;

            default:
                return new RelayDeliveryResult(false, "UNSUPPORTED_PACKET_TYPE");
        }

        // 수신자 오프라인 시 메시지 보관 없이 즉시 실패 반환
        var targetConnections = presence.GetConnectionIdsForPet(targetPetId);
        if (targetConnections.Count == 0)
        {
            if (header.PacketType == PacketType.FriendRequest && pairingRequestId is not null)
            {
                pendingRequests.Remove(pairingRequestId);
            }

            return new RelayDeliveryResult(false, "RECIPIENT_OFFLINE");
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

            return new RelayDeliveryResult(false, "DELIVERY_FAILED_OR_TIMEOUT");
        }

        // 수락 ACK 확인 시 서버 인증 테이블에도 친구 관계 영속 등록 및 양측 온라인 상태 동기화
        if (header.PacketType == PacketType.FriendAccept && pairingRequestId is not null)
        {
            var ack = TryDeserialize<AckPayload>(ackEnvelope.PayloadJson);
            if (ack is { Accepted: true })
            {
                registry.ApproveFriendship(senderPetId, targetPetId);
                pendingRequests.Remove(pairingRequestId);
                await NotifyPresenceChangedAsync(senderPetId, includeNonFriends: false).ConfigureAwait(false);
                await NotifyPresenceChangedAsync(targetPetId, includeNonFriends: false).ConfigureAwait(false);
            }
        }
        else if (header.PacketType == PacketType.FriendReject && pairingRequestId is not null)
        {
            pendingRequests.Remove(pairingRequestId);
        }
        else if (header.PacketType == PacketType.MusicFollowOn)
        {
            var ack = TryDeserialize<AckPayload>(ackEnvelope.PayloadJson);
            if (ack is { Accepted: true })
            {
                presence.SetMusicSubscription(hostPetId: targetPetId, followerPetId: senderPetId, enable: true);
            }
        }
        else if (header.PacketType == PacketType.MusicFollowOff)
        {
            presence.SetMusicSubscription(hostPetId: targetPetId, followerPetId: senderPetId, enable: false);
        }

        return new RelayDeliveryResult(true, null, ackEnvelope);
    }

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
