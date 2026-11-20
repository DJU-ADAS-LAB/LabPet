using System.Windows.Media;
using LabPet.Shared.Models;

namespace LabPet.Client.Rendering;

/// <summary>
/// 60fps UI 타이머에서 호출되어 3D 리그의 Transform3D와 EmissiveMaterial만 저부하 갱신하는 컨트롤러.
/// 시선 추적(Look-At), 대기 호흡/부유, 쓰다듬기/어지러움 반응,
/// 캐릭터별 부드러운 오디오 리듬 댄스 및 타이핑 앞발 모션을 담당한다.
/// </summary>
public sealed class PetAnimationController
{
    private sealed class ParticleState
    {
        public bool Active;
        public double Age;
        public double Lifetime = 1.1;
        public double StartX;
        public double StartY;
        public double StartZ;
        public double VelX;
        public double VelY;
        public double VelZ;
    }

    private PetRigHandle? _rig;
    private double _elapsedSeconds;

    // 위상 점프(떨림) 없이 속도가 부드럽게 변하도록 누적 위상 사용
    private double _dancePhase;
    private double _tailPhase;
    private double _auraPhase;

    // 자연스러운 자동 눈 깜빡임 상태
    private double _nextBlinkInSeconds = 2.6;
    private double _blinkProgress = -1.0;
    private readonly Random _rng = new();

    // 보간된 시선 추적 값 (-1.0 ~ +1.0)
    private double _currentLookX;
    private double _currentLookY;

    // 상호작용 반응 잔여 시간 (초)
    private double _petReactionTime;
    private double _dizzyReactionTime;

    // 비트 펄스 및 치비 고양이 키보드 타이핑 앞발 모션 상태
    private double _beatPulse;
    private double _leftPawTapTime;
    private double _rightPawTapTime;
    private int _nextPawParity;
    private int _pendingPawTaps;

    // 스레드 안전 오디오 대역 목표값 + 부드러운 보간값
    private volatile float _targetBass;
    private volatile float _targetMid;
    private volatile float _targetTreble;
    private volatile bool _pendingBeat;
    private double _smoothBass;
    private double _smoothMid;
    private double _smoothTreble;

    // 솜구름 풀링 3D 음표/별 파티클 상태
    private ParticleState[] _particleStates = [];
    private int _particleSpawnCursor;
    private double _particleEmitCooldown;

    private Color _emissiveAccentColor = Color.FromRgb(255, 158, 181);

    public void AttachRig(PetRigHandle rig, string accentHex)
    {
        _rig = rig;
        _particleStates = new ParticleState[rig.MusicParticles.Count];
        for (var i = 0; i < _particleStates.Length; i++)
        {
            _particleStates[i] = new ParticleState();
        }

        try
        {
            if (ColorConverter.ConvertFromString(accentHex) is Color c)
            {
                _emissiveAccentColor = c;
            }
        }
        catch (FormatException)
        {
            _emissiveAccentColor = Color.FromRgb(255, 158, 181);
        }
    }

    /// <summary>쓰다듬기 3D 반응(부피 보존형 점프 + 발광)을 트리거한다.</summary>
    public void TriggerPetReaction() => _petReactionTime = 0.85;

    /// <summary>창을 빠르게 좌우로 흔들었을 때 어지러움 3D 반응을 트리거한다.</summary>
    public void TriggerDizzyReaction() => _dizzyReactionTime = 1.4;

    /// <summary>키보드 타이핑 시 치비 고양이 앞발 두드리기 모션을 스레드 안전하게 요청한다.</summary>
    public void TriggerTypingPawTap() => Interlocked.Increment(ref _pendingPawTaps);

    /// <summary>오디오/네트워크 스냅샷을 갱신한다.</summary>
    public void UpdateAudioSnapshot(float bass, float mid, float treble, bool isBeat = false)
    {
        _targetBass = Math.Clamp(bass, 0f, 1f);
        _targetMid = Math.Clamp(mid, 0f, 1f);
        _targetTreble = Math.Clamp(treble, 0f, 1f);
        if (isBeat)
        {
            _pendingBeat = true;
        }
    }

    public void UpdateFrame(double deltaSeconds, double targetLookX, double targetLookY)
    {
        var rig = _rig;
        if (rig is null)
        {
            return;
        }

        deltaSeconds = Math.Clamp(deltaSeconds, 0.001, 0.05);
        _elapsedSeconds += deltaSeconds;

        // 부드러운 시선 추적 보간
        var lookLerp = Math.Min(1.0, deltaSeconds * 7.5);
        _currentLookX += (Math.Clamp(targetLookX, -1.0, 1.0) - _currentLookX) * lookLerp;
        _currentLookY += (Math.Clamp(targetLookY, -1.0, 1.0) - _currentLookY) * lookLerp;

        // 오디오 대역 부드러운 어택/릴리스 보간 (급격한 튐 방지)
        var audioLerp = Math.Min(1.0, deltaSeconds * 9.5);
        _smoothBass += (_targetBass - _smoothBass) * audioLerp;
        _smoothMid += (_targetMid - _smoothMid) * audioLerp;
        _smoothTreble += (_targetTreble - _smoothTreble) * audioLerp;

        // 비트 펄스 부드러운 감쇠
        if (_pendingBeat)
        {
            _pendingBeat = false;
            _beatPulse = 1.0;
        }
        else
        {
            _beatPulse = Math.Max(0.0, _beatPulse - deltaSeconds * 4.2);
        }

        // 키보드 타이핑 앞발 요청 소비
        var taps = Interlocked.Exchange(ref _pendingPawTaps, 0);
        if (taps > 0)
        {
            if ((_nextPawParity++ & 1) == 0)
            {
                _leftPawTapTime = 0.16;
            }
            else
            {
                _rightPawTapTime = 0.16;
            }
        }

        var bass = _smoothBass;
        var mid = _smoothMid;
        var treble = _smoothTreble;
        var beat = _beatPulse;
        var groove = Math.Clamp((bass + mid) * 0.65, 0.0, 1.0);

        // 누적 위상 갱신 (속도가 변해도 위상 점프 없이 매끄럽게 춤춤)
        _dancePhase += deltaSeconds * (2.2 + groove * 2.4);
        _tailPhase += deltaSeconds * (3.2 + groove * 2.8);
        // 오라는 작업 중 시선을 방해하지 않도록 초당 12~22도로 차분하게 회전
        _auraPhase = (_auraPhase + deltaSeconds * (12.0 + treble * 10.0)) % 360.0;

        double scaleY = 1.0;
        double offsetY = 0.0;
        double bodyRoll = 0.0;
        double headPitchExtra = 0.0;
        double headRollExtra = 0.0;
        double emissiveStrength = bass * 0.05 + beat * 0.05;

        switch (rig.SpeciesId)
        {
            case PetSpeciesIds.FluffyCloud:
            {
                // 솜구름: 부드러운 사인파 공중 부유
                offsetY = Math.Sin(_elapsedSeconds * 2.1) * 0.14 + bass * 0.15 + beat * 0.12;
                scaleY = 1.0 + Math.Sin(_elapsedSeconds * 2.1 + 0.5) * 0.03 + bass * 0.08 + beat * 0.08;
                bodyRoll = Math.Sin(_elapsedSeconds * 1.3) * (3.5 + mid * 7.5);
                break;
            }

            case PetSpeciesIds.ChibiCat:
            {
                // 치비 고양이: 구름처럼 부드러운 좌우 리듬 갸우뚱 + 통통 바운스 + 귀 쫑긋 + 매끄러운 꼬리 스윙
                var sway = Math.Sin(_dancePhase);
                var bounce = Math.Abs(Math.Sin(_dancePhase));

                offsetY = bounce * (0.04 + groove * 0.11) + beat * 0.07;
                scaleY = 1.0 + Math.Sin(_dancePhase * 2.0) * (0.02 + groove * 0.045) + beat * 0.045;
                bodyRoll = sway * (2.5 + groove * 7.5);

                // 머리는 몸통 리듬을 자연스럽게 따라가며 살짝 까딱까딱
                headRollExtra = sway * (2.0 + groove * 5.0);
                headPitchExtra = Math.Sin(_dancePhase * 2.0) * (1.8 + groove * 4.5);

                if (rig.LeftEarRotation is not null && rig.RightEarRotation is not null)
                {
                    var earTwitch = Math.Sin(_tailPhase * 1.2) * (2.5 + treble * 7.5) + beat * 6.5;
                    rig.LeftEarRotation.Angle = 15.0 + earTwitch;
                    rig.RightEarRotation.Angle = -15.0 - earTwitch;
                }

                if (rig.TailRotation is not null)
                {
                    rig.TailRotation.Angle = Math.Sin(_tailPhase) * (14.0 + groove * 16.0);
                }

                UpdateCatPaws(rig, deltaSeconds, groove, _dancePhase);
                break;
            }

            default:
            {
                // 말랑 모찌 정령: 구름처럼 귀엽게 좌우로 살랑살랑 흔들며 통통 말랑 바운스 + 은은한 발광
                var sway = Math.Sin(_dancePhase);
                var bounce = Math.Abs(Math.Sin(_dancePhase));

                offsetY = bounce * (0.04 + bass * 0.13) + beat * 0.10;
                scaleY = 1.0 + Math.Sin(_dancePhase * 2.0 - 0.4) * (0.035 + bass * 0.065) + beat * 0.06;
                bodyRoll = sway * (3.0 + groove * 8.5);
                headRollExtra = sway * (1.5 + groove * 3.5);
                emissiveStrength += beat * 0.03;
                break;
            }
        }

        // 3종 캐릭터 공통: 댄스/음악 반응 시 3D 음표·별 파티클 방출
        UpdateMusicParticles(rig, deltaSeconds, bass, treble, beat);

        // 미니 팔/날개 살랑임 (모찌 팔, 솜구름 날개)
        if (rig.LeftArmRotation is not null && rig.RightArmRotation is not null)
        {
            var armSwing = Math.Sin(_dancePhase * 1.4) * (8.0 + groove * 18.0) + (_petReactionTime > 0 ? 18.0 : 0.0);
            rig.LeftArmRotation.Angle = -armSwing;
            rig.RightArmRotation.Angle = armSwing;
        }

        // 자연스러운 자동 눈 깜빡임 (2.4~5.2초 주기, 0.15초 지속)
        UpdateAutoBlink(rig, deltaSeconds);

        // 쓰다듬기 반응
        if (_petReactionTime > 0)
        {
            _petReactionTime = Math.Max(0, _petReactionTime - deltaSeconds);
            var t = 1.0 - (_petReactionTime / 0.85);
            var bounce = Math.Sin(t * Math.PI * 2.5) * Math.Exp(-t * 2.2);
            scaleY += bounce * 0.20;
            offsetY += Math.Max(0, Math.Sin(t * Math.PI)) * 0.24;
            headRollExtra += Math.Sin(t * Math.PI * 3.0) * 9.0;
            emissiveStrength = Math.Max(emissiveStrength, (1.0 - t) * 0.16);
        }

        // 어지러움(빠른 흔들기) 반응
        var dizzyYaw = 0.0;
        if (_dizzyReactionTime > 0)
        {
            _dizzyReactionTime = Math.Max(0, _dizzyReactionTime - deltaSeconds);
            var decay = _dizzyReactionTime / 1.4;
            bodyRoll += Math.Sin(_elapsedSeconds * 26.0) * 18.0 * decay;
            dizzyYaw = Math.Sin(_elapsedSeconds * 16.0) * 24.0 * decay;
            headRollExtra += Math.Cos(_elapsedSeconds * 22.0) * 14.0 * decay;
        }

        // 부피 보존형 Squash & Stretch: X/Z는 Y의 역제곱근으로 반비례 팽창/수축 (과도한 찌그러짐 방지)
        scaleY = Math.Clamp(scaleY, 0.82, 1.24);
        var scaleXZ = 1.0 / Math.Sqrt(scaleY);

        rig.BodyScale.ScaleX = scaleXZ;
        rig.BodyScale.ScaleY = scaleY;
        rig.BodyScale.ScaleZ = scaleXZ;

        rig.BodyTranslate.OffsetY = offsetY;
        rig.BodyYaw.Angle = _currentLookX * 14.0 + dizzyYaw;
        rig.BodyPitch.Angle = _currentLookY * 8.0;
        rig.BodyRoll.Angle = bodyRoll;

        // 바닥 접지 그림자: 펫이 높게 뜰수록 은은하게 작아져 공간감 강화
        if (rig.ShadowScale is not null)
        {
            var shadowFactor = Math.Clamp(1.0 - offsetY * 0.42, 0.68, 1.08);
            rig.ShadowScale.ScaleX = shadowFactor;
            rig.ShadowScale.ScaleZ = shadowFactor;
        }

        rig.HeadYaw.Angle = _currentLookX * 18.0;
        rig.HeadPitch.Angle = _currentLookY * 12.0 + headPitchExtra;
        rig.HeadRoll.Angle = headRollExtra;

        if (rig.AuraRotation is not null)
        {
            rig.AuraRotation.Angle = _auraPhase;
        }

        if (rig.AuraTranslate is not null)
        {
            rig.AuraTranslate.OffsetY = Math.Sin(_elapsedSeconds * 1.2) * 0.035;
        }

        // 파스텔 바디 색감과 3D 음영이 하얗게 날아가지 않도록 발광 상한을 은은하게 제한
        emissiveStrength = Math.Clamp(emissiveStrength, 0.0, 0.16);
        var r = (byte)(_emissiveAccentColor.R * emissiveStrength);
        var g = (byte)(_emissiveAccentColor.G * emissiveStrength);
        var b = (byte)(_emissiveAccentColor.B * emissiveStrength);
        rig.BodyEmissiveBrush.Color = Color.FromRgb(r, g, b);
    }

    private void UpdateAutoBlink(PetRigHandle rig, double dt)
    {
        if (rig.EyeBlinkScale is null)
        {
            return;
        }

        if (_blinkProgress < 0)
        {
            _nextBlinkInSeconds -= dt;
            if (_nextBlinkInSeconds <= 0)
            {
                _blinkProgress = 0.0;
                _nextBlinkInSeconds = 2.4 + _rng.NextDouble() * 3.0;
            }
            else
            {
                rig.EyeBlinkScale.ScaleY = 1.0;
            }

            return;
        }

        const double blinkDuration = 0.15;
        _blinkProgress += dt / blinkDuration;
        if (_blinkProgress >= 1.0)
        {
            _blinkProgress = -1.0;
            rig.EyeBlinkScale.ScaleY = 1.0;
        }
        else
        {
            // 닫혔다 부드럽게 열림 (1.0 -> 0.08 -> 1.0)
            var closeFactor = Math.Sin(_blinkProgress * Math.PI);
            rig.EyeBlinkScale.ScaleY = 1.0 - closeFactor * 0.92;
        }
    }

    private void UpdateCatPaws(PetRigHandle rig, double dt, double groove, double dancePhase)
    {
        if (rig.LeftPawTranslate is null || rig.RightPawTranslate is null)
        {
            return;
        }

        _leftPawTapTime = Math.Max(0, _leftPawTapTime - dt);
        _rightPawTapTime = Math.Max(0, _rightPawTapTime - dt);

        var leftTap = _leftPawTapTime > 0 ? Math.Sin((_leftPawTapTime / 0.16) * Math.PI) * 0.17 : 0.0;
        var rightTap = _rightPawTapTime > 0 ? Math.Sin((_rightPawTapTime / 0.16) * Math.PI) * 0.17 : 0.0;

        // 리듬 댄스 중 앞발도 좌우 교대로 귀엽게 살짝 들썩임
        var leftDance = Math.Max(0.0, Math.Sin(dancePhase)) * groove * 0.08;
        var rightDance = Math.Max(0.0, -Math.Sin(dancePhase)) * groove * 0.08;

        var leftLift = Math.Max(leftTap, leftDance);
        var rightLift = Math.Max(rightTap, rightDance);

        rig.LeftPawTranslate.OffsetY = leftLift;
        rig.LeftPawTranslate.OffsetZ = leftLift * 0.45;
        rig.RightPawTranslate.OffsetY = rightLift;
        rig.RightPawTranslate.OffsetZ = rightLift * 0.45;
    }

    private void UpdateMusicParticles(PetRigHandle rig, double dt, double bass, double treble, double beat)
    {
        if (rig.MusicParticles.Count == 0 || _particleStates.Length != rig.MusicParticles.Count)
        {
            return;
        }

        _particleEmitCooldown = Math.Max(0, _particleEmitCooldown - dt);
        var energy = (bass + treble) * 0.5;
        if ((beat > 0.7 || energy > 0.24) && _particleEmitCooldown <= 0)
        {
            var idx = _particleSpawnCursor++ % _particleStates.Length;
            var angle = (_elapsedSeconds * 2.3 + idx * 1.1) % (Math.PI * 2.0);
            var st = _particleStates[idx];
            st.Active = true;
            st.Age = 0;
            st.Lifetime = 1.05;
            st.StartX = Math.Cos(angle) * 0.65;
            st.StartY = 0.25;
            st.StartZ = Math.Sin(angle) * 0.45;
            st.VelX = Math.Cos(angle) * 0.65;
            st.VelY = 0.95 + energy * 0.55;
            st.VelZ = Math.Sin(angle) * 0.45;
            _particleEmitCooldown = 0.22;
        }

        for (var i = 0; i < _particleStates.Length; i++)
        {
            var st = _particleStates[i];
            var handle = rig.MusicParticles[i];
            if (!st.Active)
            {
                handle.Scale.ScaleX = 0;
                handle.Scale.ScaleY = 0;
                handle.Scale.ScaleZ = 0;
                continue;
            }

            st.Age += dt;
            if (st.Age >= st.Lifetime)
            {
                st.Active = false;
                handle.Scale.ScaleX = 0;
                handle.Scale.ScaleY = 0;
                handle.Scale.ScaleZ = 0;
                continue;
            }

            var progress = st.Age / st.Lifetime;
            var scale = Math.Sin(progress * Math.PI) * 0.95;
            handle.Scale.ScaleX = scale;
            handle.Scale.ScaleY = scale;
            handle.Scale.ScaleZ = scale;

            handle.Translate.OffsetX = st.StartX + st.VelX * st.Age;
            handle.Translate.OffsetY = st.StartY + st.VelY * st.Age;
            handle.Translate.OffsetZ = st.StartZ + st.VelZ * st.Age;
            handle.Rotation.Angle = (st.Age * 180.0) % 360.0;
        }
    }
}
