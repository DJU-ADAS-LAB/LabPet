using System.Collections.Concurrent;
using System.Text.Json;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Network;

public sealed partial class LanP2pTransport
{
    private readonly SemaphoreSlim _presenceSlots = new(8);
    private readonly ConcurrentDictionary<string, byte> _presenceInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Ip, int Port)> _presenceResendPending = new(StringComparer.OrdinalIgnoreCase);

    internal void HandleDiscoveryHello(DiscoveryHelloPacket hello, string ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hello.PetId)
            || hello.PetId.Length > 64
            || SamePetId(hello.PetId, _localPetId)
            || !IsValidPort(hello.TcpPort))
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

        var now = DateTimeOffset.UtcNow;
        var cleanOwner = TrimText(hello.OwnerName, 32);
        var cleanPet = TrimText(hello.PetName, 32);
        var cleanAppVer = TrimText(hello.AppVersion, 24);
        var cleanLevel = Math.Clamp(hello.Level, 1, 9999);
        var compat = ProtocolConstants.EvaluateCompatibility(hello.ProtocolVersion);
        var appearance = (hello.Appearance ?? new PetAppearance()).Clone();

        if (_peers.TryGetValue(hello.PetId, out var existing))
        {
            if (existing.IsFriend)
            {
                return;
            }

            var unchanged = string.Equals(existing.OwnerName, cleanOwner, StringComparison.Ordinal)
                && string.Equals(existing.PetName, cleanPet, StringComparison.Ordinal)
                && existing.Level == cleanLevel
                && !existing.IsMusicActive
                && !existing.IsSleeping
                && !existing.IsFocusing
                && string.Equals(existing.IpAddress, ip, StringComparison.OrdinalIgnoreCase)
                && existing.TcpPort == hello.TcpPort
                && existing.ProtocolVersion == hello.ProtocolVersion
                && string.Equals(existing.AppVersion, cleanAppVer, StringComparison.Ordinal)
                && existing.Compatibility == compat
                && AppearanceEquals(existing.Appearance, appearance);

            existing.LastSeenUtc = now;
            if (unchanged)
            {
                return;
            }

            existing.OwnerName = cleanOwner;
            existing.PetName = cleanPet;
            existing.Appearance = appearance;
            existing.Level = cleanLevel;
            existing.IsMusicActive = false;
            existing.IsSleeping = false;
            existing.IsFocusing = false;
            existing.IpAddress = ip;
            existing.TcpPort = hello.TcpPort;
            existing.ProtocolVersion = hello.ProtocolVersion;
            existing.AppVersion = cleanAppVer;
            existing.Compatibility = compat;
            RaisePeersChanged();
            return;
        }

        var maxUnauthenticated = Math.Clamp(_dosOptions.MaxUnauthenticatedPeers, 1, 2048);
        if (CountUnauthenticatedPeers() >= maxUnauthenticated)
        {
            PruneUnauthenticatedPeers(now);
            if (CountUnauthenticatedPeers() >= maxUnauthenticated)
            {
                return;
            }
        }

        var snapshot = new PeerSnapshot
        {
            PetId = hello.PetId.Trim(),
            OwnerName = cleanOwner,
            PetName = cleanPet,
            Appearance = appearance,
            Level = cleanLevel,
            IsMusicActive = false,
            IsSleeping = false,
            IsFocusing = false,
            IpAddress = ip,
            TcpPort = hello.TcpPort,
            ProtocolVersion = hello.ProtocolVersion,
            AppVersion = cleanAppVer,
            Compatibility = compat,
            LastSeenUtc = now
        };

        if (_peers.TryAdd(snapshot.PetId, snapshot))
        {
            RaisePeersChanged();
        }
    }

    private int CountUnauthenticatedPeers()
    {
        var count = 0;
        foreach (var peer in _peers.Values)
        {
            if (!peer.IsFriend)
            {
                count++;
            }
        }

        return count;
    }

    private void PruneUnauthenticatedPeers(DateTimeOffset now)
    {
        var unauthTtl = Math.Clamp(_dosOptions.UnauthenticatedPeerTtlSeconds, 1, ProtocolConstants.PeerOfflineTimeoutSeconds);
        var cutoff = now - TimeSpan.FromSeconds(unauthTtl);
        foreach (var kv in _peers)
        {
            if (!kv.Value.IsFriend && kv.Value.LastSeenUtc < cutoff)
            {
                _peers.TryRemove(kv.Key, out _);
            }
        }
    }

    private static string TrimText(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }

    private static bool AppearanceEquals(PetAppearance a, PetAppearance b)
        => string.Equals(a.SpeciesId, b.SpeciesId, StringComparison.Ordinal)
           && string.Equals(a.BodyColorHex, b.BodyColorHex, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.AccentColorHex, b.AccentColorHex, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.ExpressionId, b.ExpressionId, StringComparison.Ordinal)
           && string.Equals(a.HeadAccessoryId, b.HeadAccessoryId, StringComparison.Ordinal)
           && string.Equals(a.AuraEffectId, b.AuraEffectId, StringComparison.Ordinal);

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

            if (_friendsByPetId.TryGetValue(peer.PetId, out var friend))
            {
                if (_presenceInFlight.TryAdd(friend.PetId, 0))
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
                else
                {
                    // 이전 프레즌스 동기화가 진행 중이면 완료 직후 최신 상태를 한 번 더 전송하도록 예약한다.
                    _presenceResendPending[friend.PetId] = (peer.IpAddress, peer.TcpPort);
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
            if (!ct.IsCancellationRequested
                && _presenceResendPending.TryRemove(friend.PetId, out var pendingEndpoint)
                && _friendsByPetId.TryGetValue(friend.PetId, out var latestFriend)
                && _presenceInFlight.TryAdd(latestFriend.PetId, 0))
            {
                if (_presenceSlots.Wait(0))
                {
                    StartPresenceVerification(latestFriend, pendingEndpoint.Ip, pendingEndpoint.Port);
                }
                else
                {
                    _presenceInFlight.TryRemove(latestFriend.PetId, out _);
                }
            }
        }
    }

    private FriendPresencePayload BuildPresence()
    {
        var local = _localPresence;
        return new FriendPresencePayload
        {
            OwnerName = local.OwnerName, PetName = local.PetName, Appearance = local.Appearance,
            Level = local.Level, IsMusicActive = local.IsMusicActive, IsSleeping = local.IsSleeping,
            IsFocusing = local.IsFocusing,
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
            Level = presence.Level, IsMusicActive = presence.IsMusicActive, IsSleeping = presence.IsSleeping,
            IsFocusing = presence.IsFocusing, IsFriend = true,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion, AppVersion = presence.AppVersion,
            Compatibility = ProtocolCompatibility.Compatible, LastSeenUtc = now
        };
        RaisePeersChanged();
        return true;
    }
}
