using LabPet.Server.Options;

namespace LabPet.Server.Services;

public enum ServerOverloadLevel
{
    Normal = 0,
    ShedMusic = 1,
    ShedDiscovery = 2,
    ShedNewConnections = 3
}

/// <summary>
/// 연결 비율·관리 힙 메모리·최근 거부 폭주·음악 드롭량을 감시하여
/// 원격 음악 → 비친구 공개 탐색 → 신규 연결/등록 순으로 기능을 단계 제한하고,
/// 부하가 히스테리시스 기준 이하로 일정 시간 유지되면 1단계씩 자동 복구한다.
/// </summary>
public sealed class ServerOverloadProtectionService
{
    private const long RejectionWindowMs = 10_000L;

    private readonly ServerOptions _options;
    private readonly ConnectionPresenceManager _presence;
    private readonly ThrottledLogService _throttledLog;
    private readonly ILogger<ServerOverloadProtectionService> _logger;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly Queue<long> _recentRejectionMs = new();

    private ServerOverloadLevel _currentLevel = ServerOverloadLevel.Normal;
    private DateTimeOffset? _belowHysteresisSinceUtc;
    private double? _testConnectionRatioOverride;
    private double? _testMemoryMbOverride;

    public ServerOverloadProtectionService(
        ServerOptions options,
        ConnectionPresenceManager presence,
        ThrottledLogService throttledLog,
        ILogger<ServerOverloadProtectionService> logger,
        TimeProvider? clock = null)
    {
        _options = options;
        _presence = presence;
        _throttledLog = throttledLog;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public ServerOverloadLevel CurrentLevel
    {
        get { lock (_sync) return _currentLevel; }
    }

    public bool AllowMusicRelay => CurrentLevel < ServerOverloadLevel.ShedMusic;
    public bool AllowPublicDiscovery => CurrentLevel < ServerOverloadLevel.ShedDiscovery;
    public bool AllowNewConnections => CurrentLevel < ServerOverloadLevel.ShedNewConnections;

    internal void SetTestMetricsOverride(double? connectionRatio = null, double? memoryMb = null)
    {
        lock (_sync)
        {
            _testConnectionRatioOverride = connectionRatio;
            _testMemoryMbOverride = memoryMb;
        }
    }

    public void RecordRejection()
    {
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var cutoff = nowMs - RejectionWindowMs;
        lock (_sync)
        {
            while (_recentRejectionMs.Count > 0 && _recentRejectionMs.Peek() < cutoff)
            {
                _recentRejectionMs.Dequeue();
            }

            if (_recentRejectionMs.Count < 4096)
            {
                _recentRejectionMs.Enqueue(nowMs);
            }
        }

        EvaluateNow();
    }

    public ServerOverloadLevel EvaluateNow()
    {
        var now = _clock.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var cutoff = nowMs - RejectionWindowMs;

        var maxConnections = Math.Max(1, _options.MaxTotalConnections);
        var defaultConnRatio = (double)_presence.ActiveConnectionCount / maxConnections;
        var defaultMemoryMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);

        ServerOverloadLevel? changedFrom = null;
        ServerOverloadLevel changedTo;

        lock (_sync)
        {
            while (_recentRejectionMs.Count > 0 && _recentRejectionMs.Peek() < cutoff)
            {
                _recentRejectionMs.Dequeue();
            }

            var connRatio = _testConnectionRatioOverride ?? defaultConnRatio;
            var memMb = _testMemoryMbOverride ?? defaultMemoryMb;
            var rejections = _recentRejectionMs.Count;

            var shedMusicConn = Math.Clamp(_options.OverloadShedMusicConnectionRatio, 0.3, 0.99);
            var shedDiscConn = Math.Clamp(_options.OverloadShedDiscoveryConnectionRatio, shedMusicConn, 0.995);
            var shedNewConn = Math.Clamp(_options.OverloadShedNewConnectionsRatio, shedDiscConn, 1.0);
            var hysteresis = Math.Clamp(_options.OverloadRecoveryHysteresis, 0.02, 0.30);

            var softMem = Math.Max(64.0, _options.OverloadMemorySoftLimitMb);
            var discMem = Math.Max(softMem, _options.OverloadMemoryDiscoveryLimitMb);
            var hardMem = Math.Max(discMem, _options.OverloadMemoryHardLimitMb);
            var rejThreshold = Math.Max(20, _options.OverloadRejectedPerWindowThreshold);

            var target = ServerOverloadLevel.Normal;
            if (connRatio >= shedNewConn || memMb >= hardMem)
            {
                target = ServerOverloadLevel.ShedNewConnections;
            }
            else if (connRatio >= shedDiscConn || memMb >= discMem || rejections >= rejThreshold * 2)
            {
                target = ServerOverloadLevel.ShedDiscovery;
            }
            else if (connRatio >= shedMusicConn || memMb >= softMem || rejections >= rejThreshold)
            {
                target = ServerOverloadLevel.ShedMusic;
            }

            if (target > _currentLevel)
            {
                changedFrom = _currentLevel;
                _currentLevel = target;
                _belowHysteresisSinceUtc = null;
            }
            else if (target < _currentLevel)
            {
                // 히스테리시스 하한 이하로 안정화되었는지 확인 후 단계별 복구
                var stepDownRequired = _currentLevel switch
                {
                    ServerOverloadLevel.ShedNewConnections => connRatio <= Math.Max(0, shedNewConn - hysteresis)
                        && memMb <= hardMem * 0.88,
                    ServerOverloadLevel.ShedDiscovery => connRatio <= Math.Max(0, shedDiscConn - hysteresis)
                        && memMb <= discMem * 0.88 && rejections <= rejThreshold,
                    ServerOverloadLevel.ShedMusic => connRatio <= Math.Max(0, shedMusicConn - hysteresis)
                        && memMb <= softMem * 0.88 && rejections <= rejThreshold / 2,
                    _ => true
                };

                if (stepDownRequired)
                {
                    _belowHysteresisSinceUtc ??= now;
                    var hold = TimeSpan.FromSeconds(Math.Clamp(_options.OverloadRecoveryHoldSeconds, 1, 300));
                    if (now - _belowHysteresisSinceUtc.Value >= hold)
                    {
                        changedFrom = _currentLevel;
                        _currentLevel = (ServerOverloadLevel)((int)_currentLevel - 1);
                        _belowHysteresisSinceUtc = target < _currentLevel ? now : null;
                    }
                }
                else
                {
                    _belowHysteresisSinceUtc = null;
                }
            }
            else
            {
                _belowHysteresisSinceUtc = null;
            }

            changedTo = _currentLevel;
        }

        if (changedFrom is { } prev)
        {
            _throttledLog.LogWarning(
                _logger,
                "Overload",
                changedTo.ToString(),
                "서버 과부하 보호 상태 전환: {PreviousLevel} -> {CurrentLevel}",
                prev,
                changedTo);
        }

        return changedTo;
    }
}
