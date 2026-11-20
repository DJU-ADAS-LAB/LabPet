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
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(3);
    private readonly object _pairingLock = new();
    private readonly Dictionary<string, OutgoingPairing> _outgoingPairings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingPairing> _incomingPairings = new(StringComparer.Ordinal);

    private sealed class OutgoingPairing(string peerId) : IDisposable
    {
        public string PeerId { get; } = peerId;
        public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
        public string RequestFingerprint { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow + PairingLifetime;
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
        // 응답 확인이 유실돼도 같은 키로 안전하게 재시도한다.
        public ECDiffieHellman? ReplyKey { get; set; }
        public bool IsResponding { get; set; }
        public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow + PairingLifetime;
        public void Dispose() => ReplyKey?.Dispose();
    }

    public async Task<bool> SendFriendRequestAsync(
        PeerSnapshot target, CancellationToken cancellationToken = default)
    {
        if (target.RequiresUpdate || string.IsNullOrWhiteSpace(target.IpAddress)
            || string.IsNullOrWhiteSpace(target.PetId) || !IsValidPort(target.TcpPort))
        {
            return false;
        }

        var requestId = Guid.NewGuid().ToString("N");
        FriendRequestPayload payload;
        var presence = _localPresence;
        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (_friendsByPetId.ContainsKey(target.PetId) || _outgoingPairings.Count >= MaxPendingPairings
                || _outgoingPairings.Values.Any(pair => SamePetId(pair.PeerId, target.PetId)))
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
                TcpPort = _boundTcpPort
            };
            pending.RequestFingerprint = Fingerprint(JsonSerializer.Serialize(payload, ProtocolJson.PacketOptions));
            _outgoingPairings.Add(requestId, pending);
        }
        var ack = await SendControlPacketAsync(target.IpAddress, target.TcpPort, PacketType.FriendRequest,
            payload, null, cancellationToken, target.PetId).ConfigureAwait(false);
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

    public async Task<bool> RespondFriendRequestAsync(
        string requestId, bool accept, CancellationToken cancellationToken = default)
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
                keys = PacketCrypto.DerivePairingKeys(pending.ReplyKey, pending.PublicKey, requestId,
                    pending.Request.RequesterPetId, _localPetId, pending.PublicKey, publicKey,
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
        var ack = keys is { } pairKeys
            ? await SendControlPacketAsync(request.RemoteIp, request.RemoteTcpPort, PacketType.FriendAccept,
                new FriendAcceptPayload
                {
                    RequestId = requestId, PublicKey = publicKey!, TcpPort = _boundTcpPort,
                    AccepterOwnerName = presence.OwnerName, AccepterName = presence.PetName, Appearance = presence.Appearance.Clone()
                }, null, cancellationToken, request.RequesterPetId, pairKeys.ConfirmationKey).ConfigureAwait(false)
            : await SendControlPacketAsync(request.RemoteIp, request.RemoteTcpPort, PacketType.FriendReject,
                new FriendRejectPayload { RequestId = requestId, Reason = "rejected_by_user" },
                null, cancellationToken, request.RequesterPetId).ConfigureAwait(false);

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
            FriendAccepted?.Invoke(this, new FriendAcceptedEventArgs(request.RequesterPetId,
                request.RequesterOwnerName, request.RequesterName, request.Appearance,
                confirmedKeys.FriendKey, request.RemoteIp, request.RemoteTcpPort));
        }
        return true;
    }

    private bool ReceiveFriendRequest(ControlPacketEnvelope envelope, string remoteIp)
    {
        var header = envelope.Header;
        var payload = JsonSerializer.Deserialize<FriendRequestPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _) || !IsValidPort(payload.TcpPort)
            || string.IsNullOrWhiteSpace(remoteIp) || !PacketCrypto.IsPairingPublicKey(payload.PublicKey))
        {
            return false;
        }

        var request = new FriendRequestReceivedEventArgs(payload.RequestId, header.SenderPetId,
            payload.RequesterOwnerName ?? string.Empty, payload.RequesterName, payload.Appearance ?? new PetAppearance(),
            payload.Level, remoteIp, payload.TcpPort);
        lock (_pairingLock)
        {
            PrunePairings(DateTimeOffset.UtcNow);
            if (_friendsByPetId.ContainsKey(header.SenderPetId) || _incomingPairings.Count >= MaxPendingPairings
                || _incomingPairings.ContainsKey(payload.RequestId)
                || _incomingPairings.Values.Any(pair => SamePetId(pair.Request.RequesterPetId, header.SenderPetId)))
            {
                return false;
            }
            _incomingPairings.Add(payload.RequestId, new IncomingPairing(request, payload.PublicKey, Fingerprint(envelope.PayloadJson)));
        }

        FriendRequestReceived?.Invoke(this, request);
        return true;
    }

    private bool ReceiveFriendAcceptance(ControlPacketEnvelope envelope, string remoteIp, out string? confirmationKey)
    {
        confirmationKey = null;
        var header = envelope.Header;
        var payload = JsonSerializer.Deserialize<FriendAcceptPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
        if (payload is null || !Guid.TryParseExact(payload.RequestId, "N", out _) || !IsValidPort(payload.TcpPort)
            || string.IsNullOrWhiteSpace(remoteIp))
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

            var keys = PacketCrypto.DerivePairingKeys(pending.Key, payload.PublicKey, payload.RequestId,
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

        FriendAccepted?.Invoke(this, new FriendAcceptedEventArgs(header.SenderPetId,
            payload.AccepterOwnerName ?? string.Empty, payload.AccepterName, payload.Appearance ?? new PetAppearance(),
            friendKey, remoteIp, payload.TcpPort));
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

    private void PrunePairings(DateTimeOffset now)
    {
        foreach (var item in _outgoingPairings.Where(item => item.Value.ExpiresAt <= now).ToArray())
        {
            _outgoingPairings.Remove(item.Key);
            item.Value.Dispose();
        }
        foreach (var item in _incomingPairings.Where(item => item.Value.ExpiresAt <= now && !item.Value.IsResponding).ToArray())
        {
            _incomingPairings.Remove(item.Key);
            item.Value.Dispose();
        }
    }

    private static bool SamePetId(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool IsValidPort(int port) => port is > 0 and <= 65535;
    private static string Fingerprint(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
