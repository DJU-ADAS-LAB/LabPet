namespace LabPet.Server.Options;

/// <summary>
/// LabPet 실시간 GCP/Raspberry Pi 중계 서버 운영 설정 및 DoS 방어 리소스 상한 상수.
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

    // ── HTTP 인증 엔드포인트 Rate Limit (공유 연구실 IP 환경 고려) ──────────────────────
    public int AuthChallengePerIpPerMinute { get; set; } = 60;
    public int AuthChallengeGlobalPerMinute { get; set; } = 300;
    public int AuthChallengeMaxConcurrency { get; set; } = 32;
    public int AuthTokenPerIpPerMinute { get; set; } = 60;
    public int AuthTokenGlobalPerMinute { get; set; } = 300;
    public int AuthTokenMaxConcurrency { get; set; } = 32;
    public int HealthPerIpPerMinute { get; set; } = 120;
    public int HealthGlobalPerMinute { get; set; } = 600;
    public int MaxPendingChallenges { get; set; } = 2048;
    public int MaxPendingChallengesPerIp { get; set; } = 64;

    // ── 신규 등록·영속 저장소 상한 ──────────────────────────────────────────────
    public int NewRegistrationsPerIpPerHour { get; set; } = 60;
    public int NewRegistrationsGlobalPerHour { get; set; } = 300;
    public int MaxRegisteredIdentities { get; set; } = 5000;
    public int MaxApprovedFriendships { get; set; } = 25_000;
    public long MaxRegistryFileBytes { get; set; } = 10 * 1024 * 1024;

    // ── SignalR Hub 메서드·기능별 요청 제한 ────────────────────────────────────
    public int RegisterPresencePerMinute { get; set; } = 12;
    public int UpdatePresencePer10Seconds { get; set; } = 20;
    public int SyncKnownFriendsPerMinute { get; set; } = 15;
    public int FriendRequestPerMinute { get; set; } = 12;
    public int MaxPendingFriendRequests { get; set; } = 2048;
    public int MaxPendingFriendRequestsPerSender { get; set; } = 20;
    public int MessagePer10Seconds { get; set; } = 20;
    public int InteractionPer10Seconds { get; set; } = 12;
    public int GameEventPer10Seconds { get; set; } = 45;
    public int MusicControlPer10Seconds { get; set; } = 15;

    // ── 원격 음악 중계 제한 ────────────────────────────────────────────────────
    public int MusicFramePerSubscriberPerSecond { get; set; } = 16;
    public int MusicFramePerSenderPerSecond { get; set; } = 48;
    public int MusicFrameGlobalPerSecond { get; set; } = 400;

    // ── 캐시·Kestrel·로그·HTTPS·프록시·과부하 보호 ───────────────────────────────
    public int MaxRateLimitTrackedKeys { get; set; } = 2048;
    public int KestrelMaxConcurrentConnections { get; set; } = 600;
    public int KestrelMaxConcurrentUpgradedConnections { get; set; } = 520;
    public long KestrelMaxRequestBodyBytes { get; set; } = 32 * 1024;
    public int LogThrottleWindowSeconds { get; set; } = 10;
    public int MaxLogThrottleKeys { get; set; } = 256;
    public bool EnforceHttps { get; set; } = false;
    public string[] TrustedProxies { get; set; } = ["127.0.0.1", "::1"];

    public double OverloadShedMusicConnectionRatio { get; set; } = 0.75;
    public double OverloadShedDiscoveryConnectionRatio { get; set; } = 0.85;
    public double OverloadShedNewConnectionsRatio { get; set; } = 0.95;
    public double OverloadRecoveryHysteresis { get; set; } = 0.10;
    public double OverloadMemorySoftLimitMb { get; set; } = 320;
    public double OverloadMemoryDiscoveryLimitMb { get; set; } = 420;
    public double OverloadMemoryHardLimitMb { get; set; } = 512;
    public int OverloadRejectedPerWindowThreshold { get; set; } = 200;
    public int OverloadRecoveryHoldSeconds { get; set; } = 15;
    public int CleanupIntervalSeconds { get; set; } = 30;
}
