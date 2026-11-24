namespace LabPet.Shared.Models;

/// <summary>
/// 기본 제공 3D 캐릭터 종 식별자 상수.
/// </summary>
public static class PetSpeciesIds
{
    public const string MochiSpirit = "mochi_spirit";
    public const string ChibiCat = "chibi_cat";
    public const string FluffyCloud = "fluffy_cloud";
    public const string FluffyPuppy = "fluffy_puppy";
    public const string Hachiware = "hachiware";
    public const string Chiikawa = "chiikawa";
    public const string Joey = "joey";
    public const string Usagi = "usagi";

    public static IReadOnlyList<string> All { get; } =
    [
        MochiSpirit,
        ChibiCat,
        FluffyCloud,
        FluffyPuppy,
        Hachiware,
        Chiikawa,
        Usagi,
        Joey
    ];
}

/// <summary>
/// 5개 3D 커스텀 레이어로 구성된 펫 외형 정의.
/// 신규 필드가 추가되더라도 기존 JSON 역직렬화 시 안전한 기본값으로 폴백된다.
/// </summary>
public sealed class PetAppearance
{
    /// <summary>캐릭터 종 ID (<see cref="PetSpeciesIds"/>).</summary>
    public string SpeciesId { get; set; } = PetSpeciesIds.MochiSpirit;

    /// <summary>바디 기본 색상 (#RRGGBB).</summary>
    public string BodyColorHex { get; set; } = "#FFF5F7";

    /// <summary>포인트(볼터치/귀 안쪽/오라 보조) 색상 (#RRGGBB).</summary>
    public string AccentColorHex { get; set; } = "#FF9EB5";

    /// <summary>표정 파츠 ID (예: default, happy, sleepy, sparkle).</summary>
    public string ExpressionId { get; set; } = "default";

    /// <summary>머리 소켓 3D 악세서리 ID (예: none, headset, sprout, ribbon, santa_hat).</summary>
    public string HeadAccessoryId { get; set; } = "none";

    /// <summary>주변 3D 오라/이펙트 ID (예: none, halo_ring, music_notes, snowflakes).</summary>
    public string AuraEffectId { get; set; } = "none";

    public PetAppearance Clone() => new()
    {
        SpeciesId = SpeciesId,
        BodyColorHex = BodyColorHex,
        AccentColorHex = AccentColorHex,
        ExpressionId = ExpressionId,
        HeadAccessoryId = HeadAccessoryId,
        AuraEffectId = AuraEffectId
    };
}
