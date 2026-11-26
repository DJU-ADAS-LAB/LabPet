using System.Windows.Media;

namespace LabPet.Client.Rendering;

/// <summary>미리 생성한 오라 풀의 변환과 브러시만 갱신한다. 별도 타이머나 프레임별 메시 생성 없음.</summary>
internal static class PetAuraAnimator
{
    internal static void Update(PetRigHandle rig, double seconds, double bass, double beat)
    {
        var particles = rig.AuraParticles;
        if (particles.Count == 0 || !double.IsFinite(seconds)) return;
        var response = Math.Clamp(bass * 0.08 + beat * 0.12, 0, 0.20);
        var falling = rig.AuraEffectId is "snowflakes" or "cherry_blossom";
        var floating = rig.AuraEffectId == "fireflies";
        for (var i = 0; i < particles.Count; i++)
        {
            var particle = particles[i];
            var seed = i * 2.399963229728653;
            var lifetime = 4.8 + (i % 4) * 0.7;
            var cycle = seconds / lifetime + i / (double)particles.Count;
            var age = cycle - Math.Floor(cycle);
            // 循環境界は透明にして位置のリセットを隠す。
            var fade = PetMotionClips.SmoothStep(Math.Min(age / 0.16, (1 - age) / 0.22));
            var angle = seed + seconds * (0.12 + (i % 3) * 0.025);
            // 顔の前を横切らない左右の帯。奥行きは胴体より後方に保つ。
            var side = (i & 1) == 0 ? -1.0 : 1.0;
            particle.Translate.OffsetX = side * (1.03 + 0.16 * Math.Sin(angle));
            particle.Translate.OffsetZ = -0.25 + 0.16 * Math.Cos(angle);
            particle.Translate.OffsetY = floating
                ? 0.20 + 0.50 * Math.Sin(seconds * 0.45 + seed)
                : (falling ? 1.05 - age * 1.85 : -0.65 + age * 1.70);
            if (rig.AuraEffectId == "starlight")
            {
                // 입자별로 다른 위치에서 느리게 반짝이며 작은 범위만 부유한다.
                particle.Translate.OffsetY = -0.60 + 1.65 * i / Math.Max(1d, particles.Count - 1)
                    + 0.055 * Math.Sin(seconds * 0.55 + seed);
                fade = 0.72 + 0.28 * Math.Sin(seconds * 0.70 + seed);
            }
            else if (rig.AuraEffectId == "milky_way")
            {
                // 얼굴 높이에서는 뒤로 지나가도록 기울어진 타원 궤도를 사용한다.
                var orbit = seconds * 0.24 + i * Math.PI * 2 / particles.Count;
                particle.Translate.OffsetX = 1.16 * Math.Cos(orbit);
                particle.Translate.OffsetY = -0.42 + 0.24 * Math.Sin(orbit);
                particle.Translate.OffsetZ = -0.30 + 0.48 * Math.Sin(orbit);
                fade = 0.88;
            }
            else if (rig.AuraEffectId == "cherry_blossom")
            {
                particle.Translate.OffsetX += 0.10 * Math.Sin(seconds * 0.60 + seed);
                particle.Translate.OffsetY = 1.05 - age * 1.85;
            }
            particle.Rotation.Angle = (seconds * (falling ? 28 : 18) + i * 47) % 360;
            var size = (floating ? 0.35 : 0.62) * (1 + response) * (0.90 + 0.10 * Math.Sin(angle));
            particle.Scale.ScaleX = size;
            particle.Scale.ScaleY = size;
            particle.Scale.ScaleZ = size;
            if (rig.AuraEffectId == "aurora")
            {
                // 좌우 각각 네 띠를 배치하고 위상차로 느린 물결을 만든다.
                var lane = i / 2;
                var wave = seconds * 0.38 + lane * 0.75;
                particle.Translate.OffsetX = side * (0.96 + lane * 0.045)
                    + 0.055 * Math.Sin(wave);
                particle.Translate.OffsetY = -0.45 + lane * 0.36
                    + 0.085 * Math.Sin(wave + 0.65);
                particle.Translate.OffsetZ = -0.55 - lane * 0.035;
                particle.Scale.ScaleX = 0.85 + 0.10 * Math.Sin(wave);
                particle.Scale.ScaleY = 1.10 + 0.12 * Math.Sin(wave + 0.8);
                particle.Scale.ScaleZ = 1;
                fade = 0.58 + 0.12 * Math.Sin(wave);
            }
            var color = particle.BaseColor;
            var shimmer = 0.82 + 0.18 * Math.Sin(seconds * 1.15 + seed);
            var alpha = fade * shimmer;
            if (particle.SoftGlowBrush is { } softGlow)
            {
                softGlow.Opacity = Math.Clamp(alpha * (0.48 + response), 0, 0.68);
                // 발광 평면은 정면을 유지해 회전 중 빛이 얇아지는 현상을 피한다.
                particle.Rotation.Angle = 0;
                if (rig.AuraEffectId == "halo_ring")
                {
                    // 네 원호씩 두 궤도를 구성하고 서로 반대 방향으로 빛을 흘린다.
                    var lane = i / 4;
                    var direction = lane == 0 ? 1.0 : -1.0;
                    var radius = 1.02 + lane * 0.13 + response * 0.20;
                    particle.Translate.OffsetX = 0;
                    particle.Translate.OffsetY = -0.78 + lane * 0.055;
                    particle.Translate.OffsetZ = 0;
                    particle.Scale.ScaleX = radius;
                    particle.Scale.ScaleY = 1;
                    particle.Scale.ScaleZ = radius;
                    particle.Rotation.Angle = (i % 4) * 90 + direction * seconds * 16;
                    var trail = 0.5 + 0.5 * Math.Cos(seconds * 0.85 - i % 4 * Math.PI / 2);
                    softGlow.Opacity = Math.Clamp(0.20 + trail * 0.20 + response * 0.65, 0, 0.55);
                }
                else if (rig.AuraEffectId == "electric")
                {
                    // 원호는 중심 주위에서 독립적으로 회전하고 부드럽게 확산한다.
                    var phase = seconds / 3.2 + i / (double)particles.Count;
                    var progress = phase - Math.Floor(phase);
                    var envelope = Math.Sin(Math.PI * progress);
                    var radius = 0.92 + progress * 0.18 + response * 0.35;
                    particle.Translate.OffsetX = 0;
                    particle.Translate.OffsetY = -0.62 + (i % 2) * 0.18
                        + 0.035 * Math.Sin(seconds * 0.8 + seed);
                    particle.Translate.OffsetZ = -0.18;
                    particle.Scale.ScaleX = radius;
                    particle.Scale.ScaleY = 1;
                    particle.Scale.ScaleZ = radius;
                    particle.Rotation.Angle = (i * 45 + seconds * 12) % 360;
                    softGlow.Opacity = Math.Clamp(envelope * (0.24 + response * 1.8), 0, 0.60);
                }
            }
            if (particle.TintBrush is { } tint)
                tint.Color = Color.FromArgb((byte)(155 * alpha), color.R, color.G, color.B);
            if (particle.EmissiveBrush is { } glow)
                glow.Color = Color.FromArgb((byte)((45 + response * 80) * alpha), color.R, color.G, color.B);
        }
    }
}
