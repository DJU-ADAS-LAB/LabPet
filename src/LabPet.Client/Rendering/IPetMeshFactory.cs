using System.Windows.Media;
using System.Windows.Media.Media3D;
using LabPet.Shared.Models;

namespace LabPet.Client.Rendering;

/// <summary>
/// 댄스·음악 반응 시 방출되는 3D 음표/별 풀링 파티클 핸들.
/// </summary>
public sealed class MusicParticleHandle
{
    public required ScaleTransform3D Scale { get; init; }
    public required TranslateTransform3D Translate { get; init; }
    public required AxisAngleRotation3D Rotation { get; init; }
}

[Flags]
public enum PetRigEffects
{
    None = 0,
    TypingKeyboard = 1,
    MusicParticles = 2,
    All = TypingKeyboard | MusicParticles
}

/// <summary>
/// 60fps UI 타이머가 Transform3D와 EmissiveMaterial만 저부하로 갱신하기 위해 보관하는 3D 리그 핸들.
/// </summary>
public sealed class PetRigHandle
{
    public required string SpeciesId { get; init; }
    public required Model3DGroup RootModel { get; init; }
    public required int TotalTriangleCount { get; init; }
    internal Point3D NameplateAnchor { get; init; }

    // 루트 바디 변환 (Squash & Stretch, 부유, 시선 추적, 흔들기)
    public required ScaleTransform3D BodyScale { get; init; }
    public required TranslateTransform3D BodyTranslate { get; init; }
    public required AxisAngleRotation3D BodyYaw { get; init; }
    public required AxisAngleRotation3D BodyPitch { get; init; }
    public required AxisAngleRotation3D BodyRoll { get; init; }

    // 머리/얼굴 파츠 변환 (시선 추적 및 리듬 까딱임)
    public required AxisAngleRotation3D HeadYaw { get; init; }
    public required AxisAngleRotation3D HeadPitch { get; init; }
    public required AxisAngleRotation3D HeadRoll { get; init; }

    // 자연스러운 자동 눈 깜빡임용 스케일 (눈 높이 중심 기준)
    public ScaleTransform3D? EyeBlinkScale { get; init; }

    // 바닥 소프트 접지 그림자 스케일 (점프/부유 높이에 연동)
    public ScaleTransform3D? ShadowScale { get; init; }

    // 타이핑 시 펫 앞에 팝인되는 3D 미니 키보드 스케일
    public ScaleTransform3D? TypingKeyboardScale { get; init; }

    // 종별 보조 파츠 (치비 고양이 귀/꼬리/앞발, 모찌 미니 팔, 솜구름 미니 날개)
    public AxisAngleRotation3D? LeftEarRotation { get; init; }
    public AxisAngleRotation3D? RightEarRotation { get; init; }
    public AxisAngleRotation3D? TailRotation { get; init; }
    public TranslateTransform3D? LeftPawTranslate { get; init; }
    public TranslateTransform3D? RightPawTranslate { get; init; }
    public AxisAngleRotation3D? LeftArmRotation { get; init; }
    public AxisAngleRotation3D? RightArmRotation { get; init; }
    public AxisAngleRotation3D? LeftArmPitch { get; init; }
    public AxisAngleRotation3D? RightArmPitch { get; init; }

    // 3종 공통 댄스·음악 반응용 풀링 3D 음표/별 파티클
    public IReadOnlyList<MusicParticleHandle> MusicParticles { get; init; } = [];

    // 3D 오라 이펙트 변환
    public AxisAngleRotation3D? AuraRotation { get; init; }
    public TranslateTransform3D? AuraTranslate { get; init; }

    // 음악/상호작용 반응용 발광 브러시
    public required SolidColorBrush BodyEmissiveBrush { get; init; }
    public SolidColorBrush? AuraEmissiveBrush { get; init; }
}

/// <summary>
/// 3D 펫 메시 생성 추상화.
/// 1차 구현은 절차적 MeshBuilder를 사용하며, 추후 외부 .obj 로더 구현체로 교체할 수 있다.
/// </summary>
public interface IPetMeshFactory
{
    PetRigHandle BuildPetRig(PetAppearance appearance, PetRigEffects effects = PetRigEffects.All);
    Model3DGroup CreateStudioLights();
}
