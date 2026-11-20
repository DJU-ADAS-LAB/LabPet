using LabPet.Shared.Protocol;

namespace LabPet.Shared.Models;

/// <summary>
/// %AppData%\LabPet\friends.json 에 저장되는 친구 목록 루트 문서.
/// </summary>
public sealed class FriendsStore
{
    public int SchemaVersion { get; set; } = ProtocolConstants.CurrentStorageSchemaVersion;
    public List<FriendEntry> Friends { get; set; } = [];
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 페어링 완료된 친구 정보. IP가 변경되어도 영구 <see cref="PetId"/>를 키로 식별한다.
/// </summary>
public sealed class FriendEntry
{
    public string PetId { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public string? LastKnownIp { get; set; }
    public int LastKnownTcpPort { get; set; } = ProtocolConstants.TcpControlPort;

    /// <summary>친구 전용 패킷 HMAC-SHA256 서명 검증에 사용하는 32바이트 공유 비밀키(Base64).</summary>
    public string PairSecretBase64 { get; set; } = string.Empty;

    /// <summary>오프라인 상태에서도 표시할 수 있도록 캐시된 3D 외형.</summary>
    public PetAppearance CachedAppearance { get; set; } = new();

    public int Intimacy { get; set; }
    public DateTimeOffset PairedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAtUtc { get; set; }
}
