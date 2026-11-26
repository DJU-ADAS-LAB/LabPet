using LabPet.Server.Options;
using LabPet.Server.Security;

namespace LabPet.Server.Services;

/// <summary>
/// 주기적으로 만료된 챌린지·대기 요청·중복/빈도 버킷·음악 시퀀스·로그 스로틀을 정리하고
/// 과부하 보호 상태를 평가(자동 복구 포함)하는 저부하 백그라운드 작업.
/// </summary>
public sealed class BackgroundCleanupService(
    ServerOptions options,
    AuthChallengeStore challenges,
    PendingFriendRequestStore pendingRequests,
    RateLimiterAndDedupService rateLimiter,
    RemoteMusicRelayDispatcher musicDispatcher,
    ThrottledLogService throttledLog,
    ServerOverloadProtectionService overload) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(options.CleanupIntervalSeconds, 5, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                challenges.PruneExpired(now);
                pendingRequests.PruneExpired(now);
                rateLimiter.PruneExpired(now);
                musicDispatcher.PruneExpired(now);
                throttledLog.PruneExpired(now);
                overload.EvaluateNow();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
