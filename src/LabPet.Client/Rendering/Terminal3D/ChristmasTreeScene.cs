using System;
using System.Numerics;

namespace LabPet.Client.Rendering.Terminal3D;

/// <summary>출력 장치와 독립적인 3D 크리스마스트리 등장 타임라인·조명 모드·장면 렌더러.</summary>
internal sealed class ChristmasTreeScene
{
    public const double RotationStartSeconds = 4.0;
    public const double StarStartSeconds = 5.0;
    public const int LightModeCount = 4;

    private static readonly string[] LightModeLabels = { "클래식 웜", "오로라 트윙클", "골든 글로우", "문라이트" };

    private readonly ChristmasTreeGeometry _geometry = new();

    public double ElapsedSeconds { get; private set; }
    public float RotationRadians { get; private set; }
    public float RotationSpeed { get; private set; } = 0.28f;
    public float Zoom { get; private set; } = 1.46f;
    public bool RotationPaused { get; private set; }
    public int LightMode { get; private set; }
    public string LightModeLabel => LightModeLabels[LightMode];
    public int SubmittedSamples { get; private set; }

    public void Update(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        var previous = ElapsedSeconds;
        ElapsedSeconds += deltaSeconds;
        var rotationDelta = Math.Max(0, ElapsedSeconds - RotationStartSeconds)
            - Math.Max(0, previous - RotationStartSeconds);
        if (!RotationPaused)
            RotationRadians = (float)((RotationRadians + rotationDelta * RotationSpeed) % Math.Tau);
    }

    public void RestartIntroduction()
    {
        ElapsedSeconds = 0;
        RotationRadians = 0;
    }

    public void ToggleRotation() => RotationPaused = !RotationPaused;

    public void AdjustRotation(float change)
    {
        if (float.IsFinite(change)) RotationSpeed = Math.Clamp(RotationSpeed + change, -1.4f, 1.4f);
    }

    public void AdjustZoom(float change)
    {
        if (float.IsFinite(change)) Zoom = Math.Clamp(Zoom + change, 0.45f, 2.4f);
    }

    public void CycleLighting() => LightMode = (LightMode + 1) % LightModeCount;

    public void Render(
        AsciiDepthSurface surface,
        TerminalPointProjector projector,
        float cellAspect,
        int detailStride = 1)
    {
        surface.Clear();
        var (ambientScale, warmTint) = LightMode switch
        {
            1 => (1.05f, 1.15f), // 오로라 트윙클
            2 => (1.15f, 1.50f), // 골든 글로우
            3 => (0.88f, 0.42f), // 문라이트
            _ => (1.00f, 1.00f)  // 클래식 웜
        };
        projector.Configure(RotationRadians, Zoom, cellAspect, ambientScale, warmTint);
        SubmittedSamples = 0;

        var stride = Math.Clamp(detailStride, 1, 3);
        var groundGrowth = EaseOutCubic((float)Math.Clamp(ElapsedSeconds, 0, 1));
        var treeGrowth = EaseOutCubic((float)Math.Clamp((ElapsedSeconds - 1.0) / 2.0, 0, 1));
        var decorationGrowth = EaseOutCubic((float)Math.Clamp(ElapsedSeconds - 3.0, 0, 1));
        var ornamentFade = (float)Math.Clamp(ElapsedSeconds - RotationStartSeconds, 0, 1);
        var starIgnition = EaseOutCubic((float)Math.Clamp((ElapsedSeconds - StarStartSeconds) / 0.65, 0, 1));
        var time = (float)(ElapsedSeconds % 3600.0);

        var samples = _geometry.Samples;
        for (var i = 0; i < samples.Length; i++)
        {
            ref readonly var sample = ref samples[i];
            if (stride > 1 && (sample.Material is ChristmasMaterial.Ground or ChristmasMaterial.Foliage) && (i % stride) != 0)
            {
                continue;
            }

            var position = sample.Position;
            var rgb = sample.Rgb;
            var emission = 0f;
            var specular = 0.18f;
            var shininess = 14f;
            var glyphStyle = 0;

            switch (sample.Material)
            {
                case ChristmasMaterial.Ground:
                    if (groundGrowth <= 0f) continue;
                    position.X *= groundGrowth;
                    position.Z *= groundGrowth;
                    specular = 0.14f;
                    shininess = 20f;
                    glyphStyle = 2;
                    if (sample.Phase > 0.78f)
                    {
                        emission = 0.14f + 0.18f * MathF.Max(0f, MathF.Sin(time * 2.2f + position.X * 5f + position.Z * 4f));
                    }
                    break;

                case ChristmasMaterial.Trunk:
                    if (treeGrowth <= 0f || position.Y > ChristmasTreeGeometry.TreeTop * treeGrowth) continue;
                    specular = 0.08f;
                    shininess = 8f;
                    glyphStyle = 3;
                    break;

                case ChristmasMaterial.Foliage:
                    if (treeGrowth <= 0f || position.Y > ChristmasTreeGeometry.TreeTop * treeGrowth) continue;
                    specular = 0.16f;
                    shininess = 10f;
                    glyphStyle = 1;
                    if (LightMode == 3) rgb = TintCool(rgb, 0.18f);
                    break;

                case ChristmasMaterial.SnowDust:
                    if (treeGrowth <= 0f || position.Y > ChristmasTreeGeometry.TreeTop * treeGrowth) continue;
                    specular = 0.36f;
                    shininess = 18f;
                    emission = 0.14f;
                    glyphStyle = 1;
                    break;

                case ChristmasMaterial.Garland:
                    if (decorationGrowth <= 0f || sample.Phase > decorationGrowth) continue;
                    specular = 0.85f;
                    shininess = 24f;
                    glyphStyle = 3;
                    emission = 0.42f + 0.32f * (0.5f + 0.5f * MathF.Sin(sample.Phase * MathF.Tau * 6f - time * 3.1f));
                    break;

                case ChristmasMaterial.GarlandLight:
                    if (decorationGrowth <= 0f || sample.Phase > decorationGrowth) continue;
                    specular = 1.0f;
                    shininess = 30f;
                    glyphStyle = 3;
                    var pulse = 0.5f + 0.5f * MathF.Sin(sample.Phase * MathF.Tau * 10f - time * 4.2f);
                    emission = 0.58f + 0.48f * pulse * pulse;
                    if (LightMode == 1) rgb = ShiftAuroraColor(sample.Phase * 12f + time * 1.4f);
                    break;

                case ChristmasMaterial.Gift:
                    if (decorationGrowth <= 0f) continue;
                    position.Y -= (1f - decorationGrowth) * 0.85f;
                    specular = 0.28f;
                    shininess = 14f;
                    emission = 0.16f;
                    glyphStyle = 3;
                    break;

                case ChristmasMaterial.Ribbon:
                    if (decorationGrowth <= 0f) continue;
                    position.Y -= (1f - decorationGrowth) * 0.85f;
                    specular = 0.78f;
                    shininess = 24f;
                    emission = 0.34f;
                    glyphStyle = 3;
                    break;

                case ChristmasMaterial.Ornament:
                    if (ornamentFade <= 0f) continue;
                    specular = 0.96f;
                    shininess = 36f;
                    glyphStyle = 3;
                    var twinkle = 0.5f + 0.5f * MathF.Sin(time * 2.8f + sample.Phase);
                    emission = ornamentFade * (0.36f + 0.48f * twinkle * twinkle);
                    if (LightMode == 1) rgb = ShiftAuroraColor(sample.Phase + time * 0.9f);
                    break;

                case ChristmasMaterial.Star:
                    if (starIgnition <= 0f) continue;
                    position = ChristmasTreeGeometry.StarCenter
                        + (position - ChristmasTreeGeometry.StarCenter) * (0.35f + 0.65f * starIgnition);
                    specular = 1.0f;
                    shininess = 28f;
                    glyphStyle = 3;
                    emission = starIgnition * (0.68f + 0.26f * (0.5f + 0.5f * MathF.Sin(time * 2.2f)));
                    break;
            }

            if (LightMode == 2) emission = MathF.Min(1.25f, emission * 1.35f + 0.08f);
            else if (LightMode == 3) emission *= 0.64f;

            projector.Plot(position, sample.Normal, rgb, emission, specular, shininess, sample.CullBackFace, glyphStyle);
            SubmittedSamples++;
        }

        // 5초 이후 별 점등 시 3D 광선과 황금 후광을 가산 블렌딩으로 표현
        if (starIgnition > 0f)
        {
            RenderStarAura(projector, starIgnition, time);
        }
    }

    private void RenderStarAura(TerminalPointProjector projector, float ignition, float time)
    {
        var shimmer = 0.78f + 0.22f * MathF.Sin(time * 2.6f);
        var power = ignition * shimmer * (LightMode == 3 ? 0.7f : LightMode == 2 ? 1.25f : 1f);
        var origin = ChristmasTreeGeometry.StarCenter;

        // 별 5각 외곽을 해치지 않는 은은한 황금빛 잔광 스파클
        const int rayCount = 8;
        for (var r = 0; r < rayCount; r++)
        {
            var angle = r * MathF.Tau / rayCount + time * 0.25f;
            var maxLength = (r % 2 == 0 ? 0.55f : 0.35f) * (0.88f + 0.12f * MathF.Sin(time * 3.1f + r));
            var dir = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
            for (var step = 1; step <= 3; step++)
            {
                var frac = step / 3f;
                var dist = 0.66f + frac * maxLength;
                var falloff = (1f - frac * 0.72f) * power * 0.78f;
                var color = ScaleRgb(step == 1 ? 0xFFF2AA : 0xFFB84D, Math.Clamp(falloff, 0f, 1f));
                var glyph = step == 1 ? '+' : '.';
                projector.PlotLocalGlow(origin + dir * dist, color, glyph);
            }
        }
    }

    private static float EaseOutCubic(float t)
    {
        var inv = 1f - Math.Clamp(t, 0f, 1f);
        return 1f - inv * inv * inv;
    }

    private static int TintCool(int rgb, float strength)
    {
        var r = Math.Clamp((int)(((rgb >> 16) & 255) * (1f - strength * 0.35f)), 0, 255);
        var g = Math.Clamp((int)(((rgb >> 8) & 255) * (1f - strength * 0.05f)), 0, 255);
        var b = Math.Clamp((int)((rgb & 255) + (255 - (rgb & 255)) * strength * 0.45f), 0, 255);
        return (r << 16) | (g << 8) | b;
    }

    private static int ShiftAuroraColor(float phase)
    {
        var r = (int)(135 + 115 * MathF.Sin(phase));
        var g = (int)(145 + 105 * MathF.Sin(phase + 2.094f));
        var b = (int)(155 + 100 * MathF.Sin(phase + 4.188f));
        return (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
    }

    private static int ScaleRgb(int rgb, float factor)
    {
        var r = Math.Clamp((int)(((rgb >> 16) & 255) * factor), 0, 255);
        var g = Math.Clamp((int)(((rgb >> 8) & 255) * factor), 0, 255);
        var b = Math.Clamp((int)((rgb & 255) * factor), 0, 255);
        return (r << 16) | (g << 8) | b;
    }
}
