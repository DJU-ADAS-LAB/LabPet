namespace LabPet.Client.Audio;

/// <summary>
/// UI 60fps 타이머가 락 없이 읽어가는 스레드 안전 오디오 분석 스냅샷.
/// </summary>
public readonly record struct AudioFrameSnapshot(
    float Bass,
    float Mid,
    float Treble,
    bool IsBeat,
    bool IsMusicActive,
    bool HasAudioDevice,
    string ActiveDeviceName);

/// <summary>
/// 시스템 루프백 오디오 분석기 인터페이스.
/// </summary>
public interface IAudioAnalyzer : IDisposable
{
    /// <summary>분석기 시작 (장치가 없어도 예외로 앱을 종료시키지 않는다).</summary>
    void Start();

    /// <summary>분석기 중지.</summary>
    void Stop();

    /// <summary>
    /// 현재 시점의 스냅샷을 반환한다.
    /// 무음 상태(마지막 샘플 수신 후 120ms 이상 경과)에서는 0으로 부드럽게 감쇠된 값을 반환한다.
    /// </summary>
    AudioFrameSnapshot GetCurrentSnapshot();
}
