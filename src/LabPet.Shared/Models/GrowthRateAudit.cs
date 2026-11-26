namespace LabPet.Shared.Models;

/// <summary>암호화 성장 저장에 포함되는 감시 기준값. 클라이언트 자체 변조를 막는 보안 경계는 아니다.</summary>
public sealed class GrowthRateAudit
{
    public int ExpectedLevel { get; set; }
    public long ExpectedCoins { get; set; }
    public DateTimeOffset LastObservedUtc { get; set; }
    public List<GrowthRateSample> Samples { get; set; } = [];
    public long DetectionCount { get; set; }
    public DateTimeOffset? LastDetectionUtc { get; set; }
    public string? LastDetectionReason { get; set; }
}

/// <summary>정상 경제 처리로 설명되지 않은 양의 증가량을 분 단위로 집계한다.</summary>
public sealed class GrowthRateSample
{
    public DateTimeOffset MinuteUtc { get; set; }
    public long Coins { get; set; }
    public int Levels { get; set; }
}
