using System.Security.Claims;
using LabPet.Server.Options;
using LabPet.Server.Services;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Microsoft.AspNetCore.SignalR;

namespace LabPet.Server.Hubs;

/// <summary>
/// SignalR Hub 진입 계층에서 인증된 사용자별·메서드별 요청 빈도를 공통 제한한다.
/// </summary>
public sealed class SocialHubRateLimitFilter(
    ServerOptions options,
    RateLimiterAndDedupService rateLimiter,
    ServerOverloadProtectionService overload,
    ThrottledLogService throttledLog,
    ILogger<SocialHubRateLimitFilter> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var petId = invocationContext.Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(petId))
        {
            var method = invocationContext.HubMethodName;
            switch (method)
            {
                case SocialHubConstants.RegisterPresenceMethod:
                    if (!rateLimiter.AllowHubMethod(petId, method, 60_000L, Math.Clamp(options.RegisterPresencePerMinute, 2, 120)))
                    {
                        overload.RecordRejection();
                        throttledLog.LogWarning(logger, "HubRate", $"{petId}:{method}", "Hub 요청 제한 초과: Method={Method}", method);
                        throw new HubException(FriendOperationErrorCodes.RateLimited);
                    }
                    break;

                case SocialHubConstants.UpdatePresenceMethod:
                    if (!rateLimiter.AllowHubMethod(petId, method, 10_000L, Math.Clamp(options.UpdatePresencePer10Seconds, 2, 200)))
                    {
                        overload.RecordRejection();
                        throttledLog.LogWarning(logger, "HubRate", $"{petId}:{method}", "프레즌스 갱신 과다 억제: Method={Method}", method);
                        return null;
                    }
                    break;

                case SocialHubConstants.SyncKnownFriendsMethod:
                    if (!rateLimiter.AllowHubMethod(petId, method, 60_000L, Math.Clamp(options.SyncKnownFriendsPerMinute, 2, 120)))
                    {
                        overload.RecordRejection();
                        throttledLog.LogWarning(logger, "HubRate", $"{petId}:{method}", "Hub 요청 제한 초과: Method={Method}", method);
                        throw new HubException(FriendOperationErrorCodes.RateLimited);
                    }
                    break;

                case SocialHubConstants.RemoveFriendMethod:
                    if (!rateLimiter.AllowHubMethod(petId, method, 60_000L, Math.Clamp(options.SyncKnownFriendsPerMinute, 2, 120)))
                    {
                        overload.RecordRejection();
                        throttledLog.LogWarning(logger, "HubRate", $"{petId}:{method}", "Hub 요청 제한 초과: Method={Method}", method);
                        throw new HubException(FriendOperationErrorCodes.RateLimited);
                    }
                    break;
            }
        }

        return await next(invocationContext).ConfigureAwait(false);
    }
}
