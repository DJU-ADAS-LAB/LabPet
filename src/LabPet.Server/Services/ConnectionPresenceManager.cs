using LabPet.Server.Options;
using LabPet.Server.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Server.Services;

/// <summary>
/// 메모리 기반 실시간 SignalR 접속자, 최신 프레즌스, 원격 노래 따라하기 구독 관리.
/// 채팅 메시지나 오프라인 큐는 메모리에 쌓지 않는다.
/// </summary>
public sealed class ConnectionPresenceManager(ServerOptions options)
{
    private sealed class OnlinePetSession
    {
        public string PetId { get; }
        public List<string> ConnectionIds { get; } = [];
        public RemotePresenceUpdate? Presence { get; set; }

        public OnlinePetSession(string petId)
        {
            PetId = petId;
        }
    }

    private readonly int _maxTotalConnections = Math.Clamp(options.MaxTotalConnections, 10, 10_000);
    private readonly int _maxConnectionsPerPet = Math.Clamp(options.MaxConnectionsPerPet, 1, 8);
    private readonly object _lock = new();

    private readonly Dictionary<string, OnlinePetSession> _sessionsByPetId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _petIdByConnectionId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _musicSubscribersByHost = new(StringComparer.OrdinalIgnoreCase);

    public int OnlinePetCount
    {
        get { lock (_lock) return _sessionsByPetId.Count; }
    }

    public int ActiveConnectionCount
    {
        get { lock (_lock) return _petIdByConnectionId.Count; }
    }

    public bool TryAddConnection(string petId, string connectionId, out string errorCode)
    {
        errorCode = string.Empty;
        lock (_lock)
        {
            if (_petIdByConnectionId.Count >= _maxTotalConnections)
            {
                errorCode = "SERVER_CAPACITY_REACHED";
                return false;
            }

            if (!_sessionsByPetId.TryGetValue(petId, out var session))
            {
                session = new OnlinePetSession(petId);
                _sessionsByPetId[petId] = session;
            }

            if (session.ConnectionIds.Count >= _maxConnectionsPerPet)
            {
                errorCode = "TOO_MANY_CONNECTIONS_FOR_PET";
                return false;
            }

            if (!session.ConnectionIds.Contains(connectionId, StringComparer.Ordinal))
            {
                session.ConnectionIds.Add(connectionId);
            }

            _petIdByConnectionId[connectionId] = petId;
            return true;
        }
    }

    public (bool AnyChanged, bool PublicProfileChanged) UpdatePresence(string petId, RemotePresenceUpdate update)
    {
        var appearance = (update.Appearance ?? new PetAppearance()).Clone();
        appearance.SpeciesId = TrimMax(appearance.SpeciesId, 32);
        appearance.BodyColorHex = TrimMax(appearance.BodyColorHex, 16);
        appearance.AccentColorHex = TrimMax(appearance.AccentColorHex, 16);
        appearance.ExpressionId = TrimMax(appearance.ExpressionId, 32);
        appearance.HeadAccessoryId = TrimMax(appearance.HeadAccessoryId, 32);
        appearance.AuraEffectId = TrimMax(appearance.AuraEffectId, 32);

        var petName = TrimMax(update.PetName, 32);
        var appVersion = TrimMax(update.AppVersion, 24);
        var copy = new RemotePresenceUpdate
        {
            FriendCode = TrimMax(update.FriendCode, 32).ToUpperInvariant(),
            OwnerName = TrimMax(update.OwnerName, 32),
            PetName = string.IsNullOrWhiteSpace(petName) ? "LabPet" : petName,
            Appearance = appearance,
            Level = Math.Clamp(update.Level, 1, 9999),
            IsMusicActive = update.IsMusicActive,
            IsSleeping = update.IsSleeping,
            IsFocusing = update.IsFocusing,
            AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "0.1.0" : appVersion,
            ProtocolVersion = update.ProtocolVersion,
            KnownFriendPetIds = [],
            RemovedFriendPetIds = []
        };

        lock (_lock)
        {
            if (!_sessionsByPetId.TryGetValue(petId, out var session))
            {
                return (false, false);
            }

            var prev = session.Presence;
            var publicChanged = prev is null
                || !string.Equals(prev.OwnerName, copy.OwnerName, StringComparison.Ordinal)
                || !string.Equals(prev.PetName, copy.PetName, StringComparison.Ordinal)
                || prev.Level != copy.Level
                || !string.Equals(prev.AppVersion, copy.AppVersion, StringComparison.Ordinal)
                || prev.ProtocolVersion != copy.ProtocolVersion
                || !AppearanceEquals(prev.Appearance, copy.Appearance);

            var anyChanged = publicChanged
                || prev is null
                || prev.IsMusicActive != copy.IsMusicActive
                || prev.IsSleeping != copy.IsSleeping
                || prev.IsFocusing != copy.IsFocusing;

            session.Presence = copy;
            return (anyChanged, publicChanged);
        }
    }

    private static bool AppearanceEquals(PetAppearance a, PetAppearance b)
        => string.Equals(a.SpeciesId, b.SpeciesId, StringComparison.Ordinal)
           && string.Equals(a.BodyColorHex, b.BodyColorHex, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.AccentColorHex, b.AccentColorHex, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.ExpressionId, b.ExpressionId, StringComparison.Ordinal)
           && string.Equals(a.HeadAccessoryId, b.HeadAccessoryId, StringComparison.Ordinal)
           && string.Equals(a.AuraEffectId, b.AuraEffectId, StringComparison.Ordinal);

    public (string? PetId, bool BecameOffline) RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            if (!_petIdByConnectionId.Remove(connectionId, out var petId))
            {
                return (null, false);
            }

            if (!_sessionsByPetId.TryGetValue(petId, out var session))
            {
                return (petId, true);
            }

            session.ConnectionIds.Remove(connectionId);
            if (session.ConnectionIds.Count > 0)
            {
                return (petId, false);
            }

            _sessionsByPetId.Remove(petId);
            _musicSubscribersByHost.Remove(petId);
            foreach (var followers in _musicSubscribersByHost.Values)
            {
                followers.Remove(petId);
            }

            return (petId, true);
        }
    }

    public IReadOnlyList<string> GetConnectionIdsForPet(string petId)
    {
        lock (_lock)
        {
            if (_sessionsByPetId.TryGetValue(petId, out var session))
            {
                // 최신 연결을 먼저 시도하도록 역순 반환
                var list = new List<string>(session.ConnectionIds);
                list.Reverse();
                return list;
            }

            return [];
        }
    }

    public RemoteFriendStateDto? BuildFriendDto(string petId, bool includePrivateStates = true)
    {
        lock (_lock)
        {
            if (!_sessionsByPetId.TryGetValue(petId, out var session) || session.Presence is not { } p)
            {
                return null;
            }

            return BuildDtoFromSession(session.PetId, p, includePrivateStates);
        }
    }

    /// <summary>
    /// 로그인한 펫에게 보이는 온라인 피어 스냅샷(상호 친구 전체 상태 + 비친구 공개 최소 프로필)을 반환한다.
    /// 비친구는 음악 반응/수면 상태 등 프라이버시 필드를 비활성화(false)하여 노출하지 않는다.
    /// </summary>
    public IReadOnlyList<RemoteFriendStateDto> GetVisibleOnlinePeers(
        string callerPetId,
        IdentityRegistryStore registry,
        int maxNonFriends = 50,
        bool allowPublicDiscovery = true)
    {
        var nonFriendCap = allowPublicDiscovery ? Math.Clamp(maxNonFriends, 0, 200) : 0;
        var mutualCandidateSet = new HashSet<string>(
            registry.GetPotentialMutualFriendIds(callerPetId),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<RemoteFriendStateDto>();
        var nonFriendCount = 0;

        lock (_lock)
        {
            foreach (var session in _sessionsByPetId.Values)
            {
                if (string.Equals(session.PetId, callerPetId, StringComparison.OrdinalIgnoreCase)
                    || session.Presence is not { } p)
                {
                    continue;
                }

                if (mutualCandidateSet.Contains(session.PetId))
                {
                    result.Add(BuildDtoFromSession(session.PetId, p, includePrivateStates: true));
                }
                else if (nonFriendCount < nonFriendCap)
                {
                    result.Add(BuildDtoFromSession(session.PetId, p, includePrivateStates: false));
                    nonFriendCount++;
                }
            }
        }

        return result;
    }

    public (IReadOnlyList<string> MutualFriendConnectionIds, IReadOnlyList<string> NonFriendConnectionIds)
        GetPresenceBroadcastTargets(string petId, IdentityRegistryStore registry)
    {
        var mutualSet = new HashSet<string>(
            registry.GetPotentialMutualFriendIds(petId),
            StringComparer.OrdinalIgnoreCase);

        var friendConns = new List<string>();
        var otherConns = new List<string>();
        lock (_lock)
        {
            foreach (var session in _sessionsByPetId.Values)
            {
                if (string.Equals(session.PetId, petId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (mutualSet.Contains(session.PetId))
                {
                    friendConns.AddRange(session.ConnectionIds);
                }
                else
                {
                    otherConns.AddRange(session.ConnectionIds);
                }
            }
        }

        return (friendConns, otherConns);
    }

    public IReadOnlyList<string> GetOtherOnlineConnectionIds(string excludePetId)
    {
        var connIds = new List<string>();
        lock (_lock)
        {
            foreach (var session in _sessionsByPetId.Values)
            {
                if (!string.Equals(session.PetId, excludePetId, StringComparison.OrdinalIgnoreCase))
                {
                    connIds.AddRange(session.ConnectionIds);
                }
            }
        }

        return connIds;
    }

    private static RemoteFriendStateDto BuildDtoFromSession(string petId, RemotePresenceUpdate p, bool includePrivateStates)
        => new()
        {
            PetId = petId,
            FriendCode = includePrivateStates ? p.FriendCode : string.Empty,
            OwnerName = p.OwnerName,
            PetName = p.PetName,
            Appearance = p.Appearance.Clone(),
            Level = p.Level,
            IsMusicActive = includePrivateStates && p.IsMusicActive,
            IsSleeping = includePrivateStates && p.IsSleeping,
            IsFocusing = includePrivateStates && p.IsFocusing,
            AppVersion = p.AppVersion,
            ProtocolVersion = p.ProtocolVersion
        };

    public bool IsOnline(string petId)
    {
        lock (_lock)
        {
            return _sessionsByPetId.TryGetValue(petId, out var session) && session.ConnectionIds.Count > 0;
        }
    }

    public void SetMusicSubscription(string hostPetId, string followerPetId, bool enable)
    {
        lock (_lock)
        {
            if (enable)
            {
                if (!_musicSubscribersByHost.TryGetValue(hostPetId, out var followers))
                {
                    followers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _musicSubscribersByHost[hostPetId] = followers;
                }

                followers.Add(followerPetId);
            }
            else if (_musicSubscribersByHost.TryGetValue(hostPetId, out var followers))
            {
                followers.Remove(followerPetId);
                if (followers.Count == 0)
                {
                    _musicSubscribersByHost.Remove(hostPetId);
                }
            }
        }
    }

    public void RevokeMusicSubscriptionsBetween(string petA, string petB)
    {
        if (string.IsNullOrWhiteSpace(petA) || string.IsNullOrWhiteSpace(petB))
        {
            return;
        }

        lock (_lock)
        {
            if (_musicSubscribersByHost.TryGetValue(petA, out var followersA))
            {
                followersA.Remove(petB);
                if (followersA.Count == 0)
                {
                    _musicSubscribersByHost.Remove(petA);
                }
            }

            if (_musicSubscribersByHost.TryGetValue(petB, out var followersB))
            {
                followersB.Remove(petA);
                if (followersB.Count == 0)
                {
                    _musicSubscribersByHost.Remove(petB);
                }
            }
        }
    }

    public bool IsMusicFollower(string hostPetId, string followerPetId)
    {
        lock (_lock)
        {
            return _musicSubscribersByHost.TryGetValue(hostPetId, out var set)
                && set.Contains(followerPetId);
        }
    }

    public IReadOnlyList<string> GetMusicFollowerPetIds(string hostPetId, IdentityRegistryStore registry)
    {
        List<string> followerIds;
        lock (_lock)
        {
            if (!_musicSubscribersByHost.TryGetValue(hostPetId, out var set) || set.Count == 0)
            {
                return [];
            }

            followerIds = [.. set];
        }

        return followerIds
            .Where(followerId => registry.AreMutualFriends(hostPetId, followerId))
            .ToList();
    }

    public IReadOnlyList<string> GetMusicFollowerConnectionIds(string hostPetId, IdentityRegistryStore registry)
    {
        var followerIds = GetMusicFollowerPetIds(hostPetId, registry);
        if (followerIds.Count == 0)
        {
            return [];
        }

        var connIds = new List<string>();
        foreach (var followerId in followerIds)
        {
            lock (_lock)
            {
                if (_sessionsByPetId.TryGetValue(followerId, out var session))
                {
                    connIds.AddRange(session.ConnectionIds);
                }
            }
        }

        return connIds;
    }

    private static string TrimMax(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }
}
