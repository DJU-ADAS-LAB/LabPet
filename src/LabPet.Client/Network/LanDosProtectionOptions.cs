namespace LabPet.Client.Network;

/// <summary>
/// LAN P2P 전송 계층의 DoS 방어 및 자원 상한 설정.
/// 연구실 등 동일 서브넷 다중 클라이언트 및 30Hz 오디오 동기화 동작을 해치지 않는 기본 상한을 사용한다.
/// </summary>
public sealed class LanDosProtectionOptions
{
    // 역직렬화 이전 바이트 크기 제한
    public int MaxDiscoveryUdpBytes { get; set; } = 4096;
    public int MaxMusicUdpBytes { get; set; } = 1024;
    public int MaxControlTcpBytes { get; set; } = 16 * 1024;

    // UDP 탐색(45678) 빈도 제한
    public int DiscoveryPerIpPerSecond { get; set; } = 8;
    public int DiscoveryGlobalPerSecond { get; set; } = 60;

    // UDP 음악 프레임(45679) 빈도 제한 (30Hz 동기화 + 지터 여유 반영)
    public int MusicPerFriendPerSecond { get; set; } = 45;
    public int MusicPerIpPerSecond { get; set; } = 120;
    public int MusicGlobalPerSecond { get; set; } = 400;

    // TCP 제어(45680) 동시 연결 및 빈도 제한 (친구 전용 예약 슬롯 포함)
    public int MaxTotalControlConnections { get; set; } = 16;
    public int ReservedFriendControlConnections { get; set; } = 8;
    public int MaxConcurrentTcpPerIp { get; set; } = 6;
    public int MaxConcurrentUnauthenticatedTcpPerIp { get; set; } = 3;
    public int TcpRequestsPerIpPerSecond { get; set; } = 30;
    public int TcpRequestsGlobalPerSecond { get; set; } = 120;

    // 미인증 탐색 피어 캐시 상한 및 TTL
    public int MaxUnauthenticatedPeers { get; set; } = 64;
    public int UnauthenticatedPeerTtlSeconds { get; set; } = 10;

    // 기능별 수신 요청 제한
    public int FriendRequestPerIpPerMinute { get; set; } = 6;
    public int FriendRequestGlobalPerMinute { get; set; } = 20;
    public int MessagePerSenderPer10Seconds { get; set; } = 12;
    public int InteractionPerSenderPer10Seconds { get; set; } = 8;
    public int GameEventPerSenderPer10Seconds { get; set; } = 40;
    public int MusicControlPerSenderPer10Seconds { get; set; } = 12;

    // 탐색 이벤트 병합 및 UI 알림 스로틀
    public int PeersChangedMinIntervalMs { get; set; } = 200;

    // 바운디드 레이트 리미터 버킷 최대 추적 수
    public int MaxTrackedRateBuckets { get; set; } = 512;
}
