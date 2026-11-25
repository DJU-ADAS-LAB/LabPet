using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Geometry;
using LabPet.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using WpfMeshGeometry3D = System.Windows.Media.Media3D.MeshGeometry3D;

namespace LabPet.Client.Rendering;

/// <summary>
/// GLB 스킨과 소켓으로 리그를 조립하고 이전 OBJ 및 절차적 모델을 순서대로 시도한다.
/// 외부 에셋이 없거나 손상·필수 파츠 누락 시 즉시 <see cref="ProceduralPetMeshFactory"/>로 폴백한다.
/// - 불변 메시(.obj 서브메시)와 고정 재질은 Freeze() 후 다중 펫이 공유해 메모리 사용을 최소화한다.
/// - 바디/포인트 색상 슬롯만 펫별 독립 브러시로 구성해 메시 재생성 없이 색상을 변경한다.
/// </summary>
public sealed class BlenderAssetPetMeshFactory : IPetMeshFactory
{
    public const int MaxPooledParticles = 8;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // 고정 색상 재질은 Frozen 상태로 전역 공유해 펫당 할당을 제거한다.
    // 유광 비즈 눈·촉촉한 코는 높은 스페큘러로 무광 클레이 몸체와 또렷한 질감 대비를 만든다.
    private static readonly Material FrozenEyeDarkMaterial = CreateFrozenClayMaterial(Color.FromRgb(40, 34, 46), null, 68, 175);
    private static readonly Material FrozenEyeHighlightMaterial = CreateFrozenClayMaterial(Colors.White, Color.FromRgb(85, 85, 85), 76, 190);
    private static readonly Material FrozenNoseDarkMaterial = CreateFrozenClayMaterial(Color.FromRgb(76, 48, 43), null, 64, 165);
    private static readonly Material FrozenTongueMaterial = CreateFrozenClayMaterial(Color.FromRgb(235, 132, 150), null, 14, 42);
    private static readonly Material FrozenShadowMaterial = CreateFrozenDiffuseMaterial(Color.FromArgb(42, 42, 48, 66));
    private static readonly Material FrozenAccGreenMaterial = CreateFrozenClayMaterial(Color.FromRgb(106, 204, 118), null, 12, 34);
    private static readonly Material FrozenAccRedMaterial = CreateFrozenClayMaterial(Color.FromRgb(226, 66, 76), null, 12, 34);
    private static readonly Material FrozenAccWhiteMaterial = CreateFrozenClayMaterial(Color.FromRgb(250, 250, 252), null, 10, 24);
    private static readonly Material FrozenAccGoldMaterial = CreateFrozenClayMaterial(Color.FromRgb(255, 212, 82), Color.FromRgb(42, 32, 6), 38, 125);
    private static readonly Material FrozenAccHeadbandMaterial = CreateFrozenClayMaterial(Color.FromRgb(56, 62, 78), null, 24, 78);
    private static readonly Material FrozenHachiBlueMaterial = CreateFrozenClayMaterial(Color.FromRgb(109, 169, 204), null, 11, 28);
    private static readonly Material FrozenKaniPinkMaterial = CreateFrozenClayMaterial(Color.FromRgb(248, 178, 190), null, 11, 28);

    private readonly ProceduralPetMeshFactory _fallbackFactory;
    private readonly GlbCharacterLoader _glbLoader = new();
    private readonly ObjMeshLoader _objLoader = new();
    private readonly string _modelsRootDirectory;
    private readonly Dictionary<string, WeakReference<PetModelMetadata>> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _metaCacheLock = new();

    [ActivatorUtilitiesConstructor]
    public BlenderAssetPetMeshFactory(ProceduralPetMeshFactory fallbackFactory)
        : this(fallbackFactory, null)
    {
    }

    public BlenderAssetPetMeshFactory(ProceduralPetMeshFactory fallbackFactory, string? modelsRootDirectory)
    {
        _fallbackFactory = fallbackFactory;
        _modelsRootDirectory = string.IsNullOrWhiteSpace(modelsRootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "Assets", "Models")
            : modelsRootDirectory;
    }

    /// <summary>
    /// 흰색·파스텔 피규어 하이라이트 날림과 하단 과도한 회색 음영을 방지하는 부드러운 소프트 클레이 4점 스튜디오 조명.
    /// </summary>
    public Model3DGroup CreateStudioLights()
    {
        var lights = new Model3DGroup();

        // 1. 포근한 주변광 (하단 그림자까지 파스텔 톤 유지)
        lights.Children.Add(new AmbientLight(Color.FromRgb(132, 129, 138)));

        // 2. 부드러운 메인 키 라이트 (우상단 전방 — 상단 흰색 클리핑 억제)
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(122, 116, 108),
            new Vector3D(-0.42, -0.64, -0.86)));

        // 3. 좌측 필 라이트 (전면 좌측 부드러운 입체감)
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(64, 72, 88),
            new Vector3D(0.78, -0.18, -0.58)));

        // 4. 후방 림·바운스 라이트 (바탕화면 대비 분리 및 턱/배 하단 은은한 바운스)
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(86, 100, 124),
            new Vector3D(0.32, 0.54, 0.82)));

        return lights;
    }

    public PetRigHandle BuildPetRig(PetAppearance appearance, PetRigEffects effects = PetRigEffects.All)
    {
        var path = Path.Combine(_modelsRootDirectory, "Characters", Path.GetFileName(appearance.SpeciesId) ?? "", "model.glb");
        var source = _glbLoader.Load(path);
        var metadata = source?.Metadata;
        var characterAsset = source?.Mesh;
        var skin = source == null ? null : new PetSkinInstance(source);
        if ((source == null && !TryLoadCharacterAssets(appearance.SpeciesId, out metadata, out characterAsset)) ||
            metadata is null ||
            characterAsset is null ||
            (source?.Clips == null && !HasRequiredCoreParts(characterAsset)))
        {
            return _fallbackFactory.BuildPetRig(appearance, effects);
        }

        var bodyColor = ProceduralPetMeshFactory.ParseHexColor(appearance.BodyColorHex, Color.FromRgb(255, 245, 247));
        var accentColor = ProceduralPetMeshFactory.ParseHexColor(appearance.AccentColorHex, Color.FromRgb(255, 158, 181));

        var bodyEmissiveBrush = new SolidColorBrush(Colors.Black);
        var auraEmissiveBrush = new SolidColorBrush(Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B));

        // 펫별 독립 색상 브러시 (무광 벨벳 소프트 클레이 감성, 재질 슬롯 교체만으로 색상 갱신 가능)
        var bodyDiffuseBrush = new SolidColorBrush(bodyColor);
        var bodyLightDiffuseBrush = new SolidColorBrush(ProceduralPetMeshFactory.Lighten(bodyColor, 0.42));
        var bodyShadeDiffuseBrush = new SolidColorBrush(ProceduralPetMeshFactory.Darken(bodyColor, 0.06));
        var earDarkDiffuseBrush = new SolidColorBrush(ProceduralPetMeshFactory.Darken(bodyColor, 0.23));
        var earInnerDarkDiffuseBrush = new SolidColorBrush(ProceduralPetMeshFactory.Darken(bodyColor, 0.32));
        var accentDiffuseBrush = new SolidColorBrush(accentColor);

        var bodyMat = ProceduralPetMeshFactory.CreateClayMaterial(bodyDiffuseBrush, bodyEmissiveBrush, 10, 26);
        var bodyLightMat = ProceduralPetMeshFactory.CreateClayMaterial(bodyLightDiffuseBrush, bodyEmissiveBrush, 10, 22);
        var bodyShadeMat = ProceduralPetMeshFactory.CreateClayMaterial(bodyShadeDiffuseBrush, bodyEmissiveBrush, 9, 18);
        var earDarkMat = ProceduralPetMeshFactory.CreateClayMaterial(earDarkDiffuseBrush, bodyEmissiveBrush, 10, 22);
        var earInnerDarkMat = ProceduralPetMeshFactory.CreateClayMaterial(earInnerDarkDiffuseBrush, null, 9, 16);
        var accentMat = ProceduralPetMeshFactory.CreateClayMaterial(accentDiffuseBrush, bodyEmissiveBrush, 10, 24);
        var accessoryAccentMat = ProceduralPetMeshFactory.CreateClayMaterial(accentDiffuseBrush, null, 18, 60);

        Material ResolveSlotMaterial(string slot)
        {
            if (source?.Clips is { } authored && authored.Materials.TryGetValue(slot, out var material))
                return slot == "병아리 노랑" ? bodyMat : material;
            if (metadata.PartMaterialOverrides.TryGetValue(slot, out var mappedSlot))
            {
                slot = mappedSlot;
            }

            return slot.ToLowerInvariant() switch
            {
                "body" => bodyMat,
                "body_light" => bodyLightMat,
                "body_shade" => bodyShadeMat,
                "ear_dark" => earDarkMat,
                "ear_inner_dark" => earInnerDarkMat,
                "accent" => accentMat,
                "acc_accent" => accessoryAccentMat,
                "eye_dark" => FrozenEyeDarkMaterial,
                "eye_highlight" => FrozenEyeHighlightMaterial,
                "nose_dark" => FrozenNoseDarkMaterial,
                "tongue" => FrozenTongueMaterial,
                "acc_green" => FrozenAccGreenMaterial,
                "acc_red" => FrozenAccRedMaterial,
                "acc_white" => FrozenAccWhiteMaterial,
                "acc_gold" => FrozenAccGoldMaterial,
                "acc_headband" => FrozenAccHeadbandMaterial,
                "hachi_blue" => FrozenHachiBlueMaterial,
                "kani_pink" => FrozenKaniPinkMaterial,
                _ => bodyMat
            };
        }

        // 루트 바디 변환 체인
        var bodyScale = new ScaleTransform3D(1, 1, 1);
        var bodyYaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var bodyPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        var bodyRoll = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        var bodyTranslate = new TranslateTransform3D(0, 0, 0);

        var bodyTransformGroup = new Transform3DGroup();
        bodyTransformGroup.Children.Add(bodyScale);
        bodyTransformGroup.Children.Add(new RotateTransform3D(bodyYaw));
        bodyTransformGroup.Children.Add(new RotateTransform3D(bodyPitch));
        bodyTransformGroup.Children.Add(new RotateTransform3D(bodyRoll));
        bodyTransformGroup.Children.Add(bodyTranslate);

        var rootGroup = new Model3DGroup();
        var triangleCount = 0;

        // 바닥 소프트 접지 그림자
        var shadowScale = new ScaleTransform3D(1, 1, 1);
        var shadowMb = new MeshBuilder(true, false);
        shadowMb.AddEllipsoid(
            new Vector3(0, (float)metadata.ShadowY, 0),
            (float)metadata.ShadowRadiusX,
            0.015f,
            (float)metadata.ShadowRadiusZ,
            24,
            6);
        triangleCount += ProceduralPetMeshFactory.AddGeometry(
            rootGroup,
            _fallbackFactory.GetSharedMesh(shadowMb.ToMesh()),
            FrozenShadowMaterial,
            shadowScale);

        var bodyGroup = new Model3DGroup { Transform = bodyTransformGroup };
        rootGroup.Children.Add(bodyGroup);

        // 머리 그룹 변환 체인
        var headYaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var headPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        var headRoll = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        var headPivot = metadata.HeadPivot.ToPoint3D();
        var headTransform = new Transform3DGroup();
        headTransform.Children.Add(new RotateTransform3D(headYaw, headPivot));
        headTransform.Children.Add(new RotateTransform3D(headPitch, headPivot));
        headTransform.Children.Add(new RotateTransform3D(headRoll, headPivot));
        var headGroup = new Model3DGroup { Transform = headTransform };

        // 관절 파츠 그룹 (존재하는 파츠만 활성화)
        AxisAngleRotation3D? leftEarRot = null;
        Model3DGroup? leftEarGroup = null;
        AxisAngleRotation3D? rightEarRot = null;
        Model3DGroup? rightEarGroup = null;
        AxisAngleRotation3D? tailRot = null;
        Model3DGroup? tailGroup = null;
        TranslateTransform3D? leftPawTrans = null;
        Model3DGroup? leftPawGroup = null;
        TranslateTransform3D? rightPawTrans = null;
        Model3DGroup? rightPawGroup = null;
        AxisAngleRotation3D? leftArmRot = null;
        AxisAngleRotation3D? leftArmPitch = null;
        Model3DGroup? leftArmGroup = null;
        AxisAngleRotation3D? rightArmRot = null;
        AxisAngleRotation3D? rightArmPitch = null;
        Model3DGroup? rightArmGroup = null;

        var blinkCenter = metadata.EyeBlinkCenter;
        var blink = new ScaleTransform3D(1, 1, 1, blinkCenter.X, blinkCenter.Y, blinkCenter.Z);
        var eyeGroup = new Model3DGroup();
        var mouthGroup = new Model3DGroup();
        headGroup.Children.Add(eyeGroup);
        headGroup.Children.Add(mouthGroup);

        int SelectExpression(string expression)
        {
            var suffix = NormalizeExpressionSuffix(expression);
            var eyes = ResolveExpressionPartName(characterAsset, "Eyes_", suffix, "Eyes_Default", "Eyes");
            var mouth = ResolveExpressionPartName(characterAsset, "Mouth_", suffix, "Mouth_Default", "Mouth");
            var before = ExpressionTriangles();
            eyeGroup.Children.Clear();
            mouthGroup.Children.Clear();
            eyeGroup.Transform = suffix is "Happy" or "Sleepy" ? null : blink;
            foreach (var sub in characterAsset.SubMeshes)
            {
                if (sub.PartName == eyes || sub.PartName == mouth)
                    ProceduralPetMeshFactory.AddGeometry(sub.PartName == eyes ? eyeGroup : mouthGroup,
                        sub.Geometry, ResolveSlotMaterial(sub.MaterialSlot), null);
            }
            return ExpressionTriangles() - before;
        }

        int ExpressionTriangles() => eyeGroup.Children.Concat(mouthGroup.Children).Cast<GeometryModel3D>()
            .Sum(m => ((WpfMeshGeometry3D)m.Geometry).TriangleIndices.Count / 3);

        int AddPart(Model3DGroup group, ObjSubMesh part)
        {
            if (skin != null && source!.Surfaces.ContainsKey(part))
            {
                var model = new GeometryModel3D(part.Geometry, ResolveSlotMaterial(part.MaterialSlot));
                model.BackMaterial = model.Material;
                bodyGroup.Children.Add(model);
                skin.Bind(part, model);
                return part.TriangleCount;
            }
            return ProceduralPetMeshFactory.AddGeometry(group, part.Geometry, ResolveSlotMaterial(part.MaterialSlot), null);
        }

        triangleCount += SelectExpression(appearance.ExpressionId);
        foreach (var sub in characterAsset.SubMeshes)
        {
            var part = sub.PartName;
            if (part.StartsWith("Eyes", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("Mouth", StringComparison.OrdinalIgnoreCase)) continue;

            if (string.Equals(part, "Body", StringComparison.OrdinalIgnoreCase))
            {
                triangleCount += AddPart(bodyGroup, sub);
            }
            else if (string.Equals(part, "Head", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "Cheeks", StringComparison.OrdinalIgnoreCase))
            {
                triangleCount += AddPart(headGroup, sub);
            }
            else if (string.Equals(part, "LeftEar", StringComparison.OrdinalIgnoreCase))
            {
                if (leftEarGroup is null)
                {
                    leftEarRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), metadata.LeftEarBaseAngle);
                    var earPivot = (metadata.LeftEarPivot ?? new PetVector3(-0.48, 0.94, 0)).ToPoint3D();
                    leftEarGroup = new Model3DGroup { Transform = new RotateTransform3D(leftEarRot, earPivot) };
                    headGroup.Children.Add(leftEarGroup);
                }

                triangleCount += AddPart(leftEarGroup, sub);
            }
            else if (string.Equals(part, "RightEar", StringComparison.OrdinalIgnoreCase))
            {
                if (rightEarGroup is null)
                {
                    rightEarRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), metadata.RightEarBaseAngle);
                    var earPivot = (metadata.RightEarPivot ?? new PetVector3(0.48, 0.94, 0)).ToPoint3D();
                    rightEarGroup = new Model3DGroup { Transform = new RotateTransform3D(rightEarRot, earPivot) };
                    headGroup.Children.Add(rightEarGroup);
                }

                triangleCount += AddPart(rightEarGroup, sub);
            }
            else if (string.Equals(part, "Tail", StringComparison.OrdinalIgnoreCase))
            {
                if (tailGroup is null)
                {
                    tailRot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
                    var tailPivot = (metadata.TailPivot ?? new PetVector3(0, -0.58, -0.36)).ToPoint3D();
                    tailGroup = new Model3DGroup { Transform = new RotateTransform3D(tailRot, tailPivot) };
                    bodyGroup.Children.Add(tailGroup);
                }

                triangleCount += AddPart(tailGroup, sub);
            }
            else if (string.Equals(part, "LeftPaw", StringComparison.OrdinalIgnoreCase))
            {
                if (leftPawGroup is null)
                {
                    leftPawTrans = new TranslateTransform3D(0, 0, 0);
                    leftPawGroup = new Model3DGroup { Transform = leftPawTrans };
                    bodyGroup.Children.Add(leftPawGroup);
                }

                triangleCount += AddPart(leftPawGroup, sub);
            }
            else if (string.Equals(part, "RightPaw", StringComparison.OrdinalIgnoreCase))
            {
                if (rightPawGroup is null)
                {
                    rightPawTrans = new TranslateTransform3D(0, 0, 0);
                    rightPawGroup = new Model3DGroup { Transform = rightPawTrans };
                    bodyGroup.Children.Add(rightPawGroup);
                }

                triangleCount += AddPart(rightPawGroup, sub);
            }
            else if (string.Equals(part, "LeftArm", StringComparison.OrdinalIgnoreCase))
            {
                if (leftArmGroup is null)
                {
                    leftArmRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                    leftArmPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                    var pivot = (metadata.LeftArmPivot ?? new PetVector3(-0.88, -0.10, 0.12)).ToPoint3D();
                    var tf = new Transform3DGroup();
                    tf.Children.Add(new RotateTransform3D(leftArmRot, pivot));
                    tf.Children.Add(new RotateTransform3D(leftArmPitch, pivot));
                    leftArmGroup = new Model3DGroup { Transform = tf };
                    headGroup.Children.Add(leftArmGroup);
                }

                triangleCount += AddPart(leftArmGroup, sub);
            }
            else if (string.Equals(part, "RightArm", StringComparison.OrdinalIgnoreCase))
            {
                if (rightArmGroup is null)
                {
                    rightArmRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                    rightArmPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                    var pivot = (metadata.RightArmPivot ?? new PetVector3(0.88, -0.10, 0.12)).ToPoint3D();
                    var tf = new Transform3DGroup();
                    tf.Children.Add(new RotateTransform3D(rightArmRot, pivot));
                    tf.Children.Add(new RotateTransform3D(rightArmPitch, pivot));
                    rightArmGroup = new Model3DGroup { Transform = tf };
                    headGroup.Children.Add(rightArmGroup);
                }

                triangleCount += AddPart(rightArmGroup, sub);
            }
            else
            {
                // 미분류 정적 파츠는 몸체에 연결
                triangleCount += AddPart(bodyGroup, sub);
            }
        }


        // 머리 소켓 3D 악세서리 (Blender OBJ 우선, 미존재 시 절차적 폴백)
        triangleCount += BuildHeadAccessoryFromAssetOrFallback(
            headGroup,
            appearance.HeadAccessoryId,
            metadata,
            accentColor,
            ResolveSlotMaterial);

        if (source?.Clips != null) headGroup.Transform = skin!.HeadSocket;
        bodyGroup.Children.Add(headGroup);

        // 키보드 및 풀링 파티클 (종별 팔 높이에 맞춘 단일 전면 미니 키보드 및 Blender 음표/별 메시 우선 적용)
        var typingKeyboardPlacement = TypingKeyboardPlacement.ForSpecies(appearance.SpeciesId);
        var (keyboardTriangles, typingKeyboardScale) = effects.HasFlag(PetRigEffects.TypingKeyboard)
            ? _fallbackFactory.BuildTypingKeyboard(rootGroup, accentColor, typingKeyboardPlacement, accentDiffuseBrush)
            : (0, null);

        var (particleTriangles, musicParticles) = effects.HasFlag(PetRigEffects.MusicParticles)
            ? BuildMusicParticlesFromAssetOrFallback(rootGroup, accentColor)
            : (0, Array.Empty<MusicParticleHandle>());
        triangleCount += keyboardTriangles + particleTriangles;

        // 3D 오라 이펙트 (AuraSocket 기준)
        AxisAngleRotation3D? auraRot = null;
        TranslateTransform3D? auraTrans = null;
        SolidColorBrush? auraDiffuseBrush = null;
        if (!string.Equals(appearance.AuraEffectId, "none", StringComparison.OrdinalIgnoreCase))
        {
            auraRot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            auraTrans = new TranslateTransform3D(
                metadata.AuraSocket.X,
                metadata.AuraSocket.Y,
                metadata.AuraSocket.Z);

            var auraTransform = new Transform3DGroup();
            auraTransform.Children.Add(new RotateTransform3D(auraRot));
            auraTransform.Children.Add(auraTrans);

            var auraGroup = new Model3DGroup { Transform = auraTransform };
            var auraResult = BuildAuraEffectFromAssetOrFallback(auraGroup, appearance.AuraEffectId, accentColor, auraEmissiveBrush);
            triangleCount += auraResult.Triangles;
            auraDiffuseBrush = auraResult.AuraDiffuseBrush;
            rootGroup.Children.Add(auraGroup);
        }

        return new PetRigHandle
        {
            SpeciesId = appearance.SpeciesId,
            AppearanceStructureSignature = PetRigHandle.BuildStructureSignature(appearance),
            IsExternalAsset = true,
            LeftEarBaseAngle = metadata.LeftEarBaseAngle,
            RightEarBaseAngle = metadata.RightEarBaseAngle,
            RootModel = rootGroup,
            TotalTriangleCount = triangleCount,
            NameplateAnchor = metadata.NameplateAnchor.ToPoint3D(),
            BodyBounds = bodyGroup.Bounds,
            BodyScale = bodyScale,
            BodyTranslate = bodyTranslate,
            BodyYaw = bodyYaw,
            BodyPitch = bodyPitch,
            BodyRoll = bodyRoll,
            HeadYaw = headYaw,
            HeadPitch = headPitch,
            HeadRoll = headRoll,
            EyeBlinkScale = blink,
            Skin = skin,
            BodyTransform = bodyTransformGroup,
            SelectExpression = SelectExpression,
            CreateTearEffect = () => _fallbackFactory.BuildTearEffect(headGroup, blinkCenter.ToPoint3D()),
            ShadowScale = shadowScale,
            TypingKeyboardScale = typingKeyboardScale,
            TypingKeyboard = typingKeyboardPlacement,
            LeftEarRotation = leftEarRot,
            RightEarRotation = rightEarRot,
            TailRotation = tailRot,
            LeftPawTranslate = leftPawTrans,
            RightPawTranslate = rightPawTrans,
            LeftArmRotation = leftArmRot,
            RightArmRotation = rightArmRot,
            LeftArmPitch = leftArmPitch,
            RightArmPitch = rightArmPitch,
            MusicParticles = musicParticles,
            AuraRotation = auraRot,
            AuraTranslate = auraTrans,
            BodyEmissiveBrush = bodyEmissiveBrush,
            AuraEmissiveBrush = auraEmissiveBrush,
            BodyDiffuseBrush = bodyDiffuseBrush,
            BodyLightDiffuseBrush = bodyLightDiffuseBrush,
            BodyShadeDiffuseBrush = bodyShadeDiffuseBrush,
            EarDarkDiffuseBrush = earDarkDiffuseBrush,
            EarInnerDarkDiffuseBrush = earInnerDarkDiffuseBrush,
            AccentDiffuseBrush = accentDiffuseBrush,
            AuraDiffuseBrush = auraDiffuseBrush
        };
    }

    private int BuildHeadAccessoryFromAssetOrFallback(
        Model3DGroup headGroup,
        string accessoryId,
        PetModelMetadata metadata,
        Color accentColor,
        Func<string, Material> resolveMaterial)
    {
        if (string.IsNullOrWhiteSpace(accessoryId) ||
            string.Equals(accessoryId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var safeId = Path.GetFileName(accessoryId);
        var accessoryPath = Path.Combine(_modelsRootDirectory, "Accessories", $"{safeId}.obj");
        if (_objLoader.TryLoad(accessoryPath, out var accessoryAsset) && accessoryAsset is not null)
        {
            var tf = new Transform3DGroup();
            if (string.Equals(safeId, "headset", StringComparison.OrdinalIgnoreCase))
            {
                // 기준 머리 반경(0.82) 대비 현재 캐릭터 머리 너비에 맞게 가로 폭 자동 보정
                var widthRatio = Math.Clamp(metadata.HeadRadiusX / 0.82, 0.84, 1.28);
                tf.Children.Add(new ScaleTransform3D(widthRatio, 1.0, 1.0));
            }

            tf.Children.Add(new TranslateTransform3D(
                metadata.HeadSocket.X,
                metadata.HeadSocket.Y,
                metadata.HeadSocket.Z));

            var accGroup = new Model3DGroup { Transform = tf };
            var tris = 0;
            foreach (var sub in accessoryAsset.SubMeshes)
            {
                tris += ProceduralPetMeshFactory.AddGeometry(accGroup, sub.Geometry, resolveMaterial(sub.MaterialSlot), null);
            }

            headGroup.Children.Add(accGroup);
            return tris;
        }

        return _fallbackFactory.BuildHeadAccessory(
            headGroup,
            accessoryId,
            (float)metadata.HeadSocket.Y,
            (float)metadata.HeadRadiusX,
            accentColor,
            resolveMaterial("acc_accent") is MaterialGroup mg && mg.Children[0] is DiffuseMaterial dm && dm.Brush is SolidColorBrush b ? b : null);
    }

    private (int Triangles, MusicParticleHandle[] Particles) BuildMusicParticlesFromAssetOrFallback(
        Model3DGroup rootGroup,
        Color accentColor)
    {
        var noteMesh = TryLoadFirstSubMesh(Path.Combine(_modelsRootDirectory, "Effects", "music_note.obj"));
        var starMesh = TryLoadFirstSubMesh(Path.Combine(_modelsRootDirectory, "Effects", "star_sparkle.obj"));

        return _fallbackFactory.BuildMusicParticles(
            rootGroup,
            accentColor,
            MaxPooledParticles,
            noteMesh,
            starMesh);
    }

    private (int Triangles, SolidColorBrush? AuraDiffuseBrush) BuildAuraEffectFromAssetOrFallback(
        Model3DGroup auraGroup,
        string auraId,
        Color accentColor,
        SolidColorBrush auraEmissiveBrush)
    {
        var softColor = ProceduralPetMeshFactory.Lighten(accentColor, 0.35);

        if (string.Equals(auraId, "music_notes", StringComparison.OrdinalIgnoreCase))
        {
            var noteMesh = TryLoadFirstSubMesh(Path.Combine(_modelsRootDirectory, "Effects", "music_note.obj"));
            if (noteMesh is not null)
            {
                var diffuseBrush = new SolidColorBrush(Color.FromArgb(145, softColor.R, softColor.G, softColor.B));
                var mat = ProceduralPetMeshFactory.CreateTranslucentAuraMaterial(diffuseBrush, auraEmissiveBrush);
                var tris = 0;
                for (var i = 0; i < 3; i++)
                {
                    var angle = i * (2.0 * Math.PI / 3.0) + 0.5;
                    var x = Math.Cos(angle) * 1.18;
                    var z = Math.Sin(angle) * 1.18;
                    var y = 0.22 + (i % 2 == 0 ? 0.14 : -0.10);

                    var tf = new Transform3DGroup();
                    tf.Children.Add(new ScaleTransform3D(0.9, 0.9, 0.9));
                    tf.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), i * 110.0)));
                    tf.Children.Add(new TranslateTransform3D(x, y, z));
                    tris += ProceduralPetMeshFactory.AddGeometry(auraGroup, noteMesh, mat, tf);
                }

                return (tris, diffuseBrush);
            }
        }
        else if (string.Equals(auraId, "snowflakes", StringComparison.OrdinalIgnoreCase))
        {
            var snowMesh = TryLoadFirstSubMesh(Path.Combine(_modelsRootDirectory, "Effects", "snowflake.obj"));
            if (snowMesh is not null)
            {
                var snowMat = ProceduralPetMeshFactory.CreateTranslucentAuraMaterial(
                    new SolidColorBrush(Color.FromArgb(150, 224, 244, 255)),
                    auraEmissiveBrush);
                var tris = 0;
                for (var i = 0; i < 4; i++)
                {
                    var angle = i * (Math.PI * 0.5) + 0.35;
                    var x = Math.Cos(angle) * 1.20;
                    var z = Math.Sin(angle) * 1.20;
                    var y = 0.14 + Math.Sin(i * 1.8) * 0.24;

                    var tf = new Transform3DGroup();
                    tf.Children.Add(new ScaleTransform3D(0.85, 0.85, 0.85));
                    tf.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), i * 45.0)));
                    tf.Children.Add(new TranslateTransform3D(x, y, z));
                    tris += ProceduralPetMeshFactory.AddGeometry(auraGroup, snowMesh, snowMat, tf);
                }

                return (tris, null);
            }
        }

        return _fallbackFactory.BuildAuraEffect(auraGroup, auraId, accentColor, auraEmissiveBrush);
    }

    private WpfMeshGeometry3D? TryLoadFirstSubMesh(string filePath)
    {
        return _objLoader.TryLoad(filePath, out var asset) && asset is { SubMeshes.Count: > 0 }
            ? asset.SubMeshes[0].Geometry
            : null;
    }

    private bool TryLoadCharacterAssets(string speciesId, out PetModelMetadata? metadata, out ObjMeshAsset? asset)
    {
        metadata = null;
        asset = null;
        if (string.IsNullOrWhiteSpace(speciesId))
        {
            return false;
        }

        var safeSpeciesId = Path.GetFileName(speciesId);
        var charDir = Path.Combine(_modelsRootDirectory, "Characters", safeSpeciesId);
        var metaPath = Path.Combine(charDir, "rig.json");
        var objPath = Path.Combine(charDir, "model.obj");

        if (!TryLoadMetadata(metaPath, out metadata) || metadata is null)
        {
            return false;
        }

        var offset = metadata.OriginOffset.ToPoint3D();
        return _objLoader.TryLoad(objPath, out asset, metadata.UnitScale, offset);
    }

    private bool TryLoadMetadata(string metaPath, out PetModelMetadata? metadata)
    {
        metadata = null;
        if (!File.Exists(metaPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(metaPath);
        lock (_metaCacheLock)
        {
            if (_metadataCache.TryGetValue(fullPath, out var weak) && weak.TryGetTarget(out var cached))
            {
                metadata = cached;
                return true;
            }
        }

        try
        {
            var json = File.ReadAllText(fullPath);
            metadata = JsonSerializer.Deserialize<PetModelMetadata>(json, MetadataJsonOptions);
            if (metadata is null || metadata.UnitScale <= 0 || !double.IsFinite(metadata.UnitScale))
            {
                return false;
            }

            lock (_metaCacheLock)
            {
                _metadataCache[fullPath] = new WeakReference<PetModelMetadata>(metadata);
            }

            return true;
        }
        catch
        {
            metadata = null;
            return false;
        }
    }

    private static bool HasRequiredCoreParts(ObjMeshAsset asset)
        => asset.SubMeshes.Any(m =>
            string.Equals(m.PartName, "Body", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.PartName, "Head", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeExpressionSuffix(string? expressionId)
        => string.IsNullOrWhiteSpace(expressionId)
            ? "Default"
            : expressionId.ToLowerInvariant() switch
            {
                "happy" => "Happy",
                "sleepy" => "Sleepy",
                "sparkle" => "Sparkle",
                _ => "Default"
            };

    private static string? ResolveExpressionPartName(
        ObjMeshAsset asset,
        string prefix,
        string exprSuffix,
        string defaultPartName,
        string genericPartName)
    {
        var exact = prefix + exprSuffix;
        if (asset.SubMeshes.Any(m => string.Equals(m.PartName, exact, StringComparison.OrdinalIgnoreCase)))
        {
            return exact;
        }

        if (asset.SubMeshes.Any(m => string.Equals(m.PartName, defaultPartName, StringComparison.OrdinalIgnoreCase)))
        {
            return defaultPartName;
        }

        if (asset.SubMeshes.Any(m => string.Equals(m.PartName, genericPartName, StringComparison.OrdinalIgnoreCase)))
        {
            return genericPartName;
        }

        return null;
    }

    private static Material CreateFrozenClayMaterial(Color baseColor, Color? emissiveColor, double specularPower, byte specularAlpha)
    {
        var diffuseBrush = new SolidColorBrush(baseColor);
        diffuseBrush.Freeze();

        var specularBrush = new SolidColorBrush(Color.FromArgb(specularAlpha, 255, 255, 255));
        specularBrush.Freeze();

        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(diffuseBrush));
        group.Children.Add(new SpecularMaterial(specularBrush, specularPower));

        if (emissiveColor is { } em)
        {
            var emissiveBrush = new SolidColorBrush(em);
            emissiveBrush.Freeze();
            group.Children.Add(new EmissiveMaterial(emissiveBrush));
        }

        group.Freeze();
        return group;
    }

    private static Material CreateFrozenDiffuseMaterial(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var mat = new DiffuseMaterial(brush);
        mat.Freeze();
        return mat;
    }
}
