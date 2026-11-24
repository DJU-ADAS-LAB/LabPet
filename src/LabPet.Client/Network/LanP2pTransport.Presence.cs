using System.Collections.Concurrent;
using System.Text.Json;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Network;

public sealed partial class LanP2pTransport
{
    private readonly SemaphoreSlim _presenceSlots = new(8);
    private readonly ConcurrentDictionary<string, byte> _presenceInFlight = new(StringComparer.OrdinalIgnoreCase);

    internal void HandleDiscoveryHello(DiscoveryHelloPacket hello, string ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hello.PetId) || hello.PetId.Length > 64 || SamePetId(hello.PetId, _localPetId))
        {
            return;
        }
        if (_friendsByPetId.TryGetValue(hello.PetId, out var friend))
        {
            if (!ct.IsCancellationRequested)
            {
                ProbeFriendPresence(hello, ip, friend);
            }
            return;
        }

        _peers[hello.PetId] = new PeerSnapshot
        {
            PetId = hello.PetId, OwnerName = hello.OwnerName ?? string.Empty, PetName = hello.PetName,
            Appearance = hello.Appearance ?? new PetAppearance(), Level = hello.Level, IsMusicActive = hello.IsMusicActive,
            IsSleeping = hello.IsSleeping, IpAddress = ip, TcpPort = hello.TcpPort,
            ProtocolVersion = hello.ProtocolVersion, AppVersion = hello.AppVersion,
            Compatibility = ProtocolConstants.EvaluateCompatibility(hello.ProtocolVersion), LastSeenUtc = DateTimeOffset.UtcNow
        };
        RaisePeersChanged();
    }

    private void PushPresenceToOnlineFriends(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        foreach (var peer in _peers.Values)
        {
            if (!peer.IsFriend || peer.RequiresUpdate || string.IsNullOrWhiteSpace(peer.IpAddress) || !IsValidPort(peer.TcpPort))
            {
                continue;
            }

            if (_friendsByPetId.TryGetValue(peer.PetId, out var friend) && _presenceInFlight.TryAdd(friend.PetId, 0))
            {
                if (_presenceSlots.Wait(0))
                {
                    StartPresenceVerification(friend, peer.IpAddress, peer.TcpPort);
                }
                else
                {
                    _presenceInFlight.TryRemove(friend.PetId, out _);
                }
            }
        }
    }

    private void ProbeFriendPresence(DiscoveryHelloPacket candidate, string ip, FriendEntry friend)
    {
        if (candidate.ProtocolVersion != ProtocolConstants.CurrentProtocolVersion || !IsValidPort(candidate.TcpPort)
            || !_presenceInFlight.TryAdd(friend.PetId, 0))
        {
            return;
        }

        if (!_presenceSlots.Wait(0))
        {
            _presenceInFlight.TryRemove(friend.PetId, out _);
            return;
        }
        StartPresenceVerification(friend, ip, candidate.TcpPort);
    }

    private void StartPresenceVerification(FriendEntry friend, string ip, int port)
    {
        var operation = BeginBackgroundWork(out var token);
        if (operation is null)
        {
            _presenceInFlight.TryRemove(friend.PetId, out _);
            _presenceSlots.Release();
            return;
        }
        _ = VerifyPresenceAsync(friend, ip, port, operation, token);
    }

    private async Task VerifyPresenceAsync(FriendEntry friend, string ip, int port, IDisposable operation, CancellationToken ct)
    {
        using var work = operation;
        try
        {
            var ack = await SendControlPacketAsync(ip, port, PacketType.PresenceSync, BuildPresence(),
                friend.PairSecretBase64, ct, friend.PetId).ConfigureAwait(false);
            if (!ct.IsCancellationRequested && ack is { Accepted: true, Presence: { } presence })
            {
                ApplyVerifiedPresence(friend.PetId, ip, presence);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("친구 프레즌스 반영 실패: {0}", ex);
        }
        finally
        {
            _presenceInFlight.TryRemove(friend.PetId, out _);
            _presenceSlots.Release();
        }
    }

    private FriendPresencePayload BuildPresence()
    {
        var local = _localPresence;
        return new FriendPresencePayload
        {
            OwnerName = local.OwnerName, PetName = local.PetName, Appearance = local.Appearance,
            Level = local.Level, IsMusicActive = local.IsMusicActive, IsSleeping = local.IsSleeping,
            TcpPort = _boundTcpPort, AppVersion = local.AppVersion
        };
    }

    private AckPayload ReceiveFriendPresence(PacketHeader header, string json, string ip)
    {
        var presence = JsonSerializer.Deserialize<FriendPresencePayload>(json, ProtocolJson.PacketOptions);
        if (presence is null || !ApplyVerifiedPresence(header.SenderPetId, ip, presence))
        {
            return RejectAck(header.MessageId, "INVALID_PRESENCE");
        }
        return new AckPayload { AckMessageId = header.MessageId, Accepted = true, Presence = BuildPresence() };
    }

    private bool ApplyVerifiedPresence(string petId, string ip, FriendPresencePayload presence)
    {
        if (!_friendsByPetId.TryGetValue(petId, out var friend) || !IsValidPort(presence.TcpPort)
            || presence.Appearance is null || string.IsNullOrWhiteSpace(presence.PetName)
            || !Version.TryParse(presence.AppVersion, out _))
        {
            return false;
        }
        var now = DateTimeOffset.UtcNow;
        lock (friend)
        {
            friend.LastKnownIp = ip;
            friend.LastKnownTcpPort = presence.TcpPort;
            friend.LastSeenAtUtc = now;
            friend.OwnerName = presence.OwnerName ?? string.Empty;
            friend.PetName = presence.PetName;
            friend.CachedAppearance = presence.Appearance.Clone();
        }
        _peers[petId] = new PeerSnapshot
        {
            PetId = petId, IpAddress = ip, TcpPort = presence.TcpPort, PetName = presence.PetName,
            OwnerName = presence.OwnerName ?? string.Empty, Appearance = presence.Appearance.Clone(),
            Level = presence.Level, IsMusicActive = presence.IsMusicActive, IsSleeping = presence.IsSleeping, IsFriend = true,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion, AppVersion = presence.AppVersion,
            Compatibility = ProtocolCompatibility.Compatible, LastSeenUtc = now
        };
        RaisePeersChanged();
        return true;
    }
}
