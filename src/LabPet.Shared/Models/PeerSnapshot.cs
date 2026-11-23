using LabPet.Shared.Protocol;

namespace LabPet.Shared.Models;

/// <summary>
/// 피어와의 실시간 통신 경로 종류.
/// </summary>
public enum PeerTransportKind
{
    Lan = 0,
    GcpRelay = 1
}

/// <summary>
/// LAN 탐색 또는 원격 전송 계층에서 관측된 온라인 피어 스냅샷.
/// </summary>
public sealed class PeerSnapshot
{
    public string PetId { get; set; } = string.Empty;
    public string FriendCode { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public PetAppearance Appearance { get; set; } = new();
    public int Level { get; set; } = 1;
    public bool IsMusicActive { get; set; }
    public bool IsSleeping { get; set; }
    public PeerTransportKind TransportKind { get; set; } = PeerTransportKind.Lan;
    public string IpAddress { get; set; } = string.Empty;
    public int TcpPort { get; set; } = ProtocolConstants.TcpControlPort;
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentProtocolVersion;
    public string AppVersion { get; set; } = "0.1.0";
    public ProtocolCompatibility Compatibility { get; set; } = ProtocolCompatibility.Compatible;
    public bool IsFriend { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>프로토콜 버전 불일치로 업데이트가 필요한지 여부.</summary>
    public bool RequiresUpdate => Compatibility != ProtocolCompatibility.Compatible;
}

/// <summary>
/// 로컬 펫이 네트워크에 송출하는 현재 상태 스냅샷.
/// </summary>
public sealed record LocalPresenceState(
    string OwnerName,
    string PetName,
    PetAppearance Appearance,
    int Level,
    bool IsMusicActive,
    string AppVersion,
    bool IsSleeping = false,
    string FriendCode = "");

/// <summary>
/// 노래 따라하기(30Hz)용 경량 오디오 대역 프레임 데이터.
/// </summary>
public readonly record struct MusicFrameData(
    float Bass,
    float Mid,
    float Treble,
    bool IsBeat,
    uint Sequence);
