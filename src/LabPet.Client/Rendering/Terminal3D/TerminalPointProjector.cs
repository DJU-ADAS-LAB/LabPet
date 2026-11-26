using System;
using System.Numerics;

namespace LabPet.Client.Rendering.Terminal3D;

/// <summary>
/// 3D 표면 샘플의 월드 변환, Blinn-Phong + 프레넬 림 + 코어 발광 조명,
/// 원근 투영 및 깊이 버퍼 기록을 담당하는 범용 터미널 3D 프로젝터.
/// </summary>
internal sealed class TerminalPointProjector
{
    public const float NearPlane = 0.15f;

    private static readonly Vector3 CameraPosition = new(0f, 3.65f, 11.9f);
    private static readonly Vector3 CameraTarget = new(0f, 3.12f, 0f);
    private static readonly Vector3 KeyLightDir = Vector3.Normalize(new Vector3(-3.8f, 5.8f, 5.4f));
    private static readonly Vector3 FillLightDir = Vector3.Normalize(new Vector3(4.5f, 2.4f, -2.2f));
    private static readonly Vector3 TreeGlowOrigin = new(0f, 2.85f, 0f);

    private readonly AsciiDepthSurface _surface;
    private Matrix4x4 _world = Matrix4x4.Identity;
    private Matrix4x4 _view;
    private float _scale;
    private float _cellAspect = 0.5f;
    private float _ambientScale = 1f;
    private float _warmTintStrength = 1f;

    public TerminalPointProjector(AsciiDepthSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _view = Matrix4x4.CreateLookAt(CameraPosition, CameraTarget, Vector3.UnitY);
    }

    public void Configure(
        float rotationRadians,
        float zoom,
        float cellWidthToHeight,
        float ambientScale = 1f,
        float warmTintStrength = 1f)
    {
        if (!float.IsFinite(rotationRadians) || !float.IsFinite(zoom)
            || !float.IsFinite(cellWidthToHeight) || cellWidthToHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellWidthToHeight));

        _world = Matrix4x4.CreateRotationY(rotationRadians);
        _view = Matrix4x4.CreateLookAt(CameraPosition, CameraTarget, Vector3.UnitY);
        _cellAspect = cellWidthToHeight;
        _ambientScale = Math.Clamp(ambientScale, 0.45f, 1.5f);
        _warmTintStrength = Math.Clamp(warmTintStrength, 0f, 1.6f);
        // 창 가로·세로 비율에 맞춰 전체 장면(높이 ~6.2, 반지름 ~3.35)이 잘리지 않고 균형 있게 채워지도록 자동 스케일
        _scale = MathF.Min(_surface.Rows, _surface.Columns * _cellAspect)
            * 0.96f * Math.Clamp(zoom, 0.45f, 2.4f);
    }

    /// <summary>월드 공간 좌표를 스크린 셀 좌표와 양의 카메라 깊이로 투영한다.</summary>
    public bool TryProjectWorld(Vector3 worldPosition, out int column, out int row, out float depth)
    {
        column = 0;
        row = 0;
        depth = 0;
        var camera = Vector3.Transform(worldPosition, _view);
        depth = -camera.Z;
        if (!float.IsFinite(depth) || depth < NearPlane) return false;
        var x = _surface.Columns * 0.5f + camera.X * _scale / (depth * _cellAspect);
        var y = _surface.Rows * 0.5f - camera.Y * _scale / depth;
        if (!float.IsFinite(x) || !float.IsFinite(y)
            || x < 0 || y < 0 || x >= _surface.Columns || y >= _surface.Rows) return false;
        column = (int)x;
        row = (int)y;
        return true;
    }

    /// <summary>로컬(회전 적용) 공간 좌표를 투영해 스크린 셀과 깊이를 얻는다.</summary>
    public bool TryProjectLocal(Vector3 localPosition, out int column, out int row, out float depth)
        => TryProjectWorld(Vector3.Transform(localPosition, _world), out column, out row, out depth);

    /// <summary>표면 법선과 재질 속성(광택·발광·후면 제거·문자 스타일)으로 Blinn-Phong + 프레넬 림 조명을 계산해 기록한다.</summary>
    public bool Plot(
        Vector3 position,
        Vector3 normal,
        int rgb,
        float emission = 0f,
        float specularStrength = 0.2f,
        float shininess = 16f,
        bool cullBackFace = false,
        int glyphStyle = 0)
    {
        var worldPosition = Vector3.Transform(position, _world);
        var camera = Vector3.Transform(worldPosition, _view);
        var depth = -camera.Z;
        if (!float.IsFinite(depth) || depth < NearPlane) return false;

        var x = _surface.Columns * 0.5f + camera.X * _scale / (depth * _cellAspect);
        var y = _surface.Rows * 0.5f - camera.Y * _scale / depth;
        if (!float.IsFinite(x) || !float.IsFinite(y)
            || x < 0 || y < 0 || x >= _surface.Columns || y >= _surface.Rows) return false;

        var transformedNormal = Vector3.TransformNormal(normal, _world);
        var nLenSq = transformedNormal.LengthSquared();
        if (!float.IsFinite(nLenSq) || nLenSq < 1e-6f) return false;
        transformedNormal /= MathF.Sqrt(nLenSq);

        var toCamera = CameraPosition - worldPosition;
        var vLenSq = toCamera.LengthSquared();
        if (!float.IsFinite(vLenSq) || vLenSq < 1e-6f) return false;
        var viewDir = toCamera / MathF.Sqrt(vLenSq);

        var nDotV = Vector3.Dot(transformedNormal, viewDir);
        if (cullBackFace && nDotV < -0.06f) return false;

        var shadingNormal = nDotV >= 0f ? transformedNormal : -transformedNormal;
        var absNDotV = MathF.Abs(nDotV);

        if (!float.IsFinite(emission)) emission = 0f;
        emission = Math.Clamp(emission, 0f, 1.35f);

        var keyDiff = MathF.Max(0f, Vector3.Dot(shadingNormal, KeyLightDir));
        var fillDiff = MathF.Max(0f, Vector3.Dot(shadingNormal, FillLightDir));

        var toCore = TreeGlowOrigin - worldPosition;
        var coreDistSq = toCore.LengthSquared() + 1.4f;
        var coreGlow = 0.16f * _warmTintStrength / coreDistSq;

        var halfVec = KeyLightDir + viewDir;
        var hLenSq = halfVec.LengthSquared();
        var spec = 0f;
        if (specularStrength > 0f && hLenSq > 1e-6f)
        {
            var nDotH = MathF.Max(0f, Vector3.Dot(shadingNormal, halfVec / MathF.Sqrt(hLenSq)));
            spec = MathF.Pow(nDotH, shininess) * specularStrength;
        }

        var rim = MathF.Pow(1f - Math.Clamp(absNDotV, 0f, 1f), 2.6f) * 0.22f;
        var depthFade = Math.Clamp(1.18f - depth * 0.017f, 0.58f, 1.10f);

        var baseR = (rgb >> 16) & 255;
        var baseG = (rgb >> 8) & 255;
        var baseB = rgb & 255;

        var lightR = (0.26f * _ambientScale + 0.72f * keyDiff + 0.24f * fillDiff + coreGlow * 1.10f + emission * 0.96f) * depthFade;
        var lightG = (0.31f * _ambientScale + 0.74f * keyDiff + 0.28f * fillDiff + coreGlow * 0.90f + emission * 0.94f) * depthFade;
        var lightB = (0.33f * _ambientScale + 0.64f * keyDiff + 0.34f * fillDiff + coreGlow * 0.55f + emission * 0.86f) * depthFade;

        var specBoost = spec * depthFade;
        var rimBoost = rim * depthFade;

        // 지면(style==2)은 어두운 밤눈 톤을 유지하도록 림 라이트를 낮춤
        var rimScale = glyphStyle == 2 ? 0.18f : 1f;
        var r = Math.Clamp((int)(baseR * lightR + 255f * specBoost + 90f * rimBoost * rimScale), 0, 255);
        var g = Math.Clamp((int)(baseG * lightG + 250f * specBoost + 140f * rimBoost * rimScale), 0, 255);
        var b = Math.Clamp((int)(baseB * lightB + 220f * specBoost + 185f * rimBoost * rimScale), 0, 255);

        var luminance = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
        var density = glyphStyle == 2
            ? Math.Clamp(0.08f + luminance * 0.72f, 0.05f, 0.85f)
            : Math.Clamp(0.24f + luminance * 0.76f + specBoost * 0.24f + emission * 0.18f, 0.08f, 1f);

        return _surface.TryWriteStyled((int)x, (int)y, depth, (r << 16) | (g << 8) | b, density, glyphStyle);
    }

    /// <summary>파티클(눈송이·배경 별·불꽃 입자)을 지정 ASCII 문자로 깊이 버퍼에 기록한다.</summary>
    public bool PlotParticle(Vector3 worldPosition, int rgb, char glyph, bool additiveGlow = false)
    {
        if (!TryProjectWorld(worldPosition, out var col, out var row, out var depth)) return false;
        return additiveGlow
            ? _surface.TryBlendGlow(col, row, depth, rgb, glyph)
            : _surface.TryWriteGlyph(col, row, depth, rgb, glyph);
    }

    /// <summary>회전하는 장면 공간의 발광 후광/광선 효과를 가산 블렌딩으로 기록한다.</summary>
    public bool PlotLocalGlow(Vector3 localPosition, int rgb, char glyph)
    {
        if (!TryProjectLocal(localPosition, out var col, out var row, out var depth)) return false;
        return _surface.TryBlendGlow(col, row, depth, rgb, glyph);
    }
}
