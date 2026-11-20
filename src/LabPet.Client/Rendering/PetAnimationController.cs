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

    // 비트 펄스 및 3종 캐릭터 키보드 타이핑 자세·타건 상태
    private double _beatPulse;
    private double _leftPawTapTime;
    private double _rightPawTapTime;
    private double _typingStanceHoldTime;
    private double _typingStanceWeight;
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

        // 키보드 타이핑 요청 소비 (3종 캐릭터 공통 미니 키보드 자세 + 좌/우 교대 타건)
        var taps = Interlocked.Exchange(ref _pendingPawTaps, 0);
        if (taps > 0)
        {
            _typingStanceHoldTime = 0.95;
            if ((_nextPawParity++ & 1) == 0)
            {
                _leftPawTapTime = 0.17;
            }
            else
            {
                _rightPawTapTime = 0.17;
            }
        }

        _typingStanceHoldTime = Math.Max(0, _typingStanceHoldTime - deltaSeconds);
        var stanceTarget = _typingStanceHoldTime > 0 ? 1.0 : 0.0;
        var stanceSpeed = stanceTarget > _typingStanceWeight ? 14.0 : 5.5;
        _typingStanceWeight += (stanceTarget - _typingStanceWeight) * Math.Min(1.0, deltaSeconds * stanceSpeed);
        var stance = _typingStanceWeight;

        _leftPawTapTime = Math.Max(0, _leftPawTapTime - deltaSeconds);
        _rightPawTapTime = Math.Max(0, _rightPawTapTime - deltaSeconds);

        // 타건 곡선: 살짝 들어 올렸다 키보드를 콕 내려찍는 실제 타자 모션
        var leftTapWave = _leftPawTapTime > 0 ? Math.Sin((_leftPawTapTime / 0.17) * Math.PI) : 0.0;
        var rightTapWave = _rightPawTapTime > 0 ? Math.Sin((_rightPawTapTime / 0.17) * Math.PI) : 0.0;
        var leftStrike = ComputeKeyStrikeOffset(_leftPawTapTime, 0.17);
        var rightStrike = ComputeKeyStrikeOffset(_rightPawTapTime, 0.17);
        var anyTapWave = Math.Max(leftTapWave, rightTapWave);
        var signedTap = rightTapWave - leftTapWave; // 왼쪽 타자: -1, 오른쪽 타자: +1

        // 3D 미니 키보드 팝인/팝아웃
        if (rig.TypingKeyboardScale is not null)
        {
            var kbScale = stance < 0.01 ? 0.0 : stance;
            rig.TypingKeyboardScale.ScaleX = kbScale;
            rig.TypingKeyboardScale.ScaleY = kbScale;
            rig.TypingKeyboardScale.ScaleZ = kbScale;
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
                // 솜구름: 부드러운 사인파 공중 부유 + 타이핑 자세 시 키보드 쪽 집중 & 날개 타닥타닥
                offsetY = Math.Sin(_elapsedSeconds * 2.1) * 0.14 * (1.0 - stance * 0.55) + bass * 0.15 + beat * 0.12 - stance * 0.06 + anyTapWave * 0.05;
                scaleY = 1.0 + Math.Sin(_elapsedSeconds * 2.1 + 0.5) * 0.03 + bass * 0.08 + beat * 0.08 - anyTapWave * 0.035;
                bodyRoll = Math.Sin(_elapsedSeconds * 1.3) * (3.5 + mid * 7.5) + signedTap * 5.5;
                headPitchExtra = stance * 10.0 + anyTapWave * 4.5;
                break;
            }

            case PetSpeciesIds.ChibiCat:
            {
                // 치비 고양이: 타이핑 시 미니 키보드 위에서 봉고캣처럼 양 앞발 교대 내려치기
                var sway = Math.Sin(_dancePhase);
                var bounce = Math.Abs(Math.Sin(_dancePhase));

                offsetY = bounce * (0.04 + groove * 0.11) + beat * 0.07 + anyTapWave * 0.025;
                scaleY = 1.0 + Math.Sin(_dancePhase * 2.0) * (0.02 + groove * 0.045) + beat * 0.045 - anyTapWave * 0.03;
                bodyRoll = sway * (2.5 + groove * 7.5) + signedTap * 4.0;

                headRollExtra = sway * (2.0 + groove * 5.0) + signedTap * 3.5;
                headPitchExtra = Math.Sin(_dancePhase * 2.0) * (1.8 + groove * 4.5) + stance * 11.5 + anyTapWave * 5.5;

                if (rig.LeftEarRotation is not null && rig.RightEarRotation is not null)
                {
                    var earTwitch = Math.Sin(_tailPhase * 1.2) * (2.5 + treble * 7.5) + beat * 6.5;
                    rig.LeftEarRotation.Angle = 15.0 + earTwitch + leftTapWave * 8.0;
                    rig.RightEarRotation.Angle = -15.0 - earTwitch - rightTapWave * 8.0;
                }

                if (rig.TailRotation is not null)
                {
                    rig.TailRotation.Angle = Math.Sin(_tailPhase) * (14.0 + groove * 16.0) + signedTap * 7.0;
                }

                UpdateFrontPaws(rig, groove, _dancePhase, stance, leftStrike, rightStrike,
                    stanceLiftY: 0.08, stanceReachZ: 0.28, strikeHeight: 0.16);
                break;
            }

            case PetSpeciesIds.FluffyPuppy:
            {
                // 솜뭉치 강아지: 살랑 바운스 + 신나게 빠른 꼬리 흔들기 + 처진 귀 펄럭임
                // 타이핑 시 짧은 앞다리를 미니 키보드 위로 들어 올려 좌우 교대로 콕콕 두드림
                var sway = Math.Sin(_dancePhase);
                var bounce = Math.Abs(Math.Sin(_dancePhase));

                offsetY = bounce * (0.03 + groove * 0.09) + beat * 0.06 + anyTapWave * 0.02;
                scaleY = 1.0 + Math.Sin(_dancePhase * 2.0) * (0.015 + groove * 0.04) + beat * 0.04 - anyTapWave * 0.025;
                bodyRoll = sway * (2.0 + groove * 6.0) + signedTap * 3.0;

                headRollExtra = sway * (2.5 + groove * 5.0) + signedTap * 3.0;
                headPitchExtra = Math.Sin(_dancePhase * 2.0) * (1.5 + groove * 4.0) + stance * 11.0 + anyTapWave * 5.0;

                if (rig.LeftEarRotation is not null && rig.RightEarRotation is not null)
                {
                    // 처진 귀는 바깥쪽(왼쪽 귀 음수, 오른쪽 귀 양수)으로 펄럭임
                    var earFlop = Math.Abs(Math.Sin(_tailPhase * 0.9)) * (2.0 + treble * 7.0) + beat * 6.0;
                    rig.LeftEarRotation.Angle = -4.0 - earFlop - leftTapWave * 5.0;
                    rig.RightEarRotation.Angle = 4.0 + earFlop + rightTapWave * 5.0;
                }

                if (rig.TailRotation is not null)
                {
                    rig.TailRotation.Angle = Math.Sin(_tailPhase * 2.2) * (18.0 + groove * 14.0) + signedTap * 6.0;
                }

                UpdateFrontPaws(rig, groove, _dancePhase, stance, leftStrike, rightStrike,
                    stanceLiftY: 0.18, stanceReachZ: 0.30, strikeHeight: 0.12);
                break;
            }

            default:
            {
                // 말랑 모찌 정령: 좌우 살랑 바운스 + 타이핑 자세 시 앞쪽 키보드 집중 & 양팔 교대 타건
                var sway = Math.Sin(_dancePhase);
                var bounce = Math.Abs(Math.Sin(_dancePhase));

                offsetY = bounce * (0.04 + bass * 0.13) + beat * 0.10 + anyTapWave * 0.04;
                scaleY = 1.0 + Math.Sin(_dancePhase * 2.0 - 0.4) * (0.035 + bass * 0.065) + beat * 0.06 - anyTapWave * 0.05;
                bodyRoll = sway * (3.0 + groove * 8.5) + signedTap * 5.0;
                headRollExtra = sway * (1.5 + groove * 3.5) + signedTap * 3.5;
                headPitchExtra = stance * 10.5 + anyTapWave * 4.5;
                emissiveStrength += beat * 0.03;
                break;
            }
        }

        // 3종 캐릭터 공통: 댄스/음악 반응 시 3D 음표·별 파티클 방출
        UpdateMusicParticles(rig, deltaSeconds, bass, treble, beat);

        // 미니 팔/날개 살랑임 + 타이핑 시 앞쪽 키보드를 향해 교대로 타닥타닥 내려치기
        if (rig.LeftArmRotation is not null && rig.RightArmRotation is not null)
        {
            var armSwing = Math.Sin(_dancePhase * 1.4) * (8.0 + groove * 18.0) * (1.0 - stance * 0.7) + (_petReactionTime > 0 ? 18.0 : 0.0);
            var stanceInward = stance * 28.0;
            rig.LeftArmRotation.Angle = -armSwing + stanceInward - leftStrike * 26.0;
            rig.RightArmRotation.Angle = armSwing - stanceInward + rightStrike * 26.0;
        }

        if (rig.LeftArmPitch is not null && rig.RightArmPitch is not null)
        {
            // X축 회전으로 팔/날개가 앞쪽 아래 미니 키보드를 향해 콕콕 두드림
            rig.LeftArmPitch.Angle = stance * 24.0 - leftStrike * 34.0;
            rig.RightArmPitch.Angle = stance * 24.0 - rightStrike * 34.0;
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
        rig.BodyYaw.Angle = _currentLookX * 14.0 * (1.0 - stance * 0.65) + dizzyYaw;
        rig.BodyPitch.Angle = _currentLookY * 8.0 * (1.0 - stance * 0.7) + stance * 4.5;
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

    /// <summary>
    /// 키 타건 모션 곡선: 초반에 살짝 들어 올렸다가(+1.0) 후반에 키보드로 콕 내려찍음(-0.85).
    /// </summary>
    private static double ComputeKeyStrikeOffset(double remainingTime, double duration)
    {
        if (remainingTime <= 0)
        {
            return 0.0;
        }

        var progress = 1.0 - (remainingTime / duration);
        return progress < 0.45
            ? Math.Sin((progress / 0.45) * Math.PI * 0.5)
            : Math.Cos(((progress - 0.45) / 0.55) * Math.PI) * 0.92;
    }

    /// <summary>
    /// 앞발(치비 고양이·솜뭉치 강아지) 공용 갱신: 리듬 들썩임 + 타이핑 자세에서 미니 키보드 위로 뻗어 교대 타건.
    /// </summary>
    /// <param name="stanceLiftY">타이핑 자세에서 앞발을 들어 올리는 높이.</param>
    /// <param name="stanceReachZ">타이핑 자세에서 앞발을 앞으로 뻗는 거리.</param>
    /// <param name="strikeHeight">타건 곡선 1.0당 앞발 상하 이동량.</param>
    private static void UpdateFrontPaws(
        PetRigHandle rig,
        double groove,
        double dancePhase,
        double stance,
        double leftStrike,
        double rightStrike,
        double stanceLiftY,
        double stanceReachZ,
        double strikeHeight)
    {
        if (rig.LeftPawTranslate is null || rig.RightPawTranslate is null)
        {
            return;
        }

        // 리듬 댄스 중 앞발도 좌우 교대로 귀엽게 살짝 들썩임
        var leftDance = Math.Max(0.0, Math.Sin(dancePhase)) * groove * 0.08 * (1.0 - stance);
        var rightDance = Math.Max(0.0, -Math.Sin(dancePhase)) * groove * 0.08 * (1.0 - stance);

        // 타이핑 자세: 앞발을 미니 키보드 위로 앞으로 뻗고, 타건 시 위→아래로 타닥타닥 내려침
        var baseY = stance * stanceLiftY;
        var baseZ = stance * stanceReachZ;

        rig.LeftPawTranslate.OffsetY = baseY + leftStrike * strikeHeight + leftDance;
        rig.LeftPawTranslate.OffsetZ = baseZ + leftDance * 0.45;
        rig.RightPawTranslate.OffsetY = baseY + rightStrike * strikeHeight + rightDance;
        rig.RightPawTranslate.OffsetZ = baseZ + rightDance * 0.45;
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
