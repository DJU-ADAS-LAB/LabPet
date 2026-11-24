using Velopack;

namespace LabPet.Client.Updates;

/// <summary>
/// Velopack 기반 백그라운드 차분 자동 업데이트 서비스.
/// 앱 시작 시, 5분 주기, LAN 피어 신버전 감지 시, 또는 사용자 수동 확인 시 업데이트를 내려받고 재시작 적용을 제공한다.
/// </summary>
public sealed class UpdateService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PeerHintCooldown = TimeSpan.FromMinutes(2);

    private readonly UpdateSourceOptions _options;
    private readonly object _lifecycle = new();
    private readonly AsyncOperationLifetime _checks = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Lazy<Task> _shutdown;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;
    private Task? _periodicCheck;
    private DateTimeOffset _lastPeerHintCheckUtc = DateTimeOffset.MinValue;

    public bool IsUpdateReady => _pendingUpdate is not null;
    public string? PendingVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    public event EventHandler<string>? UpdateReady;
    public event EventHandler<string>? StatusMessage;

    public UpdateService(UpdateSourceOptions? options = null)
    {
        _options = options ?? new UpdateSourceOptions();
        _shutdown = new Lazy<Task>(ShutdownAsync);
    }

    public void StartPeriodicChecks(bool silentAutoApplyOnStartup = false)
    {
        lock (_lifecycle)
        {
            if (_periodicCheck is not null || _stopCts.IsCancellationRequested)
            {
                return;
            }
            _periodicCheck = RunPeriodicChecksAsync(silentAutoApplyOnStartup, _stopCts.Token);
        }
    }

    private async Task RunPeriodicChecksAsync(bool silentAutoApplyOnStartup, CancellationToken ct)
    {
        try
        {
            // Windows 부팅 직후 Wi-Fi/LAN 연결 안정화를 위해 8초 대기 후 첫 확인
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            var updated = await CheckAndDownloadUpdatesAsync(
                isManualCheck: false,
                silentAutoApply: silentAutoApplyOnStartup,
                ct).ConfigureAwait(false);

            // 부팅 직후 네트워크가 아직 안 붙었을 수 있으므로 1회 재시도 (20초 후)
            if (silentAutoApplyOnStartup && !updated && !IsUpdateReady)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                await CheckAndDownloadUpdatesAsync(
                    isManualCheck: false,
                    silentAutoApply: true,
                    ct).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(PeriodicCheckInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await CheckAndDownloadUpdatesAsync(isManualCheck: false, silentAutoApply: false, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    /// <summary>
    /// LAN 피어에서 더 높은 앱 버전이 관측되었을 때 5분 주기를 기다리지 않고 즉시 업데이트 확인을 트리거한다.
    /// </summary>
    public void TriggerImmediateCheckFromPeerHint()
    {
        if (IsUpdateReady)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastPeerHintCheckUtc < PeerHintCooldown)
        {
            return;
        }

        _lastPeerHintCheckUtc = now;
        _ = RunPeerHintCheckAsync();
    }

    private async Task RunPeerHintCheckAsync()
    {
        try { await CheckAndDownloadUpdatesAsync(isManualCheck: false).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public Task<bool> CheckAndDownloadUpdatesAsync(bool isManualCheck, CancellationToken cancellationToken = default)
        => CheckAndDownloadUpdatesAsync(isManualCheck, silentAutoApply: false, cancellationToken);

    private async Task<bool> CheckAndDownloadUpdatesAsync(
        bool isManualCheck,
        bool silentAutoApply,
        CancellationToken cancellationToken)
    {
        using var operation = _checks.TryBegin();
        if (operation is null)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
        var token = linked.Token;
        await _checkLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _manager ??= new UpdateManager(_options.CreateUpdateSource());

            if (!_manager.IsInstalled)
            {
                if (isManualCheck)
                {
                    StatusMessage?.Invoke(this, "개발/포터블 실행 환경입니다. Velopack 설치 버전에서 자동 업데이트가 동작합니다.");
                }

                return false;
            }

            // 이미 다운로드된 업데이트가 있으면 수동 확인 시 즉시 적용 팝업 재요청
            if (_pendingUpdate is not null)
            {
                var readyVersion = _pendingUpdate.TargetFullRelease.Version.ToString();
                if (isManualCheck)
                {
                    UpdateReady?.Invoke(this, readyVersion);
                }

                return true;
            }

            var updateInfo = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (updateInfo is null)
            {
                if (isManualCheck)
                {
                    StatusMessage?.Invoke(this, "현재 최신 버전의 LabPet을 사용 중입니다.");
                }

                return false;
            }

            await _manager.DownloadUpdatesAsync(updateInfo, cancelToken: token).ConfigureAwait(false);
            _pendingUpdate = updateInfo;

            // Windows 시작 자동 실행(--autostart) 시에는 팝업 없이 백그라운드 다운로드 후 즉시 재시작 적용
            if (silentAutoApply)
            {
                _manager.ApplyUpdatesAndRestart(updateInfo);
                return true;
            }

            var versionStr = updateInfo.TargetFullRelease.Version.ToString();
            UpdateReady?.Invoke(this, versionStr);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (isManualCheck)
            {
                StatusMessage?.Invoke(this, $"업데이트 확인 실패: {ex.Message}");
            }

            return false;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>준비된 차분 업데이트를 적용하고 앱을 재시작한다.</summary>
    public void ApplyUpdatesAndRestart()
    {
        if (_manager is null || _pendingUpdate is null)
        {
            return;
        }

        _manager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    public void Dispose() => _shutdown.Value.GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(_shutdown.Value);

    private async Task ShutdownAsync()
    {
        var drain = _checks.DrainAsync();
        Task? periodic;
        lock (_lifecycle)
        {
            _stopCts.Cancel();
            periodic = _periodicCheck;
        }
        try
        {
            await Task.WhenAll(drain, periodic ?? Task.CompletedTask).ConfigureAwait(false);
        }
        finally
        {
            _checkLock.Dispose();
            _stopCts.Dispose();
        }
    }
}
