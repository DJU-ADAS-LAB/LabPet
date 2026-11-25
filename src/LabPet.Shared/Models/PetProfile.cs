using LabPet.Shared.Protocol;

namespace LabPet.Shared.Models;

/// <summary>
/// %AppData%\LabPet\profile.json 에 저장되는 로컬 펫 프로필 루트 문서.
/// </summary>
public sealed class PetProfile
{
    public const double MinimumPetScale = 0.5;
    public const double DefaultPetScale = 1.0;
    public const double MaximumPetScale = 1.5;

    public int SchemaVersion { get; set; } = ProtocolConstants.CurrentStorageSchemaVersion;

    /// <summary>DHCP IP 변경과 무관하게 펫을 고유 식별하는 영구 UUID v4.</summary>
    public string PetId { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>사용자에게 표시되는 짧은 친구 코드 (예: LP-XXXX-XXXX).</summary>
    public string FriendCode { get; set; } = GenerateFriendCode();

    /// <summary>사용자가 설정한 내 이름(주인 닉네임).</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>사용자가 설정한 펫 이름.</summary>
    public string PetName { get; set; } = "모찌";

    /// <summary>초기 온보딩 완료 여부.</summary>
    public bool IsOnboardingCompleted { get; set; }

    /// <summary>3D 커스텀 외형.</summary>
    public PetAppearance Appearance { get; set; } = new();

    /// <summary>개인 성장 및 상점 해금 상태.</summary>
    public SoloProgress SoloProgress { get; set; } = new();

    /// <summary>향후 GCP 클라우드 동기화용 메타데이터.</summary>
    public SyncMetadata SyncMetadata { get; set; } = new();

    /// <summary>마지막 창 위치 X (null이면 기본 우하단 배치).</summary>
    public double? LastWindowLeft { get; set; }

    /// <summary>마지막 창 위치 Y (null이면 기본 우하단 배치).</summary>
    public double? LastWindowTop { get; set; }

    /// <summary>바탕화면 펫 표시 배율 (0.5 ~ 1.5, 기본값 1.0).</summary>
    public double PetScale { get; set; } = DefaultPetScale;

    /// <summary>바탕화면 펫 투명도 (0.25 ~ 1.0, 기본값 1.0).</summary>
    public double PetOpacity { get; set; } = 1.0;

    /// <summary>Windows 시작 시 자동 실행 설정 (null이면 최초 실행 시 기본값 true로 자동 등록).</summary>
    public bool? AutoStartEnabled { get; set; }

    /// <summary>업데이트 이후 뽀모도로 신규 안내 배지(빨간 점)를 클릭해 확인했는지 여부.</summary>
    public bool HasSeenPomodoroFeatureBadge { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public static double NormalizePetScale(double scale) => double.IsFinite(scale) && scale > 0
        ? Math.Clamp(scale, MinimumPetScale, MaximumPetScale)
        : DefaultPetScale;

    public static string GenerateFriendCode()
    {
        var raw = Guid.NewGuid().ToString("N").ToUpperInvariant();
        return $"LP-{raw[..4]}-{raw[4..8]}";
    }
}
