namespace LabPet.Server.Options;

/// <summary>
/// LabPet 실시간 GCP 중계 서버 운영 설정 및 리소스 상한 상수.
/// 환경변수(`LABPET_*`) 또는 appsettings.json으로 오버라이드한다.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "LabPetServer";

    public string ListenUrl { get; set; } = "http://127.0.0.1:5000";

    /// <summary>HMAC Bearer 토큰 서명용 서버 비밀키(Base64, 32바이트 이상 권장). 미설정 시 RequireConfiguredSecret에 따라 동작.</summary>
    public string ServerSecret { get; set; } = string.Empty;

    /// <summary>true이면 ServerSecret 미설정 시 서버 시작을 거부한다. 운영(Production) 배포 시 반드시 true로 사용한다.</summary>
    public bool RequireConfiguredSecret { get; set; } = false;

    /// <summary>최소 영속 식별자·친구 승인 테이블 저장 경로 (메시지 내용은 절대 저장하지 않음).</summary>
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LabPetServer");

    public int ChallengeLifetimeSeconds { get; set; } = 60;
    public int TokenLifetimeMinutes { get; set; } = 360;
    public int PendingRequestLifetimeSeconds { get; set; } = 180;
    public int MaxTotalConnections { get; set; } = 500;
    public int MaxConnectionsPerPet { get; set; } = 2;
    public int MaxControlPacketBytes { get; set; } = 16 * 1024;
    public int ControlRateLimitPerSecond { get; set; } = 20;
    public int MusicFrameRateLimitPerSecond { get; set; } = 18;
    public int MaxDeclaredFriendsPerPet { get; set; } = 200;
    public int MaxPublicDiscoveryPeers { get; set; } = 50;
    public int MessageDedupWindowSeconds { get; set; } = 120;
    public int RelayAckTimeoutMs { get; set; } = 4000;
}
