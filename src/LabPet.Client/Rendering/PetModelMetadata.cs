using System.Numerics;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>
/// rig.json에 저장되는 3D 좌표/피벗 벡터.
/// </summary>
public sealed record PetVector3(double X, double Y, double Z)
{
    public Point3D ToPoint3D() => new(X, Y, Z);
    public Vector3D ToVector3D() => new(X, Y, Z);
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}

/// <summary>
/// Blender에서 내보낸 캐릭터 OBJ 에셋의 좌표계, 피벗, 소켓, 재질 규약 메타데이터(rig.json).
/// </summary>
public sealed class PetModelMetadata
{
    /// <summary>WPF 3D 표준 좌표계: +Y Up, +Z Front, +X 화면 오른쪽.</summary>
    public string CoordinateSystem { get; set; } = "WPF_Y_UP_Z_FRONT";

    /// <summary>메시 스케일 보정 계수 (기본 1.0).</summary>
    public double UnitScale { get; set; } = 1.0;

    /// <summary>메시 원점 오프셋 보정.</summary>
    public PetVector3 OriginOffset { get; set; } = new(0, 0, 0);

    /// <summary>머리 회전 중심 피벗.</summary>
    public PetVector3 HeadPivot { get; set; } = new(0, 0.24, 0);

    /// <summary>머리 위 3D 액세서리 장착 소켓.</summary>
    public PetVector3 HeadSocket { get; set; } = new(0, 1.12, 0);

    /// <summary>헤드셋 등 머리 너비에 맞출 반경.</summary>
    public double HeadRadiusX { get; set; } = 0.82;

    /// <summary>오라·파티클 중심 소켓.</summary>
    public PetVector3 AuraSocket { get; set; } = new(0, 0, 0);

    /// <summary>바닥 접지 그림자 Y 높이 및 반경.</summary>
    public double ShadowY { get; set; } = -0.94;
    public double ShadowRadiusX { get; set; } = 0.78;
    public double ShadowRadiusZ { get; set; } = 0.68;

    /// <summary>눈 깜빡임 스케일 중심(눈 높이/전면 깊이).</summary>
    public PetVector3 EyeBlinkCenter { get; set; } = new(0, 0.48, 0.74);

    /// <summary>이름표 정렬 기준점.</summary>
    public PetVector3 NameplateAnchor { get; set; } = new(0, -0.94, 0.35);

    /// <summary>종별 관절 회전 피벗과 기본 각도.</summary>
    public PetVector3? LeftEarPivot { get; set; }
    public PetVector3? RightEarPivot { get; set; }
    public double LeftEarBaseAngle { get; set; }
    public double RightEarBaseAngle { get; set; }
    public PetVector3? TailPivot { get; set; }
    public PetVector3? LeftArmPivot { get; set; }
    public PetVector3? RightArmPivot { get; set; }

    /// <summary>표정별 눈/입 파츠 이름 매핑 및 기본 폴백.</summary>
    public Dictionary<string, string> PartMaterialOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
