using System;
using System.Collections.Generic;
using System.Numerics;

namespace LabPet.Client.Rendering.Terminal3D;

internal enum ChristmasMaterial
{
    Ground,
    Trunk,
    Foliage,
    SnowDust,
    Garland,
    GarlandLight,
    Ornament,
    Gift,
    Ribbon,
    Star
}

internal readonly record struct ChristmasSurfaceSample(
    Vector3 Position,
    Vector3 Normal,
    int Rgb,
    ChristmasMaterial Material,
    float Phase = 0f,
    bool CullBackFace = true);

/// <summary>
/// 카메라와 출력 장치에 독립적인 고밀도 절차적 3D 크리스마스트리 장면 지오메트리 캐시.
/// 또렷하게 층진 6단 풍성한 전나무·어두운 밤눈 원형 지면·선명한 황금 나선 가랜드·보석 오너먼트·
/// 선명한 보라/빨강/청록/파랑 리본 선물 상자·큰 5각 황금 별을 생성한다.
/// </summary>
internal sealed class ChristmasTreeGeometry
{
    public const int TierCount = 6;
    public const float GroundRadius = 3.35f;
    public const float TreeTop = 5.42f;
    public static readonly Vector3 StarCenter = new(0f, TreeTop + 0.52f, 0f);

    private readonly record struct TierSpec(float BaseY, float Height, float BottomRadius, float TopRadius, int Lobes);
    private readonly record struct GiftPlacement(Vector3 Center, Vector3 Size, float YawRadians, int BoxRgb, int RibbonRgb);

    // 각 단이 아래 단의 좁아진 목 위로 뚜렷하게 계단처럼 돌출되는 클래식 6단 실루엣
    private static readonly TierSpec[] Tiers =
    {
        new(1.04f, 0.96f, 2.62f, 1.32f, 12), // 1단 (가장 풍성한 하단)
        new(1.80f, 0.92f, 2.26f, 1.04f, 10), // 2단 (+0.75 이상 계단 돌출)
        new(2.52f, 0.88f, 1.88f, 0.78f,  9), // 3단
        new(3.20f, 0.82f, 1.48f, 0.54f,  8), // 4단
        new(3.84f, 0.76f, 1.08f, 0.30f,  7), // 5단
        new(4.44f, 0.98f, 0.68f, 0.03f,  6)  // 6단 (별 바로 아래 뾰족한 정상)
    };

    // 어두운 눈 지면 위 좌·중·우로 균형 잡히게 배치된 6개 선물 상자 (보라·빨강·청록·파랑)
    private static readonly GiftPlacement[] GiftPlacements =
    {
        new(new(-2.28f, 0f,  1.15f), new(0.86f, 0.88f, 0.78f),  0.22f, 0x9F4CFF, 0xFFE44D), // 좌전면 보라 + 골드 리본
        new(new(-1.72f, 0f,  1.86f), new(0.76f, 0.76f, 0.72f), -0.32f, 0xF2446B, 0xF5FAFF), // 좌전면 빨강/핑크 + 화이트 리본
        new(new( 0.12f, 0f,  2.42f), new(0.86f, 0.82f, 0.76f),  0.08f, 0x1DD6C8, 0xF5FAFF), // 중앙전면 청록 + 화이트 리본
        new(new( 2.24f, 0f,  1.16f), new(0.88f, 0.86f, 0.80f), -0.24f, 0xA855FF, 0xFFE44D), // 우전면 보라 + 골드 리본
        new(new( 2.15f, 0f, -1.28f), new(0.84f, 0.80f, 0.76f),  0.40f, 0x3D82FF, 0xFFE45E), // 우후면 파랑 + 골드 리본
        new(new(-2.05f, 0f, -1.42f), new(0.84f, 0.84f, 0.76f), -0.40f, 0xE83558, 0xFFE66D)  // 좌후면 빨강 + 골드 리본
    };

    private static readonly ChristmasSurfaceSample[] CachedSamples = Build();
    public ReadOnlySpan<ChristmasSurfaceSample> Samples => CachedSamples;

    private static ChristmasSurfaceSample[] Build()
    {
        var samples = new List<ChristmasSurfaceSample>(105000);
        AddGround(samples);
        AddTrunk(samples);
        AddFoliage(samples);
        AddGarland(samples);
        AddOrnaments(samples);
        AddGifts(samples);
        AddStar(samples);
        return samples.ToArray();
    }

    private static float TierRadiusAt(in TierSpec spec, float u, float angle, int tierIndex)
    {
        // 아래쪽은 살짝 둥글게 부풀고 위로 갈수록 매끄럽게 좁아지는 전나무 단면
        var arch = MathF.Sin(u * MathF.PI) * 0.08f * spec.BottomRadius;
        var baseR = spec.BottomRadius + (spec.TopRadius - spec.BottomRadius) * MathF.Pow(u, 0.92f) + arch;
        var lobeWave = MathF.Cos(angle * spec.Lobes + tierIndex * 1.15f);
        var scallop = 1f + 0.085f * (1f - u * 0.65f) * lobeWave;
        return baseR * scallop;
    }

    /// <summary>높이 y와 방위각 angle에서의 외곽 나뭇가지 반지름을 계산한다.</summary>
    public static float SurfaceRadiusAt(float y, float angle)
    {
        var maxRadius = 0f;
        for (var i = 0; i < Tiers.Length; i++)
        {
            ref readonly var spec = ref Tiers[i];
            var u = (y - spec.BaseY) / spec.Height;
            if (u < 0f || u > 1f) continue;
            var candidate = TierRadiusAt(spec, u, angle, i);
            if (candidate > maxRadius) maxRadius = candidate;
        }
        return maxRadius;
    }

    private static float GroundHeightAt(float x, float z)
    {
        var r2 = x * x + z * z;
        var norm = Math.Clamp(r2 / (GroundRadius * GroundRadius), 0f, 1f);
        var dome = 0.24f * (1f - norm);
        var drift = 0.03f * MathF.Sin(x * 1.9f + 0.3f) * MathF.Cos(z * 1.7f - 0.2f) * (1f - norm * 0.6f);
        return dome + drift;
    }

    private static void AddGround(List<ChristmasSurfaceSample> samples)
    {
        // 어두운 밤눈 질감: 희미한 눈결·반짝임만 드러내 선물 상자와 초록 트리가 극명하게 대비되도록 구성
        const float step = 0.085f;
        for (var x = -GroundRadius; x <= GroundRadius; x += step)
        for (var z = -GroundRadius; z <= GroundRadius; z += step)
        {
            var r2 = x * x + z * z;
            if (r2 > GroundRadius * GroundRadius) continue;

            var y = GroundHeightAt(x, z);
            const float eps = 0.05f;
            var hx = (GroundHeightAt(x + eps, z) - GroundHeightAt(x - eps, z)) / (2f * eps);
            var hz = (GroundHeightAt(x, z + eps) - GroundHeightAt(x, z - eps)) / (2f * eps);
            var normal = Vector3.Normalize(new Vector3(-hx, 1f, -hz));

            var distRatio = MathF.Sqrt(r2) / GroundRadius;
            var radialFade = 1f - distRatio * distRatio * 0.62f;
            var ripple = 0.5f + 0.5f * MathF.Sin(x * 11.7f + z * 13.3f);
            var frostGlint = MathF.Sin(x * 29.1f) * MathF.Cos(z * 31.7f);

            int rgb;
            if (frostGlint > 0.78f)
            {
                rgb = ScaleRgb(0xDCEBFF, radialFade); // 드문드문 반짝이는 눈 결정
            }
            else if (ripple > 0.58f)
            {
                rgb = ScaleRgb(0x6C829B, radialFade); // 은은한 눈결 능선
            }
            else
            {
                rgb = ScaleRgb(0x2B3A4D, radialFade); // 깊은 밤눈 그림자
            }

            samples.Add(new(new Vector3(x, y, z), normal, rgb, ChristmasMaterial.Ground, frostGlint, CullBackFace: false));
        }
    }

    private static void AddTrunk(List<ChristmasSurfaceSample> samples)
    {
        const int angularSteps = 56;
        const int heightSteps = 26;
        for (var a = 0; a < angularSteps; a++)
        for (var h = 0; h <= heightSteps; h++)
        {
            var u = (float)h / heightSteps;
            var angle = MathF.Tau * a / angularSteps;
            var radius = 0.34f - u * 0.07f;
            var nx = MathF.Cos(angle);
            var nz = MathF.Sin(angle);
            var bark = 0.78f + 0.22f * MathF.Sin(angle * 12f + u * 8f);
            samples.Add(new(
                new Vector3(nx * radius, 0.22f + u * 0.96f, nz * radius),
                Vector3.Normalize(new Vector3(nx, 0.08f, nz)),
                ScaleRgb(0x8D5630, bark),
                ChristmasMaterial.Trunk,
                0f,
                CullBackFace: true));
        }
    }

    private static void AddFoliage(List<ChristmasSurfaceSample> samples)
    {
        for (var tier = 0; tier < Tiers.Length; tier++)
        {
            ref readonly var spec = ref Tiers[tier];
            var heightSteps = 24 + (Tiers.Length - tier) * 2;
            var angularSteps = 96 + (Tiers.Length - tier) * 14;

            for (var hi = 0; hi <= heightSteps; hi++)
            {
                var u = (float)hi / heightSteps;

                for (var ai = 0; ai < angularSteps; ai++)
                {
                    var angle = MathF.Tau * ai / angularSteps;
                    var lobeWave = MathF.Cos(angle * spec.Lobes + tier * 1.15f);
                    var lobeSlope = -MathF.Sin(angle * spec.Lobes + tier * 1.15f);
                    var radius = TierRadiusAt(spec, u, angle, tier);

                    var boughDroop = (1f - u) * (1f - u) * (0.09f + 0.045f * MathF.Max(0f, lobeWave));
                    var y = spec.BaseY + u * spec.Height - boughDroop;

                    var cosA = MathF.Cos(angle);
                    var sinA = MathF.Sin(angle);
                    var radial = new Vector3(cosA, 0f, sinA);
                    var tangent = new Vector3(-sinA, 0f, cosA);

                    var normal = Vector3.Normalize(
                        radial * (0.86f + 0.12f * lobeWave)
                        + Vector3.UnitY * (0.58f + 0.24f * u)
                        + tangent * (0.20f * lobeSlope));

                    // 층 아래 경계(u가 작을수록)와 윗층 바로 아래(u가 클수록)에 자연스러운 깊은 음영을 넣어 6단 경계를 선명하게 분리
                    var tierShade = u < 0.14f
                        ? 0.56f + (u / 0.14f) * 0.44f
                        : (tier < Tiers.Length - 1 && u > 0.78f ? 1f - ((u - 0.78f) / 0.22f) * 0.42f : 1f);

                    // 레퍼런스처럼 풍성하고 통일감 있는 민트-에메랄드-송엽색 그라데이션
                    var frondHighlight = 0.5f + 0.5f * lobeWave;
                    var midEmerald = LerpRgb(0x136E3D, 0x24B862, Math.Clamp(0.28f + 0.52f * frondHighlight + 0.18f * (1f - u), 0f, 1f));
                    var brightTip = LerpRgb(midEmerald, 0x62EB8B, Math.Clamp((frondHighlight - 0.55f) / 0.45f, 0f, 1f) * 0.52f);
                    var foliageRgb = ScaleRgb(brightTip, tierShade);

                    // 최상단·외곽 끝자락에 은은한 서리 포인트만 소량 추가
                    var snowNoise = MathF.Sin(angle * 21f + tier * 3.7f) * MathF.Cos(u * 17f);
                    var isSnowDust = lobeWave > 0.76f && u > 0.25f && u < 0.58f && snowNoise > 0.68f;
                    var finalRgb = isSnowDust ? 0xCCF2E8 : foliageRgb;
                    var material = isSnowDust ? ChristmasMaterial.SnowDust : ChristmasMaterial.Foliage;

                    samples.Add(new(
                        new Vector3(radius * cosA, y, radius * sinA),
                        normal,
                        finalRgb,
                        material,
                        u,
                        CullBackFace: false));
                }
            }

            // 각 단의 돌출된 하단 처마(언더컷) 면을 살짝 어둡게 채워 계단형 실루엣의 입체감 극대화
            const int skirtSteps = 84;
            for (var ai = 0; ai < skirtSteps; ai++)
            {
                var angle = MathF.Tau * ai / skirtSteps;
                var outerR = TierRadiusAt(spec, 0f, angle, tier);
                var cosA = MathF.Cos(angle);
                var sinA = MathF.Sin(angle);
                var y = spec.BaseY - 0.08f;
                for (var ri = 1; ri <= 4; ri++)
                {
                    var frac = ri / 4f;
                    var r = outerR * (0.68f + 0.30f * frac);
                    samples.Add(new(
                        new Vector3(r * cosA, y, r * sinA),
                        Vector3.Normalize(new Vector3(cosA * 0.25f, -0.96f, sinA * 0.25f)),
                        0x0A3D22,
                        ChristmasMaterial.Foliage,
                        0f,
                        CullBackFace: false));
                }
            }
        }
    }

    private static void AddGarland(List<ChristmasSurfaceSample> samples)
    {
        // 레퍼런스처럼 트리 전면을 사선으로 선명하게 가로지르는 5회전 황금 빛 리본
        const int steps = 1550;
        const float turns = 4.85f;
        for (var i = 0; i <= steps; i++)
        {
            var u = (float)i / steps;
            var angle = u * MathF.Tau * turns - 0.85f;
            var swag = 0.045f * MathF.Sin(angle * 3f);
            var y = 1.18f + u * 3.82f - swag;
            var radius = SurfaceRadiusAt(y, angle) + 0.085f;
            var radial = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            var center = radial * radius + Vector3.UnitY * y;

            for (var j = 0; j < 7; j++)
            {
                var tubeAngle = MathF.Tau * j / 7f + u * 28f;
                var n = Vector3.Normalize(radial * MathF.Cos(tubeAngle) + Vector3.UnitY * MathF.Sin(tubeAngle));
                var goldRgb = j % 2 == 0 ? 0xFFAE24 : 0xFFE359;
                samples.Add(new(center + n * 0.052f, n, goldRgb, ChristmasMaterial.Garland, u, CullBackFace: true));
            }

            if (i % 16 == 0)
            {
                var bulbPos = center + radial * 0.062f;
                samples.Add(new(bulbPos, radial, 0xFFF4A8, ChristmasMaterial.GarlandLight, u, CullBackFace: false));
            }
        }
    }

    private static void AddOrnaments(List<ChristmasSurfaceSample> samples)
    {
        // 초록 트리와 황금 가랜드를 해치지 않으면서 보석처럼 또렷하게 빛나는 오너먼트
        int[] palette =
        {
            0xFF385C, // 루비 레드
            0xFF7B47, // 웜 코랄
            0x44EEFF, // 아이스 시안
            0xFF9ED2, // 로즈 펄
            0xC278FF, // 바이올렛
            0xFFE266  // 골드 펄
        };

        const int ornamentCount = 34;
        for (var i = 0; i < ornamentCount; i++)
        {
            var t = (i + 0.5f) / ornamentCount;
            var y = 1.24f + t * 3.72f;
            var angle = i * 2.3999632f + 0.4f;
            var surfaceR = SurfaceRadiusAt(y, angle);
            if (surfaceR < 0.30f) surfaceR = 0.30f;
            var radius = 0.115f + (i % 2) * 0.018f - t * 0.02f;
            var center = new Vector3((surfaceR + radius * 0.42f) * MathF.Cos(angle), y,
                (surfaceR + radius * 0.42f) * MathF.Sin(angle));

            AddSphere(samples, center, radius, palette[i % palette.Length], ChristmasMaterial.Ornament,
                rings: 10, segments: 20, phase: i * 0.81f);
        }
    }

    private static void AddGifts(List<ChristmasSurfaceSample> samples)
    {
        foreach (var gift in GiftPlacements)
        {
            var groundY = GroundHeightAt(gift.Center.X, gift.Center.Z);
            var boxCenter = new Vector3(gift.Center.X, groundY + gift.Size.Y * 0.5f, gift.Center.Z);
            var rot = Matrix4x4.CreateRotationY(gift.YawRadians);
            AddGiftBox(samples, boxCenter, gift.Size, rot, gift.BoxRgb, gift.RibbonRgb);
            AddRibbonBow(samples, boxCenter + Vector3.UnitY * (gift.Size.Y * 0.5f + 0.03f),
                MathF.Min(gift.Size.X, gift.Size.Z) * 0.30f, rot, gift.RibbonRgb);
        }
    }

    private static void AddGiftBox(
        List<ChristmasSurfaceSample> samples,
        Vector3 center,
        Vector3 size,
        Matrix4x4 rotation,
        int boxRgb,
        int ribbonRgb)
    {
        const int steps = 22;
        var lidRgb = LightenRgb(boxRgb, 32);
        for (var axis = 0; axis < 3; axis++)
        for (var sign = -1; sign <= 1; sign += 2)
        for (var a = 0; a <= steps; a++)
        for (var b = 0; b <= steps; b++)
        {
            var u = (float)a / steps - 0.5f;
            var v = (float)b / steps - 0.5f;
            var local = axis switch
            {
                0 => new Vector3(sign * size.X * 0.5f, u * size.Y, v * size.Z),
                1 => new Vector3(u * size.X, sign * size.Y * 0.5f, v * size.Z),
                _ => new Vector3(u * size.X, v * size.Y, sign * size.Z * 0.5f)
            };

            var isLidBand = axis != 1 && local.Y > size.Y * 0.28f;
            if (isLidBand || (axis == 1 && sign > 0))
            {
                local.X *= 1.045f;
                local.Z *= 1.045f;
            }

            var localNormal = axis == 0 ? Vector3.UnitX * sign
                : axis == 1 ? Vector3.UnitY * sign
                : Vector3.UnitZ * sign;

            // 상자 정면·측면·윗면을 또렷하게 가로지르는 넓은 리본 띠
            var isRibbon = axis == 1 ? MathF.Abs(u) < 0.12f || MathF.Abs(v) < 0.12f
                : axis == 0 ? MathF.Abs(v) < 0.12f
                : MathF.Abs(u) < 0.12f;

            var rgb = isRibbon ? ribbonRgb : (isLidBand || (axis == 1 && sign > 0)) ? lidRgb : boxRgb;
            var material = isRibbon ? ChristmasMaterial.Ribbon : ChristmasMaterial.Gift;

            var worldPos = center + Vector3.Transform(local, rotation);
            var worldNorm = Vector3.TransformNormal(localNormal, rotation);
            samples.Add(new(worldPos, worldNorm, rgb, material, 0f, CullBackFace: true));
        }
    }

    private static void AddRibbonBow(
        List<ChristmasSurfaceSample> samples,
        Vector3 topCenter,
        float bowRadius,
        Matrix4x4 rotation,
        int ribbonRgb)
    {
        AddSphere(samples, topCenter + Vector3.UnitY * (bowRadius * 0.28f), bowRadius * 0.34f,
            ribbonRgb, ChristmasMaterial.Ribbon, rings: 6, segments: 12);

        for (var loop = 0; loop < 4; loop++)
        {
            var loopAngle = loop * MathF.PI * 0.5f + MathF.PI * 0.25f;
            var loopDir = new Vector3(MathF.Cos(loopAngle), 0f, MathF.Sin(loopAngle));
            var loopSide = new Vector3(-MathF.Sin(loopAngle), 0f, MathF.Cos(loopAngle));

            const int arcSteps = 16;
            for (var i = 0; i <= arcSteps; i++)
            {
                var t = (float)i / arcSteps * MathF.PI;
                var sinT = MathF.Sin(t);
                var arch = loopDir * (sinT * bowRadius * 1.15f)
                    + Vector3.UnitY * (MathF.Sin(t * 0.85f) * bowRadius * 0.96f + 0.02f);

                for (var w = -1; w <= 1; w++)
                {
                    var local = arch + loopSide * (w * bowRadius * 0.18f);
                    var localNormal = Vector3.Normalize(loopDir * MathF.Sin(t) + Vector3.UnitY * 0.72f + loopSide * (w * 0.15f));
                    samples.Add(new(
                        topCenter + Vector3.Transform(local, rotation),
                        Vector3.TransformNormal(localNormal, rotation),
                        ribbonRgb,
                        ChristmasMaterial.Ribbon,
                        0f,
                        CullBackFace: false));
                }
            }
        }
    }

    private static void AddStar(List<ChristmasSurfaceSample> samples)
    {
        // 어느 회전 각도에서도 넓은 좌우 팔과 또렷한 5각 실루엣이 유지되는 직교 이중 3D 황금 별
        var outline = new Vector3[10];
        for (var i = 0; i < outline.Length; i++)
        {
            var angle = MathF.PI * 0.5f + i * MathF.Tau / outline.Length;
            var radius = i % 2 == 0 ? 0.84f : 0.27f;
            outline[i] = new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
        }

        const int steps = 22;
        var rotations = new[] { Matrix4x4.Identity, Matrix4x4.CreateRotationY(MathF.PI * 0.5f) };
        foreach (var rot in rotations)
        {
            for (var i = 0; i < outline.Length; i++)
            {
                var b = outline[i];
                var c = outline[(i + 1) % outline.Length];
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    var peak = new Vector3(0f, 0f, 0.17f * sign);
                    var localNormal = Vector3.Normalize(Vector3.Cross(b - peak, c - peak));
                    if (localNormal.Z * sign < 0f) localNormal = -localNormal;
                    var worldNormal = Vector3.TransformNormal(localNormal, rot);

                    for (var u = 0; u <= steps; u++)
                    for (var v = 0; v <= steps - u; v++)
                    {
                        var fu = (float)u / steps;
                        var fv = (float)v / steps;
                        var p = peak * (1f - fu - fv) + b * fu + c * fv;
                        var coreGlow = 1f - Math.Clamp(p.Length() / 0.84f, 0f, 1f);
                        var starRgb = LerpRgb(0xFFAE2B, 0xFFF7BE, coreGlow * 0.78f);
                        samples.Add(new(StarCenter + Vector3.Transform(p, rot), worldNormal, starRgb,
                            ChristmasMaterial.Star, coreGlow, CullBackFace: true));
                    }
                }
            }
        }
    }

    private static void AddSphere(
        List<ChristmasSurfaceSample> samples,
        Vector3 center,
        float radius,
        int rgb,
        ChristmasMaterial material,
        int rings = 10,
        int segments = 20,
        float phase = 0f)
    {
        for (var y = 0; y <= rings; y++)
        for (var x = 0; x < segments; x++)
        {
            var latitude = MathF.PI * y / rings;
            var longitude = MathF.Tau * x / segments;
            var n = new Vector3(
                MathF.Sin(latitude) * MathF.Cos(longitude),
                MathF.Cos(latitude),
                MathF.Sin(latitude) * MathF.Sin(longitude));
            samples.Add(new(center + n * radius, n, rgb, material, phase, CullBackFace: true));
        }
    }

    private static int LerpRgb(int from, int to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var r = (int)(((from >> 16) & 255) + (((to >> 16) & 255) - ((from >> 16) & 255)) * t);
        var g = (int)(((from >> 8) & 255) + (((to >> 8) & 255) - ((from >> 8) & 255)) * t);
        var b = (int)((from & 255) + ((to & 255) - (from & 255)) * t);
        return (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
    }

    private static int LightenRgb(int rgb, int amount)
    {
        var r = Math.Min(255, ((rgb >> 16) & 255) + amount);
        var g = Math.Min(255, ((rgb >> 8) & 255) + amount);
        var b = Math.Min(255, (rgb & 255) + amount);
        return (r << 16) | (g << 8) | b;
    }

    private static int ScaleRgb(int rgb, float factor)
    {
        var r = Math.Clamp((int)(((rgb >> 16) & 255) * factor), 0, 255);
        var g = Math.Clamp((int)(((rgb >> 8) & 255) * factor), 0, 255);
        var b = Math.Clamp((int)((rgb & 255) * factor), 0, 255);
        return (r << 16) | (g << 8) | b;
    }
}
