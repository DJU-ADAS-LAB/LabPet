using System.Windows.Media;
using LabPet.Shared.Models;

namespace LabPet.Client.Rendering;

/// <summary>
/// 60fps/유휴 UI 타이머에서 호출되어 3D 리그의 Transform3D와 Material 색상만 저부하 갱신하는 컨트롤러.
/// - 8종 재사용 모션 클립 + 종별 움직임 프로파일 차별화
/// - SmoothStep 클립 크로스페이드, 지수 감쇠 관성 보간(dt 독립), 비트 과장 억제 쿨다운
/// - 시선 추적(Look-At), 자동 눈 깜빡임, 타이핑 앞발/날개 모션, 풀링 3D 이펙트 파티클 알파 페이드 제어
/// </summary>
public sealed class PetAnimationController
{
    private sealed class ParticleState
    {
        public bool Active;
        public double Age;
        public double Lifetime = 1.15;
        public double StartX;
        public double StartY;
        public double StartZ;
        public double VelX;
        public double VelY;
        public double VelZ;
        public double SwirlPhase;
        public double MaxScale = 0.92;
    }

    private PetRigHandle? _rig;
    private SpeciesMotionProfile _speciesProfile = SpeciesMotionProfile.Mochi;
    private double _elapsedSeconds;

    // 누적 위상 (위상 점프 없이 부드럽게 속도 변화)
    private double _dancePhase;
    private double _lastBeatTime = double.NegativeInfinity;
    private double _beatInterval;
    private double _tailPhase;
    private double _auraPhase;

    // 자연스러운 자동 눈 깜빡임
    private double _nextBlinkInSeconds = 2.6;
    private double _blinkProgress = -1.0;
    private readonly Random _rng = new();

    // 보간된 시선 추적 (-1.0 ~ +1.0)
    private double _currentLookX;
    private double _currentLookY;

    // 상호작용·수면·기상 반응 상태
    private const double WakeUpReactionDuration = 0.95;
    private const double GameWinDuration = 1.35;
    private const double GameLossDuration = 2.10;
    private const double SleepEyeClosedScale = 0.07;

    private double _petReactionTime;
    private double _dizzyReactionTime;
    private double _wakeUpReactionTime;
    private bool _isSleeping;
    private double _sleepWeight;
    private bool _isGrabbed;
    private double _grabWeight;
    private double _grabPhase;
    private bool _gameWon;
    private double _gameReactionTime;
    private PetTearEffect? _tearEffect;

    // 비트 펄스 및 타이핑 자세/교대 타건 상태
    private double _beatPulse;
    private double _leftPawTapTime;
    private double _rightPawTapTime;
    private double _typingStanceHoldTime;
    private double _typingStanceWeight;
    private int _nextPawParity;
    private int _pendingPawTaps;

    // 스레드 안전 오디오/네트워크 스냅샷 + 감쇠 보간
    private volatile float _targetBass;
    private volatile float _targetMid;
    private volatile float _targetTreble;
    private volatile bool _pendingBeat;
    private long _lastSnapshotTimestampTicks;
    private double _smoothBass;
    private double _smoothMid;
    private double _smoothTreble;
    private double _musicWeight;

    // 모션 클립 전환 및 크로스페이드 상태
    private PetMotionClipKind _currentClip = PetMotionClipKind.Idle;
    private PetMotionClipKind _previousClip = PetMotionClipKind.Idle;
    private double _clipBlend = 1.0;
    private double _clipHoldSeconds;
    private double _beatClipSwitchCooldown;
    private int _clipCycleCursor;
    private PetPose _smoothedPose = PetPose.Identity;

    // 풀링 3D 음표/별 이펙트 파티클 상태 (할당 0 재사용)
    private ParticleState[] _particleStates = [];
    private int _particleSpawnCursor;
    private double _particleEmitCooldown;

    private Color _emissiveAccentColor = Color.FromRgb(255, 158, 181);

    public PetMotionClipKind CurrentMotionClip => _currentClip;
    public bool IsSleeping => _isSleeping;
    public bool IsWakingUp => _wakeUpReactionTime > 0.01;
    internal bool IsCrying => _tearEffect?.IsVisible == true;

    internal bool HasInteractiveMotion =>
        _petReactionTime > 0 || _dizzyReactionTime > 0 || _wakeUpReactionTime > 0
        || _isGrabbed || _grabWeight > 0 || _gameReactionTime > 0
        || (_sleepWeight > 0.01 && _sleepWeight < 0.99)
        || _typingStanceHoldTime > 0
        || _typingStanceWeight > 0.01 || Volatile.Read(ref _pendingPawTaps) > 0
        || _musicWeight > 0.02 || HasActiveParticles();

    private double _snackSeconds;
    internal void StartBreakSnack() => _snackSeconds = 2;

    public void AttachRig(PetRigHandle rig, string accentHex)
    {
        ResetGameVisuals();
        _rig = rig;
        _tearEffect = null;
        if (_gameReactionTime > 0)
        {
            ApplyGameExpression();
        }
        _speciesProfile = SpeciesMotionProfile.ForSpecies(rig.SpeciesId);
        _currentClip = PetMotionClipKind.Idle;
        _previousClip = PetMotionClipKind.Idle;
        _clipBlend = 1.0;
        _clipHoldSeconds = 0.0;
        _smoothedPose = PetPose.Identity;
        _sleepWeight = _isSleeping ? 1.0 : 0.0;
        _wakeUpReactionTime = 0.0;

        _particleStates = new ParticleState[rig.MusicParticles.Count];
        for (var i = 0; i < _particleStates.Length; i++)
        {
            _particleStates[i] = new ParticleState();
        }

        UpdateAccentColor(accentHex);
    }

    /// <summary>리그를 재생성하지 않고 발광 포인트 색상만 즉시 갱신한다.</summary>
    public void UpdateAccentColor(string accentHex)
    {
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

    internal void DetachRig()
    {
        ResetGameVisuals();
        _rig = null;
        _tearEffect = null;
        _gameReactionTime = 0;
        _isGrabbed = false;
        _grabWeight = 0;
        _particleStates = [];
        _wakeUpReactionTime = 0.0;
    }

    /// <summary>
    /// 수면 상태를 전환한다. 수면 중 깨어날 때는 깜짝 놀라는 기상 모션을 자동 트리거한다.
    /// </summary>
    public void SetSleeping(bool isSleeping)
    {
        if (_isSleeping == isSleeping)
        {
            return;
        }

        _isSleeping = isSleeping;
        if (isSleeping)
        {
            _wakeUpReactionTime = 0;
            _petReactionTime = _dizzyReactionTime = _typingStanceHoldTime = 0;
            _gameReactionTime = 0;
            ResetGameVisuals();
        }
        else
        {
            TriggerWakeUpStartle();
        }
    }

    /// <summary>수면에서 깨어날 때 화들짝 점프하고 눈을 번쩍 뜨는 깜짝 기상 모션을 트리거한다.</summary>
    public void TriggerWakeUpStartle()
    {
        _wakeUpReactionTime = WakeUpReactionDuration;
        _blinkProgress = -1.0;
        _nextBlinkInSeconds = 2.6;
        EmitReactionStarBurst(count: 4);
    }

    /// <summary>쓰다듬기/기쁨 3D 반응(부피 보존형 점프 + 발광 + 별 파티클 버스트)을 트리거한다.</summary>
    public void TriggerPetReaction()
    {
        if (_isSleeping || IsWakingUp) return;
        _petReactionTime = 0.90;
        EmitReactionStarBurst(count: 3);
    }

    /// <summary>창을 빠르게 좌우로 흔들었을 때 어지러움 3D 반응을 트리거한다.</summary>
    public void TriggerDizzyReaction()
    {
        if (!_isSleeping && !IsWakingUp) _dizzyReactionTime = 1.4;
    }

    internal void SetGrabbed(bool grabbed)
    {
        if (grabbed && _grabWeight == 0)
        {
            _grabPhase = 0;
        }
        _isGrabbed = grabbed;
    }

    internal void TriggerGameReaction(bool won)
    {
        if (_isSleeping)
        {
            return;
        }

        _gameWon = won;
        _gameReactionTime = won ? GameWinDuration : GameLossDuration;
        _petReactionTime = _dizzyReactionTime = _wakeUpReactionTime = 0;
        ApplyGameExpression();
        if (won)
        {
            _tearEffect?.Update(0, 0);
            EmitReactionStarBurst(count: 4);
        }
    }

    private void ApplyGameExpression()
    {
        _rig?.SetReactionExpression(_gameWon ? "happy" : "sleepy");
        if (!_gameWon)
        {
            _tearEffect ??= _rig?.CreateTearEffect?.Invoke();
        }
    }

    private void ResetGameVisuals()
    {
        _rig?.SetReactionExpression(null);
        _tearEffect?.Update(0, 0);
    }

    /// <summary>키보드 타이핑 시 앞발/팔/날개 두드리기 모션을 스레드 안전하게 요청한다.</summary>
    public void TriggerTypingPawTap() => Interlocked.Increment(ref _pendingPawTaps);

    /// <summary>로컬 오디오/네트워크 수신 스냅샷을 갱신한다.</summary>
    public void UpdateAudioSnapshot(float bass, float mid, float treble, bool isBeat = false)
    {
        _targetBass = Math.Clamp(bass, 0f, 1f);
        _targetMid = Math.Clamp(mid, 0f, 1f);
        _targetTreble = Math.Clamp(treble, 0f, 1f);
        Interlocked.Exchange(ref _lastSnapshotTimestampTicks, Environment.TickCount64);
        if (isBeat)
        {
            _pendingBeat = true;
        }
    }

    public void UpdateFrame(double deltaSeconds, double targetLookX, double targetLookY, bool speaking = false)
    {
        var rig = _rig;
        if (rig is null)
        {
            return;
        }

        deltaSeconds = Math.Clamp(deltaSeconds, 0.001, 0.06);
        _elapsedSeconds += deltaSeconds;

        // 네트워크 패킷 지연·유실 방어: 350ms 이상 새 스냅샷이 안 오면 부드럽게 무음 목표치로 감쇠
        var ageMs = Environment.TickCount64 - Interlocked.Read(ref _lastSnapshotTimestampTicks);
        var rawBass = _targetBass;
        var rawMid = _targetMid;
        var rawTreble = _targetTreble;
        if (ageMs > 350)
        {
            var staleFade = Math.Clamp(1.0 - (ageMs - 350) / 700.0, 0.0, 1.0);
            rawBass *= (float)staleFade;
            rawMid *= (float)staleFade;
            rawTreble *= (float)staleFade;
        }

        // 시선 추적 지수 보간 (프레임 시간 독립)
        var lookLerp = 1.0 - Math.Exp(-8.0 * deltaSeconds);
        _currentLookX += (Math.Clamp(targetLookX, -1.0, 1.0) - _currentLookX) * lookLerp;
        _currentLookY += (Math.Clamp(targetLookY, -1.0, 1.0) - _currentLookY) * lookLerp;

        // 비대칭 어택(빠른 반응) / 릴리스(부드러운 여운) 오디오 보간
        _smoothBass += (rawBass - _smoothBass) * (1.0 - Math.Exp(-(rawBass > _smoothBass ? 14.0 : 5.5) * deltaSeconds));
        _smoothMid += (rawMid - _smoothMid) * (1.0 - Math.Exp(-(rawMid > _smoothMid ? 13.0 : 5.5) * deltaSeconds));
        _smoothTreble += (rawTreble - _smoothTreble) * (1.0 - Math.Exp(-(rawTreble > _smoothTreble ? 15.0 : 6.0) * deltaSeconds));

        var beatTriggeredThisFrame = false;
        if (_pendingBeat)
        {
            _pendingBeat = false;
            var interval = _elapsedSeconds - _lastBeatTime;
            if (interval >= 0.25)
            {
                if (interval <= 1.5)
                    _beatInterval = _beatInterval == 0 ? interval : _beatInterval * 0.72 + interval * 0.28;
                else
                    _beatInterval = 0;
                _lastBeatTime = _elapsedSeconds;
            }
            _beatPulse = 1.0;
            beatTriggeredThisFrame = true;
        }
        else
        {
            _beatPulse = Math.Max(0.0, _beatPulse - deltaSeconds * 3.8);
        }

        // 키보드 타이핑 자세 및 좌/우 교대 타건
        var taps = Interlocked.Exchange(ref _pendingPawTaps, 0);
        if (taps > 0 && !_isSleeping && !IsWakingUp && !_isGrabbed)
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
        var stanceDamp = stanceTarget > _typingStanceWeight ? 14.0 : 5.5;
        _typingStanceWeight += (stanceTarget - _typingStanceWeight) * (1.0 - Math.Exp(-stanceDamp * deltaSeconds));
        var stance = _typingStanceWeight;

        _leftPawTapTime = Math.Max(0, _leftPawTapTime - deltaSeconds);
        _rightPawTapTime = Math.Max(0, _rightPawTapTime - deltaSeconds);

        var leftTapWave = _leftPawTapTime > 0 ? Math.Sin((_leftPawTapTime / 0.17) * Math.PI) : 0.0;
        var rightTapWave = _rightPawTapTime > 0 ? Math.Sin((_rightPawTapTime / 0.17) * Math.PI) : 0.0;
        var leftStrike = ComputeKeyStrikeOffset(_leftPawTapTime, 0.17);
        var rightStrike = ComputeKeyStrikeOffset(_rightPawTapTime, 0.17);
        var anyTapWave = Math.Max(leftTapWave, rightTapWave);
        var signedTap = rightTapWave - leftTapWave;

        if (rig.TypingKeyboardScale is not null)
        {
            var kbScale = _isSleeping || _isGrabbed || stance < 0.01 ? 0.0 : stance;
            rig.TypingKeyboardScale.ScaleX = kbScale;
            rig.TypingKeyboardScale.ScaleY = kbScale;
            rig.TypingKeyboardScale.ScaleZ = kbScale;
        }

        var bass = _smoothBass;
        var mid = _smoothMid;
        var treble = _smoothTreble;
        var beat = _beatPulse;
        var musicEnergy = Math.Clamp(bass * 0.45 + mid * 0.35 + treble * 0.20 + beat * 0.22, 0.0, 1.0);

        // 음악 시작/종료 부드러운 무게 전환
        var musicTargetWeight = musicEnergy > 0.05 ? 1.0 : 0.0;
        var musicFadeSpeed = musicTargetWeight > _musicWeight ? 5.2 : 2.1;
        _musicWeight += (musicTargetWeight - _musicWeight) * (1.0 - Math.Exp(-musicFadeSpeed * deltaSeconds));

        // 누적 위상 갱신
        var grooveSpeed = 2.1 + musicEnergy * 2.5;
        if (_beatInterval > 0 && _elapsedSeconds - _lastBeatTime < 2)
            grooveSpeed += (Math.Clamp(Math.PI / _beatInterval, 2.2, 7.0) - grooveSpeed) * _musicWeight;
        _dancePhase += deltaSeconds * grooveSpeed;
        _tailPhase += deltaSeconds * (3.0 + musicEnergy * 2.8);
        _auraPhase = (_auraPhase + deltaSeconds * (12.0 + treble * 10.0 + beat * 16.0)) % 360.0;

        // 모션 클립 선택 및 크로스페이드 전환
        UpdateMotionClipSelection(deltaSeconds, beatTriggeredThisFrame, musicEnergy, bass, mid, treble);

        var idlePose = PetMotionClips.EvaluateClip(
            PetMotionClipKind.Idle, _speciesProfile, _dancePhase, _tailPhase, _elapsedSeconds, 0, 0, 0, 0);

        var currentMusicPose = _currentClip == PetMotionClipKind.Idle
            ? idlePose
            : PetMotionClips.EvaluateClip(_currentClip, _speciesProfile, _dancePhase, _tailPhase, _elapsedSeconds, bass, mid, treble, beat);

        var blendedDancePose = currentMusicPose;
        if (_clipBlend < 0.999)
        {
            _clipBlend = Math.Min(1.0, _clipBlend + deltaSeconds / 0.42);
            var prevPose = PetMotionClips.EvaluateClip(
                _previousClip, _speciesProfile, _dancePhase, _tailPhase, _elapsedSeconds, bass, mid, treble, beat);
            blendedDancePose = PetPose.Lerp(in prevPose, in currentMusicPose, PetMotionClips.SmoothStep(_clipBlend));
        }

        // 수면 비중 보간 (잠들 때는 느긋하게, 깨어날 때는 재빠르게 전환)
        var targetSleeping = _isSleeping && !_isGrabbed;
        var sleepTarget = targetSleeping ? 1.0 : 0.0;
        var sleepSpeed = targetSleeping ? 2.8 : 14.0;
        _sleepWeight = Math.Clamp(
            _sleepWeight + (sleepTarget - _sleepWeight) * (1.0 - Math.Exp(-sleepSpeed * deltaSeconds)),
            0.0,
            1.0);
        var sleepWeight = _sleepWeight;
        var awakeFactor = 1.0 - sleepWeight;

        // 음악 비중(_musicWeight)에 따라 대기(Idle) <-> 댄스 블렌딩 후 수면 포즈 혼합 및 관성 감쇠 적용
        var targetPose = PetPose.Lerp(in idlePose, in blendedDancePose, _musicWeight);
        if (sleepWeight > 0.001)
        {
            var breath = Math.Sin(_elapsedSeconds * 1.45);
            var slowSway = Math.Sin(_elapsedSeconds * 0.72);
            var earDroop = _speciesProfile.OutwardFlopEars ? 10.0 - breath * 1.2 : 12.0 - breath * 1.5;
            var sleepPose = new PetPose
            {
                BodyOffsetY = -0.075 + breath * 0.012,
                BodyScaleY = 0.935 + breath * 0.032,
                BodyPitch = 9.0 + breath * 1.8,
                BodyRoll = slowSway * 3.5,
                HeadPitch = 19.0 + Math.Sin(_elapsedSeconds * 1.45 - 0.6) * 3.2,
                HeadRoll = 10.5 + slowSway * 2.5,
                LeftEarOffset = _speciesProfile.OutwardFlopEars ? -earDroop : earDroop,
                RightEarOffset = _speciesProfile.OutwardFlopEars ? earDroop : -earDroop,
                TailAngle = slowSway * 4.5,
                LeftPawY = -0.02,
                RightPawY = -0.02,
                LeftArmRoll = -8.0,
                RightArmRoll = 8.0
            };
            targetPose = PetPose.Lerp(in targetPose, in sleepPose, sleepWeight);
        }

        var poseDamp = 1.0 - Math.Exp(-14.0 * deltaSeconds);
        _smoothedPose = PetPose.Lerp(in _smoothedPose, in targetPose, poseDamp);

        _grabWeight = Math.Clamp(_grabWeight + deltaSeconds * (_isGrabbed ? 5.5 : -8.0), 0, 1);
        if (_grabWeight > 0)
        {
            _grabPhase = (_grabPhase + deltaSeconds * 25.0) % (2 * Math.PI);
        }
        var grabWeight = PetMotionClips.SmoothStep(_grabWeight);
        var grabSwing = Math.Sin(_grabPhase) * grabWeight;
        var leftKick = Math.Max(0, grabSwing);
        var rightKick = Math.Max(0, -grabSwing);
        var activeStance = stance * awakeFactor * (1.0 - grabWeight);
        var p = _smoothedPose;
        var offsetX = p.BodyOffsetX * (1.0 - activeStance * 0.7);
        var offsetY = p.BodyOffsetY + anyTapWave * 0.03 * awakeFactor;
        var scaleY = p.BodyScaleY - anyTapWave * 0.035 * awakeFactor;
        var bodyRoll = p.BodyRoll * (1.0 - activeStance * 0.5) + signedTap * 4.0 * awakeFactor;
        var bodyYawExtra = p.BodyYaw * (1.0 - activeStance * 0.75) + signedTap * 3.5 * awakeFactor;
        var bodyPitchExtra = p.BodyPitch * (1.0 - activeStance * 0.7);
        var headYawExtra = p.HeadYaw * (1.0 - activeStance * 0.8);
        var headPitchExtra = p.HeadPitch + activeStance * 11.0 + anyTapWave * 5.0 * awakeFactor;
        var headRollExtra = p.HeadRoll + signedTap * 3.2 * awakeFactor;
        var emissiveStrength = p.EmissiveBoost + (bass * 0.04 + beat * 0.045) * awakeFactor;

        // 풀링 3D 이펙트 파티클 갱신 (음표/별 알파 페이드 및 반응 버스트; 수면 중 신규 음표 방출 억제)
        UpdateMusicParticles(
            rig,
            deltaSeconds,
            sleepWeight > 0.5 ? 0 : bass,
            sleepWeight > 0.5 ? 0 : treble,
            sleepWeight > 0.5 ? 0 : beat);

        // 자연스러운 자동 눈 깜빡임 / 수면 감김 / 깜짝 기상 눈 번쩍 뜨기
        UpdateAutoBlink(rig, deltaSeconds);

        // 쓰다듬기/기쁨 반응 오버레이
        if (_petReactionTime > 0)
        {
            _petReactionTime = Math.Max(0, _petReactionTime - deltaSeconds);
            var t = 1.0 - (_petReactionTime / 0.90);
            var bounce = Math.Sin(t * Math.PI * 2.5) * Math.Exp(-t * 2.2);
            scaleY += bounce * 0.20;
            offsetY += Math.Max(0, Math.Sin(t * Math.PI)) * 0.24;
            headRollExtra += Math.Sin(t * Math.PI * 3.0) * 9.0;
            emissiveStrength = Math.Max(emissiveStrength, (1.0 - t) * 0.16);
        }

        // 어지러움(빠른 흔들기) 반응 오버레이
        var dizzyYaw = 0.0;
        if (_dizzyReactionTime > 0)
        {
            _dizzyReactionTime = Math.Max(0, _dizzyReactionTime - deltaSeconds);
            var decay = _dizzyReactionTime / 1.4;
            bodyRoll += Math.Sin(_elapsedSeconds * 26.0) * 18.0 * decay;
            dizzyYaw = Math.Sin(_elapsedSeconds * 16.0) * 24.0 * decay;
            headRollExtra += Math.Cos(_elapsedSeconds * 22.0) * 14.0 * decay;
        }

        // 깜짝 놀라며 깨어나는 기상 모션 오버레이 (화들짝 점프 → 착지 스쿼시·좌우 두리번거림 → 안착)
        var wakeEarBoost = 0.0;
        var wakeArmBoost = 0.0;
        if (_wakeUpReactionTime > 0)
        {
            _wakeUpReactionTime = Math.Max(0, _wakeUpReactionTime - deltaSeconds);
            var pWake = 1.0 - (_wakeUpReactionTime / WakeUpReactionDuration);
            if (pWake < 0.28)
            {
                var u = pWake / 0.28;
                var jump = Math.Sin(u * Math.PI);
                offsetY += 0.34 * jump;
                scaleY += 0.18 * jump;
                bodyPitchExtra -= 14.0 * jump;
                headPitchExtra -= 16.0 * jump;
                wakeEarBoost = 22.0 * jump;
                wakeArmBoost = 32.0 * jump;
            }
            else if (pWake < 0.72)
            {
                var u = (pWake - 0.28) / 0.44;
                var settle = 1.0 - u * 0.5;
                var squash = Math.Sin(u * Math.PI) * (1.0 - u * 0.4);
                var lookAround = Math.Sin(u * Math.PI * 2.5) * settle;
                scaleY -= 0.11 * squash;
                offsetY -= 0.03 * squash;
                headYawExtra += 24.0 * lookAround;
                headRollExtra += 10.0 * lookAround;
                bodyYawExtra += 8.0 * lookAround;
                wakeEarBoost = 8.0 * (1.0 - u);
                wakeArmBoost = 10.0 * (1.0 - u);
            }
            else
            {
                var u = (pWake - 0.72) / 0.28;
                var fade = 1.0 - PetMotionClips.SmoothStep(u);
                headRollExtra += Math.Sin(u * Math.PI) * 3.0 * fade;
            }

            emissiveStrength = Math.Max(emissiveStrength, (1.0 - pWake) * 0.16);
        }

        // 잡힌 동안 몸을 좌우로 비틀고 양쪽 앞발을 번갈아 걷어찬다.
        offsetY += (0.11 + 0.045 * Math.Abs(Math.Cos(_grabPhase))) * grabWeight;
        bodyRoll += grabSwing * 13.0;
        bodyYawExtra += grabSwing * 11.0;
        headRollExtra -= grabSwing * 6.0;

        var gameArmLift = 0.0;
        if (_gameReactionTime > 0)
        {
            _gameReactionTime = Math.Max(0, _gameReactionTime - deltaSeconds);
            var duration = _gameWon ? GameWinDuration : GameLossDuration;
            var elapsed = duration - _gameReactionTime;
            var weight = Math.Min(1.0, elapsed * 8.0) * Math.Min(1.0, _gameReactionTime * 4.0);
            if (_gameWon)
            {
                // 눈웃음을 유지하며 어깨를 들고 두 번 가볍게 뛰어오른다.
                var hop = Math.Abs(Math.Sin(elapsed / duration * Math.PI * 2)) * weight;
                offsetY += hop * 0.28;
                scaleY += hop * 0.09;
                headPitchExtra -= hop * 9.0;
                gameArmLift = (weight + hop) * 15.0;
            }
            else
            {
                // 고개와 어깨를 떨구고 숨을 들이쉬듯 몸을 떨며 눈물이 떨어진다.
                var sob = Math.Sin(elapsed * 19.0) * weight;
                offsetY -= weight * 0.075 - sob * 0.018;
                scaleY -= weight * 0.06;
                headPitchExtra += weight * 13.0 + sob * 3.0;
                headRollExtra += sob * 4.0;
                gameArmLift = -weight * 12.0;
                _tearEffect?.Update(elapsed, weight);
            }

            if (_gameReactionTime == 0)
            {
                ResetGameVisuals();
            }
        }

        // 귀 적용 (rig.json / 종별 기본 각도 + 깜짝 기상 귀 쫑긋)
        if (rig.LeftEarRotation is not null && rig.RightEarRotation is not null)
        {
            var leftWakeEar = _speciesProfile.OutwardFlopEars ? -wakeEarBoost : wakeEarBoost;
            var rightWakeEar = _speciesProfile.OutwardFlopEars ? wakeEarBoost : -wakeEarBoost;
            rig.LeftEarRotation.Angle = rig.LeftEarBaseAngle + p.LeftEarOffset
                + leftTapWave * (_speciesProfile.OutwardFlopEars ? -5.0 : 8.0) * awakeFactor + leftWakeEar;
            rig.RightEarRotation.Angle = rig.RightEarBaseAngle + p.RightEarOffset
                + rightTapWave * (_speciesProfile.OutwardFlopEars ? 5.0 : -8.0) * awakeFactor + rightWakeEar;
        }

        // 꼬리 적용
        if (rig.TailRotation is not null)
        {
            rig.TailRotation.Angle = p.TailAngle + signedTap * 6.5 * awakeFactor;
        }

        // 앞발 (모션 클립 춤 + 타이핑 교대 타건 블렌딩)
        if (rig.LeftPawTranslate is not null && rig.RightPawTranslate is not null)
        {
            var proceduralStandingPuppy = rig.SpeciesId == PetSpeciesIds.FluffyPuppy && !rig.IsExternalAsset;
            var liftY = proceduralStandingPuppy ? 0.18 : 0.08;
            var reachZ = proceduralStandingPuppy ? 0.30 : 0.25;
            var strikeHeight = proceduralStandingPuppy ? 0.12 : 0.14;

            rig.LeftPawTranslate.OffsetY = activeStance * liftY + leftStrike * strikeHeight * awakeFactor + p.LeftPawY * (1.0 - activeStance) + leftKick * 0.16;
            rig.LeftPawTranslate.OffsetZ = activeStance * reachZ + p.LeftPawZ * (1.0 - activeStance) + leftKick * 0.12;
            rig.RightPawTranslate.OffsetY = activeStance * liftY + rightStrike * strikeHeight * awakeFactor + p.RightPawY * (1.0 - activeStance) + rightKick * 0.16;
            rig.RightPawTranslate.OffsetZ = activeStance * reachZ + p.RightPawZ * (1.0 - activeStance) + rightKick * 0.12;
        }

        // 미니 팔/날개 (모션 클립 + 쓰다듬기/깜짝 기상 반응 + 타이핑 타건)
        if (rig.LeftArmRotation is not null && rig.RightArmRotation is not null)
        {
            var reactionLift = 18.0 * (_petReactionTime / 0.90) + gameArmLift + grabWeight * 16.0;
            var stanceInward = activeStance * 28.0;
            rig.LeftArmRotation.Angle = p.LeftArmRoll * (1.0 - activeStance * 0.7) + reactionLift + stanceInward - leftStrike * 26.0 * awakeFactor + wakeArmBoost + leftKick * 24.0;
            rig.RightArmRotation.Angle = p.RightArmRoll * (1.0 - activeStance * 0.7) - reactionLift - stanceInward + rightStrike * 26.0 * awakeFactor - wakeArmBoost - rightKick * 24.0;
        }

        if (rig.LeftArmPitch is not null && rig.RightArmPitch is not null)
        {
            rig.LeftArmPitch.Angle = p.LeftArmPitch * (1.0 - activeStance * 0.7) + activeStance * 24.0 - leftStrike * 34.0 * awakeFactor;
            rig.RightArmPitch.Angle = p.RightArmPitch * (1.0 - activeStance * 0.7) + activeStance * 24.0 - rightStrike * 34.0 * awakeFactor;
        }

        // 부피 보존형 Squash & Stretch 및 안전 클램프 (급격한 튐·창 경계 이탈 방지)
        scaleY = Math.Clamp(scaleY, 0.80, 1.24);
        var scaleXZ = 1.0 / Math.Sqrt(scaleY);

        rig.BodyScale.ScaleX = scaleXZ;
        rig.BodyScale.ScaleY = scaleY;
        rig.BodyScale.ScaleZ = scaleXZ;

        rig.BodyTranslate.OffsetX = Math.Clamp(offsetX, -0.25, 0.25);
        rig.BodyTranslate.OffsetY = Math.Clamp(offsetY, -0.15, 0.45);
        rig.BodyTranslate.OffsetZ = Math.Clamp(p.BodyOffsetZ + activeStance * 0.11, -0.15, 0.22);

        rig.BodyYaw.Angle = _currentLookX * 14.0 * (1.0 - activeStance * 0.65) * awakeFactor + bodyYawExtra + dizzyYaw;
        rig.BodyPitch.Angle = _currentLookY * 8.0 * (1.0 - activeStance * 0.7) * awakeFactor + activeStance * 7.0 + anyTapWave * 2.2 * awakeFactor + bodyPitchExtra;
        rig.BodyRoll.Angle = Math.Clamp(bodyRoll, -26.0, 26.0);

        if (rig.ShadowScale is not null)
        {
            var shadowFactor = Math.Clamp(1.0 - rig.BodyTranslate.OffsetY * 0.42, 0.68, 1.08);
            rig.ShadowScale.ScaleX = shadowFactor;
            rig.ShadowScale.ScaleZ = shadowFactor;
        }

        rig.HeadYaw.Angle = _currentLookX * 18.0 * awakeFactor + headYawExtra;
        rig.HeadPitch.Angle = _currentLookY * 12.0 * awakeFactor + headPitchExtra;
        rig.HeadRoll.Angle = Math.Clamp(headRollExtra, -22.0, 22.0);

        _snackSeconds = Math.Max(0, _snackSeconds - deltaSeconds);
        var stride = Math.Sin(_dancePhase * 2) * (14 * bass + 7 * mid) * awakeFactor * (1 - activeStance);
        var walk = _smoothedPose.StepWeight * awakeFactor * (1 - activeStance);
        stride += Math.Sin(_dancePhase * 2) * walk * 18;
        var waveTime = _petReactionTime > 0 && !_isSleeping && !IsWakingUp ? 0.90 - _petReactionTime : -1;
        rig.Skin?.Update(rig, (float)deltaSeconds, (float)stride, (float)activeStance,
            (float)(leftStrike * awakeFactor), (float)(rightStrike * awakeFactor), (float)waveTime, (float)walk,
            _isSleeping ? "sleep" : IsWakingUp ? "jump" : _gameReactionTime > 0 ? (_gameWon ? "happy" : "sit")
            : _isGrabbed ? "run" : _snackSeconds > 0 ? "eat" : _petReactionTime > 0 ? "wave"
            : speaking ? "talk" : _musicWeight > 0.15
                ? (_currentClip == PetMotionClipKind.Walk ? "walk" : "dance") : "idle");

        if (rig.AuraRotation is not null)
        {
            rig.AuraRotation.Angle = _auraPhase;
        }

        if (rig.AuraTranslate is not null)
        {
            rig.AuraTranslate.OffsetY = Math.Sin(_elapsedSeconds * 1.35) * (0.032 + beat * 0.02);
        }

        emissiveStrength = Math.Clamp(emissiveStrength, 0.0, 0.16);
        var r = (byte)(_emissiveAccentColor.R * emissiveStrength);
        var g = (byte)(_emissiveAccentColor.G * emissiveStrength);
        var b = (byte)(_emissiveAccentColor.B * emissiveStrength);
        rig.BodyEmissiveBrush.Color = Color.FromRgb(r, g, b);
    }

    private void UpdateMotionClipSelection(
        double dt,
        bool beatTriggered,
        double musicEnergy,
        double bass,
        double mid,
        double treble)
    {
        _clipHoldSeconds += dt;
        _beatClipSwitchCooldown = Math.Max(0.0, _beatClipSwitchCooldown - dt);

        if (_musicWeight < 0.12 && musicEnergy <= 0.05)
        {
            if (_currentClip != PetMotionClipKind.Idle)
            {
                SwitchClip(PetMotionClipKind.Idle);
            }

            return;
        }

        if (_currentClip == PetMotionClipKind.Idle && musicEnergy > 0.07)
        {
            var next = PickNextDanceClip(bass, mid, treble);
            SwitchClip(next);
            _beatClipSwitchCooldown = 1.4;
            return;
        }

        // 최소 유지 시간(1.5초) 경과 후 강한 비트 또는 주기 만료(3.6초) 시 새 모션으로 자연스럽게 순환
        var readyForSwitch = _clipHoldSeconds >= 1.5;
        var periodicTurn = _clipHoldSeconds >= 3.6;
        if (readyForSwitch && ((beatTriggered && _beatClipSwitchCooldown <= 0) || periodicTurn))
        {
            var next = PickNextDanceClip(bass, mid, treble);
            if (next != _currentClip)
            {
                SwitchClip(next);
            }

            _beatClipSwitchCooldown = 1.4;
        }
    }

    private PetMotionClipKind PickNextDanceClip(double bass, double mid, double treble)
    {
        var cycle = _speciesProfile.DanceCycle;
        if (cycle.Length == 0)
        {
            return PetMotionClipKind.Groove;
        }

        // 주파수 지배 대역에 따라 가장 어울리는 클립을 우선하되 연속 반복 방지
        PetMotionClipKind preferred;
        var cursor = _clipCycleCursor++;
        if (bass > 0.68 && _currentClip != PetMotionClipKind.HappyJump && cursor % 3 == 0)
        {
            preferred = PetMotionClipKind.HappyJump;
        }
        else if (treble > mid + 0.12 && _speciesProfile.PawDanceScale > 0 && _currentClip != PetMotionClipKind.PawDance)
        {
            preferred = PetMotionClipKind.PawDance;
        }
        else if (mid > bass + 0.10 && _currentClip != PetMotionClipKind.Turn)
        {
            preferred = PetMotionClipKind.Turn;
        }
        else
        {
            preferred = cycle[cursor % cycle.Length];
            if (preferred == _currentClip && cycle.Length > 1)
            {
                preferred = cycle[_clipCycleCursor++ % cycle.Length];
            }
        }

        return preferred;
    }

    private void SwitchClip(PetMotionClipKind nextClip)
    {
        if (nextClip == _currentClip)
        {
            return;
        }

        _previousClip = _currentClip;
        _currentClip = nextClip;
        _clipBlend = 0.0;
        _clipHoldSeconds = 0.0;
    }

    private void UpdateAutoBlink(PetRigHandle rig, double dt)
    {
        if (rig.EyeBlinkScale is null)
        {
            return;
        }

        if (_wakeUpReactionTime > 0.01)
        {
            _blinkProgress = -1.0;
            rig.EyeBlinkScale.ScaleY = 1.0;
            return;
        }

        if (_sleepWeight > 0.01)
        {
            _blinkProgress = -1.0;
            var closedAmount = Math.Clamp(_sleepWeight * 1.35, 0.0, 1.0);
            rig.EyeBlinkScale.ScaleY = 1.0 + (SleepEyeClosedScale - 1.0) * closedAmount;
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
            var closeFactor = Math.Sin(_blinkProgress * Math.PI);
            rig.EyeBlinkScale.ScaleY = 1.0 - closeFactor * 0.92;
        }
    }

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

    private void EmitReactionStarBurst(int count)
    {
        if (_particleStates.Length == 0 || _rig is null)
        {
            return;
        }

        var spawned = 0;
        for (var attempt = 0; attempt < _particleStates.Length && spawned < count; attempt++)
        {
            var idx = (_particleSpawnCursor + attempt) % _particleStates.Length;
            if (spawned == 0 && !string.Equals(_rig.MusicParticles[idx].ParticleKind, "star", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var angle = (Math.PI * 2.0 * spawned / count) + _elapsedSeconds * 1.7;
            var st = _particleStates[idx];
            st.Active = true;
            st.Age = 0;
            st.Lifetime = 0.95;
            st.StartX = Math.Cos(angle) * 0.85;
            st.StartY = 0.45;
            st.StartZ = Math.Sin(angle) * 0.50;
            st.VelX = Math.Cos(angle) * 0.72;
            st.VelY = 1.15;
            st.VelZ = Math.Sin(angle) * 0.48;
            st.SwirlPhase = angle;
            st.MaxScale = 1.05;
            spawned++;
        }

        _particleSpawnCursor = (_particleSpawnCursor + count) % _particleStates.Length;
    }

    private bool HasActiveParticles()
    {
        for (var i = 0; i < _particleStates.Length; i++)
        {
            if (_particleStates[i].Active)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateMusicParticles(PetRigHandle rig, double dt, double bass, double treble, double beat)
    {
        if (rig.MusicParticles.Count == 0 || _particleStates.Length != rig.MusicParticles.Count)
        {
            return;
        }

        _particleEmitCooldown = Math.Max(0, _particleEmitCooldown - dt);
        var energy = (bass + treble) * 0.5;
        if ((beat > 0.68 || energy > 0.22) && _particleEmitCooldown <= 0)
        {
            var idx = _particleSpawnCursor++ % _particleStates.Length;
            var angle = (_elapsedSeconds * 2.1 + idx * 1.15) % (Math.PI * 2.0);
            var st = _particleStates[idx];
            st.Active = true;
            st.Age = 0;
            st.Lifetime = 1.18;
            // 캐릭터 외곽 반경 0.90~1.05에서 방출해 머리 액세서리와의 시각적 겹침 최소화
            var radius = 0.92 + (idx % 3) * 0.08;
            st.StartX = Math.Cos(angle) * radius;
            st.StartY = 0.35 + (idx % 2) * 0.16;
            st.StartZ = Math.Sin(angle) * (radius * 0.58);
            st.VelX = Math.Cos(angle) * 0.55;
            st.VelY = 0.88 + energy * 0.50;
            st.VelZ = Math.Sin(angle) * 0.38;
            st.SwirlPhase = idx * 1.3;
            st.MaxScale = 0.82 + beat * 0.20;
            _particleEmitCooldown = 0.24;
        }

        for (var i = 0; i < _particleStates.Length; i++)
        {
            var st = _particleStates[i];
            var handle = rig.MusicParticles[i];
            if (!st.Active)
            {
                HideParticle(handle);
                continue;
            }

            st.Age += dt;
            if (st.Age >= st.Lifetime)
            {
                st.Active = false;
                HideParticle(handle);
                continue;
            }

            var progress = st.Age / st.Lifetime;
            // 빠른 pop-in, 부드러운 포물선 스케일/알파 감쇠
            var scaleEnvelope = progress < 0.22
                ? PetMotionClips.SmoothStep(progress / 0.22)
                : Math.Cos(((progress - 0.22) / 0.78) * Math.PI * 0.5);
            var scale = Math.Max(0.0, scaleEnvelope * st.MaxScale);

            var alphaEnvelope = progress < 0.18
                ? progress / 0.18
                : Math.Clamp(1.0 - Math.Pow((progress - 0.18) / 0.82, 1.4), 0.0, 1.0);
            var alphaByte = (byte)Math.Clamp(alphaEnvelope * 225.0, 0, 255);

            handle.Scale.ScaleX = scale;
            handle.Scale.ScaleY = scale;
            handle.Scale.ScaleZ = scale;

            // 부드러운 상승 나선 + 중력 감속 궤적
            var swirlX = Math.Sin(st.Age * 3.8 + st.SwirlPhase) * 0.09 * progress;
            var arcY = st.VelY * st.Age - 0.18 * st.Age * st.Age;
            handle.Translate.OffsetX = st.StartX + st.VelX * st.Age + swirlX;
            handle.Translate.OffsetY = st.StartY + arcY;
            handle.Translate.OffsetZ = st.StartZ + st.VelZ * st.Age;
            handle.Rotation.Angle = (st.Age * 145.0) % 360.0;

            var c = handle.BaseColor;
            if (handle.TintBrush is not null)
            {
                handle.TintBrush.Color = Color.FromArgb(alphaByte, c.R, c.G, c.B);
            }

            if (handle.EmissiveBrush is not null)
            {
                var glowAlpha = (byte)Math.Clamp(alphaEnvelope * 75.0, 0, 255);
                handle.EmissiveBrush.Color = Color.FromArgb(glowAlpha, c.R, c.G, c.B);
            }
        }
    }

    private static void HideParticle(MusicParticleHandle handle)
    {
        if (handle.Scale.ScaleX != 0 || handle.Scale.ScaleY != 0 || handle.Scale.ScaleZ != 0)
        {
            handle.Scale.ScaleX = 0;
            handle.Scale.ScaleY = 0;
            handle.Scale.ScaleZ = 0;
        }

        if (handle.TintBrush is not null && handle.TintBrush.Color.A != 0)
        {
            var c = handle.BaseColor;
            handle.TintBrush.Color = Color.FromArgb(0, c.R, c.G, c.B);
        }

        if (handle.EmissiveBrush is not null && handle.EmissiveBrush.Color.A != 0)
        {
            var c = handle.BaseColor;
            handle.EmissiveBrush.Color = Color.FromArgb(0, c.R, c.G, c.B);
        }
    }
}
