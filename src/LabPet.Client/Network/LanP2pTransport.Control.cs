using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.Network;

public sealed partial class LanP2pTransport
{
    private Task TcpAcceptLoopAsync(TcpListener listener, CancellationToken ct)
        => Task.WhenAll(Enumerable.Range(0, MaxConcurrentControlConnections)
            .Select(_ => TcpControlWorkerAsync(listener, ct)));

    private async Task TcpControlWorkerAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                await HandleIncomingTcpClientAsync(client, ct).ConfigureAwait(false);
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

    private async Task HandleIncomingTcpClientAsync(TcpClient client, CancellationToken shutdownToken)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                timeout.CancelAfter(InboundControlTimeout);
                var ct = timeout.Token;

                var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                var remoteIp = remoteEndPoint is not null
                    ? (remoteEndPoint.Address.IsIPv4MappedToIPv6 ? remoteEndPoint.Address.MapToIPv4().ToString() : remoteEndPoint.Address.ToString())
                    : string.Empty;
                await using var stream = client.GetStream();

                var envelope = await ReadFramedJsonAsync<ControlPacketEnvelope>(stream, ct).ConfigureAwait(false);
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
                if (envelope.IsEncrypted || !ReceiveFriendRequest(envelope, remoteIp))
                    return RejectAck(header.MessageId, "PAIRING_REQUEST_REJECTED");
                break;

            case PacketType.FriendAccept:
                if (envelope.IsEncrypted || !ReceiveFriendAcceptance(envelope, remoteIp, out ackKey))
                    return RejectAck(header.MessageId, "PAIRING_CONFIRMATION_FAILED");
                break;

            case PacketType.FriendReject:
                if (envelope.IsEncrypted || !ReceiveFriendRejection(envelope))
                    return RejectAck(header.MessageId, "PAIRING_REQUEST_UNKNOWN");
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
                var p = JsonSerializer.Deserialize<GameEventPayload>(effectivePayloadJson, ProtocolJson.PacketOptions);
                if (p is null || string.IsNullOrWhiteSpace(p.GameType) || string.IsNullOrWhiteSpace(p.Action))
                {
                    return RejectAck(header.MessageId, "INVALID_PAYLOAD");
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
            var ackEnvelope = await ReadFramedJsonAsync<ControlPacketEnvelope>(stream, timeoutCts.Token).ConfigureAwait(false);
            return ReadVerifiedAck(ackEnvelope, header.MessageId, expectedPetId, confirmationKey ?? pairSecretBase64);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs($"Send:{packetType}", ex.Message, ex));
            return null;
        }
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

    private static async Task<T?> ReadFramedJsonAsync<T>(NetworkStream stream, CancellationToken ct)
    {
        var lengthPrefix = new byte[4];
        if (!await ReadExactAsync(stream, lengthPrefix, ct).ConfigureAwait(false))
        {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
        if (length <= 0 || length > 65536)
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
