using LabPet.Shared.Models;

namespace LabPet.Shared.Protocol;

/// <summary>
/// LAN P2P 및 향후 원격 프로토콜의 공용 패킷 유형.
/// </summary>
public enum PacketType
{
    Hello = 0,
    FriendRequest = 1,
    FriendAccept = 2,
    FriendReject = 3,
    BubbleMessage = 4,
    QuickReaction = 5,
    PetInteraction = 6,
    MusicFollowOn = 7,
    MusicFollowOff = 8,
    Ack = 9,
    GameEvent = 10,
    PresenceSync = 11
}

/// <summary>
/// 모든 TCP 제어 패킷에 포함되는 공통 헤더.
/// </summary>
public sealed class PacketHeader
{
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentProtocolVersion;
    public string AppVersion { get; set; } = "0.1.0";
    public PacketType PacketType { get; set; }
    public string SenderPetId { get; set; } = string.Empty;
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>친구 전용 패킷의 HMAC-SHA256 서명(Base64). 비친구 패킷(FriendRequest 등)은 null.</summary>
    public string? SignatureBase64 { get; set; }
}

/// <summary>
/// UDP 45678 주기적 브로드캐스트 탐색 패킷.
/// </summary>
public sealed class DiscoveryHelloPacket
{
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentProtocolVersion;
    public string AppVersion { get; set; } = "0.1.0";
    public string PetId { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public PetAppearance Appearance { get; set; } = new();
    public int Level { get; set; } = 1;
    public bool IsMusicActive { get; set; }
    public bool IsSleeping { get; set; }
    public int TcpPort { get; set; } = ProtocolConstants.TcpControlPort;
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// UDP 45679 구독자 대상 30Hz 경량 오디오 프레임 유니캐스트 패킷.
/// </summary>
public sealed class MusicFramePacket
{
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentProtocolVersion;
    public string SenderPetId { get; set; } = string.Empty;
    public uint Sequence { get; set; }
    public string SubscriptionId { get; set; } = string.Empty;
    public float Bass { get; set; }
    public float Mid { get; set; }
    public float Treble { get; set; }
    public bool IsBeat { get; set; }
    public string SignatureBase64 { get; set; } = string.Empty;
}

/// <summary>
/// TCP 45680 제어 패킷 봉투.
/// 친구 전용 패킷은 <see cref="IsEncrypted"/>가 true이며, <see cref="PayloadJson"/>에 AES-256-GCM 암호문(Base64)이 담긴다.
/// </summary>
public sealed class ControlPacketEnvelope
{
    public PacketHeader Header { get; set; } = new();
    public string PayloadJson { get; set; } = "{}";
    public bool IsEncrypted { get; set; }
    public string? NonceBase64 { get; set; }
    public string? AuthTagBase64 { get; set; }
}

public sealed class FriendRequestPayload
{
    public string RequestId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string RequesterOwnerName { get; set; } = string.Empty;
    public string RequesterName { get; set; } = string.Empty;
    public PetAppearance Appearance { get; set; } = new();
    public int Level { get; set; } = 1;
    public int TcpPort { get; set; } = ProtocolConstants.TcpControlPort;
}

public sealed class FriendAcceptPayload
{
    public string RequestId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string AccepterOwnerName { get; set; } = string.Empty;
    public string AccepterName { get; set; } = string.Empty;
    public PetAppearance Appearance { get; set; } = new();
    public int TcpPort { get; set; } = ProtocolConstants.TcpControlPort;
}

public sealed class FriendRejectPayload
{
    public string RequestId { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public sealed class BubbleMessagePayload
{
    public string Text { get; set; } = string.Empty;
}

public sealed class QuickReactionPayload
{
    public string ReactionId { get; set; } = string.Empty;
}

public sealed class PetInteractionPayload
{
    public string InteractionType { get; set; } = "pet";
}

public sealed class MusicFollowPayload
{
    public int ListenerUdpPort { get; set; } = ProtocolConstants.UdpMusicFramePort;
    public string SubscriptionId { get; set; } = string.Empty;
}

public sealed class AckPayload
{
    public string AckMessageId { get; set; } = string.Empty;
    public bool Accepted { get; set; } = true;
    public string? ErrorCode { get; set; }
    public FriendPresencePayload? Presence { get; set; }
}

public sealed class FriendPresencePayload
{
    public string OwnerName { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public PetAppearance Appearance { get; set; } = new();
    public int Level { get; set; }
    public bool IsMusicActive { get; set; }
    public bool IsSleeping { get; set; }
    public int TcpPort { get; set; }
    public string AppVersion { get; set; } = string.Empty;
}

/// <summary>
/// 친구 전용 LAN P2P 코인 게임(가위바위보, 타이핑 스프린트, 공동 사다리) 이벤트 페이로드.
/// </summary>
public sealed class GameEventPayload
{
    /// <summary>게임 종류: "rps", "typing", "ladder".</summary>
    public string GameType { get; set; } = string.Empty;

    /// <summary>게임 세션 고유 ID.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>동작: "invite", "accept", "decline", "move", "progress", "finish", "cancel".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>배팅/참가 코인.</summary>
    public int WagerCoins { get; set; }

    /// <summary>선택값(rock/paper/scissors), 타이핑 점수, 사다리 결과 요약 등 부가 데이터.</summary>
    public string Data { get; set; } = string.Empty;
}
