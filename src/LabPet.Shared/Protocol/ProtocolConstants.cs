namespace LabPet.Shared.Protocol;

/// <summary>
/// LAN P2P 및 향후 원격 전송 계층이 공유하는 프로토콜 버전, 포트 및 타임아웃 상수.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>현재 클라이언트가 송신하는 프로토콜 버전.</summary>
    public const int CurrentProtocolVersion = 1;

    /// <summary>이 클라이언트가 하위 호환으로 수용할 수 있는 최소 프로토콜 버전.</summary>
    public const int MinSupportedProtocolVersion = 1;

    /// <summary>로컬 JSON 저장소 프로필/친구 스키마 현재 버전.</summary>
    public const int CurrentStorageSchemaVersion = 1;

    /// <summary>LAN 피어 탐색(HELLO 브로드캐스트) UDP 포트.</summary>
    public const int UdpDiscoveryPort = 45678;

    /// <summary>노래 따라하기(30Hz 오디오 프레임 유니캐스트) UDP 포트.</summary>
    public const int UdpMusicFramePort = 45679;

    /// <summary>친구 신청/수락, 말풍선 메시지, 구독 제어용 TCP 포트.</summary>
    public const int TcpControlPort = 45680;

    /// <summary>UDP HELLO 송신 주기(초).</summary>
    public const int DiscoveryIntervalSeconds = 3;

    /// <summary>피어 무응답 시 오프라인으로 판정하는 기준(초).</summary>
    public const int PeerOfflineTimeoutSeconds = 10;

    /// <summary>
    /// 노래 따라하기 호스트 무음 자동 해제 기준(ms).
    /// 곡 간 전환·잔잔한 구간·짧은 일시정지 시 바로 끊기지 않도록 3분(180,000ms) 여유를 둔다.
    /// </summary>
    public const int MusicHostSilenceTimeoutMs = 180_000;

    /// <summary>
    /// 노래 따라하기 UDP 프레임 미수신(호스트 종료/오프라인) 시 로컬 모드 복귀 기준(ms).
    /// 일시적 Wi-Fi 지터로 끊기지 않도록 15초(15,000ms)로 설정한다.
    /// </summary>
    public const int MusicFrameTimeoutMs = 15_000;

    /// <summary>말풍선 메시지 최대 글자 수.</summary>
    public const int MaxBubbleMessageLength = 100;

    /// <summary>친구 인증용 HMAC 공유 비밀키 길이(바이트).</summary>
    public const int PairSecretByteLength = 32;

    /// <summary>상대 피어의 프로토콜 버전 호환성을 판정한다.</summary>
    public static ProtocolCompatibility EvaluateCompatibility(int peerProtocolVersion)
    {
        if (peerProtocolVersion < MinSupportedProtocolVersion)
        {
            return ProtocolCompatibility.PeerOutdated;
        }

        if (peerProtocolVersion > CurrentProtocolVersion)
        {
            return ProtocolCompatibility.LocalOutdated;
        }

        return ProtocolCompatibility.Compatible;
    }
}

/// <summary>
/// 피어 간 프로토콜 버전 호환 상태.
/// </summary>
public enum ProtocolCompatibility
{
    /// <summary>정상 통신 가능.</summary>
    Compatible = 0,

    /// <summary>상대 피어가 지원 최소 버전보다 낮음 (상대 업데이트 필요).</summary>
    PeerOutdated = 1,

    /// <summary>상대 피어가 현재 로컬 버전보다 높음 (로컬 업데이트 필요).</summary>
    LocalOutdated = 2
}
