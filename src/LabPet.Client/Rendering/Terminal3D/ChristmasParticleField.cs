using System;
using System.Numerics;

namespace LabPet.Client.Rendering.Terminal3D;

/// <summary>
/// 고정 크기 오브젝트 풀 기반의 원근 눈송이·밤하늘 반짝이는 별빛·다단계 불꽃놀이 효과.
/// 장면 회전과 독립적인 월드 좌표에서 경과 시간 기준으로 갱신해 프레임 독립성과 무할당을 유지한다.
/// </summary>
internal sealed class ChristmasParticleField
{
    public const int MaximumSnow = 480;
    public const int MaximumSparks = 512;
    public const int SkyStarCount = 96;

    private static readonly string[] SnowLevelLabels = { "약함", "보통", "함박눈" };

    private struct Spark
    {
        public Vector3 Origin;
        public Vector3 Velocity;
        public double Born;
        public float LaunchDuration;
        public float BurstLifetime;
        public int Color;
        public bool IsCore;
    }

    private struct SkyStar
    {
        public Vector3 Position;
        public int BaseRgb;
        public float Speed;
        public float Phase;
    }

    private readonly Vector3[] _snowSeeds = new Vector3[MaximumSnow];
    private readonly SkyStar[] _skyStars = new SkyStar[SkyStarCount];
    private readonly Spark[] _sparks = new Spark[MaximumSparks];
    private readonly Random _random = new(1225);
    private int _nextSpark;

    public int SnowLevel { get; private set; } = 1;
    public string SnowLevelLabel => SnowLevelLabels[SnowLevel];
    public int SnowCount => SnowLevel switch { 0 => 120, 1 => 280, _ => MaximumSnow };
    public int ActiveSparks { get; private set; }

    public ChristmasParticleField()
    {
        for (var i = 0; i < _snowSeeds.Length; i++)
        {
            _snowSeeds[i] = new Vector3(
                (float)_random.NextDouble() * 15f - 7.5f,
                (float)_random.NextDouble() * 9.5f,
                (float)_random.NextDouble() * 11.5f - 5.2f);
        }

        int[] starTints = { 0xD8EBFF, 0xFFF3D4, 0xB8D4FF, 0xFFFFFF };
        for (var i = 0; i < _skyStars.Length; i++)
        {
            var x = (float)_random.NextDouble() * 26f - 13f;
            var y = 0.6f + (float)_random.NextDouble() * 10.5f;
            var z = -9.5f - (float)_random.NextDouble() * 7.5f;
            _skyStars[i] = new SkyStar
            {
                Position = new Vector3(x, y, z),
                BaseRgb = starTints[i % starTints.Length],
                Speed = 1.4f + (float)_random.NextDouble() * 2.6f,
                Phase = (float)_random.NextDouble() * MathF.Tau
            };
        }
    }

    public void CycleSnow() => SnowLevel = (SnowLevel + 1) % 3;

    public void Reset()
    {
        Array.Clear(_sparks);
        _nextSpark = 0;
        ActiveSparks = 0;
    }

    /// <summary>트리 가시성을 해치지 않는 좌·우 상공에 상승 꼬리 + 2중 구형 불꽃놀이를 발사한다.</summary>
    public void TriggerFirework(double time)
    {
        if (!double.IsFinite(time) || time < 0) return;

        var side = _random.Next(2) == 0 ? -1 : 1;
        var origin = new Vector3(
            side * (4.1f + (float)_random.NextDouble() * 1.35f),
            4.35f + (float)_random.NextDouble() * 1.55f,
            -3.6f - (float)_random.NextDouble() * 0.9f);

        int[] outerPalette = { 0xFF6B9D, 0xFFD966, 0x5CE6FF, 0xC68CFF, 0x7CFF9E };
        int[] corePalette = { 0xFFF6CC, 0xFFFFFF, 0xFFE394 };
        var outerColor = outerPalette[_random.Next(outerPalette.Length)];
        var coreColor = corePalette[_random.Next(corePalette.Length)];

        const float launchDuration = 0.42f;
        const int outerCount = 56;
        const int coreCount = 24;

        for (var i = 0; i < outerCount; i++)
        {
            var z = 1f - 2f * (i + 0.5f) / outerCount;
            var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            var angle = i * 2.3999632f;
            var dir = new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
            var speed = 1.45f + (float)_random.NextDouble() * 0.55f;
            _sparks[_nextSpark] = new Spark
            {
                Origin = origin,
                Velocity = dir * speed,
                Born = time,
                LaunchDuration = launchDuration,
                BurstLifetime = 1.65f + (float)_random.NextDouble() * 0.55f,
                Color = outerColor,
                IsCore = false
            };
            _nextSpark = (_nextSpark + 1) % MaximumSparks;
        }

        for (var i = 0; i < coreCount; i++)
        {
            var z = 1f - 2f * (i + 0.5f) / coreCount;
            var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            var angle = i * 2.3999632f + 0.7f;
            var dir = new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
            _sparks[_nextSpark] = new Spark
            {
                Origin = origin,
                Velocity = dir * (0.72f + (float)_random.NextDouble() * 0.25f),
                Born = time,
                LaunchDuration = launchDuration,
                BurstLifetime = 1.15f + (float)_random.NextDouble() * 0.35f,
                Color = coreColor,
                IsCore = true
            };
            _nextSpark = (_nextSpark + 1) % MaximumSparks;
        }
    }

    public void Render(TerminalPointProjector projector, double time, float zoom, float cellAspect)
    {
        if (!double.IsFinite(time) || time < 0) return;

        projector.Configure(0f, zoom, cellAspect);
        var fTime = (float)(time % 3600.0);

        // 1. 밤하늘 원경 반짝이는 별빛 (트리가 차지하지 않은 깊은 배경에만 자연스럽게 표시)
        for (var i = 0; i < _skyStars.Length; i++)
        {
            ref readonly var star = ref _skyStars[i];
            var twinkle = 0.28f + 0.72f * MathF.Pow(0.5f + 0.5f * MathF.Sin(fTime * star.Speed + star.Phase), 2.2f);
            var glyph = twinkle > 0.86f ? '*' : twinkle > 0.58f ? '+' : '.';
            projector.PlotParticle(star.Position, ScaleRgb(star.BaseRgb, twinkle), glyph, additiveGlow: true);
        }

        // 2. 원근감과 깊이가 적용된 3D 눈송이
        var count = SnowCount;
        for (var i = 0; i < count; i++)
        {
            var seed = _snowSeeds[i];
            var speed = 0.48 + (i % 7) * 0.11;
            var y = 8.4f - (float)((seed.Y + time * speed) % 9.5);
            var sway = 0.28f * MathF.Sin(fTime * (0.55f + (i % 5) * 0.12f) + i * 0.7f);
            var x = seed.X + sway;
            var pos = new Vector3(x, y, seed.Z);

            if (!projector.TryProjectWorld(pos, out _, out _, out var depth)) continue;

            // 카메라와 가까운 눈은 크고 밝게(*, +), 먼 눈은 작고 은은하게(o, :, .)
            var glyph = depth < 6.8f ? '*'
                : depth < 9.2f ? '+'
                : depth < 11.8f ? 'o'
                : depth < 14.0f ? ':' : '.';
            var brightness = Math.Clamp(1.18f - depth * 0.055f, 0.34f, 1f);
            var snowRgb = ScaleRgb(depth < 8.5f ? 0xF2FAFF : 0xB8D8F5, brightness);
            projector.PlotParticle(pos, snowRgb, glyph);
        }

        // 3. 다단계 불꽃놀이 (상승 꼬리 → 확장 폭발 + 잔광 궤적)
        ActiveSparks = 0;
        var span = _sparks.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            ref readonly var spark = ref span[i];
            if (spark.BurstLifetime <= 0f) continue;

            var age = time - spark.Born;
            var totalLife = spark.LaunchDuration + spark.BurstLifetime;
            if (age < 0 || age >= totalLife) continue;

            ActiveSparks++;
            if (age < spark.LaunchDuration)
            {
                // 상승 로켓은 묶음당 대표 코어 1개만 그려 깔끔한 상승 궤적 표현
                if (!spark.IsCore || (i % 8) != 0) continue;
                var lt = (float)(age / spark.LaunchDuration);
                var eased = 1f - (1f - lt) * (1f - lt);
                var rocketPos = new Vector3(spark.Origin.X, 0.4f + (spark.Origin.Y - 0.4f) * eased, spark.Origin.Z);
                projector.PlotParticle(rocketPos, 0xFFE89E, '^', additiveGlow: true);
                projector.PlotParticle(rocketPos - Vector3.UnitY * 0.22f, 0xD98B38, '|', additiveGlow: true);
                projector.PlotParticle(rocketPos - Vector3.UnitY * 0.44f, 0x8F4D1E, '.', additiveGlow: true);
                continue;
            }

            var t = (float)(age - spark.LaunchDuration);
            var frac = t / spark.BurstLifetime;
            var fade = MathF.Pow(1f - frac, 0.58f);
            var drag = 1f - 0.24f * t;
            var gravity = 0.34f * t * t;
            var pos = spark.Origin + spark.Velocity * (t * MathF.Max(0.38f, drag)) - Vector3.UnitY * gravity;

            // 반짝이는 글리터 깜빡임
            var glitter = frac > 0.65f ? (0.62f + 0.38f * MathF.Sin(fTime * 22f + i * 1.9f)) : 1f;
            var headIntensity = Math.Clamp(fade * glitter, 0f, 1f);
            var headGlyph = frac < 0.42f ? '*' : frac < 0.74f ? '+' : '.';
            projector.PlotParticle(pos, ScaleRgb(spark.Color, headIntensity), headGlyph, additiveGlow: true);

            // 선명한 잔광 꼬리 (0.10초 전 위치)
            if (t > 0.06f && frac < 0.90f)
            {
                var prevT = t - 0.10f;
                var prevDrag = 1f - 0.24f * prevT;
                var tailPos = spark.Origin + spark.Velocity * (prevT * MathF.Max(0.38f, prevDrag))
                    - Vector3.UnitY * (0.34f * prevT * prevT);
                projector.PlotParticle(tailPos, ScaleRgb(spark.Color, headIntensity * 0.76f), ':', additiveGlow: true);
            }
        }
    }

    private static int ScaleRgb(int rgb, float factor)
    {
        var r = Math.Clamp((int)(((rgb >> 16) & 255) * factor), 0, 255);
        var g = Math.Clamp((int)(((rgb >> 8) & 255) * factor), 0, 255);
        var b = Math.Clamp((int)((rgb & 255) * factor), 0, 255);
        return (r << 16) | (g << 8) | b;
    }
}
