using NAudio.Dsp;

namespace LabPet.Client.Audio;

/// <summary>
/// 1,024-point FFT(Hamming Window) 기반 저음/중음/고음 대역 에너지 추출 및
/// 이동 평균 기반 적응형 비트 감지 프로세서.
/// </summary>
public sealed class AudioSpectrumProcessor
{
    public const int FftLength = 1024;
    private const int FftOrder = 10; // 2^10 = 1024
    private const int EnergyHistoryLength = 43; // 약 0.9초 @48kHz/1024
    private const float BeatThresholdMultiplier = 1.42f;
    private const float MinBeatEnergyFloor = 0.14f;
    private const double BeatCooldownSeconds = 0.18;

    private readonly float[] _hammingWindow = new float[FftLength];
    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private readonly float[] _bassHistory = new float[EnergyHistoryLength];
    private int _historyCursor;
    private int _historyCount;
    private double _lastBeatTimestampSeconds = -10.0;

    public AudioSpectrumProcessor()
    {
        for (var i = 0; i < FftLength; i++)
        {
            _hammingWindow[i] = (float)(0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (FftLength - 1)));
        }
    }

    /// <summary>
    /// 모노 32-bit float PCM 1,024샘플 블록을 분석해 정규화된 대역(0..1)과 비트 감지 여부를 반환한다.
    /// </summary>
    public (float Bass, float Mid, float Treble, bool IsBeat) AnalyzeBlock(
        ReadOnlySpan<float> monoSamples,
        int sampleRate,
        double timestampSeconds)
    {
        var count = Math.Min(monoSamples.Length, FftLength);
        for (var i = 0; i < FftLength; i++)
        {
            var sample = i < count ? monoSamples[i] : 0f;
            _fftBuffer[i].X = sample * _hammingWindow[i];
            _fftBuffer[i].Y = 0f;
        }

        FastFourierTransform.FFT(forward: true, m: FftOrder, data: _fftBuffer);

        var nyquistBins = FftLength / 2;
        var binHz = (double)sampleRate / FftLength;

        var bassSum = 0.0;
        var bassBins = 0;
        var midSum = 0.0;
        var midBins = 0;
        var trebleSum = 0.0;
        var trebleBins = 0;

        for (var i = 1; i < nyquistBins; i++)
        {
            var freq = i * binHz;
            var mag = Math.Sqrt(_fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y);

            if (freq is >= 20.0 and < 250.0)
            {
                bassSum += mag;
                bassBins++;
            }
            else if (freq is >= 250.0 and < 2000.0)
            {
                midSum += mag;
                midBins++;
            }
            else if (freq is >= 2000.0 and <= 12000.0)
            {
                trebleSum += mag;
                trebleBins++;
            }
        }

        // 체감 반응에 맞춘 스케일링 (0.0 ~ 1.0 클램프)
        var bass = NormalizeBand(bassBins > 0 ? bassSum / bassBins : 0.0, gain: 22.0);
        var mid = NormalizeBand(midBins > 0 ? midSum / midBins : 0.0, gain: 32.0);
        var treble = NormalizeBand(trebleBins > 0 ? trebleSum / trebleBins : 0.0, gain: 48.0);

        // 이동 평균 기반 적응형 비트 감지
        var movingAverage = ComputeBassMovingAverage();
        var isBeat = _historyCount >= 8
            && bass >= MinBeatEnergyFloor
            && bass > movingAverage * BeatThresholdMultiplier
            && (timestampSeconds - _lastBeatTimestampSeconds) >= BeatCooldownSeconds;

        if (isBeat)
        {
            _lastBeatTimestampSeconds = timestampSeconds;
        }

        _bassHistory[_historyCursor] = bass;
        _historyCursor = (_historyCursor + 1) % EnergyHistoryLength;
        if (_historyCount < EnergyHistoryLength)
        {
            _historyCount++;
        }

        return (bass, mid, treble, isBeat);
    }

    public void Reset()
    {
        Array.Clear(_bassHistory, 0, _bassHistory.Length);
        _historyCursor = 0;
        _historyCount = 0;
        _lastBeatTimestampSeconds = -10.0;
    }

    private float ComputeBassMovingAverage()
    {
        if (_historyCount == 0)
        {
            return 0f;
        }

        var sum = 0f;
        for (var i = 0; i < _historyCount; i++)
        {
            sum += _bassHistory[i];
        }

        return sum / _historyCount;
    }

    private static float NormalizeBand(double avgMagnitude, double gain)
    {
        // 로그성 압축으로 작은 소리에도 부드럽게 반응하고 큰 소리에서 포화 방지
        var scaled = Math.Log10(1.0 + avgMagnitude * gain * 9.0);
        return (float)Math.Clamp(scaled, 0.0, 1.0);
    }
}
