namespace LabPet.Shared.Models;

/// <summary>
/// 개인 성장 및 수집 진행도.
/// </summary>
public sealed class SoloProgress
{
    public int Level { get; set; } = 1;
    public long Xp { get; set; }
    public long Coins { get; set; }
    public long TotalKeystrokes { get; set; }
    public long TotalMouseClicks { get; set; }
    public long TotalUptimeMinutes { get; set; }
    public int CompletedPomodoros { get; set; }

    /// <summary>해금된 3D 악세서리/표정/오라/컬러 ID 목록.</summary>
    public List<string> UnlockedItemIds { get; set; } =
    [
        "expr_default",
        "expr_happy",
        "acc_none",
        "acc_sprout",
        "aura_none"
    ];
}

/// <summary>
/// 향후 GCP Firestore 클라우드 백업 및 다중 기기 동기화를 위한 메타데이터.
/// </summary>
public sealed class SyncMetadata
{
    public string? CloudUserId { get; set; }
    public DateTimeOffset? LastSyncedAtUtc { get; set; }
    public long Revision { get; set; }
}
