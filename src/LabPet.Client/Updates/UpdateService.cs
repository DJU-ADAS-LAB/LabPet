using Velopack;

namespace LabPet.Client.Updates;

/// <summary>
/// Velopack 기반 백그라운드 차분 자동 업데이트 서비스.
/// 앱 시작 시, 1시간 주기, 또는 사용자 수동 확인 시 업데이트를 내려받고 재시작 적용을 제공한다.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromHours(1);

    private readonly UpdateSourceOptions _options;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _periodicCts;

    public bool IsUpdateReady => _pendingUpdate is not null;
    public string? PendingVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    public event EventHandler<string>? UpdateReady;
    public event EventHandler<string>? StatusMessage;

    public UpdateService(UpdateSourceOptions? options = null)
    {
        _options = options ?? new UpdateSourceOptions();
    }

    public void StartPeriodicChecks()
    {
        if (_periodicCts is not null)
        {
            return;
        }

        _periodicCts = new CancellationTokenSource();
        var ct = _periodicCts.Token;

        _ = Task.Run(async () =>
        {
            // 앱 시작 직후 UI 초기화를 방해하지 않도록 잠시 대기 후 첫 확인
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                await CheckAndDownloadUpdatesAsync(isManualCheck: false, ct).ConfigureAwait(false);

                using var timer = new PeriodicTimer(PeriodicCheckInterval);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    await CheckAndDownloadUpdatesAsync(isManualCheck: false, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 정상 종료
            }
        }, ct);
    }

    public async Task<bool> CheckAndDownloadUpdatesAsync(bool isManualCheck, CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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

            var updateInfo = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                if (isManualCheck)
                {
                    StatusMessage?.Invoke(this, "현재 최신 버전의 LabPet을 사용 중입니다.");
                }

                return false;
            }

            await _manager.DownloadUpdatesAsync(updateInfo, cancelToken: cancellationToken).ConfigureAwait(false);
            _pendingUpdate = updateInfo;

            var versionStr = updateInfo.TargetFullRelease.Version.ToString();
            UpdateReady?.Invoke(this, versionStr);
            StatusMessage?.Invoke(this, $"새 버전 v{versionStr} 준비됨 · 트레이에서 재시작하여 적용");
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

    public void Dispose()
    {
        try
        {
            _periodicCts?.Cancel();
            _periodicCts?.Dispose();
        }
        catch
        {
            // 무시
        }

        _checkLock.Dispose();
    }
}
