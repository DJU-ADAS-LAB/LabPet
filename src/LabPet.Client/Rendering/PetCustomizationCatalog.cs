using LabPet.Shared.Models;

namespace LabPet.Client.Rendering;

/// <summary>
/// 온보딩·외형 편집에서 공용으로 사용하는 3D 커스텀 레이어 옵션 메타데이터.
/// 모든 커스텀 옵션은 기본 무료로 제공된다.
/// </summary>
public sealed record CustomizationOption(
    string Id,
    string DisplayName,
    string Description,
    string? PreviewHex = null);

public static class PetCustomizationCatalog
{
    public static IReadOnlyList<CustomizationOption> Species { get; } =
    [
        new(PetSpeciesIds.MochiSpirit, "말랑 모찌 정령", "쫀득한 3D 타원체 바디와 볼록한 볼터치", "#FFF5F7"),
        new(PetSpeciesIds.ChibiCat, "치비 고양이", "큰 둥근 머리, 쫑긋한 3D 귀, 곡선 꼬리", "#FFE6CC"),
        new(PetSpeciesIds.FluffyCloud, "솜구름", "유기적으로 뭉친 몽실한 구름 클러스터", "#F0F8FF"),
        new(PetSpeciesIds.FluffyPuppy, "솜뭉치 강아지", "축 처진 복슬 귀, 짧은 네 발, 깃털 꼬리", "#F2C27D"),
        new(PetSpeciesIds.Hachiware, "하치와레", "파란 가르마 무늬와 쫑긋한 고양이 귀, 파란 꼬리", "#FFF5F7"),
        new(PetSpeciesIds.Chiikawa, "치이카와", "동글동글 하얀 찹쌀 몸통과 작은 둥근 귀", "#FFF5F7"),
        new(PetSpeciesIds.Joey, "리처드", "노란 오리와 곰 문양 티셔츠", "#FFD029"),
        new(PetSpeciesIds.PinkReference, "카니", "파스텔 핑크 머리와 게 집게발 귀, 삐죽 머리털이 매력적인 캐릭터", "#FFF5F7"),
        new(PetSpeciesIds.Usagi, "우사기", "따뜻한 크림 옐로우 몸통과 쫑긋 솟은 긴 토끼 귀", "#FFF7CC")
    ];

    public static IReadOnlyList<CustomizationOption> BodyColors { get; } =
    [
        new("#FFD029", "오리 노랑", "리처드의 노란 깃털", "#FFD029"),
        new("#FFF5F7", "밀크 모찌", "따뜻한 크림 화이트", "#FFF5F7"),
        new("#FFE6CC", "피치 크림", "부드러운 복숭아 파스텔", "#FFE6CC"),
        new("#F2C27D", "허니 골드", "포근한 골든 강아지 털색", "#F2C27D"),
        new("#E6F4FF", "소다 솜사탕", "맑은 하늘 파스텔", "#E6F4FF"),
        new("#E9F7E8", "말차 밀크", "포근한 민트 그린", "#E9F7E8"),
        new("#F3E8FF", "라벤더", "은은한 연보라 클레이", "#F3E8FF"),
        new("#FFF7CC", "버터 옐로우", "달콤한 레몬 크림", "#FFF7CC")
    ];

    public static IReadOnlyList<CustomizationOption> AccentColors { get; } =
    [
        new("#FF9EB5", "딸기 핑크", "말랑한 볼터치 핑크", "#FF9EB5"),
        new("#FFAD75", "살구 코랄", "따뜻한 오렌지 포인트", "#FFAD75"),
        new("#7CC6FE", "스카이 블루", "시원한 하늘색 포인트", "#7CC6FE"),
        new("#A78BFA", "바이올렛", "몽환적인 보라 포인트", "#A78BFA"),
        new("#6EE7B7", "민트 리프", "산뜻한 민트 포인트", "#6EE7B7")
    ];

    public static IReadOnlyList<CustomizationOption> Expressions { get; } =
    [
        new("default", "기본 미소", "동글동글 순한 눈망울과 작은 미소"),
        new("happy", "방긋 웃음", "반달 눈웃음과 환한 볼터치"),
        new("sleepy", "졸림", "나른하게 감긴 눈과 평온한 표정"),
        new("sparkle", "반짝임", "하이라이트가 강조된 호기심 눈빛")
    ];

    public static IReadOnlyList<CustomizationOption> HeadAccessories { get; } =
    [
        new("none", "없음", "자연스러운 기본 실루엣"),
        new("sprout", "새싹", "정수리 위 초록 줄기와 두 잎"),
        new("ribbon", "리본", "입체 나비 리본 파츠"),
        new("headset", "3D 헤드셋", "음악 감상용 둥근 이어컵 헤드셋"),
        new("santa_hat", "산타 모자", "복슬한 흰 방울이 달린 꼬깔 모자"),
        new("crown", "미니 왕관", "반짝이는 골드 3D 미니 왕관")
    ];

    public static IReadOnlyList<CustomizationOption> AuraEffects { get; } =
    [
        new("none", "없음", "오라 이펙트 비활성"),
        new("halo_ring", "회전 링", "펫 주위를 부드럽게 도는 발광 링"),
        new("music_notes", "음표", "주변에 떠다니는 3D 음표 파츠"),
        new("snowflakes", "눈송이", "은은하게 회전하는 3D 결정 파티클")
    ];
}
