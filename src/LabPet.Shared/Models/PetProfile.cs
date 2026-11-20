using LabPet.Shared.Protocol;

namespace LabPet.Shared.Models;

/// <summary>
/// %AppData%\LabPet\profile.json 에 저장되는 로컬 펫 프로필 루트 문서.
/// </summary>
public sealed class PetProfile
{
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

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public static string GenerateFriendCode()
    {
        var raw = Guid.NewGuid().ToString("N").ToUpperInvariant();
        return $"LP-{raw[..4]}-{raw[4..8]}";
    }
}
