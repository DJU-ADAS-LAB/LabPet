using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Geometry;
using LabPet.Shared.Models;
using HelixMeshGeometry3D = HelixToolkit.Geometry.MeshGeometry3D;
using WpfMeshGeometry3D = System.Windows.Media.Media3D.MeshGeometry3D;

namespace LabPet.Client.Rendering;

/// <summary>
/// HelixToolkit.Wpf 3.x (HelixToolkit.Geometry.MeshBuilder) 기반 상용 피규어 감성 절차적 3D 메시 팩토리.
/// 부드러운 곡면, 눈 깜빡임 리그, 종별 디테일 파츠(모찌 미니 팔, 치비 고양이 주둥이·코·수염·뒷발·젤리, 솜구름 투톤 클러스터·미니 날개,
/// 솜뭉치 강아지 처진 귀·크림 주둥이·가슴 털·네 발·깃털 꼬리),
/// 바닥 접지 그림자 및 4점 스튜디오 조명으로 말랑한 소프트 클레이 피규어 퀄리티를 구현한다.
/// </summary>
public sealed class ProceduralPetMeshFactory : IPetMeshFactory
{
    private const int MaxCachedMeshes = 256;
    private readonly Dictionary<int, WeakReference<WpfMeshGeometry3D>> _meshCache = [];
    private readonly object _meshCacheLock = new();

    public Model3DGroup CreateStudioLights()
    {
        var lights = new Model3DGroup();

        // 1. 포근한 주변광 (클레이 음영을 부드럽고 화사하게)
        lights.Children.Add(new AmbientLight(Color.FromRgb(109, 107, 116)));

        // 2. 따뜻한 메인 키 라이트 (우상단 전방)
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(211, 201, 191),
            new Vector3D(-0.48, -0.70, -0.90)));

        // 3. 부드러운 좌측 필 라이트 (전면 볼륨감 완화)
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(129, 141, 161),
            new Vector3D(0.76, -0.22, -0.58)));

        // 4. 은은한 후방 림 라이트 (실루엣 입체감 및 분리감)
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(141, 166, 199),
            new Vector3D(0.40, 0.58, 0.80)));

        return lights;
    }

    public PetRigHandle BuildPetRig(PetAppearance appearance, PetRigEffects effects = PetRigEffects.All)
    {
        var bodyColor = ParseHexColor(appearance.BodyColorHex, Color.FromRgb(255, 245, 247));
        var accentColor = ParseHexColor(appearance.AccentColorHex, Color.FromRgb(255, 158, 181));

        var bodyEmissiveBrush = new SolidColorBrush(Colors.Black);
        var auraEmissiveBrush = new SolidColorBrush(Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B));

        // 부드러운 무광 소프트 클레이 재질 (무재생성 색상 교체를 위해 Diffuse 브러시 보관)
        var bodyDiffuseBrush = new SolidColorBrush(bodyColor);
        var bodyLightDiffuseBrush = new SolidColorBrush(Lighten(bodyColor, 0.42));
        var bodyShadeDiffuseBrush = new SolidColorBrush(Darken(bodyColor, 0.06));
        var accentDiffuseBrush = new SolidColorBrush(accentColor);
        SolidColorBrush? earDarkDiffuseBrush = null;
        SolidColorBrush? earInnerDarkDiffuseBrush = null;
        SolidColorBrush? auraDiffuseBrush = null;

        var bodyMaterial = CreateClayMaterial(bodyDiffuseBrush, bodyEmissiveBrush, specularPower: 16, specularAlpha: 68);
        var softLightBodyMaterial = CreateClayMaterial(bodyLightDiffuseBrush, bodyEmissiveBrush, specularPower: 14, specularAlpha: 56);
        var softShadeBodyMaterial = CreateClayMaterial(bodyShadeDiffuseBrush, bodyEmissiveBrush, specularPower: 14, specularAlpha: 50);
        var accentMaterial = CreateClayMaterial(accentDiffuseBrush, bodyEmissiveBrush, specularPower: 15, specularAlpha: 66);
        var darkEyeMaterial = CreateClayMaterial(Color.FromRgb(42, 36, 48), null, specularPower: 58, specularAlpha: 155);
        var whiteHighlightMaterial = CreateClayMaterial(Colors.White, new SolidColorBrush(Color.FromRgb(75, 75, 75)), specularPower: 64, specularAlpha: 170);

        // 루트 변환 체인
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

        // 머리/표정 그룹 변환 체인
        var headYaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var headPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        var headRoll = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);

        var rootGroup = new Model3DGroup();
        var triangleCount = 0;

        // 바닥 소프트 접지 그림자 (피규어가 바닥에 자연스럽게 안착된 느낌)
        var shadowScale = new ScaleTransform3D(1, 1, 1);
        var shadowY = appearance.SpeciesId == PetSpeciesIds.ChibiCat ? -0.92f : -0.96f;
        var shadowMb = new MeshBuilder(true, false);
        shadowMb.AddEllipsoid(new Vector3(0, shadowY, 0), 0.78f, 0.015f, 0.68f, 24, 6);
        var shadowMat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(44, 42, 48, 66)));
        triangleCount += AddGeometry(rootGroup, shadowMb, shadowMat, shadowScale);

        var bodyGroup = new Model3DGroup { Transform = bodyTransformGroup };
        rootGroup.Children.Add(bodyGroup);

        AxisAngleRotation3D? leftEarRot = null;
        AxisAngleRotation3D? rightEarRot = null;
        AxisAngleRotation3D? tailRot = null;
        TranslateTransform3D? leftPawTrans = null;
        TranslateTransform3D? rightPawTrans = null;
        AxisAngleRotation3D? leftArmRot = null;
        AxisAngleRotation3D? rightArmRot = null;
        AxisAngleRotation3D? leftArmPitch = null;
        AxisAngleRotation3D? rightArmPitch = null;
        Func<Point3D>? rightHandPosition = null;

        float headPivotY;
        float headSocketY;
        float faceCenterY;
        float faceZ;
        float headRadiusX;

        var headGroup = new Model3DGroup();

        switch (appearance.SpeciesId)
        {
            case PetSpeciesIds.ChibiCat:
            {
                headPivotY = 0.24f;
                headSocketY = 1.12f;
                faceCenterY = 0.42f;
                faceZ = 0.74f;
                headRadiusX = 0.82f;

                // 통통하고 부드러운 몸통
                var torsoMb = new MeshBuilder(true, false);
                torsoMb.AddEllipsoid(new Vector3(0, -0.44f, 0), 0.52f, 0.47f, 0.46f, 30, 20);
                triangleCount += AddGeometry(bodyGroup, torsoMb, bodyMaterial);

                // 밝은 배 패치
                var bellyMb = new MeshBuilder(true, false);
                bellyMb.AddEllipsoid(new Vector3(0, -0.47f, 0.34f), 0.30f, 0.27f, 0.16f, 22, 14);
                triangleCount += AddGeometry(bodyGroup, bellyMb, softLightBodyMaterial);

                // 둥근 앞발 + 작은 핑크 젤리 포인트 (타이핑/리듬 반응용)
                leftPawTrans = new TranslateTransform3D(0, 0, 0);
                var leftPawGroup = new Model3DGroup { Transform = leftPawTrans };
                var leftPawMb = new MeshBuilder(true, false);
                leftPawMb.AddEllipsoid(new Vector3(-0.33f, -0.60f, 0.36f), 0.165f, 0.135f, 0.185f, 20, 14);
                triangleCount += AddGeometry(leftPawGroup, leftPawMb, bodyMaterial);
                var leftPadMb = new MeshBuilder(true, false);
                leftPadMb.AddEllipsoid(new Vector3(-0.33f, -0.62f, 0.51f), 0.065f, 0.055f, 0.03f, 12, 8);
                triangleCount += AddGeometry(leftPawGroup, leftPadMb, accentMaterial);
                bodyGroup.Children.Add(leftPawGroup);

                rightPawTrans = new TranslateTransform3D(0, 0, 0);
                var rightPawGroup = new Model3DGroup { Transform = rightPawTrans };
                var rightPawMb = new MeshBuilder(true, false);
                rightPawMb.AddEllipsoid(new Vector3(0.33f, -0.60f, 0.36f), 0.165f, 0.135f, 0.185f, 20, 14);
                triangleCount += AddGeometry(rightPawGroup, rightPawMb, bodyMaterial);
                var rightPadMb = new MeshBuilder(true, false);
                rightPadMb.AddEllipsoid(new Vector3(0.33f, -0.62f, 0.51f), 0.065f, 0.055f, 0.03f, 12, 8);
                triangleCount += AddGeometry(rightPawGroup, rightPadMb, accentMaterial);
                bodyGroup.Children.Add(rightPawGroup);
                rightHandPosition = () => rightPawGroup.Transform.Transform(new Point3D(0.33, -0.60, 0.51));

                // 귀여운 앉은 뒷발 2개
                var backFeetMb = new MeshBuilder(true, false);
                backFeetMb.AddEllipsoid(new Vector3(-0.28f, -0.82f, 0.12f), 0.19f, 0.11f, 0.22f, 18, 12);
                backFeetMb.AddEllipsoid(new Vector3(0.28f, -0.82f, 0.12f), 0.19f, 0.11f, 0.22f, 18, 12);
                triangleCount += AddGeometry(bodyGroup, backFeetMb, bodyMaterial);

                // 부드러운 S곡선 꼬리 (끝부분 포인트 컬러)
                tailRot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
                var tailGroup = new Model3DGroup
                {
                    Transform = new RotateTransform3D(tailRot, new Point3D(0, -0.58, -0.36))
                };
                var tailMb = new MeshBuilder(true, false);
                var tailPath = new List<Vector3>
                {
                    new(0, -0.58f, -0.36f),
                    new(0.08f, -0.56f, -0.54f),
                    new(0.18f, -0.46f, -0.70f),
                    new(0.26f, -0.28f, -0.82f),
                    new(0.27f, -0.08f, -0.82f),
                    new(0.22f, 0.06f, -0.76f)
                };
                tailMb.AddTube(tailPath, diameter: 0.185f, thetaDiv: 18, isTubeClosed: false);
                triangleCount += AddGeometry(tailGroup, tailMb, bodyMaterial);

                var tailTipMb = new MeshBuilder(true, false);
                tailTipMb.AddSphere(new Vector3(0.22f, 0.06f, -0.76f), 0.105f, 16, 12);
                triangleCount += AddGeometry(tailGroup, tailTipMb, softLightBodyMaterial);
                bodyGroup.Children.Add(tailGroup);

                // 매끄럽고 큰 둥근 고양이 머리
                var headMb = new MeshBuilder(true, false);
                headMb.AddEllipsoid(new Vector3(0, 0.42f, 0), 0.82f, 0.71f, 0.75f, 36, 24);
                triangleCount += AddGeometry(headGroup, headMb, bodyMaterial);

                // 둥근 고양이 주둥이(머즐 패드 2개)
                var muzzleMb = new MeshBuilder(true, false);
                muzzleMb.AddEllipsoid(new Vector3(-0.105f, faceCenterY - 0.11f, faceZ - 0.01f), 0.135f, 0.10f, 0.09f, 16, 10);
                muzzleMb.AddEllipsoid(new Vector3(0.105f, faceCenterY - 0.11f, faceZ - 0.01f), 0.135f, 0.10f, 0.09f, 16, 10);
                triangleCount += AddGeometry(headGroup, muzzleMb, softLightBodyMaterial);

                // 작은 핑크 코
                var noseMb = new MeshBuilder(true, false);
                noseMb.AddEllipsoid(new Vector3(0, faceCenterY - 0.045f, faceZ + 0.065f), 0.062f, 0.045f, 0.042f, 14, 10);
                triangleCount += AddGeometry(headGroup, noseMb, accentMaterial);

                // 귀여운 양볼 수염 2쌍
                var whiskerMb = new MeshBuilder(true, false);
                whiskerMb.AddCylinder(new Vector3(-0.56f, faceCenterY - 0.06f, faceZ - 0.16f), new Vector3(-0.84f, faceCenterY - 0.02f, faceZ - 0.22f), 0.014f, 6);
                whiskerMb.AddCylinder(new Vector3(-0.56f, faceCenterY - 0.14f, faceZ - 0.16f), new Vector3(-0.84f, faceCenterY - 0.18f, faceZ - 0.22f), 0.014f, 6);
                whiskerMb.AddCylinder(new Vector3(0.56f, faceCenterY - 0.06f, faceZ - 0.16f), new Vector3(0.84f, faceCenterY - 0.02f, faceZ - 0.22f), 0.014f, 6);
                whiskerMb.AddCylinder(new Vector3(0.56f, faceCenterY - 0.14f, faceZ - 0.16f), new Vector3(0.84f, faceCenterY - 0.18f, faceZ - 0.22f), 0.014f, 6);
                triangleCount += AddGeometry(headGroup, whiskerMb, darkEyeMaterial);

                // 왼쪽 둥근 끝 삼각 귀
                leftEarRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 15);
                var leftEarGroup = new Model3DGroup
                {
                    Transform = new RotateTransform3D(leftEarRot, new Point3D(-0.48, 0.94, 0.0))
                };
                var leftEarOuterMb = new MeshBuilder(true, false);
                leftEarOuterMb.AddCone(new Vector3(-0.48f, 0.86f, 0.0f), new Vector3(-0.22f, 1.0f, 0.08f), 0.28f, 0.065f, 0.43f, true, true, 24);
                leftEarOuterMb.AddSphere(new Vector3(-0.57f, 1.28f, 0.03f), 0.07f, 14, 10);
                triangleCount += AddGeometry(leftEarGroup, leftEarOuterMb, bodyMaterial);

                var leftEarInnerMb = new MeshBuilder(true, false);
                leftEarInnerMb.AddCone(new Vector3(-0.46f, 0.90f, 0.085f), new Vector3(-0.20f, 1.0f, 0.10f), 0.16f, 0.035f, 0.28f, true, true, 18);
                triangleCount += AddGeometry(leftEarGroup, leftEarInnerMb, accentMaterial);
                headGroup.Children.Add(leftEarGroup);

                // 오른쪽 둥근 끝 삼각 귀
                rightEarRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), -15);
                var rightEarGroup = new Model3DGroup
                {
                    Transform = new RotateTransform3D(rightEarRot, new Point3D(0.48, 0.94, 0.0))
                };
                var rightEarOuterMb = new MeshBuilder(true, false);
                rightEarOuterMb.AddCone(new Vector3(0.48f, 0.86f, 0.0f), new Vector3(0.22f, 1.0f, 0.08f), 0.28f, 0.065f, 0.43f, true, true, 24);
                rightEarOuterMb.AddSphere(new Vector3(0.57f, 1.28f, 0.03f), 0.07f, 14, 10);
                triangleCount += AddGeometry(rightEarGroup, rightEarOuterMb, bodyMaterial);

                var rightEarInnerMb = new MeshBuilder(true, false);
                rightEarInnerMb.AddCone(new Vector3(0.46f, 0.90f, 0.085f), new Vector3(0.20f, 1.0f, 0.10f), 0.16f, 0.035f, 0.28f, true, true, 18);
                triangleCount += AddGeometry(rightEarGroup, rightEarInnerMb, accentMaterial);
                headGroup.Children.Add(rightEarGroup);

                break;
            }

            case PetSpeciesIds.FluffyPuppy:
            {
                headPivotY = 0.0f;
                headSocketY = 1.00f;
                faceCenterY = 0.36f;
                faceZ = 0.77f;
                headRadiusX = 0.72f;

                earDarkDiffuseBrush = new SolidColorBrush(Darken(bodyColor, 0.23));
                earInnerDarkDiffuseBrush = new SolidColorBrush(Darken(bodyColor, 0.32));
                var earMaterial = CreateClayMaterial(earDarkDiffuseBrush, bodyEmissiveBrush, specularPower: 14, specularAlpha: 52);
                var innerEarMaterial = CreateClayMaterial(earInnerDarkDiffuseBrush, null, specularPower: 12, specularAlpha: 42);
                var noseMaterial = CreateClayMaterial(Color.FromRgb(80, 50, 45), null, specularPower: 46, specularAlpha: 132);

                // 3/4 각도로 비스듬히 선 긴 몸통 그룹 (얼굴은 정면 유지, 엉덩이·꼬리는 오른쪽 뒤로)
                var torsoTransform = new Transform3DGroup();
                torsoTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), -45)));
                torsoTransform.Children.Add(new TranslateTransform3D(0.28, 0, 0));
                var torsoGroup = new Model3DGroup { Transform = torsoTransform };

                // 길쭉하고 통통한 몸통 (로컬 +Z가 앞쪽)
                var torsoMb = new MeshBuilder(true, false);
                torsoMb.AddEllipsoid(new Vector3(0, -0.40f, -0.32f), 0.42f, 0.34f, 0.69f, 24, 16);
                triangleCount += AddGeometry(torsoGroup, torsoMb, bodyMaterial);

                // 복슬한 크림색 가슴 털
                var chestMb = new MeshBuilder(true, false);
                chestMb.AddEllipsoid(new Vector3(0, -0.30f, 0.32f), 0.30f, 0.28f, 0.22f, 16, 12);
                chestMb.AddSphere(new Vector3(-0.12f, -0.46f, 0.36f), 0.13f, 12, 8);
                chestMb.AddSphere(new Vector3(0.12f, -0.46f, 0.36f), 0.13f, 12, 8);
                triangleCount += AddGeometry(torsoGroup, chestMb, softLightBodyMaterial);

                // 짧은 뒷다리 2개 + 크림색 발
                var backLegMb = new MeshBuilder(true, false);
                backLegMb.AddEllipsoid(new Vector3(-0.22f, -0.64f, -0.78f), 0.13f, 0.24f, 0.15f, 14, 10);
                backLegMb.AddEllipsoid(new Vector3(0.22f, -0.64f, -0.78f), 0.13f, 0.24f, 0.15f, 14, 10);
                triangleCount += AddGeometry(torsoGroup, backLegMb, bodyMaterial);
                var backPawMb = new MeshBuilder(true, false);
                backPawMb.AddEllipsoid(new Vector3(-0.22f, -0.86f, -0.74f), 0.13f, 0.085f, 0.15f, 14, 8);
                backPawMb.AddEllipsoid(new Vector3(0.22f, -0.86f, -0.74f), 0.13f, 0.085f, 0.15f, 14, 8);
                triangleCount += AddGeometry(torsoGroup, backPawMb, softLightBodyMaterial);

                // 위로 말려 올라간 복슬 깃털 꼬리 (밑동 기준 좌우 흔들기)
                tailRot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
                var tailGroup = new Model3DGroup
                {
                    Transform = new RotateTransform3D(tailRot, new Point3D(0, -0.26, -0.92))
                };
                var tailPath = new List<Vector3>
                {
                    new(0, -0.26f, -0.92f),
                    new(0, -0.14f, -1.04f),
                    new(0, 0.02f, -1.13f),
                    new(0, 0.18f, -1.16f),
                    new(0, 0.34f, -1.11f),
                    new(0, 0.45f, -0.98f)
                };
                var tailMb = new MeshBuilder(true, false);
                tailMb.AddTube(tailPath, diameter: 0.24f, thetaDiv: 16, isTubeClosed: false);
                triangleCount += AddGeometry(tailGroup, tailMb, bodyMaterial);

                var tailPlumeMb = new MeshBuilder(true, false);
                tailPlumeMb.AddEllipsoid(new Vector3(0, 0.24f, -1.21f), 0.12f, 0.24f, 0.14f, 14, 10);
                tailPlumeMb.AddSphere(tailPath[5], 0.115f, 12, 8);
                triangleCount += AddGeometry(tailGroup, tailPlumeMb, softLightBodyMaterial);
                torsoGroup.Children.Add(tailGroup);

                bodyGroup.Children.Add(torsoGroup);

                // 정면 좌우 대칭 앞다리 (타이핑 시 미니 키보드 위로 들어 올려 두드림)
                leftPawTrans = new TranslateTransform3D(0, 0, 0);
                var leftFrontGroup = new Model3DGroup { Transform = leftPawTrans };
                var leftLegMb = new MeshBuilder(true, false);
                leftLegMb.AddEllipsoid(new Vector3(-0.19f, -0.66f, 0.16f), 0.12f, 0.22f, 0.13f, 14, 10);
                triangleCount += AddGeometry(leftFrontGroup, leftLegMb, bodyMaterial);
                var leftFrontPawMb = new MeshBuilder(true, false);
                leftFrontPawMb.AddEllipsoid(new Vector3(-0.19f, -0.86f, 0.20f), 0.13f, 0.085f, 0.15f, 14, 8);
                triangleCount += AddGeometry(leftFrontGroup, leftFrontPawMb, softLightBodyMaterial);
                bodyGroup.Children.Add(leftFrontGroup);

                rightPawTrans = new TranslateTransform3D(0, 0, 0);
                var rightFrontGroup = new Model3DGroup { Transform = rightPawTrans };
                var rightLegMb = new MeshBuilder(true, false);
                rightLegMb.AddEllipsoid(new Vector3(0.19f, -0.66f, 0.16f), 0.12f, 0.22f, 0.13f, 14, 10);
                triangleCount += AddGeometry(rightFrontGroup, rightLegMb, bodyMaterial);
                var rightFrontPawMb = new MeshBuilder(true, false);
                rightFrontPawMb.AddEllipsoid(new Vector3(0.19f, -0.86f, 0.20f), 0.13f, 0.085f, 0.15f, 14, 8);
                triangleCount += AddGeometry(rightFrontGroup, rightFrontPawMb, softLightBodyMaterial);
                bodyGroup.Children.Add(rightFrontGroup);
                rightHandPosition = () => rightFrontGroup.Transform.Transform(new Point3D(0.19, -0.86, 0.35));

                // 크고 동그란 머리 + 통통한 볼살
                var headMb = new MeshBuilder(true, false);
                headMb.AddEllipsoid(new Vector3(0, 0.38f, 0.12f), headRadiusX, 0.62f, 0.61f, 32, 20);
                headMb.AddEllipsoid(new Vector3(0, 0.18f, 0.20f), 0.64f, 0.37f, 0.51f, 22, 14);
                triangleCount += AddGeometry(headGroup, headMb, bodyMaterial);

                // 양쪽으로 도톰한 크림색 주둥이
                var muzzleMb = new MeshBuilder(true, false);
                muzzleMb.AddEllipsoid(new Vector3(-0.10f, faceCenterY - 0.145f, faceZ - 0.065f), 0.155f, 0.135f, 0.17f, 18, 12);
                muzzleMb.AddEllipsoid(new Vector3(0.10f, faceCenterY - 0.145f, faceZ - 0.065f), 0.155f, 0.135f, 0.17f, 18, 12);
                triangleCount += AddGeometry(headGroup, muzzleMb, softLightBodyMaterial);

                // 둥근 초콜릿색 코와 작은 광택
                var noseMb = new MeshBuilder(true, false);
                noseMb.AddEllipsoid(new Vector3(0, faceCenterY - 0.06f, faceZ + 0.10f), 0.091f, 0.060f, 0.060f, 18, 12);
                triangleCount += AddGeometry(headGroup, noseMb, noseMaterial);
                var noseHighlight = new MeshBuilder(true, false);
                noseHighlight.AddEllipsoid(new Vector3(-0.027f, faceCenterY - 0.039f, faceZ + 0.150f), 0.024f, 0.012f, 0.008f, 10, 6);
                triangleCount += AddGeometry(headGroup, noseHighlight, whiteHighlightMaterial);

                // 길게 처진 귀와 안쪽 털이 윗부분을 중심으로 함께 움직인다.
                leftEarRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), -4);
                rightEarRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 4);
                for (var side = -1; side <= 1; side += 2)
                {
                    var earGroup = new Model3DGroup
                    {
                        Transform = new RotateTransform3D(side < 0 ? leftEarRot : rightEarRot, new Point3D(side * 0.58, 0.72, 0.16))
                    };
                    var earMb = new MeshBuilder(true, false);
                    earMb.AddEllipsoid(new Vector3(side * 0.67f, 0.32f, 0.16f), 0.20f, 0.43f, 0.24f, 18, 12);
                    earMb.AddEllipsoid(new Vector3(side * 0.73f, 0.08f, 0.18f), 0.23f, 0.30f, 0.23f, 18, 12);
                    triangleCount += AddGeometry(earGroup, earMb, earMaterial);
                    var innerEarMb = new MeshBuilder(true, false);
                    innerEarMb.AddEllipsoid(new Vector3(side * 0.77f, 0.13f, 0.37f), 0.121f, 0.235f, 0.066f, 14, 10);
                    triangleCount += AddGeometry(earGroup, innerEarMb, innerEarMaterial);
                    headGroup.Children.Add(earGroup);
                }

                break;
            }

            case PetSpeciesIds.FluffyCloud:
            {
                headPivotY = 0.0f;
                headSocketY = 0.94f;
                faceCenterY = 0.04f;
                faceZ = 0.84f;
                headRadiusX = 0.96f;

                // 투톤 몽실 구름 클러스터 (상단 밝은 구름 + 하단 소프트 그림자 구름)
                var cloudTopMb = new MeshBuilder(true, false);
                cloudTopMb.AddEllipsoid(new Vector3(0, 0.04f, 0.02f), 0.88f, 0.72f, 0.82f, 30, 20);
                cloudTopMb.AddSphere(new Vector3(-0.60f, -0.02f, 0.06f), 0.51f, 22, 16);
                cloudTopMb.AddSphere(new Vector3(0.60f, -0.02f, 0.06f), 0.51f, 22, 16);
                cloudTopMb.AddSphere(new Vector3(-0.36f, 0.40f, -0.04f), 0.49f, 22, 16);
                cloudTopMb.AddSphere(new Vector3(0.36f, 0.40f, -0.04f), 0.49f, 22, 16);
                cloudTopMb.AddSphere(new Vector3(0.0f, 0.52f, -0.02f), 0.47f, 22, 16);
                triangleCount += AddGeometry(headGroup, cloudTopMb, bodyMaterial);

                var cloudBottomMb = new MeshBuilder(true, false);
                cloudBottomMb.AddSphere(new Vector3(-0.38f, -0.34f, -0.06f), 0.44f, 18, 12);
                cloudBottomMb.AddSphere(new Vector3(0.38f, -0.34f, -0.06f), 0.44f, 18, 12);
                cloudBottomMb.AddSphere(new Vector3(0.0f, -0.26f, -0.38f), 0.50f, 18, 12);
                // 아래쪽 작은 솜방울 디테일
                cloudBottomMb.AddSphere(new Vector3(-0.56f, -0.58f, 0.08f), 0.14f, 14, 10);
                cloudBottomMb.AddSphere(new Vector3(0.56f, -0.58f, 0.08f), 0.14f, 14, 10);
                triangleCount += AddGeometry(headGroup, cloudBottomMb, softShadeBodyMaterial);

                // 양옆에 살랑이는 미니 천사 구름 날개 (Z축 파닥임 + X축 앞쪽 타건 회전)
                leftArmRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                leftArmPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                var leftWingTf = new Transform3DGroup();
                var leftWingPivot = new Point3D(-0.92, 0.06, -0.04);
                leftWingTf.Children.Add(new RotateTransform3D(leftArmRot, leftWingPivot));
                leftWingTf.Children.Add(new RotateTransform3D(leftArmPitch, leftWingPivot));
                var leftWingMb = new MeshBuilder(true, false);
                leftWingMb.AddEllipsoid(new Vector3(-1.12f, 0.08f, -0.04f), 0.26f, 0.14f, 0.12f, 16, 10);
                triangleCount += AddGeometry(headGroup, leftWingMb, softLightBodyMaterial, leftWingTf);

                rightArmRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                rightArmPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                var rightWingTf = new Transform3DGroup();
                var rightWingPivot = new Point3D(0.92, 0.06, -0.04);
                rightWingTf.Children.Add(new RotateTransform3D(rightArmRot, rightWingPivot));
                rightWingTf.Children.Add(new RotateTransform3D(rightArmPitch, rightWingPivot));
                var rightWingMb = new MeshBuilder(true, false);
                rightWingMb.AddEllipsoid(new Vector3(1.12f, 0.08f, -0.04f), 0.26f, 0.14f, 0.12f, 16, 10);
                triangleCount += AddGeometry(headGroup, rightWingMb, softLightBodyMaterial, rightWingTf);
                rightHandPosition = () => headGroup.Transform.Transform(
                    rightWingTf.Transform(new Point3D(1.30, 0.08, 0.02)));

                break;
            }

            case PetSpeciesIds.Hachiware:
            case PetSpeciesIds.Chiikawa:
            case PetSpeciesIds.Usagi:
            case PetSpeciesIds.PinkReference:
            {
                headPivotY = 0.02f;
                headSocketY = 0.86f;
                faceCenterY = 0.24f;
                faceZ = 0.63f;
                headRadiusX = 0.78f;

                var isHachi = appearance.SpeciesId == PetSpeciesIds.Hachiware;
                var isUsagi = appearance.SpeciesId == PetSpeciesIds.Usagi;
                var isKani = appearance.SpeciesId == PetSpeciesIds.PinkReference;
                var hachiBlueMat = CreateClayMaterial(Color.FromRgb(109, 169, 204), bodyEmissiveBrush, specularPower: 14, specularAlpha: 48);
                var kaniPinkMat = CreateClayMaterial(Color.FromRgb(248, 178, 190), bodyEmissiveBrush, specularPower: 11, specularAlpha: 28);

                var torsoMb = new MeshBuilder(true, false);
                torsoMb.AddEllipsoid(new Vector3(0, -0.36f, 0), 0.56f, 0.44f, 0.50f, 28, 18);
                torsoMb.AddEllipsoid(new Vector3(-0.22f, -0.80f, 0.06f), 0.14f, 0.15f, 0.16f, 16, 10);
                torsoMb.AddEllipsoid(new Vector3(0.22f, -0.80f, 0.06f), 0.14f, 0.15f, 0.16f, 16, 10);
                triangleCount += AddGeometry(bodyGroup, torsoMb, bodyMaterial);

                leftArmRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                leftArmPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                var leftArmTf = new Transform3DGroup();
                var leftArmPivot = new Point3D(-0.54, -0.28, 0.10);
                leftArmTf.Children.Add(new RotateTransform3D(leftArmRot, leftArmPivot));
                leftArmTf.Children.Add(new RotateTransform3D(leftArmPitch, leftArmPivot));
                var leftArmMb = new MeshBuilder(true, false);
                leftArmMb.AddEllipsoid(new Vector3(-0.62f, -0.30f, 0.14f), 0.15f, 0.11f, 0.12f, 14, 10);
                triangleCount += AddGeometry(bodyGroup, leftArmMb, bodyMaterial, leftArmTf);

                rightArmRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                rightArmPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                var rightArmTf = new Transform3DGroup();
                var rightArmPivot = new Point3D(0.54, -0.28, 0.10);
                rightArmTf.Children.Add(new RotateTransform3D(rightArmRot, rightArmPivot));
                rightArmTf.Children.Add(new RotateTransform3D(rightArmPitch, rightArmPivot));
                var rightArmMb = new MeshBuilder(true, false);
                rightArmMb.AddEllipsoid(new Vector3(0.62f, -0.30f, 0.14f), 0.15f, 0.11f, 0.12f, 14, 10);
                triangleCount += AddGeometry(bodyGroup, rightArmMb, bodyMaterial, rightArmTf);
                rightHandPosition = () => rightArmTf.Transform(new Point3D(0.72, -0.30, 0.22));

                tailRot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
                var tailGroup = new Model3DGroup
                {
                    Transform = new RotateTransform3D(tailRot, new Point3D(0, -0.62, -0.45))
                };
                var tailMb = new MeshBuilder(true, false);
                tailMb.AddSphere(new Vector3(isHachi ? -0.18f : 0f, -0.60f, -0.54f), 0.14f, 14, 10);
                triangleCount += AddGeometry(tailGroup, tailMb, isHachi ? hachiBlueMat : softLightBodyMaterial);
                bodyGroup.Children.Add(tailGroup);

                var headMb = new MeshBuilder(true, false);
                headMb.AddEllipsoid(new Vector3(0, 0.33f, 0), 0.72f, 0.56f, 0.63f, 32, 22);
                triangleCount += AddGeometry(headGroup, headMb, bodyMaterial);

                if (isHachi)
                {
                    var capMb = new MeshBuilder(true, false);
                    capMb.AddEllipsoid(new Vector3(-0.30f, 0.64f, 0.16f), 0.34f, 0.22f, 0.34f, 18, 12);
                    capMb.AddEllipsoid(new Vector3(0.30f, 0.64f, 0.16f), 0.34f, 0.22f, 0.34f, 18, 12);
                    triangleCount += AddGeometry(headGroup, capMb, hachiBlueMat);
                }
                else if (isKani)
                {
                    var capMb = new MeshBuilder(true, false);
                    capMb.AddEllipsoid(new Vector3(0f, 0.66f, 0.02f), 0.66f, 0.24f, 0.56f, 20, 12);
                    triangleCount += AddGeometry(headGroup, capMb, kaniPinkMat);
                }

                leftEarRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                rightEarRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                for (var side = -1; side <= 1; side += 2)
                {
                    var earRot = side < 0 ? leftEarRot : rightEarRot;
                    var earPivot = isUsagi
                        ? new Point3D(side * 0.15, 0.78, 0)
                        : new Point3D(side * 0.40, 0.77, 0);
                    var earGroup = new Model3DGroup { Transform = new RotateTransform3D(earRot, earPivot) };
                    var earMb = new MeshBuilder(true, false);
                    if (isUsagi)
                    {
                        earMb.AddEllipsoid(new Vector3(side * 0.16f, 1.05f, 0), 0.11f, 0.32f, 0.09f, 16, 12);
                        triangleCount += AddGeometry(earGroup, earMb, bodyMaterial);
                    }
                    else if (isHachi)
                    {
                        earMb.AddCone(new Vector3(side * 0.40f, 0.75f, 0), new Vector3(side * 0.14f, 1.0f, 0.04f), 0.21f, 0.05f, 0.32f, true, true, 18);
                        triangleCount += AddGeometry(earGroup, earMb, hachiBlueMat);
                    }
                    else if (isKani)
                    {
                        earMb.AddSphere(new Vector3(side * 0.46f, 0.94f, 0.01f), 0.15f, 16, 12);
                        earMb.AddSphere(new Vector3(side * 0.54f, 1.06f, 0.01f), 0.11f, 14, 10);
                        earMb.AddSphere(new Vector3(side * 0.38f, 1.08f, 0.01f), 0.11f, 14, 10);
                        triangleCount += AddGeometry(earGroup, earMb, kaniPinkMat);
                    }
                    else
                    {
                        earMb.AddSphere(new Vector3(side * 0.44f, 0.84f, 0.01f), 0.15f, 16, 12);
                        triangleCount += AddGeometry(earGroup, earMb, bodyMaterial);
                    }

                    headGroup.Children.Add(earGroup);
                }

                break;
            }

            case PetSpeciesIds.MochiSpirit:
            default:
            {
                headPivotY = -0.10f;
                headSocketY = 0.84f;
                faceCenterY = 0.02f;
                faceZ = 0.94f;
                headRadiusX = 1.04f;

                // 매끄럽고 쫀득한 모찌 3D 타원체 바디
                var mochiMb = new MeshBuilder(true, false);
                mochiMb.AddEllipsoid(new Vector3(0, -0.02f, 0), 1.04f, 0.86f, 0.97f, 36, 24);
                mochiMb.AddEllipsoid(new Vector3(0, -0.44f, 0), 1.02f, 0.45f, 0.95f, 30, 18);
                // 뒤쪽 조그만 모찌 꽁지
                mochiMb.AddSphere(new Vector3(0, -0.34f, -0.96f), 0.18f, 14, 10);
                triangleCount += AddGeometry(headGroup, mochiMb, bodyMaterial);

                // 양옆에 귀엽게 달린 말랑 미니 팔 (Z축 흔들림 + X축 앞쪽 키보드 타건)
                leftArmRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                leftArmPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                var leftArmTf = new Transform3DGroup();
                var leftArmPivot = new Point3D(-0.88, -0.16, 0.22);
                leftArmTf.Children.Add(new RotateTransform3D(leftArmRot, leftArmPivot));
                leftArmTf.Children.Add(new RotateTransform3D(leftArmPitch, leftArmPivot));
                var leftArmMb = new MeshBuilder(true, false);
                leftArmMb.AddEllipsoid(new Vector3(-1.02f, -0.18f, 0.24f), 0.22f, 0.14f, 0.16f, 16, 10);
                triangleCount += AddGeometry(headGroup, leftArmMb, bodyMaterial, leftArmTf);

                rightArmRot = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                rightArmPitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                var rightArmTf = new Transform3DGroup();
                var rightArmPivot = new Point3D(0.88, -0.16, 0.22);
                rightArmTf.Children.Add(new RotateTransform3D(rightArmRot, rightArmPivot));
                rightArmTf.Children.Add(new RotateTransform3D(rightArmPitch, rightArmPivot));
                var rightArmMb = new MeshBuilder(true, false);
                rightArmMb.AddEllipsoid(new Vector3(1.02f, -0.18f, 0.24f), 0.22f, 0.14f, 0.16f, 16, 10);
                triangleCount += AddGeometry(headGroup, rightArmMb, bodyMaterial, rightArmTf);
                rightHandPosition = () => headGroup.Transform.Transform(
                    rightArmTf.Transform(new Point3D(1.18, -0.18, 0.34)));

                break;
            }
        }

        // 머리/얼굴 회전 중심 적용
        var headTransform = new Transform3DGroup();
        var pivot = new Point3D(0, headPivotY, 0);
        headTransform.Children.Add(new RotateTransform3D(headYaw, pivot));
        headTransform.Children.Add(new RotateTransform3D(headPitch, pivot));
        headTransform.Children.Add(new RotateTransform3D(headRoll, pivot));
        headGroup.Transform = headTransform;

        // 게임의 임시 표정과 사용자 설정 표정은 같은 얼굴 그룹을 교체한다.
        var faceGroup = new Model3DGroup();
        headGroup.Children.Add(faceGroup);
        var eyeCenter = new Point3D(0, faceCenterY + 0.06, faceZ);
        var eyeBlinkScale = new ScaleTransform3D(1, 1, 1, eyeCenter.X, eyeCenter.Y, eyeCenter.Z);
        var expressionTriangles = 0;
        var speciesId = appearance.SpeciesId;
        int SelectExpression(string expression)
        {
            faceGroup.Children.Clear();
            var previous = expressionTriangles;
            expressionTriangles = BuildExpressionParts(
                faceGroup, expression, faceCenterY, faceZ, speciesId,
                darkEyeMaterial, whiteHighlightMaterial, accentMaterial, eyeBlinkScale);
            return expressionTriangles - previous;
        }
        triangleCount += SelectExpression(appearance.ExpressionId);

        // 보조 회전은 소켓을 중심으로 적용하고 머리 관절의 자식으로 유지한다.
        var accessoryId = appearance.HeadAccessoryId?.ToLowerInvariant() ?? "none";
        AxisAngleRotation3D? accessoryRotation = accessoryId is "sprout" or "ribbon" or "santa_hat"
            ? new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0) : null;
        var accessoryLed = accessoryId == "headset"
            ? new SolidColorBrush(Color.FromRgb(154, 220, 255)) { Opacity = 0.08 } : null;
        var accessoryGroup = new Model3DGroup();
        if (accessoryRotation is not null && accessoryId != "santa_hat")
            accessoryGroup.Transform = new RotateTransform3D(accessoryRotation, new Point3D(0, headSocketY, 0));
        triangleCount += BuildHeadAccessory(
            accessoryGroup,
            accessoryId,
            headSocketY,
            headRadiusX,
            accentColor,
            accentDiffuseBrush,
            pomRotation: accessoryId == "santa_hat" ? accessoryRotation : null,
            ledBrush: accessoryLed);
        headGroup.Children.Add(accessoryGroup);

        bodyGroup.Children.Add(headGroup);

        var typingKeyboardPlacement = TypingKeyboardPlacement.ForSpecies(appearance.SpeciesId);
        (int keyboardTriangles, ScaleTransform3D? typingKeyboardScale) = effects.HasFlag(PetRigEffects.TypingKeyboard)
            ? BuildTypingKeyboard(rootGroup, accentColor, typingKeyboardPlacement, accentDiffuseBrush)
            : (0, null);
        var (particleTriangles, musicParticles) = effects.HasFlag(PetRigEffects.MusicParticles)
            ? BuildMusicParticles(rootGroup, accentColor)
            : (0, Array.Empty<MusicParticleHandle>());
        triangleCount += keyboardTriangles + particleTriangles;

        // 3D 오라/주변 이펙트
        AxisAngleRotation3D? auraRot = null;
        TranslateTransform3D? auraTrans = null;
        MusicParticleHandle[] auraParticles = [];
        var auraId = appearance.AuraEffectId?.ToLowerInvariant() ?? "none";
        if (auraId == "halo_ring")
        {
            var auraGroup = new Model3DGroup();
            var pool = PetHaloPool.Build(auraGroup, accentColor);
            auraParticles = pool.Particles;
            triangleCount += pool.Triangles;
            rootGroup.Children.Add(auraGroup);
        }
        else if (auraId is "music_notes" or "snowflakes")
        {
            var auraGroup = new Model3DGroup();
            var pool = PetLegacyAuraPool.Build(auraGroup, auraId, accentColor);
            auraParticles = pool.Particles;
            triangleCount += pool.Triangles;
            rootGroup.Children.Add(auraGroup);
        }
        else if (auraId is "starlight" or "cherry_blossom" or "milky_way" or "fireflies" or "aurora" or "electric")
        {
            var auraGroup = new Model3DGroup();
            var pool = auraId switch
            {
                "fireflies" => PetAuraParticlePool.BuildSoftGlow(auraGroup, auraId, Color.FromRgb(255, 222, 132)),
                "aurora" => PetAuroraPool.Build(auraGroup),
                "electric" => PetEnergyPool.Build(auraGroup),
                _ => PetAuraParticlePool.BuildTheme(auraGroup, auraId, accentColor)
            };
            auraParticles = pool.Particles;
            triangleCount += pool.Triangles;
            rootGroup.Children.Add(auraGroup);
        }
        else if (auraId != "none")
        {
            auraRot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            auraTrans = new TranslateTransform3D(0, 0, 0);

            var auraTransform = new Transform3DGroup();
            auraTransform.Children.Add(new RotateTransform3D(auraRot));
            auraTransform.Children.Add(auraTrans);

            var auraGroup = new Model3DGroup { Transform = auraTransform };
            var auraResult = BuildAuraEffect(auraGroup, auraId, accentColor, auraEmissiveBrush);
            triangleCount += auraResult.Triangles;
            auraDiffuseBrush = auraResult.AuraDiffuseBrush;
            rootGroup.Children.Add(auraGroup);
        }

        // 모든 오라 분기는 마지막 자식으로 전용 그룹을 추가한다.
        // 공유 메시 자체가 아니라 완성된 모델 트리만 장식으로 등록한다.
        if (auraId != "none")
            PetEffectHitTest.MarkDecorative(rootGroup.Children[rootGroup.Children.Count - 1]);

        var leftEarBaseAngle = appearance.SpeciesId switch
        {
            PetSpeciesIds.ChibiCat => 15.0,
            PetSpeciesIds.FluffyPuppy => -4.0,
            _ => 0.0
        };
        var rightEarBaseAngle = appearance.SpeciesId switch
        {
            PetSpeciesIds.ChibiCat => -15.0,
            PetSpeciesIds.FluffyPuppy => 4.0,
            _ => 0.0
        };

        return new PetRigHandle
        {
            SpeciesId = appearance.SpeciesId,
            AppearanceStructureSignature = PetRigHandle.BuildStructureSignature(appearance),
            IsExternalAsset = false,
            LeftEarBaseAngle = leftEarBaseAngle,
            RightEarBaseAngle = rightEarBaseAngle,
            RootModel = rootGroup,
            TotalTriangleCount = triangleCount,
            NameplateAnchor = new Point3D(0, bodyGroup.Bounds.Y, 0.35),
            BodyBounds = bodyGroup.Bounds,
            BodyTransform = bodyTransformGroup,
            BodyScale = bodyScale,
            BodyTranslate = bodyTranslate,
            BodyYaw = bodyYaw,
            BodyPitch = bodyPitch,
            BodyRoll = bodyRoll,
            HeadYaw = headYaw,
            HeadPitch = headPitch,
            HeadRoll = headRoll,
            EyeBlinkScale = eyeBlinkScale,
            SelectExpression = SelectExpression,
            CreateTearEffect = () => BuildTearEffect(headGroup, eyeCenter),
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
            RightHandPositionProvider = rightHandPosition,
            HeadAccessoryId = accessoryId,
            AccessoryRotation = accessoryRotation,
            AccessoryEmissiveBrush = accessoryLed,
            AuraParticles = auraParticles,
            AuraEffectId = auraId,
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

    internal const float TypingKeyboardCenterY = -0.52f;
    internal const float TypingKeyboardCenterZ = 0.78f;
    internal const float TypingKeyboardKeyTopY = TypingKeyboardCenterY + TypingKeyboardPlacement.KeycapTopOffsetY;

    internal (int Triangles, ScaleTransform3D Scale) BuildTypingKeyboard(
        Model3DGroup rootGroup,
        Color accentColor,
        TypingKeyboardPlacement placement,
        SolidColorBrush? sharedAccentBrush = null)
    {
        var centerY = placement.CenterY;
        var centerZ = placement.CenterZ;
        var scale = new ScaleTransform3D(0, 0, 0, 0, centerY, centerZ);
        var keyboard = new Model3DGroup { Transform = scale };

        var keyboardBase = new MeshBuilder(true, false);
        keyboardBase.AddBox(new Vector3(0, centerY, centerZ), 1.16f, 0.085f, 0.44f);
        var triangles = AddGeometry(keyboard, keyboardBase, CreateClayMaterial(Color.FromRgb(242, 245, 252), null, 22, 80));

        var keys = new MeshBuilder(true, false);
        for (var row = 0; row < 3; row++)
        {
            var z = centerZ - 0.06f + row * 0.11f;
            for (var col = 0; col < 7; col++)
            {
                var x = -0.45f + col * 0.15f;
                keys.AddBox(new Vector3(x, centerY + 0.055f, z), 0.11f, 0.04f, 0.08f);
            }
        }

        triangles += AddGeometry(keyboard, keys, CreateClayMaterial(Colors.White, null, 18, 70));

        var spacebar = new MeshBuilder(true, false);
        // 펫과 가까운 키보드 가장자리에 스페이스바를 둔다.
        spacebar.AddBox(new Vector3(0, centerY + 0.055f, centerZ - 0.16f), 0.44f, 0.04f, 0.07f);
        var spacebarMat = sharedAccentBrush is not null
            ? CreateClayMaterial(sharedAccentBrush, null, 20, 75)
            : CreateClayMaterial(accentColor, null, 20, 75);
        triangles += AddGeometry(keyboard, spacebar, spacebarMat);
        rootGroup.Children.Add(keyboard);
        return (triangles, scale);
    }

    internal (int Triangles, MusicParticleHandle[] Particles) BuildMusicParticles(
        Model3DGroup rootGroup,
        Color accentColor,
        int particleCount = 6,
        WpfMeshGeometry3D? externalNoteMesh = null,
        WpfMeshGeometry3D? externalStarMesh = null)
    {
        var baseColor = Lighten(accentColor, 0.25);

        WpfMeshGeometry3D noteMesh;
        if (externalNoteMesh is not null)
        {
            noteMesh = externalNoteMesh;
        }
        else
        {
            var note = new MeshBuilder(true, false);
            note.AddEllipsoid(Vector3.Zero, 0.10f, 0.08f, 0.06f, 12, 8);
            note.AddCylinder(new Vector3(0.08f, 0, 0), new Vector3(0.08f, 0.26f, 0), 0.022f, 8);
            note.AddBox(new Vector3(0.14f, 0.23f, 0), 0.12f, 0.05f, 0.03f);
            noteMesh = GetSharedMesh(note.ToMesh());
        }

        WpfMeshGeometry3D starMesh;
        if (externalStarMesh is not null)
        {
            starMesh = externalStarMesh;
        }
        else
        {
            var star = new MeshBuilder(true, false);
            star.AddOctahedron(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ, 0.12f, 0.12f);
            starMesh = GetSharedMesh(star.ToMesh());
        }

        var triangles = 0;
        var particles = new MusicParticleHandle[particleCount];
        for (var i = 0; i < particles.Length; i++)
        {
            var tintBrush = new SolidColorBrush(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B));
            var emissiveBrush = new SolidColorBrush(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B));
            var particleMaterial = CreateClayMaterial(tintBrush, emissiveBrush, 32, 110);

            var scale = new ScaleTransform3D(0, 0, 0);
            var rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            var translation = new TranslateTransform3D(0, 0, 0);
            var transform = new Transform3DGroup();
            transform.Children.Add(scale);
            transform.Children.Add(new RotateTransform3D(rotation));
            transform.Children.Add(translation);

            var isNote = i % 2 == 0;
            triangles += AddGeometry(rootGroup, isNote ? noteMesh : starMesh, particleMaterial, transform);
            particles[i] = new MusicParticleHandle
            {
                Scale = scale,
                Translate = translation,
                Rotation = rotation,
                TintBrush = tintBrush,
                EmissiveBrush = emissiveBrush,
                BaseColor = baseColor,
                ParticleKind = isNote ? "note" : "star"
            };
        }

        return (triangles, particles);
    }

    internal PetTearEffect BuildTearEffect(Model3DGroup head, Point3D eyeCenter)
    {
        var mesh = new MeshBuilder(true, false);
        mesh.AddEllipsoid(Vector3.Zero, 0.047f, 0.075f, 0.032f, 10, 8);
        return new PetTearEffect(head, eyeCenter, GetSharedMesh(mesh.ToMesh()));
    }

    private int BuildExpressionParts(
        Model3DGroup parent,
        string expressionId,
        float faceCenterY,
        float faceZ,
        string speciesId,
        Material eyeMaterial,
        Material highlightMaterial,
        Material cheekMaterial,
        ScaleTransform3D blinkScale)
    {
        var tris = 0;
        var isPuppy = speciesId == PetSpeciesIds.FluffyPuppy;

        // 부드러운 3D 볼터치
        var cheekMb = new MeshBuilder(true, false);
        cheekMb.AddEllipsoid(new Vector3(-0.43f, faceCenterY - 0.14f, faceZ - 0.08f), 0.165f, 0.10f, 0.075f, 18, 12);
        cheekMb.AddEllipsoid(new Vector3(0.43f, faceCenterY - 0.14f, faceZ - 0.08f), 0.165f, 0.10f, 0.075f, 18, 12);
        tris += AddGeometry(parent, cheekMb, cheekMaterial);

        // 눈 그룹 (깜빡임 스케일 적용)
        var eyeY = faceCenterY + 0.06f;
        var canBlink = expressionId is not ("happy" or "sleepy");
        var eyeGroup = new Model3DGroup { Transform = canBlink ? blinkScale : null };

        var eyeMb = new MeshBuilder(true, false);
        var highlightMb = new MeshBuilder(true, false);

        switch (expressionId)
        {
            case "happy":
            {
                AddEyeArc(eyeMb, -0.28f, eyeY, faceZ, 0.13f, 0.055f);
                AddEyeArc(eyeMb, 0.28f, eyeY, faceZ, 0.13f, 0.055f);
                break;
            }

            case "sleepy":
            {
                eyeMb.AddEllipsoid(new Vector3(-0.28f, faceCenterY + 0.04f, faceZ), 0.13f, 0.036f, 0.05f, 16, 10);
                eyeMb.AddEllipsoid(new Vector3(0.28f, faceCenterY + 0.04f, faceZ), 0.13f, 0.036f, 0.05f, 16, 10);
                break;
            }

            case "sparkle":
            {
                eyeMb.AddEllipsoid(new Vector3(-0.28f, eyeY, faceZ), 0.125f, 0.148f, 0.08f, 20, 14);
                eyeMb.AddEllipsoid(new Vector3(0.28f, eyeY, faceZ), 0.125f, 0.148f, 0.08f, 20, 14);

                highlightMb.AddSphere(new Vector3(-0.24f, eyeY + 0.05f, faceZ + 0.065f), 0.048f, 12, 8);
                highlightMb.AddSphere(new Vector3(0.32f, eyeY + 0.05f, faceZ + 0.065f), 0.048f, 12, 8);
                highlightMb.AddSphere(new Vector3(-0.32f, eyeY - 0.04f, faceZ + 0.065f), 0.025f, 10, 8);
                highlightMb.AddSphere(new Vector3(0.24f, eyeY - 0.04f, faceZ + 0.065f), 0.025f, 10, 8);
                break;
            }

            default:
            {
                var eyeRadiusY = isPuppy ? 0.144f : 0.122f;
                eyeMb.AddEllipsoid(new Vector3(-0.28f, eyeY, faceZ), 0.108f, eyeRadiusY, 0.078f, 18, 12);
                eyeMb.AddEllipsoid(new Vector3(0.28f, eyeY, faceZ), 0.108f, eyeRadiusY, 0.078f, 18, 12);

                highlightMb.AddSphere(new Vector3(-0.245f, eyeY + 0.042f, faceZ + 0.058f), 0.038f, 12, 8);
                highlightMb.AddSphere(new Vector3(0.315f, eyeY + 0.042f, faceZ + 0.058f), 0.038f, 12, 8);
                highlightMb.AddSphere(new Vector3(-0.305f, eyeY - 0.035f, faceZ + 0.056f), 0.018f, 8, 6);
                highlightMb.AddSphere(new Vector3(0.255f, eyeY - 0.035f, faceZ + 0.056f), 0.018f, 8, 6);
                break;
            }
        }

        tris += AddGeometry(eyeGroup, eyeMb, eyeMaterial);
        if (highlightMb.Positions.Count > 0)
        {
            tris += AddGeometry(eyeGroup, highlightMb, highlightMaterial);
        }

        parent.Children.Add(eyeGroup);

        // 종과 표정에 맞춘 입 파츠
        var mouthMb = new MeshBuilder(true, false);
        if (isPuppy && expressionId != "sleepy")
        {
            mouthMb.AddEllipsoid(new Vector3(0, faceCenterY - 0.216f, faceZ + 0.097f), 0.101f, 0.070f, 0.037f, 16, 10);
            var tongueMb = new MeshBuilder(true, false);
            tongueMb.AddEllipsoid(new Vector3(0, faceCenterY - 0.253f, faceZ + 0.125f), 0.059f, 0.048f, 0.019f, 14, 10);
            var tongueMaterial = CreateClayMaterial(Color.FromRgb(230, 129, 147), null, 18, 64);
            tris += AddGeometry(parent, tongueMb, tongueMaterial);
        }
        else if (speciesId == PetSpeciesIds.ChibiCat || isPuppy)
        {
            var mouthZ = isPuppy ? faceZ + 0.058f : faceZ;
            var mouthY = isPuppy ? faceCenterY - 0.030f : faceCenterY;
            var leftW = new List<Vector3>
            {
                new(-0.11f, mouthY - 0.12f, mouthZ + 0.045f),
                new(-0.055f, mouthY - 0.165f, mouthZ + 0.065f),
                new(0.0f, mouthY - 0.115f, mouthZ + 0.072f)
            };
            var rightW = new List<Vector3>
            {
                new(0.0f, mouthY - 0.115f, mouthZ + 0.072f),
                new(0.055f, mouthY - 0.165f, mouthZ + 0.065f),
                new(0.11f, mouthY - 0.12f, mouthZ + 0.045f)
            };
            mouthMb.AddTube(leftW, diameter: 0.034f, thetaDiv: 10, isTubeClosed: false);
            mouthMb.AddTube(rightW, diameter: 0.034f, thetaDiv: 10, isTubeClosed: false);
        }
        else
        {
            var mouthPath = new List<Vector3>
            {
                new(-0.095f, faceCenterY - 0.10f, faceZ + 0.01f),
                new(-0.045f, faceCenterY - 0.145f, faceZ + 0.022f),
                new(0.0f, faceCenterY - 0.158f, faceZ + 0.026f),
                new(0.045f, faceCenterY - 0.145f, faceZ + 0.022f),
                new(0.095f, faceCenterY - 0.10f, faceZ + 0.01f)
            };
            mouthMb.AddTube(mouthPath, diameter: 0.040f, thetaDiv: 12, isTubeClosed: false);
        }

        tris += AddGeometry(parent, mouthMb, eyeMaterial);
        return tris;
    }

    internal int BuildHeadAccessory(
        Model3DGroup parent,
        string accessoryId,
        float socketY,
        float headRadiusX,
        Color accentColor,
        SolidColorBrush? sharedAccentBrush = null,
        AxisAngleRotation3D? pomRotation = null,
        SolidColorBrush? ledBrush = null)
    {
        var tris = 0;
        var accentMat = sharedAccentBrush is not null
            ? CreateClayMaterial(sharedAccentBrush, null, 22, 85)
            : CreateClayMaterial(accentColor, null, 22, 85);

        switch (accessoryId)
        {
            case "sprout":
            {
                var greenMat = CreateClayMaterial(Color.FromRgb(106, 204, 118), null, 18, 75);
                var stemMb = new MeshBuilder(true, false);
                stemMb.AddTube(
                    new List<Vector3>
                    {
                        new(0, socketY - 0.06f, 0),
                        new(0.015f, socketY + 0.10f, 0.015f),
                        new(0.02f, socketY + 0.22f, 0.02f),
                        new(0, socketY + 0.34f, 0)
                    },
                    diameter: 0.065f,
                    thetaDiv: 12,
                    isTubeClosed: false);
                stemMb.AddEllipsoid(new Vector3(-0.18f, socketY + 0.38f, 0.02f), 0.20f, 0.075f, 0.11f, 18, 12);
                stemMb.AddEllipsoid(new Vector3(0.18f, socketY + 0.36f, -0.02f), 0.18f, 0.070f, 0.10f, 18, 12);
                tris += AddGeometry(parent, stemMb, greenMat);
                break;
            }

            case "ribbon":
            {
                var ribMb = new MeshBuilder(true, false);
                ribMb.AddSphere(new Vector3(0, socketY + 0.05f, 0.12f), 0.11f, 16, 12);
                ribMb.AddEllipsoid(new Vector3(-0.24f, socketY + 0.09f, 0.10f), 0.22f, 0.14f, 0.09f, 18, 12);
                ribMb.AddEllipsoid(new Vector3(0.24f, socketY + 0.09f, 0.10f), 0.22f, 0.14f, 0.09f, 18, 12);
                tris += AddGeometry(parent, ribMb, accentMat);
                break;
            }

            case "headset":
            {
                var bandMat = CreateClayMaterial(Color.FromRgb(56, 62, 78), null, 30, 100);
                var cupMat = sharedAccentBrush is not null
                    ? CreateClayMaterial(sharedAccentBrush, null, 24, 90)
                    : CreateClayMaterial(accentColor, null, 24, 90);

                var bandPoints = new List<Vector3>();
                const int segs = 22;
                var rx = headRadiusX + 0.08f;
                var ry = 0.56f;
                var baseY = socketY - 0.42f;
                for (var i = 0; i <= segs; i++)
                {
                    var angle = MathF.PI * i / segs;
                    bandPoints.Add(new Vector3(MathF.Cos(angle) * rx, baseY + MathF.Sin(angle) * ry, 0.0f));
                }

                var bandMb = new MeshBuilder(true, false);
                bandMb.AddTube(bandPoints, diameter: 0.12f, thetaDiv: 14, isTubeClosed: false);
                tris += AddGeometry(parent, bandMb, bandMat);

                var cupMb = new MeshBuilder(true, false);
                cupMb.AddEllipsoid(new Vector3(-rx, baseY, 0.0f), 0.16f, 0.26f, 0.24f, 20, 14);
                cupMb.AddEllipsoid(new Vector3(rx, baseY, 0.0f), 0.16f, 0.26f, 0.24f, 20, 14);
                tris += AddGeometry(parent, cupMb, cupMat);
                if (ledBrush is not null)
                {
                    var ledMb = new MeshBuilder(true, false);
                    ledMb.AddEllipsoid(new Vector3(-rx, baseY, 0.235f), 0.07f, 0.15f, 0.018f, 12, 8);
                    ledMb.AddEllipsoid(new Vector3(rx, baseY, 0.235f), 0.07f, 0.15f, 0.018f, 12, 8);
                    tris += AddGeometry(parent, ledMb, new EmissiveMaterial(ledBrush));
                }
                break;
            }

            case "santa_hat":
            {
                var redMat = CreateClayMaterial(Color.FromRgb(226, 66, 76), null, 16, 70);
                var whiteMat = CreateClayMaterial(Color.FromRgb(250, 250, 252), null, 14, 55);

                var brimMb = new MeshBuilder(true, false);
                AddRingTube(brimMb, new Vector3(0, socketY - 0.04f, 0), radius: 0.48f, tubeDiameter: 0.18f, segments: 28);
                tris += AddGeometry(parent, brimMb, whiteMat);
                // 방울만 모자 끝에 매달아 테두리와 모자 본체는 흔들리지 않게 한다.
                var pomGroup = new Model3DGroup();
                if (pomRotation is not null)
                    pomGroup.Transform = new RotateTransform3D(pomRotation,
                        new Point3D(0.25, socketY + 0.70, -0.08));
                var pomMb = new MeshBuilder(true, false);
                pomMb.AddSphere(new Vector3(0.28f, socketY + 0.58f, -0.08f), 0.15f, 18, 12);
                tris += AddGeometry(pomGroup, pomMb, whiteMat);
                parent.Children.Add(pomGroup);

                var coneMb = new MeshBuilder(true, false);
                coneMb.AddCone(new Vector3(0, socketY - 0.02f, 0), new Vector3(0.32f, 1.0f, -0.10f), 0.44f, 0.05f, 0.72f, true, true, 24);
                tris += AddGeometry(parent, coneMb, redMat);
                break;
            }

            case "crown":
            {
                var goldMat = CreateClayMaterial(Color.FromRgb(255, 212, 82), new SolidColorBrush(Color.FromRgb(42, 32, 6)), 38, 130);
                var gemMat = sharedAccentBrush is not null
                    ? CreateClayMaterial(sharedAccentBrush, null, 44, 140)
                    : CreateClayMaterial(accentColor, null, 44, 140);

                var crownMb = new MeshBuilder(true, false);
                AddRingTube(crownMb, new Vector3(0, socketY + 0.02f, 0), radius: 0.34f, tubeDiameter: 0.11f, segments: 24);
                for (var i = 0; i < 5; i++)
                {
                    var angle = i * 2.0f * MathF.PI / 5.0f;
                    var x = MathF.Cos(angle) * 0.33f;
                    var z = MathF.Sin(angle) * 0.33f;
                    crownMb.AddCone(new Vector3(x, socketY + 0.04f, z), Vector3.UnitY, 0.11f, 0.025f, 0.28f, true, true, 12);
                }

                tris += AddGeometry(parent, crownMb, goldMat);

                var gemMb = new MeshBuilder(true, false);
                gemMb.AddSphere(new Vector3(0, socketY + 0.08f, 0.36f), 0.07f, 14, 10);
                tris += AddGeometry(parent, gemMb, gemMat);
                break;
            }
        }

        return tris;
    }

    internal (int Triangles, SolidColorBrush? AuraDiffuseBrush) BuildAuraEffect(
        Model3DGroup parent,
        string auraId,
        Color accentColor,
        SolidColorBrush auraEmissiveBrush)
    {
        var tris = 0;
        var softColor = Lighten(accentColor, 0.35);
        // 캐릭터 얼굴을 가리지 않고 은은하게 어우러지는 반투명 파스텔 오라 재질
        var auraDiffuseBrush = new SolidColorBrush(Color.FromArgb(135, softColor.R, softColor.G, softColor.B));
        var auraMat = CreateTranslucentAuraMaterial(auraDiffuseBrush, auraEmissiveBrush);
        SolidColorBrush? trackableDiffuseBrush = null;

        switch (auraId)
        {
            case "halo_ring":
            {
                // 캐릭터 발밑 주변에 차분하게 깔리는 얇은 반투명 링 (얼굴 가림 방지)
                var ringMb = new MeshBuilder(true, false);
                AddRingTube(ringMb, new Vector3(0, -0.78f, 0), radius: 1.24f, tubeDiameter: 0.030f, segments: 42);
                tris += AddGeometry(parent, ringMb, auraMat);
                trackableDiffuseBrush = auraDiffuseBrush;
                break;
            }

            case "music_notes":
            {
                // 작고 은은한 반투명 3D 음표 3개가 외곽에서 천천히 맴돔
                var noteMb = new MeshBuilder(true, false);
                for (var i = 0; i < 3; i++)
                {
                    var angle = i * (2.0f * MathF.PI / 3.0f) + 0.5f;
                    var x = MathF.Cos(angle) * 1.32f;
                    var z = MathF.Sin(angle) * 1.32f;
                    var y = 0.25f + (i % 2 == 0 ? 0.14f : -0.10f);

                    noteMb.AddEllipsoid(new Vector3(x, y, z), 0.075f, 0.056f, 0.045f, 12, 8);
                    noteMb.AddCylinder(new Vector3(x + 0.056f, y, z), new Vector3(x + 0.056f, y + 0.21f, z), 0.018f, 8);
                    noteMb.AddBox(new Vector3(x + 0.10f, y + 0.19f, z), 0.095f, 0.038f, 0.025f);
                }

                tris += AddGeometry(parent, noteMb, auraMat);
                trackableDiffuseBrush = auraDiffuseBrush;
                break;
            }

            case "snowflakes":
            {
                // 작고 차분한 반투명 눈결정 4개
                var snowMat = CreateTranslucentAuraMaterial(new SolidColorBrush(Color.FromArgb(145, 224, 244, 255)), auraEmissiveBrush);
                var snowMb = new MeshBuilder(true, false);
                for (var i = 0; i < 4; i++)
                {
                    var angle = i * (MathF.PI * 0.5f) + 0.35f;
                    var x = MathF.Cos(angle) * 1.34f;
                    var z = MathF.Sin(angle) * 1.34f;
                    var y = 0.12f + MathF.Sin(i * 1.8f) * 0.28f;
                    snowMb.AddOctahedron(new Vector3(x, y, z), Vector3.UnitY, Vector3.UnitZ, 0.068f, 0.068f);
                }

                tris += AddGeometry(parent, snowMb, snowMat);
                break;
            }
        }

        return (tris, trackableDiffuseBrush);
    }

    internal static MaterialGroup CreateTranslucentAuraMaterial(SolidColorBrush diffuseBrush, SolidColorBrush emissiveBrush)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(diffuseBrush));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 18));
        group.Children.Add(new EmissiveMaterial(emissiveBrush));
        return group;
    }

    private static void AddEyeArc(MeshBuilder mb, float cx, float cy, float cz, float halfWidth, float archHeight)
    {
        var pts = new List<Vector3>
        {
            new(cx - halfWidth, cy - archHeight * 0.35f, cz),
            new(cx - halfWidth * 0.65f, cy + archHeight * 0.65f, cz + 0.015f),
            new(cx, cy + archHeight, cz + 0.025f),
            new(cx + halfWidth * 0.65f, cy + archHeight * 0.65f, cz + 0.015f),
            new(cx + halfWidth, cy - archHeight * 0.35f, cz)
        };
        mb.AddTube(pts, diameter: 0.052f, thetaDiv: 12, isTubeClosed: false);
    }

    internal static void AddRingTube(MeshBuilder mb, Vector3 center, float radius, float tubeDiameter, int segments)
    {
        var pts = new List<Vector3>(segments + 1);
        for (var i = 0; i <= segments; i++)
        {
            var angle = 2.0f * MathF.PI * i / segments;
            pts.Add(new Vector3(
                center.X + MathF.Cos(angle) * radius,
                center.Y,
                center.Z + MathF.Sin(angle) * radius));
        }

        mb.AddTube(pts, diameter: tubeDiameter, thetaDiv: 14, isTubeClosed: true);
    }

    internal int AddGeometry(Model3DGroup group, MeshBuilder builder, Material material, Transform3D? transform = null)
        => AddGeometry(group, GetSharedMesh(builder.ToMesh()), material, transform);

    internal static int AddGeometry(Model3DGroup group, WpfMeshGeometry3D mesh, Material material, Transform3D? transform)
    {
        var model = new GeometryModel3D(mesh, material)
        {
            BackMaterial = material
        };

        if (transform is not null)
        {
            model.Transform = transform;
        }

        group.Children.Add(model);
        return mesh.TriangleIndices.Count / 3;
    }

    internal WpfMeshGeometry3D GetSharedMesh(HelixMeshGeometry3D source)
    {
        var hash = new HashCode();
        hash.Add(source.Positions.Count);
        foreach (var position in source.Positions)
        {
            hash.Add(position);
        }

        hash.Add(source.Normals?.Count ?? 0);
        if (source.Normals is { } normals)
        {
            foreach (var normal in normals)
            {
                hash.Add(normal);
            }
        }

        hash.Add(source.TriangleIndices.Count);
        foreach (var index in source.TriangleIndices)
        {
            hash.Add(index);
        }

        var key = hash.ToHashCode();
        lock (_meshCacheLock)
        {
            _meshCache.TryGetValue(key, out var entry);
            // 해시가 같은 경우에도 정점, 법선, 인덱스가 모두 같은 메시만 재사용한다.
            if (entry is not null && entry.TryGetTarget(out var cached) && MatchesMesh(cached, source))
            {
                return cached;
            }

            var mesh = ToWpfMesh(source);
            if (entry is null && _meshCache.Count == MaxCachedMeshes)
            {
                var released = _meshCache.FirstOrDefault(pair => !pair.Value.TryGetTarget(out _));
                _meshCache.Remove(released.Value is null ? _meshCache.Keys.First() : released.Key);
            }

            // 열린 장면이 모두 닫히면 캐시가 메시를 계속 보유하지 않게 한다.
            _meshCache[key] = new WeakReference<WpfMeshGeometry3D>(mesh);
            return mesh;
        }
    }

    private static bool MatchesMesh(WpfMeshGeometry3D cached, HelixMeshGeometry3D source)
    {
        var cachedPositions = cached.Positions;
        var cachedNormals = cached.Normals;
        var cachedIndices = cached.TriangleIndices;
        if (cachedPositions.Count != source.Positions.Count
            || cachedNormals.Count != (source.Normals?.Count ?? 0)
            || cachedIndices.Count != source.TriangleIndices.Count)
        {
            return false;
        }

        for (var i = 0; i < cachedPositions.Count; i++)
        {
            var p = source.Positions[i];
            if (!cachedPositions[i].Equals(new Point3D(p.X, p.Y, p.Z)))
            {
                return false;
            }
        }

        if (source.Normals is { } normals)
        {
            for (var i = 0; i < normals.Count; i++)
            {
                var n = normals[i];
                if (!cachedNormals[i].Equals(new Vector3D(n.X, n.Y, n.Z)))
                {
                    return false;
                }
            }
        }

        for (var i = 0; i < cachedIndices.Count; i++)
        {
            if (cachedIndices[i] != source.TriangleIndices[i])
            {
                return false;
            }
        }

        return true;
    }

    private static WpfMeshGeometry3D ToWpfMesh(HelixMeshGeometry3D helixMesh)
    {
        var wpfMesh = new WpfMeshGeometry3D();

        var positions = new Point3DCollection(helixMesh.Positions.Count);
        foreach (var p in helixMesh.Positions)
        {
            positions.Add(new Point3D(p.X, p.Y, p.Z));
        }

        wpfMesh.Positions = positions;

        if (helixMesh.Normals is { Count: > 0 })
        {
            var normals = new Vector3DCollection(helixMesh.Normals.Count);
            foreach (var n in helixMesh.Normals)
            {
                normals.Add(new Vector3D(n.X, n.Y, n.Z));
            }

            wpfMesh.Normals = normals;
        }

        var indices = new Int32Collection(helixMesh.TriangleIndices.Count);
        foreach (var idx in helixMesh.TriangleIndices)
        {
            indices.Add(idx);
        }

        wpfMesh.TriangleIndices = indices;
        wpfMesh.Freeze();
        return wpfMesh;
    }

    internal static MaterialGroup CreateClayMaterial(Color baseColor, SolidColorBrush? emissiveBrush, double specularPower, byte specularAlpha = 90)
        => CreateClayMaterial(new SolidColorBrush(baseColor), emissiveBrush, specularPower, specularAlpha);

    internal static MaterialGroup CreateClayMaterial(SolidColorBrush diffuseBrush, SolidColorBrush? emissiveBrush, double specularPower, byte specularAlpha = 90)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(diffuseBrush));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(specularAlpha, 255, 255, 255)), specularPower));

        if (emissiveBrush is not null)
        {
            group.Children.Add(new EmissiveMaterial(emissiveBrush));
        }

        return group;
    }

    internal static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color parsed)
            {
                return parsed;
            }
        }
        catch (FormatException)
        {
        }

        return fallback;
    }

    internal static Color Lighten(Color c, double factor)
    {
        var r = (byte)Math.Clamp(c.R + (255 - c.R) * factor, 0, 255);
        var g = (byte)Math.Clamp(c.G + (255 - c.G) * factor, 0, 255);
        var b = (byte)Math.Clamp(c.B + (255 - c.B) * factor, 0, 255);
        return Color.FromRgb(r, g, b);
    }

    internal static Color Darken(Color c, double factor)
    {
        var r = (byte)Math.Clamp(c.R * (1.0 - factor), 0, 255);
        var g = (byte)Math.Clamp(c.G * (1.0 - factor), 0, 255);
        var b = (byte)Math.Clamp(c.B * (1.0 - factor), 0, 255);
        return Color.FromRgb(r, g, b);
    }
}
