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
    public SolidColorBrush? TintBrush { get; init; }
    public SolidColorBrush? EmissiveBrush { get; init; }
    public Color BaseColor { get; set; } = Color.FromRgb(255, 194, 212);
    public string ParticleKind { get; init; } = "note";
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
/// 캐릭터 종별 팔·앞발·날개 높이와 몸통 전면 여유에 맞춘 타이핑 미니 키보드 배치 프로필.
/// </summary>
internal readonly record struct TypingKeyboardPlacement(
    float CenterY,
    float CenterZ,
    float BodyForwardZ,
    float KeyReachX,
    float ArmForwardOffsetZ = 0f,
    float ArmInwardOffsetX = 0f)
{
    public const float KeycapTopOffsetY = 0.055f + 0.02f;

    public float KeyTopY => CenterY + KeycapTopOffsetY;

    public static readonly TypingKeyboardPlacement Default = new(-0.52f, 0.78f, 0.10f, 0.24f);

    public static TypingKeyboardPlacement ForSpecies(string? speciesId) => speciesId switch
    {
        PetSpeciesIds.ChibiCat => new(-0.52f, 0.77f, 0.10f, 0.24f),
        PetSpeciesIds.FluffyPuppy => new(-0.52f, 0.78f, 0.10f, 0.24f),
        PetSpeciesIds.Chiikawa => new(-0.46f, 0.79f, 0.09f, 0.36f, ArmForwardOffsetZ: 0.14f, ArmInwardOffsetX: 0.05f),
        PetSpeciesIds.Hachiware => new(-0.46f, 0.81f, 0.09f, 0.36f, ArmForwardOffsetZ: 0.14f, ArmInwardOffsetX: 0.05f),
        PetSpeciesIds.Usagi => new(-0.46f, 0.79f, 0.09f, 0.36f, ArmForwardOffsetZ: 0.14f, ArmInwardOffsetX: 0.05f),
        PetSpeciesIds.PinkReference => new(-0.46f, 0.79f, 0.09f, 0.36f, ArmForwardOffsetZ: 0.14f, ArmInwardOffsetX: 0.05f),
        PetSpeciesIds.MochiSpirit => new(-0.62f, 1.05f, 0.07f, 0.44f, ArmForwardOffsetZ: 0.15f, ArmInwardOffsetX: 0.08f),
        PetSpeciesIds.FluffyCloud => new(-0.56f, 0.90f, 0.07f, 0.46f, ArmForwardOffsetZ: 0.16f, ArmInwardOffsetX: 0.08f),
        PetSpeciesIds.Joey => new(-0.44f, 0.56f, 0.08f, 0.28f),
        _ => Default
    };
}

/// <summary>
/// 60fps UI 타이머가 Transform3D와 Material 색상만 저부하로 갱신하기 위해 보관하는 3D 리그 핸들.
/// </summary>
public sealed class PetRigHandle
{
    public required string SpeciesId { get; init; }
    public required Model3DGroup RootModel { get; init; }
    public required int TotalTriangleCount { get => _triangleCount; init => _triangleCount = value; }
    private int _triangleCount;
    internal Transform3D? BodyTransform { get; init; }
    internal PetSkinInstance? Skin { get; init; }
    internal Func<string, int>? SelectExpression { get; init; }
    internal Func<PetTearEffect>? CreateTearEffect { get; init; }
    internal string? ReactionExpression { get; private set; }
    internal Point3D NameplateAnchor { get; init; }
    internal Rect3D BodyBounds { get; init; }

    // 절차형 모델은 실제 팔/앞발 변환을 재사용하여 몸통 로컬 좌표를 제공한다.
    internal Func<Point3D>? RightHandPositionProvider { get; init; }

    /// <summary>관절 갱신 후 호출. 몸통 변환을 포함한 RootModel 내부의 3D 좌표이며 화면 좌표가 아니다.</summary>
    internal bool TryGetRightHandPosition(out Point3D position)
    {
        position = default;
        RootModel.Dispatcher.VerifyAccess();
        Point3D hand;
        if (Skin is not null)
        {
            if (!Skin.TryGetRightHandPosition(out hand)) return false;
        }
        else if (RightHandPositionProvider is { } provider)
        {
            hand = provider();
        }
        else return false;
        var transformed = BodyTransform?.Transform(hand) ?? hand;
        if (!double.IsFinite(transformed.X) || !double.IsFinite(transformed.Y) ||
            !double.IsFinite(transformed.Z)) return false;
        position = transformed;
        return true;
    }

    /// <summary>색상을 제외한 구조 시그니처 (SpeciesId|ExpressionId|HeadAccessoryId|AuraEffectId).</summary>
    public string AppearanceStructureSignature { get => _signature; init => _signature = value; }
    private string _signature = string.Empty;

    /// <summary>Blender 외부 에셋(.obj)에서 로드되었는지 여부 (폴백 시 false).</summary>
    public bool IsExternalAsset { get; init; }

    /// <summary>종별 좌·우 귀 기본 각도 (deg).</summary>
    public double LeftEarBaseAngle { get; init; }
    public double RightEarBaseAngle { get; init; }

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

    // 타이핑 시 펫 앞에 팝인되는 3D 미니 키보드 스케일 및 종별 배치 프로필
    public ScaleTransform3D? TypingKeyboardScale { get; init; }
    internal TypingKeyboardPlacement TypingKeyboard { get; init; } = TypingKeyboardPlacement.Default;

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

    // 댄스·음악·반응 이펙트용 풀링 3D 음표/별 파티클
    public IReadOnlyList<MusicParticleHandle> MusicParticles { get; init; } = [];

    // 3D 오라 이펙트 변환
    public AxisAngleRotation3D? AuraRotation { get; init; }
    public TranslateTransform3D? AuraTranslate { get; init; }

    // 음악/상호작용 반응용 발광 브러시
    public required SolidColorBrush BodyEmissiveBrush { get; init; }
    public SolidColorBrush? AuraEmissiveBrush { get; init; }

    // 메시 재생성 없이 색상 슬롯만 즉시 교체하기 위한 Diffuse 브러시 참조
    public SolidColorBrush? BodyDiffuseBrush { get; init; }
    public SolidColorBrush? BodyLightDiffuseBrush { get; init; }
    public SolidColorBrush? BodyShadeDiffuseBrush { get; init; }
    public SolidColorBrush? EarDarkDiffuseBrush { get; init; }
    public SolidColorBrush? EarInnerDarkDiffuseBrush { get; init; }
    public SolidColorBrush? AccentDiffuseBrush { get; init; }
    public SolidColorBrush? AuraDiffuseBrush { get; init; }

    internal bool TryUpdateAppearance(PetAppearance appearance)
    {
        var signature = BuildStructureSignature(appearance);
        if (signature != AppearanceStructureSignature)
        {
            var old = AppearanceStructureSignature.Split('|');
            if (SelectExpression == null || old.Length != 4 || old[0] != appearance.SpeciesId ||
                old[2] != appearance.HeadAccessoryId || old[3] != appearance.AuraEffectId) return false;
            if (ReactionExpression is null)
                _triangleCount += SelectExpression(appearance.ExpressionId);
            _signature = signature;
        }
        UpdateColors(appearance.BodyColorHex, appearance.AccentColorHex);
        return true;
    }

    internal void SetReactionExpression(string? expression)
    {
        if (SelectExpression is null || ReactionExpression == expression)
        {
            return;
        }

        ReactionExpression = expression;
        var savedExpression = AppearanceStructureSignature.Split('|')[1];
        _triangleCount += SelectExpression(expression ?? savedExpression);
    }

    public static string BuildStructureSignature(PetAppearance appearance)
        => $"{appearance.SpeciesId}|{appearance.ExpressionId}|{appearance.HeadAccessoryId}|{appearance.AuraEffectId}";

    /// <summary>
    /// 메시를 다시 생성하지 않고 바디·포인트 색상 슬롯의 브러시만 즉시 갱신한다.
    /// </summary>
    public void UpdateColors(string bodyColorHex, string accentColorHex)
    {
        var bodyColor = ProceduralPetMeshFactory.ParseHexColor(bodyColorHex, Color.FromRgb(255, 245, 247));
        var accentColor = ProceduralPetMeshFactory.ParseHexColor(accentColorHex, Color.FromRgb(255, 158, 181));

        if (BodyDiffuseBrush is not null) BodyDiffuseBrush.Color = bodyColor;
        if (BodyLightDiffuseBrush is not null) BodyLightDiffuseBrush.Color = ProceduralPetMeshFactory.Lighten(bodyColor, 0.42);
        if (BodyShadeDiffuseBrush is not null) BodyShadeDiffuseBrush.Color = ProceduralPetMeshFactory.Darken(bodyColor, 0.06);
        if (EarDarkDiffuseBrush is not null) EarDarkDiffuseBrush.Color = ProceduralPetMeshFactory.Darken(bodyColor, 0.23);
        if (EarInnerDarkDiffuseBrush is not null) EarInnerDarkDiffuseBrush.Color = ProceduralPetMeshFactory.Darken(bodyColor, 0.32);
        if (AccentDiffuseBrush is not null) AccentDiffuseBrush.Color = accentColor;

        if (AuraEmissiveBrush is not null)
        {
            AuraEmissiveBrush.Color = Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B);
        }

        if (AuraDiffuseBrush is not null)
        {
            var softAura = ProceduralPetMeshFactory.Lighten(accentColor, 0.35);
            AuraDiffuseBrush.Color = Color.FromArgb(AuraDiffuseBrush.Color.A, softAura.R, softAura.G, softAura.B);
        }

        var particleColor = ProceduralPetMeshFactory.Lighten(accentColor, 0.25);
        foreach (var particle in MusicParticles)
        {
            particle.BaseColor = particleColor;
            if (particle.TintBrush is not null)
            {
                particle.TintBrush.Color = Color.FromArgb(particle.TintBrush.Color.A, particleColor.R, particleColor.G, particleColor.B);
            }
        }
    }
}

/// <summary>
/// 3D 펫 메시 생성 추상화.
/// 외부 Blender .obj 에셋 로더 또는 절차적 MeshBuilder 구현체를 통해 PetRigHandle을 조립한다.
/// </summary>
public interface IPetMeshFactory
{
    PetRigHandle BuildPetRig(PetAppearance appearance, PetRigEffects effects = PetRigEffects.All);
    Model3DGroup CreateStudioLights();
}
