using LabPet.Server.Security;

namespace LabPet.Server.Services;

/// <summary>
/// 60초 주기로 만료된 챌린지·대기 요청·중복 캐시를 정리하는 저부하 백그라운드 작업.
/// </summary>
public sealed class BackgroundCleanupService(
    AuthChallengeStore challenges,
    PendingFriendRequestStore pendingRequests,
    RateLimiterAndDedupService rateLimiter) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
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
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
