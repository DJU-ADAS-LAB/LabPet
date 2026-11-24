using LabPet.Shared.Models;

namespace LabPet.Client.Rendering;

/// <summary>
/// 재사용 가능한 캐릭터 모션 클립 종류.
/// </summary>
public enum PetMotionClipKind
{
    Idle = 0,
    Groove = 1,
    Bounce = 2,
    HeadBob = 3,
    PawDance = 4,
    Turn = 5,
    HappyJump = 6,
    Reaction = 7,
    Walk = 8
}

/// <summary>
/// 힙 할당 없는 값 타입 3D 관절 포즈 스냅샷 (Transform3D 반영 직전 블렌딩·감쇠용).
/// </summary>
internal struct PetPose
{
    public double StepWeight;
    public double BodyOffsetX;
    public double BodyOffsetY;
    public double BodyOffsetZ;
    public double BodyScaleY;
    public double BodyYaw;
    public double BodyPitch;
    public double BodyRoll;
    public double HeadYaw;
    public double HeadPitch;
    public double HeadRoll;
    public double LeftEarOffset;
    public double RightEarOffset;
    public double TailAngle;
    public double LeftPawY;
    public double LeftPawZ;
    public double RightPawY;
    public double RightPawZ;
    public double LeftArmRoll;
    public double LeftArmPitch;
    public double RightArmRoll;
    public double RightArmPitch;
    public double EmissiveBoost;

    public static PetPose Identity => new() { BodyScaleY = 1.0 };

    public static PetPose Lerp(in PetPose a, in PetPose b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return new PetPose
        {
            StepWeight = a.StepWeight + (b.StepWeight - a.StepWeight) * t,
            BodyOffsetX = a.BodyOffsetX + (b.BodyOffsetX - a.BodyOffsetX) * t,
            BodyOffsetY = a.BodyOffsetY + (b.BodyOffsetY - a.BodyOffsetY) * t,
            BodyOffsetZ = a.BodyOffsetZ + (b.BodyOffsetZ - a.BodyOffsetZ) * t,
            BodyScaleY = a.BodyScaleY + (b.BodyScaleY - a.BodyScaleY) * t,
            BodyYaw = a.BodyYaw + (b.BodyYaw - a.BodyYaw) * t,
            BodyPitch = a.BodyPitch + (b.BodyPitch - a.BodyPitch) * t,
            BodyRoll = a.BodyRoll + (b.BodyRoll - a.BodyRoll) * t,
            HeadYaw = a.HeadYaw + (b.HeadYaw - a.HeadYaw) * t,
            HeadPitch = a.HeadPitch + (b.HeadPitch - a.HeadPitch) * t,
            HeadRoll = a.HeadRoll + (b.HeadRoll - a.HeadRoll) * t,
            LeftEarOffset = a.LeftEarOffset + (b.LeftEarOffset - a.LeftEarOffset) * t,
            RightEarOffset = a.RightEarOffset + (b.RightEarOffset - a.RightEarOffset) * t,
            TailAngle = a.TailAngle + (b.TailAngle - a.TailAngle) * t,
            LeftPawY = a.LeftPawY + (b.LeftPawY - a.LeftPawY) * t,
            LeftPawZ = a.LeftPawZ + (b.LeftPawZ - a.LeftPawZ) * t,
            RightPawY = a.RightPawY + (b.RightPawY - a.RightPawY) * t,
            RightPawZ = a.RightPawZ + (b.RightPawZ - a.RightPawZ) * t,
            LeftArmRoll = a.LeftArmRoll + (b.LeftArmRoll - a.LeftArmRoll) * t,
            LeftArmPitch = a.LeftArmPitch + (b.LeftArmPitch - a.LeftArmPitch) * t,
            RightArmRoll = a.RightArmRoll + (b.RightArmRoll - a.RightArmRoll) * t,
            RightArmPitch = a.RightArmPitch + (b.RightArmPitch - a.RightArmPitch) * t,
            EmissiveBoost = a.EmissiveBoost + (b.EmissiveBoost - a.EmissiveBoost) * t
        };
    }
}

/// <summary>
/// 캐릭터 종별 움직임 성향·진폭·모션 클립 순환 시퀀스 프로파일.
/// </summary>
internal sealed record SpeciesMotionProfile(
    double BounceAmplitude,
    double SquashFactor,
    double SwayAmplitude,
    double HeadBobScale,
    double PawDanceScale,
    double ArmSwingScale,
    double EarScale,
    bool OutwardFlopEars,
    double TailAmplitude,
    double TailSpeedMultiplier,
    double FloatBaseAmplitude,
    double TurnAmplitude,
    PetMotionClipKind[] DanceCycle)
{
    public static readonly SpeciesMotionProfile Mochi = new(
        BounceAmplitude: 0.135,
        SquashFactor: 1.45,
        SwayAmplitude: 8.5,
        HeadBobScale: 0.90,
        PawDanceScale: 0.0,
        ArmSwingScale: 1.35,
        EarScale: 0.0,
        OutwardFlopEars: false,
        TailAmplitude: 0.0,
        TailSpeedMultiplier: 1.0,
        FloatBaseAmplitude: 0.028,
        TurnAmplitude: 14.0,
        DanceCycle:
        [
            PetMotionClipKind.Bounce,
            PetMotionClipKind.Groove,
            PetMotionClipKind.HappyJump,
            PetMotionClipKind.HeadBob,
            PetMotionClipKind.Turn
        ]);

    public static readonly SpeciesMotionProfile Cat = new(
        BounceAmplitude: 0.105,
        SquashFactor: 1.0,
        SwayAmplitude: 7.5,
        HeadBobScale: 1.22,
        PawDanceScale: 1.35,
        ArmSwingScale: 0.0,
        EarScale: 1.30,
        OutwardFlopEars: false,
        TailAmplitude: 18.0,
        TailSpeedMultiplier: 1.05,
        FloatBaseAmplitude: 0.020,
        TurnAmplitude: 18.0,
        DanceCycle:
        [
            PetMotionClipKind.Walk,
            PetMotionClipKind.PawDance,
            PetMotionClipKind.HeadBob,
            PetMotionClipKind.Groove,
            PetMotionClipKind.Turn,
            PetMotionClipKind.Bounce,
            PetMotionClipKind.HappyJump
        ]);

    public static readonly SpeciesMotionProfile Puppy = new(
        BounceAmplitude: 0.100,
        SquashFactor: 0.95,
        SwayAmplitude: 7.2,
        HeadBobScale: 1.05,
        PawDanceScale: 1.15,
        ArmSwingScale: 0.0,
        EarScale: 1.35,
        OutwardFlopEars: true,
        TailAmplitude: 24.0,
        TailSpeedMultiplier: 1.95,
        FloatBaseAmplitude: 0.022,
        TurnAmplitude: 16.0,
        DanceCycle:
        [
            PetMotionClipKind.Walk,
            PetMotionClipKind.Bounce,
            PetMotionClipKind.PawDance,
            PetMotionClipKind.Turn,
            PetMotionClipKind.Groove,
            PetMotionClipKind.HappyJump,
            PetMotionClipKind.HeadBob
        ]);

    public static readonly SpeciesMotionProfile Cloud = new(
        BounceAmplitude: 0.115,
        SquashFactor: 0.68,
        SwayAmplitude: 8.8,
        HeadBobScale: 0.85,
        PawDanceScale: 0.0,
        ArmSwingScale: 1.50,
        EarScale: 0.0,
        OutwardFlopEars: false,
        TailAmplitude: 0.0,
        TailSpeedMultiplier: 1.0,
        FloatBaseAmplitude: 0.135,
        TurnAmplitude: 22.0,
        DanceCycle:
        [
            PetMotionClipKind.Groove,
            PetMotionClipKind.Turn,
            PetMotionClipKind.HeadBob,
            PetMotionClipKind.Bounce,
            PetMotionClipKind.HappyJump
        ]);

    public static readonly SpeciesMotionProfile ChiikawaTrio = new(
        BounceAmplitude: 0.125,
        SquashFactor: 1.25,
        SwayAmplitude: 8.2,
        HeadBobScale: 1.15,
        PawDanceScale: 0.0,
        ArmSwingScale: 1.45,
        EarScale: 1.28,
        OutwardFlopEars: false,
        TailAmplitude: 16.0,
        TailSpeedMultiplier: 1.35,
        FloatBaseAmplitude: 0.025,
        TurnAmplitude: 17.5,
        DanceCycle:
        [
            PetMotionClipKind.Walk,
            PetMotionClipKind.Bounce,
            PetMotionClipKind.Groove,
            PetMotionClipKind.HeadBob,
            PetMotionClipKind.HappyJump,
            PetMotionClipKind.Turn
        ]);

    private static readonly SpeciesMotionProfile Shy = ChiikawaTrio with
    { BounceAmplitude = 0.075, ArmSwingScale = 0.78, SwayAmplitude = 5.5, TurnAmplitude = 10 };
    private static readonly SpeciesMotionProfile Rabbit = ChiikawaTrio with
    { BounceAmplitude = 0.155, EarScale = 1.55, ArmSwingScale = 1.65, TurnAmplitude = 21 };

    public static SpeciesMotionProfile ForSpecies(string? speciesId) => speciesId switch
    {
        PetSpeciesIds.ChibiCat => Cat,
        PetSpeciesIds.FluffyPuppy => Puppy,
        PetSpeciesIds.FluffyCloud => Cloud,
        PetSpeciesIds.Joey or PetSpeciesIds.Hachiware => ChiikawaTrio,
        PetSpeciesIds.Chiikawa => Shy,
        PetSpeciesIds.Usagi => Rabbit,
        _ => Mochi
    };
}

/// <summary>
/// 8종 모션 클립 평가기 및 보간 곡선 유틸리티.
/// </summary>
internal static class PetMotionClips
{
    public static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    public static double EaseInOutCubic(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t < 0.5
            ? 4.0 * t * t * t
            : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) * 0.5;
    }

    public static PetPose EvaluateClip(
        PetMotionClipKind clip,
        SpeciesMotionProfile profile,
        double dancePhase,
        double tailPhase,
        double elapsedSeconds,
        double bass,
        double mid,
        double treble,
        double beat)
    {
        var groove = Math.Clamp(bass * 0.55 + mid * 0.45, 0.0, 1.0);
        var sin1 = Math.Sin(dancePhase);
        var cos1 = Math.Cos(dancePhase);
        var sin2 = Math.Sin(dancePhase * 2.0);
        var absSin = Math.Abs(sin1);

        var pose = PetPose.Identity;

        // 종별 기본 꼬리·귀 반응 (모든 댄스 클립에서 일관되게 살아 움직임)
        if (profile.TailAmplitude > 0)
        {
            var tailSpeedPhase = tailPhase * profile.TailSpeedMultiplier;
            pose.TailAngle = Math.Sin(tailSpeedPhase) * (profile.TailAmplitude * 0.55 + groove * profile.TailAmplitude * 0.65);
        }

        if (profile.EarScale > 0)
        {
            var earBaseMotion = Math.Sin(tailPhase * 1.15) * (2.2 + treble * 6.5) * profile.EarScale + beat * 5.0 * profile.EarScale;
            if (profile.OutwardFlopEars)
            {
                var flop = Math.Abs(earBaseMotion);
                pose.LeftEarOffset = -flop;
                pose.RightEarOffset = flop;
            }
            else
            {
                pose.LeftEarOffset = earBaseMotion;
                pose.RightEarOffset = -earBaseMotion;
            }
        }

        switch (clip)
        {
            case PetMotionClipKind.Idle:
            {
                var breathe = Math.Sin(elapsedSeconds * 2.05);
                var gentleShift = Math.Sin(elapsedSeconds * 0.95);
                pose.BodyOffsetY = Math.Sin(elapsedSeconds * 2.05 - 0.3) * profile.FloatBaseAmplitude;
                pose.BodyScaleY = 1.0 + breathe * 0.025 * profile.SquashFactor;
                pose.BodyRoll = gentleShift * 1.6;
                pose.HeadRoll = -gentleShift * 1.8;
                pose.HeadPitch = Math.Sin(elapsedSeconds * 1.4) * 1.6;
                pose.LeftArmRoll = -4.0 - Math.Sin(elapsedSeconds * 2.05) * 3.5 * profile.ArmSwingScale;
                pose.RightArmRoll = 4.0 + Math.Sin(elapsedSeconds * 2.05) * 3.5 * profile.ArmSwingScale;
                break;
            }

            case PetMotionClipKind.Groove:
            {
                pose.BodyOffsetX = sin1 * (0.045 + mid * 0.085);
                pose.BodyOffsetY = absSin * (0.025 + bass * profile.BounceAmplitude * 0.65)
                    + (profile == SpeciesMotionProfile.Cloud ? Math.Sin(elapsedSeconds * 2.1) * 0.08 : 0.0);
                pose.BodyScaleY = 1.0 + Math.Sin(dancePhase * 2.0 - 0.35) * (0.028 + bass * 0.055) * profile.SquashFactor;
                pose.BodyRoll = sin1 * (3.0 + mid * profile.SwayAmplitude);
                pose.HeadRoll = -sin1 * (2.5 + mid * 5.5) + cos1 * 2.0;
                pose.HeadPitch = sin2 * (2.0 + bass * 4.5) * profile.HeadBobScale;

                var armSwing = sin1 * (10.0 + groove * 16.0) * profile.ArmSwingScale;
                pose.LeftArmRoll = -armSwing;
                pose.RightArmRoll = armSwing;
                pose.LeftPawY = Math.Max(0.0, sin1) * (0.04 + groove * 0.06) * profile.PawDanceScale;
                pose.RightPawY = Math.Max(0.0, -sin1) * (0.04 + groove * 0.06) * profile.PawDanceScale;
                pose.EmissiveBoost = beat * 0.03;
                break;
            }

            case PetMotionClipKind.Bounce:
            {
                // 탄성 바운스: 바닥에서 눌렸다(squash)가 위로 튀어 오름(stretch)
                var spring = Math.Pow(absSin, 0.78);
                var preSquash = Math.Cos(dancePhase * 2.0);
                pose.BodyOffsetY = spring * (0.05 + bass * profile.BounceAmplitude + beat * 0.05);
                pose.BodyScaleY = 1.0 - preSquash * (0.045 + bass * 0.085) * profile.SquashFactor + beat * 0.04 * profile.SquashFactor;
                pose.BodyRoll = sin1 * (2.0 + groove * 4.5);
                pose.BodyPitch = Math.Sin(dancePhase * 2.0) * (1.8 + bass * 3.5);
                pose.HeadPitch = Math.Sin(dancePhase * 2.0 - 0.4) * (2.5 + bass * 5.5) * profile.HeadBobScale;
                pose.HeadRoll = sin1 * 3.5;

                var lift = spring * (12.0 + bass * 18.0) * profile.ArmSwingScale;
                pose.LeftArmRoll = -lift;
                pose.RightArmRoll = lift;
                pose.LeftPawY = spring * 0.05 * profile.PawDanceScale;
                pose.RightPawY = spring * 0.05 * profile.PawDanceScale;
                pose.EmissiveBoost = bass * 0.04 + beat * 0.04;
                break;
            }

            case PetMotionClipKind.Walk:
                pose.StepWeight = 1;
                pose.BodyOffsetX = sin1 * 0.055;
                pose.BodyRoll = sin1 * 4;
                pose.HeadRoll = -sin1 * 2;
                pose.LeftArmPitch = sin1 * 16;
                pose.RightArmPitch = -pose.LeftArmPitch;
                pose.LeftPawZ = Math.Max(0, sin1) * 0.12;
                pose.RightPawZ = Math.Max(0, -sin1) * 0.12;
                break;

            case PetMotionClipKind.HeadBob:
            {
                var nod = Math.Sin(dancePhase * 2.0);
                var doubleNod = Math.Sin(dancePhase * 4.0) * 0.25;
                pose.BodyOffsetY = Math.Max(0.0, nod) * (0.025 + bass * 0.06);
                pose.BodyScaleY = 1.0 + Math.Cos(dancePhase * 2.0) * (0.02 + groove * 0.04) * profile.SquashFactor;
                pose.BodyRoll = sin1 * 2.8;
                pose.HeadPitch = (nod * (4.5 + (bass + mid) * 7.5) + doubleNod * 3.0) * profile.HeadBobScale;
                pose.HeadRoll = sin1 * (3.5 + treble * 5.0);
                pose.HeadYaw = cos1 * 4.5;

                var tapAlt = Math.Sin(dancePhase * 2.0);
                pose.LeftArmPitch = Math.Max(0.0, tapAlt) * 14.0 * profile.ArmSwingScale;
                pose.RightArmPitch = Math.Max(0.0, -tapAlt) * 14.0 * profile.ArmSwingScale;
                pose.LeftPawY = Math.Max(0.0, tapAlt) * 0.05 * profile.PawDanceScale;
                pose.RightPawY = Math.Max(0.0, -tapAlt) * 0.05 * profile.PawDanceScale;
                break;
            }

            case PetMotionClipKind.PawDance:
            {
                // 앞발/팔 좌우 교대 리듬 댄스 (고양이·강아지·모찌 시그니처)
                var alt = Math.Sin(dancePhase * 1.5);
                var leftUp = Math.Max(0.0, alt);
                var rightUp = Math.Max(0.0, -alt);

                pose.BodyOffsetX = alt * 0.035;
                pose.BodyOffsetY = Math.Abs(alt) * (0.035 + groove * 0.075);
                pose.BodyScaleY = 1.0 + Math.Sin(dancePhase * 3.0) * 0.035 * profile.SquashFactor;
                pose.BodyRoll = alt * (4.0 + groove * 6.5);
                pose.HeadRoll = -alt * 5.5;
                pose.HeadPitch = Math.Sin(dancePhase * 3.0) * 3.5;

                pose.LeftPawY = leftUp * (0.08 + groove * 0.11) * Math.Max(0.5, profile.PawDanceScale);
                pose.LeftPawZ = leftUp * 0.10 * Math.Max(0.5, profile.PawDanceScale);
                pose.RightPawY = rightUp * (0.08 + groove * 0.11) * Math.Max(0.5, profile.PawDanceScale);
                pose.RightPawZ = rightUp * 0.10 * Math.Max(0.5, profile.PawDanceScale);

                pose.LeftArmRoll = (leftUp * 26.0 - 6.0) * profile.ArmSwingScale;
                pose.RightArmRoll = (-rightUp * 26.0 + 6.0) * profile.ArmSwingScale;
                pose.LeftArmPitch = leftUp * 20.0 * profile.ArmSwingScale;
                pose.RightArmPitch = rightUp * 20.0 * profile.ArmSwingScale;
                pose.EmissiveBoost = beat * 0.04;
                break;
            }

            case PetMotionClipKind.Turn:
            {
                // 좌우 부드러운 3D 회전으로 입체 피규어 실루엣 표현
                var turnWave = Math.Sin(dancePhase * 0.75);
                var smoothedTurn = Math.Sign(turnWave) * EaseInOutCubic(Math.Abs(turnWave));

                pose.BodyYaw = smoothedTurn * (profile.TurnAmplitude * (0.65 + mid * 0.45));
                pose.HeadYaw = smoothedTurn * -7.5; // 몸이 돌 때 정면 시선 쪽으로 고개 살짝 보상
                pose.BodyOffsetY = absSin * (0.03 + bass * 0.07)
                    + (profile == SpeciesMotionProfile.Cloud ? Math.Sin(elapsedSeconds * 2.0) * 0.09 : 0.0);
                pose.BodyScaleY = 1.0 + Math.Sin(dancePhase * 1.5) * 0.03 * profile.SquashFactor;
                pose.BodyRoll = smoothedTurn * 4.5;
                pose.HeadRoll = smoothedTurn * -4.0;
                pose.LeftArmRoll = -smoothedTurn * 14.0 * profile.ArmSwingScale;
                pose.RightArmRoll = -smoothedTurn * 14.0 * profile.ArmSwingScale;
                break;
            }

            case PetMotionClipKind.HappyJump:
            {
                var cycle = (dancePhase * 0.7 / Math.PI) % 1;
                var flight = Math.Clamp((cycle - 0.19) / 0.60, 0, 1);
                var jumpArc = Math.Sin(flight * Math.PI);
                var crouch = cycle < 0.19 ? Math.Sin(cycle / 0.19 * Math.PI)
                    : cycle > 0.79 ? Math.Sin((cycle - 0.79) / 0.21 * Math.PI) : 0;
                pose.BodyOffsetY = jumpArc * (0.14 + bass * profile.BounceAmplitude + beat * 0.08) - crouch * 0.025;
                pose.BodyScaleY = 1 + (jumpArc * 0.085 - crouch * 0.09) * profile.SquashFactor;
                pose.BodyRoll = Math.Sin(dancePhase * 0.7) * 6.5;
                pose.HeadPitch = -jumpArc * 7.0;
                pose.HeadRoll = Math.Cos(dancePhase * 1.4) * 5.0;
                pose.LeftArmRoll = jumpArc * 24.0 * profile.ArmSwingScale;
                pose.RightArmRoll = -jumpArc * 24.0 * profile.ArmSwingScale;
                pose.LeftPawY = jumpArc * 0.09 * profile.PawDanceScale;
                pose.RightPawY = jumpArc * 0.09 * profile.PawDanceScale;
                pose.TailAngle += Math.Sin(dancePhase * 4.0) * 8.0;
                pose.EmissiveBoost = 0.05 + jumpArc * 0.06;
                break;
            }

            case PetMotionClipKind.Reaction:
            default:
            {
                // 반응 클립 기본 곡선 (실제 타이머 진행률은 PetAnimationController에서 제어)
                pose.BodyScaleY = 1.08;
                pose.BodyOffsetY = 0.12;
                pose.EmissiveBoost = 0.12;
                break;
            }
        }

        return pose;
    }
}
