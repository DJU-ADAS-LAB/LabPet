using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LabPet.Client.Audio;

/// <summary>
/// NAudio WasapiLoopbackCapture 기반 시스템 출력 오디오 분석기.
/// - WaveFormatExtensible(Windows WASAPI Shared Mode 기본 포맷), IEEE Float 32bit, PCM 16/24/32bit 모두 지원
/// - 무음 감쇠: 마지막 샘플 수신 후 120ms 경과 시 목표값을 0으로 부드럽게 감쇠
/// - 장치 0개 폴백: 활성 재생 장치가 없는 환경에서도 예외로 종료되지 않음
/// - 핫스왑 복구: IMMNotificationClient로 스피커↔이어폰 변경 감지 시 자동 재연결
/// </summary>
public sealed class LoopbackAudioAnalyzer : IAudioAnalyzer, IMMNotificationClient
{
    private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private const double SilenceThresholdMs = 120.0;
    private const double FullDecayDurationMs = 450.0;
    private const double BeatHoldDurationMs = 95.0;
    private const float MusicActiveEnergyThreshold = 0.035f;

    private readonly AudioSpectrumProcessor _processor = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lifecycleLock = new();
    private readonly float[] _sampleRing = new float[AudioSpectrumProcessor.FftLength];

    private int _sampleRingPos;
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _activeDevice;
    private WasapiLoopbackCapture? _capture;
    private CancellationTokenSource? _reconnectCts;
    private bool _isStarted;
    private bool _isDisposed;

    // 스레드 안전 최신 분석 상태 (UI 스레드는 읽기만 수행)
    private volatile float _rawBass;
    private volatile float _rawMid;
    private volatile float _rawTreble;
    private long _lastDataReceivedTicks;
    private long _lastBeatTicks;
    private volatile bool _hasAudioDevice;
    private volatile string _activeDeviceName = "재생 장치 없음";

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_isDisposed || _isStarted)
            {
                return;
            }

            _isStarted = true;
            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                _deviceEnumerator.RegisterEndpointNotificationCallback(this);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _deviceEnumerator = null;
            }

            TryStartCaptureLocked();
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _isStarted = false;
            CancelPendingReconnectLocked();
            StopCaptureLocked();

            if (_deviceEnumerator is not null)
            {
                try
                {
                    _deviceEnumerator.UnregisterEndpointNotificationCallback(this);
                    _deviceEnumerator.Dispose();
                }
                catch
                {
                    // COM 정리 예외 무시
                }

                _deviceEnumerator = null;
            }
        }
    }

    public AudioFrameSnapshot GetCurrentSnapshot()
    {
        var nowTicks = _clock.ElapsedTicks;
        var lastDataTicks = Interlocked.Read(ref _lastDataReceivedTicks);

        if (!_hasAudioDevice || lastDataTicks == 0)
        {
            return new AudioFrameSnapshot(0f, 0f, 0f, false, false, _hasAudioDevice, _activeDeviceName);
        }

        var elapsedSinceDataMs = (nowTicks - lastDataTicks) * 1000.0 / Stopwatch.Frequency;
        var decayFactor = 1.0f;

        // 무음 감쇠: 마지막 수신 후 120ms 경과 시 0으로 부드럽게 감쇠
        if (elapsedSinceDataMs > SilenceThresholdMs)
        {
            var decayProgress = (elapsedSinceDataMs - SilenceThresholdMs) / FullDecayDurationMs;
            decayFactor = (float)Math.Clamp(1.0 - decayProgress, 0.0, 1.0);
        }

        var bass = _rawBass * decayFactor;
        var mid = _rawMid * decayFactor;
        var treble = _rawTreble * decayFactor;

        var lastBeatTicks = Interlocked.Read(ref _lastBeatTicks);
        var elapsedSinceBeatMs = lastBeatTicks > 0
            ? (nowTicks - lastBeatTicks) * 1000.0 / Stopwatch.Frequency
            : double.MaxValue;

        var isBeat = decayFactor > 0.5f && elapsedSinceBeatMs <= BeatHoldDurationMs;
        var isMusicActive = (bass + mid + treble) / 3.0f >= MusicActiveEnergyThreshold && decayFactor > 0.1f;

        return new AudioFrameSnapshot(bass, mid, treble, isBeat, isMusicActive, _hasAudioDevice, _activeDeviceName);
    }

    private void TryStartCaptureLocked()
    {
        StopCaptureLocked();
        _processor.Reset();
        _sampleRingPos = 0;

        try
        {
            if (_deviceEnumerator is null)
            {
                _hasAudioDevice = false;
                _activeDeviceName = "재생 장치 없음";
                return;
            }

            // 주의: WasapiLoopbackCapture 수명 동안 MMDevice를 유지해야 하므로 using으로 조기 해제하지 않음
            var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice is null || defaultDevice.State != DeviceState.Active)
            {
                defaultDevice?.Dispose();
                _hasAudioDevice = false;
                _activeDeviceName = "활성 재생 장치 없음";
                return;
            }

            var capture = new WasapiLoopbackCapture(defaultDevice);
            capture.DataAvailable += OnCaptureDataAvailable;
            capture.RecordingStopped += OnCaptureRecordingStopped;
            capture.StartRecording();

            _activeDevice = defaultDevice;
            _capture = capture;
            _activeDeviceName = $"{defaultDevice.FriendlyName} ({capture.WaveFormat.SampleRate / 1000}kHz, {capture.WaveFormat.BitsPerSample}bit)";
            _hasAudioDevice = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // 활성 재생 장치가 0개인 PC 또는 장치 초기화 실패 시 안전 폴백
            _hasAudioDevice = false;
            _activeDeviceName = "재생 장치 없음 (안전 대기)";
            StopCaptureLocked();
        }
    }

    private void StopCaptureLocked()
    {
        if (_capture is not null)
        {
            try
            {
                _capture.DataAvailable -= OnCaptureDataAvailable;
                _capture.RecordingStopped -= OnCaptureRecordingStopped;
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch
            {
                // 장치 핫스왑 중 이미 해제된 캡처 정리 예외 무시
            }
            finally
            {
                _capture = null;
            }
        }

        if (_activeDevice is not null)
        {
            try
            {
                _activeDevice.Dispose();
            }
            catch
            {
                // 무시
            }
            finally
            {
                _activeDevice = null;
            }
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || sender is not WasapiLoopbackCapture capture)
        {
            return;
        }

        var format = capture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        var nowSeconds = _clock.Elapsed.TotalSeconds;

        var isFloat32 = format.BitsPerSample == 32 && (
            format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible extFloat && extFloat.SubFormat == SubTypeIeeeFloat));

        var isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
            (format is WaveFormatExtensible extPcm && extPcm.SubFormat == SubTypePcm);

        if (isFloat32)
        {
            var frameBytes = channels * 4;
            var frameCount = e.BytesRecorded / frameBytes;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var mono = 0f;
                var baseByte = frame * frameBytes;
                for (var ch = 0; ch < channels; ch++)
                {
                    mono += BitConverter.ToSingle(e.Buffer, baseByte + ch * 4);
                }

                PushMonoSample(mono / channels, format.SampleRate, nowSeconds);
            }
        }
        else if (isPcm && format.BitsPerSample == 16)
        {
            var frameBytes = channels * 2;
            var frameCount = e.BytesRecorded / frameBytes;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var mono = 0f;
                var baseByte = frame * frameBytes;
                for (var ch = 0; ch < channels; ch++)
                {
                    mono += BitConverter.ToInt16(e.Buffer, baseByte + ch * 2) / 32768f;
                }

                PushMonoSample(mono / channels, format.SampleRate, nowSeconds);
            }
        }
        else if (isPcm && format.BitsPerSample == 24)
        {
            var frameBytes = channels * 3;
            var frameCount = e.BytesRecorded / frameBytes;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var mono = 0f;
                var baseByte = frame * frameBytes;
                for (var ch = 0; ch < channels; ch++)
                {
                    var idx = baseByte + ch * 3;
                    int sample24 = e.Buffer[idx] | (e.Buffer[idx + 1] << 8) | ((sbyte)e.Buffer[idx + 2] << 16);
                    mono += sample24 / 8388608f;
                }

                PushMonoSample(mono / channels, format.SampleRate, nowSeconds);
            }
        }
        else if (isPcm && format.BitsPerSample == 32)
        {
            var frameBytes = channels * 4;
            var frameCount = e.BytesRecorded / frameBytes;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var mono = 0f;
                var baseByte = frame * frameBytes;
                for (var ch = 0; ch < channels; ch++)
                {
                    mono += BitConverter.ToInt32(e.Buffer, baseByte + ch * 4) / 2147483648f;
                }

                PushMonoSample(mono / channels, format.SampleRate, nowSeconds);
            }
        }
    }

    private void PushMonoSample(float sample, int sampleRate, double nowSeconds)
    {
        _sampleRing[_sampleRingPos++] = sample;
        if (_sampleRingPos < AudioSpectrumProcessor.FftLength)
        {
            return;
        }

        _sampleRingPos = 0;
        var (bass, mid, treble, isBeat) = _processor.AnalyzeBlock(_sampleRing, sampleRate, nowSeconds);

        _rawBass = bass;
        _rawMid = mid;
        _rawTreble = treble;
        Interlocked.Exchange(ref _lastDataReceivedTicks, _clock.ElapsedTicks);

        if (isBeat)
        {
            Interlocked.Exchange(ref _lastBeatTicks, _clock.ElapsedTicks);
        }
    }

    private void OnCaptureRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            ScheduleReconnect();
        }
    }

    private void ScheduleReconnect()
    {
        CancellationToken token;
        lock (_lifecycleLock)
        {
            if (!_isStarted || _isDisposed)
            {
                return;
            }

            CancelPendingReconnectLocked();
            _reconnectCts = new CancellationTokenSource();
            token = _reconnectCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // 스피커↔블루투스 이어폰 전환 안정화를 위한 짧은 디바운스
                await Task.Delay(350, token).ConfigureAwait(false);
                lock (_lifecycleLock)
                {
                    if (_isStarted && !_isDisposed && !token.IsCancellationRequested)
                    {
                        TryStartCaptureLocked();
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // 새 장치 변경 이벤트로 대체됨
            }
        }, token);
    }

    private void CancelPendingReconnectLocked()
    {
        if (_reconnectCts is null)
        {
            return;
        }

        try
        {
            _reconnectCts.Cancel();
            _reconnectCts.Dispose();
        }
        catch
        {
            // 무시
        }

        _reconnectCts = null;
    }

    // IMMNotificationClient 구현: 출력 장치 핫스왑 감지
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
        {
            ScheduleReconnect();
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        ScheduleReconnect();
    }

    public void OnDeviceAdded(string pwstrDeviceId) => ScheduleReconnect();
    public void OnDeviceRemoved(string deviceId) => ScheduleReconnect();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        Stop();
    }
}
