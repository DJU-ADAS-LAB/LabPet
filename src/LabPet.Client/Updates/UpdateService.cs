using System.Diagnostics;
using Velopack;

namespace LabPet.Client.Updates;

internal enum UpdatePhase
{
    None,
    Available,
    Downloading,
    Ready,
    Applying
}

internal sealed record UpdateState(UpdatePhase Phase = UpdatePhase.None, string? Version = null, string? Error = null)
{
    public bool IsBusy => Phase is UpdatePhase.Downloading or UpdatePhase.Applying;
    public bool CanInstall => Phase is UpdatePhase.Available or UpdatePhase.Ready;

    public string? Message => Error ?? (Version is null ? null : Phase switch
    {
        UpdatePhase.Available => $"새 버전 v{Version}이 있습니다. 업데이트할까요?",
        UpdatePhase.Downloading => $"새 버전 v{Version} 다운로드 중...",
        UpdatePhase.Ready => $"새 버전 v{Version} 다운로드 완료. 업데이트할까요?",
        UpdatePhase.Applying => $"새 버전 v{Version} 적용을 위해 재시작합니다.",
        _ => null
    });
}

/// <summary>업데이트 확인과 다운로드·적용을 직렬화하고 두 화면의 상태를 공유한다.</summary>
public sealed class UpdateService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PeerHintCooldown = TimeSpan.FromMinutes(2);

    private readonly Lazy<IUpdateBackend> _backend;
    private readonly object _lifecycle = new();
    private readonly AsyncOperationLifetime _operations = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Lazy<Task> _shutdown;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    private volatile UpdateState _state = new();
    private UpdateInfo? _availableUpdate;
    private VelopackAsset? _downloadedUpdate;
    private Task? _periodicCheck;
    private DateTimeOffset _lastPeerHintCheckUtc = DateTimeOffset.MinValue;

    internal UpdateState State => _state;
    internal event EventHandler? StateChanged;
    internal event EventHandler? RestartRequested;

    public UpdateService(UpdateSourceOptions? options = null)
        : this(() => new VelopackBackend(new UpdateManager((options ?? new UpdateSourceOptions()).CreateUpdateSource())))
    {
    }

    internal UpdateService(IUpdateBackend backend) : this(() => backend) { }

    private UpdateService(Func<IUpdateBackend> createBackend)
    {
        _backend = new Lazy<IUpdateBackend>(createBackend, LazyThreadSafetyMode.None);
        _shutdown = new Lazy<Task>(ShutdownAsync);
    }

    public void StartPeriodicChecks(bool silentAutoApplyOnStartup = false)
    {
        lock (_lifecycle)
        {
            if (_periodicCheck is null && !_stopCts.IsCancellationRequested)
            {
                _periodicCheck = RunPeriodicChecksAsync(silentAutoApplyOnStartup, _stopCts.Token);
            }
        }
    }

    private async Task RunPeriodicChecksAsync(bool silentAutoApplyOnStartup, CancellationToken ct)
    {
        try
        {
            // Windows 부팅 직후 Wi-Fi/LAN 연결 안정화 대기
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            await CheckAsync(silentAutoApplyOnStartup, ct).ConfigureAwait(false);

            if (silentAutoApplyOnStartup && State.Phase is UpdatePhase.None or UpdatePhase.Available)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                await CheckAsync(silentAutoApply: true, ct).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(PeriodicCheckInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await CheckAsync(silentAutoApply: false, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 정상 종료
        }
    }

    public void TriggerImmediateCheckFromPeerHint()
    {
        lock (_lifecycle)
        {
            var now = DateTimeOffset.UtcNow;
            if (State.Phase is UpdatePhase.Ready or UpdatePhase.Applying ||
                now - _lastPeerHintCheckUtc < PeerHintCooldown)
            {
                return;
            }
            _lastPeerHintCheckUtc = now;
        }

        _ = CheckAsync(silentAutoApply: false);
    }

    internal Task<string?> CheckForUpdatesAsync() => CheckAsync(silentAutoApply: false);

    internal Task<string?> CheckAsync(bool silentAutoApply, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (State.Phase == UpdatePhase.Applying)
            {
                return null;
            }

            try
            {
                var backend = _backend.Value;
                if (!backend.IsInstalled)
                {
                    return "개발/포터블 실행 환경입니다. 설치 버전에서 업데이트할 수 있습니다.";
                }

                _downloadedUpdate ??= backend.PendingUpdate;
                if (_downloadedUpdate is not null)
                {
                    if (silentAutoApply)
                    {
                        await DownloadAndApplyAsync(silent: true, token).ConfigureAwait(false);
                    }
                    else
                    {
                        SetState(new(UpdatePhase.Ready, _downloadedUpdate.Version.ToString()));
                    }
                    return null;
                }

                _availableUpdate = await backend.CheckForUpdatesAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (_availableUpdate is null)
                {
                    SetState(new());
                    return "현재 최신 버전의 LabPet을 사용 중입니다.";
                }

                if (silentAutoApply)
                {
                    await DownloadAndApplyAsync(silent: true, token).ConfigureAwait(false);
                }
                else
                {
                    SetState(new(UpdatePhase.Available, _availableUpdate.TargetFullRelease.Version.ToString()));
                }
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Trace.TraceWarning("업데이트 확인 실패: {0}", ex);
                return $"업데이트 확인 실패: {ex.Message}";
            }
        }, cancellationToken);

    internal Task InstallUpdateAsync()
    {
        var expected = State;
        if (!expected.CanInstall)
        {
            return Task.CompletedTask;
        }

        return RunExclusiveAsync(async token =>
        {
            // 확인 중 버전이 바뀌거나 다른 화면에서 먼저 실행했다면 기존 클릭을 재사용하지 않는다.
            if (ReferenceEquals(State, expected))
            {
                await DownloadAndApplyAsync(silent: false, token).ConfigureAwait(false);
            }
            return null;
        });
    }

    private async Task DownloadAndApplyAsync(bool silent, CancellationToken token)
    {
        var release = _downloadedUpdate ?? _availableUpdate?.TargetFullRelease;
        if (release is null)
        {
            return;
        }

        var version = release.Version.ToString();
        try
        {
            if (_downloadedUpdate is null && _availableUpdate is not null)
            {
                SetState(new(UpdatePhase.Downloading, silent ? null : version));
                await _backend.Value.DownloadUpdatesAsync(_availableUpdate, token).ConfigureAwait(false);
                _downloadedUpdate = release;
            }

            token.ThrowIfCancellationRequested();
            SetState(new(UpdatePhase.Applying, silent ? null : version));
            _backend.Value.ScheduleApplyAndRestart(release, silent);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(new(_downloadedUpdate is null ? UpdatePhase.Available : UpdatePhase.Ready, version));
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("업데이트 다운로드 또는 적용 실패: {0}", ex);
            if (_downloadedUpdate is null)
            {
                SetState(new(UpdatePhase.Available, version, $"다운로드 실패: {ex.Message}"));
            }
            else
            {
                SetState(new(UpdatePhase.Ready, version, $"업데이트 적용 실패: {ex.Message}"));
            }
            return;
        }

        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(UpdateState state)
    {
        if (state != _state)
        {
            _state = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<string?> RunExclusiveAsync(
        Func<CancellationToken, Task<string?>> run,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin();
        if (operation is null)
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
        var token = linked.Token;
        try
        {
            await _updateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await run(token).ConfigureAwait(false);
            }
            finally
            {
                _updateLock.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _shutdown.Value.GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(_shutdown.Value);

    private async Task ShutdownAsync()
    {
        var drain = _operations.DrainAsync();
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
            _updateLock.Dispose();
            _stopCts.Dispose();
        }
    }

    internal interface IUpdateBackend
    {
        bool IsInstalled { get; }
        VelopackAsset? PendingUpdate { get; }
        Task<UpdateInfo?> CheckForUpdatesAsync();
        Task DownloadUpdatesAsync(UpdateInfo update, CancellationToken ct);
        void ScheduleApplyAndRestart(VelopackAsset update, bool silent);
    }

    private sealed class VelopackBackend(UpdateManager manager) : IUpdateBackend
    {
        public bool IsInstalled => manager.IsInstalled;
        public VelopackAsset? PendingUpdate => manager.UpdatePendingRestart;
        public Task<UpdateInfo?> CheckForUpdatesAsync() => manager.CheckForUpdatesAsync();
        public Task DownloadUpdatesAsync(UpdateInfo update, CancellationToken ct)
            => manager.DownloadUpdatesAsync(update, cancelToken: ct);

        public void ScheduleApplyAndRestart(VelopackAsset update, bool silent)
            => manager.WaitExitThenApplyUpdates(update, silent: silent, restart: true, restartArgs: []);
    }
}
