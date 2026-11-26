using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.Network;

public sealed partial class LanP2pTransport
{
    internal sealed record ControlSendOutcome(
        AckPayload? VerifiedAck,
        string? ErrorCode,
        bool IsTransient,
        string? Detail = null);

    private async Task TcpAcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                TryDispatchAcceptedTcpClient(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }
    }

    private void TryDispatchAcceptedTcpClient(TcpClient client, CancellationToken ct)
    {
        var acquiredTotal = false;
        var acquiredUnauth = false;
        var incrementedIp = false;
        var remoteIp = string.Empty;

        try
        {
            var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            remoteIp = remoteEndPoint is not null
                ? (remoteEndPoint.Address.IsIPv4MappedToIPv6 ? remoteEndPoint.Address.MapToIPv4().ToString() : remoteEndPoint.Address.ToString())
                : string.Empty;

            if (string.IsNullOrWhiteSpace(remoteIp))
            {
                client.Dispose();
                return;
            }

            if (!_tcpRequestLimiter.Allow("ip|" + remoteIp, 1000L, _dosOptions.TcpRequestsPerIpPerSecond)
                || !_tcpRequestLimiter.Allow("global", 1000L, _dosOptions.TcpRequestsGlobalPerSecond))
            {
                Interlocked.Increment(ref _droppedTcpConnectionsByLimit);
                client.Dispose();
                return;
            }

            var isFriendIp = IsKnownFriendIp(remoteIp);
            if (!isFriendIp)
            {
                if (!_unauthenticatedTcpSlots.Wait(0))
                {
                    Interlocked.Increment(ref _droppedTcpConnectionsByLimit);
                    client.Dispose();
                    return;
                }

                acquiredUnauth = true;
            }

            if (!_totalTcpSlots.Wait(0))
            {
                Interlocked.Increment(ref _droppedTcpConnectionsByLimit);
                client.Dispose();
                return;
            }

            acquiredTotal = true;

            var maxPerIp = isFriendIp
                ? Math.Clamp(_dosOptions.MaxConcurrentTcpPerIp, 1, 64)
                : Math.Clamp(_dosOptions.MaxConcurrentUnauthenticatedTcpPerIp, 1, 64);

            var activeForIp = _activeTcpByIp.AddOrUpdate(remoteIp, 1, static (_, prev) => prev + 1);
            incrementedIp = true;
            if (activeForIp > maxPerIp)
            {
                Interlocked.Increment(ref _droppedTcpConnectionsByLimit);
                client.Dispose();
                return;
            }

            var operation = BeginBackgroundWork(out var shutdownToken);
            if (operation is null || ct.IsCancellationRequested)
            {
                operation?.Dispose();
                client.Dispose();
                return;
            }

            _ = HandleAcceptedTcpClientWithSlotsAsync(
                client,
                remoteIp,
                acquiredUnauth,
                operation,
                shutdownToken);
            // 소유권이 비동기 핸들러로 이관됨
            acquiredTotal = false;
            acquiredUnauth = false;
            incrementedIp = false;
        }
        catch
        {
            client.Dispose();
        }
        finally
        {
            if (incrementedIp && !string.IsNullOrEmpty(remoteIp))
            {
                DecrementActiveTcpIp(remoteIp);
            }

            if (acquiredTotal)
            {
                _totalTcpSlots.Release();
            }

            if (acquiredUnauth)
            {
                _unauthenticatedTcpSlots.Release();
            }
        }
    }

    private async Task HandleAcceptedTcpClientWithSlotsAsync(
        TcpClient client,
        string remoteIp,
        bool acquiredUnauthSlot,
        IDisposable backgroundWork,
        CancellationToken shutdownToken)
    {
        try
        {
            using (backgroundWork)
            {
                await HandleIncomingTcpClientAsync(client, remoteIp, shutdownToken).ConfigureAwait(false);
            }
        }
        finally
        {
            DecrementActiveTcpIp(remoteIp);
            _totalTcpSlots.Release();
            if (acquiredUnauthSlot)
            {
                _unauthenticatedTcpSlots.Release();
            }
        }
    }

    private void DecrementActiveTcpIp(string remoteIp)
    {
        while (_activeTcpByIp.TryGetValue(remoteIp, out var current))
        {
            if (current <= 1)
            {
                if (_activeTcpByIp.TryRemove(new KeyValuePair<string, int>(remoteIp, current)))
                {
                    break;
                }
            }
            else if (_activeTcpByIp.TryUpdate(remoteIp, current - 1, current))
            {
                break;
            }
        }
    }

    private bool IsKnownFriendIp(string remoteIp)
    {
        foreach (var friend in _friendsByPetId.Values)
        {
            string? knownIp;
            lock (friend)
            {
                knownIp = friend.LastKnownIp;
            }

            if (!string.IsNullOrWhiteSpace(knownIp)
                && string.Equals(knownIp, remoteIp, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var peer in _peers.Values)
        {
            if (peer.IsFriend && !string.IsNullOrWhiteSpace(peer.IpAddress)
                && string.Equals(peer.IpAddress, remoteIp, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task HandleIncomingTcpClientAsync(TcpClient client, string remoteIp, CancellationToken shutdownToken)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                timeout.CancelAfter(InboundControlTimeout);
                var ct = timeout.Token;

                await using var stream = client.GetStream();

                var envelope = await ReadFramedJsonAsync<ControlPacketEnvelope>(stream, _dosOptions.MaxControlTcpBytes, ct)
                    .ConfigureAwait(false);
                if (envelope?.Header is null)
                {
                    return;
                }

                var ack = ProcessIncomingControlEnvelope(envelope, remoteIp, out var ackKey);
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

                await WriteFramedJsonAsync(stream, ackEnvelope, ct).ConfigureAwait(false);
            }
            catch
            {
                // 비정상 TCP 연결 종료 무시
            }
        }
    }

    private AckPayload ProcessIncomingControlEnvelope(ControlPacketEnvelope envelope, string remoteIp, out string? ackKey)
    {
        var header = envelope.Header;
        ackKey = null;

        // 1. 프로토콜 버전 호환성 차단
        var compat = ProtocolConstants.EvaluateCompatibility(header.ProtocolVersion);
        if (compat != ProtocolCompatibility.Compatible)
        {
            return new AckPayload
            {
                AckMessageId = header.MessageId,
                Accepted = false,
                ErrorCode = "PROTOCOL_MISMATCH"
            };
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

        // 2. 친구 전용 패킷은 AES-256-GCM 암호화 + HMAC-SHA256 서명 검증 필수
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
            return DispatchControlPacket(envelope, envelope.PayloadJson, remoteIp, null, out ackKey);
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
        if (!PacketCrypto.TryDecryptControlPayload(envelope, verifiedPairSecret, out var effectivePayloadJson))
        {
            return RejectAck(header.MessageId, "DECRYPTION_FAILED");
        }

        ackKey = verifiedPairSecret;
        var replayWindow = _replayWindows.GetOrAdd(header.SenderPetId, _ => new ControlReplayWindow());
        return replayWindow.Execute(header, Fingerprint($"{(int)header.PacketType}|{effectivePayloadJson}"), now, () =>
        {
            lock (friend)
            {
                if (!string.IsNullOrWhiteSpace(remoteIp))
                {
                    friend.LastKnownIp = remoteIp;
                }

                friend.LastSeenAtUtc = now;
            }

            return DispatchControlPacket(envelope, effectivePayloadJson, remoteIp, verifiedPairSecret, out _);
        });
    }

    private AckPayload DispatchControlPacket(ControlPacketEnvelope envelope, string effectivePayloadJson,
        string remoteIp, string? verifiedPairSecret, out string? ackKey)
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

                if (!_featureLimiter.Allow("fr|ip|" + remoteIp, 60_000L, _dosOptions.FriendRequestPerIpPerMinute)
                    || !_featureLimiter.Allow("fr|global", 60_000L, _dosOptions.FriendRequestGlobalPerMinute))
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.RateLimited);
                }

                if (_localPresence.IsFocusing)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.PomodoroFocusBlocked);
                }

                if (!ReceiveFriendRequest(envelope, remoteIp, out var requestError))
                {
                    return RejectAck(header.MessageId, requestError);
                }
                break;

            case PacketType.FriendAccept:
                if (envelope.IsEncrypted)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.InvalidPairingEnvelope);
                }

                if (!ReceiveFriendAcceptance(envelope, remoteIp, out ackKey, out var acceptError))
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
                if (!_featureLimiter.Allow("msg|" + header.SenderPetId, 10_000L, _dosOptions.MessagePerSenderPer10Seconds))
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.RateLimited);
                }

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
                if (!_featureLimiter.Allow("msg|" + header.SenderPetId, 10_000L, _dosOptions.MessagePerSenderPer10Seconds))
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.RateLimited);
                }

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
                if (!_featureLimiter.Allow("act|" + header.SenderPetId, 10_000L, _dosOptions.InteractionPerSenderPer10Seconds))
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.RateLimited);
                }

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
                if (!_featureLimiter.Allow("musctrl|" + header.SenderPetId, 10_000L, _dosOptions.MusicControlPerSenderPer10Seconds))
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.RateLimited);
                }

                if (_localPresence.IsFocusing)
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.PomodoroFocusBlocked);
                }

                var p = JsonSerializer.Deserialize<MusicFollowPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (p is not null && IsValidPort(p.ListenerUdpPort) && Guid.TryParseExact(p.SubscriptionId, "N", out _)
                    && IPAddress.TryParse(remoteIp, out var parsedIp) && verifiedPairSecret is not null)
                {
                    _musicSubscribers[header.SenderPetId] = new MusicSubscriber(
                        header.SenderPetId,
                        new IPEndPoint(parsedIp, p.ListenerUdpPort),
                        verifiedPairSecret,
                        p.SubscriptionId);
                    MusicFollowStateChanged?.Invoke(this, new MusicFollowStateChangedEventArgs(header.SenderPetId, true));
                    break;
                }
                return RejectAck(header.MessageId, "INVALID_SUBSCRIPTION");
            }

            case PacketType.MusicFollowOff:
            {
                if (!_featureLimiter.Allow("musctrl|" + header.SenderPetId, 10_000L, _dosOptions.MusicControlPerSenderPer10Seconds))
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.RateLimited);
                }

                var p = JsonSerializer.Deserialize<MusicFollowPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (_musicSubscribers.TryGetValue(header.SenderPetId, out var subscriber)
                    && subscriber.SubscriptionId == p?.SubscriptionId
                    && _musicSubscribers.TryRemove(new KeyValuePair<string, MusicSubscriber>(header.SenderPetId, subscriber)))
                {
                    MusicFollowStateChanged?.Invoke(this, new MusicFollowStateChangedEventArgs(header.SenderPetId, false));
                }

                break;
            }

            case PacketType.GameEvent:
            {
                if (!_featureLimiter.Allow("game|" + header.SenderPetId, 10_000L, _dosOptions.GameEventPerSenderPer10Seconds))
                {
                    return RejectAck(header.MessageId, FriendOperationErrorCodes.RateLimited);
                }

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
                return ReceiveFriendPresence(header, effectivePayloadJson, remoteIp);
            default:
                return RejectAck(header.MessageId, "UNSUPPORTED_TYPE");
        }

        return new AckPayload { AckMessageId = header.MessageId, Accepted = true };
    }

    private async Task<AckPayload?> SendControlPacketAsync<TPayload>(
        string remoteIp,
        int remotePort,
        PacketType packetType,
        TPayload payload,
        string? pairSecretBase64,
        CancellationToken ct,
        string expectedPetId,
        string? confirmationKey = null,
        string? messageId = null)
    {
        var outcome = await SendControlPacketWithOutcomeAsync(
            remoteIp,
            remotePort,
            packetType,
            payload,
            pairSecretBase64,
            ct,
            expectedPetId,
            confirmationKey,
            messageId).ConfigureAwait(false);
        return outcome.VerifiedAck;
    }

    private async Task<ControlSendOutcome> SendControlPacketWithOutcomeAsync<TPayload>(
        string remoteIp,
        int remotePort,
        PacketType packetType,
        TPayload payload,
        string? pairSecretBase64,
        CancellationToken ct,
        string expectedPetId,
        string? confirmationKey = null,
        string? messageId = null)
    {
        try
        {
            var payloadJson = JsonSerializer.Serialize(payload, ProtocolJson.PacketOptions);
            var header = new PacketHeader
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                AppVersion = _localPresence.AppVersion,
                PacketType = packetType,
                SenderPetId = _localPetId,
                MessageId = messageId ?? Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTimeOffset.UtcNow
            };

            ControlPacketEnvelope envelope;
            if (!string.IsNullOrWhiteSpace(pairSecretBase64))
            {
                var (ciphertextBase64, nonceBase64, tagBase64) =
                    PacketCrypto.EncryptControlPayload(header, payloadJson, pairSecretBase64);

                envelope = new ControlPacketEnvelope
                {
                    Header = header,
                    PayloadJson = ciphertextBase64,
                    IsEncrypted = true,
                    NonceBase64 = nonceBase64,
                    AuthTagBase64 = tagBase64
                };
                var signatureInput = PacketCrypto.BuildEncryptedPayloadSignatureInput(envelope);
                var canonical = PacketSigner.BuildControlCanonicalString(header, signatureInput);
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

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(OutboundControlTimeout);

            using var client = new TcpClient();
            await client.ConnectAsync(remoteIp, remotePort, timeoutCts.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            await WriteFramedJsonAsync(stream, envelope, timeoutCts.Token).ConfigureAwait(false);
            var ackEnvelope = await ReadFramedJsonAsync<ControlPacketEnvelope>(stream, _dosOptions.MaxControlTcpBytes, timeoutCts.Token)
                .ConfigureAwait(false);
            return InspectAckEnvelope(
                ackEnvelope,
                header.MessageId,
                expectedPetId,
                confirmationKey ?? pairSecretBase64,
                allowUnsignedRejection: packetType == PacketType.FriendAccept);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ControlSendOutcome(null, FriendOperationErrorCodes.Cancelled, false, "요청이 취소되었습니다.");
        }
        catch (OperationCanceledException ex)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs(
                $"Send:{packetType}",
                ex.Message,
                ex,
                FriendOperationErrorCodes.DeliveryFailedOrTimeout,
                PeerTransportKind.Lan));
            return new ControlSendOutcome(null, FriendOperationErrorCodes.DeliveryFailedOrTimeout, true, "상대방 응답 시간이 초과되었습니다.");
        }
        catch (SocketException sex)
        {
            var code = sex.SocketErrorCode == SocketError.ConnectionRefused
                ? FriendOperationErrorCodes.RecipientOffline
                : FriendOperationErrorCodes.NetworkError;
            TransportError?.Invoke(this, new TransportErrorEventArgs(
                $"Send:{packetType}",
                sex.Message,
                sex,
                code,
                PeerTransportKind.Lan));
            return new ControlSendOutcome(null, code, true, sex.Message);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs(
                $"Send:{packetType}",
                ex.Message,
                ex,
                FriendOperationErrorCodes.NetworkError,
                PeerTransportKind.Lan));
            return new ControlSendOutcome(null, FriendOperationErrorCodes.NetworkError, true, ex.Message);
        }
    }

    internal static ControlSendOutcome InspectAckEnvelope(
        ControlPacketEnvelope? envelope,
        string messageId,
        string expectedPetId,
        string? signatureKey,
        bool allowUnsignedRejection = false)
    {
        if (envelope?.Header is not { PacketType: PacketType.Ack } header || envelope.IsEncrypted
            || !string.Equals(header.SenderPetId, expectedPetId, StringComparison.OrdinalIgnoreCase))
        {
            return new ControlSendOutcome(null, FriendOperationErrorCodes.DeliveryFailedOrTimeout, true, "응답 헤더가 올바르지 않습니다.");
        }

        if (ProtocolConstants.EvaluateCompatibility(header.ProtocolVersion) != ProtocolCompatibility.Compatible)
        {
            return new ControlSendOutcome(null, FriendOperationErrorCodes.ProtocolMismatch, false, "프로토콜 버전이 호환되지 않습니다.");
        }

        AckPayload? ack;
        try
        {
            ack = JsonSerializer.Deserialize<AckPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        }
        catch (JsonException)
        {
            return new ControlSendOutcome(null, FriendOperationErrorCodes.DeliveryFailedOrTimeout, true, "응답 포맷을 해석할 수 없습니다.");
        }

        if (ack is null || ack.AckMessageId != messageId)
        {
            return new ControlSendOutcome(null, FriendOperationErrorCodes.DeliveryFailedOrTimeout, true, "응답 메시지 ID가 일치하지 않습니다.");
        }

        if (signatureKey is not null)
        {
            var hasSignature = !string.IsNullOrWhiteSpace(header.SignatureBase64);
            var signatureValid = hasSignature && VerifyPlaintextSignature(envelope, signatureKey);
            if (ack.Accepted)
            {
                if (!signatureValid)
                {
                    return new ControlSendOutcome(null, FriendOperationErrorCodes.InvalidAckSignature, false, "ACK 확인 서명 검증에 실패했습니다.");
                }
            }
            else if (hasSignature)
            {
                if (!signatureValid)
                {
                    return new ControlSendOutcome(null, FriendOperationErrorCodes.InvalidAckSignature, false, "거부 ACK 서명 검증에 실패했습니다.");
                }
            }
            else if (!allowUnsignedRejection)
            {
                return new ControlSendOutcome(null, FriendOperationErrorCodes.InvalidAckSignature, false, "서명되지 않은 ACK 응답입니다.");
            }
        }

        if (!ack.Accepted)
        {
            var code = string.IsNullOrWhiteSpace(ack.ErrorCode)
                ? FriendOperationErrorCodes.PairingRequestRejected
                : ack.ErrorCode;
            return new ControlSendOutcome(ack, code, FriendOperationErrorCodes.IsTransient(code), code);
        }

        return new ControlSendOutcome(ack, null, false);
    }

    internal static AckPayload? ReadVerifiedAck(
        ControlPacketEnvelope? envelope, string messageId, string expectedPetId, string? signatureKey)
    {
        if (envelope?.Header is not { PacketType: PacketType.Ack } header || envelope.IsEncrypted
            || !string.Equals(header.SenderPetId, expectedPetId, StringComparison.OrdinalIgnoreCase)
            || ProtocolConstants.EvaluateCompatibility(header.ProtocolVersion) != ProtocolCompatibility.Compatible
            || (signatureKey is not null && !VerifyPlaintextSignature(envelope, signatureKey)))
        {
            return null;
        }

        try
        {
            var ack = JsonSerializer.Deserialize<AckPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
            return ack?.AckMessageId == messageId ? ack : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SignPlaintext(ControlPacketEnvelope envelope, string key)
        => envelope.Header.SignatureBase64 = PacketSigner.ComputeSignature(
            PacketSigner.BuildControlCanonicalString(envelope.Header, envelope.PayloadJson), key);

    private static bool VerifyPlaintextSignature(ControlPacketEnvelope envelope, string key)
        => PacketSigner.VerifySignature(PacketSigner.BuildControlCanonicalString(envelope.Header, envelope.PayloadJson),
            envelope.Header.SignatureBase64, key);

    private static async Task WriteFramedJsonAsync<T>(NetworkStream stream, T value, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJson.PacketOptions);
        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, body.Length);
        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<T?> ReadFramedJsonAsync<T>(NetworkStream stream, int maxFrameBytes, CancellationToken ct)
    {
        var lengthPrefix = new byte[4];
        if (!await ReadExactAsync(stream, lengthPrefix, ct).ConfigureAwait(false))
        {
            return default;
        }

        var limit = Math.Clamp(maxFrameBytes, 1024, 65536);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
        if (length <= 0 || length > limit)
        {
            return default;
        }

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(body, ProtocolJson.PacketOptions);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
