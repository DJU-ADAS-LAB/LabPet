using System.Net;
using System.Threading.RateLimiting;
using LabPet.Server.Hubs;
using LabPet.Server.Options;
using LabPet.Server.Security;
using LabPet.Server.Services;
using LabPet.Server.Storage;
using LabPet.Shared.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;

namespace LabPet.Server;

/// <summary>
/// 실제 운영 Program 진입점과 인프로세스 Kestrel 통합 테스트가 동일한 서버 파이프라인을 공유하기 위한 빌더.
/// </summary>
public static class LabPetServerHost
{
    public static WebApplication BuildApplication(
        ServerOptions? overrideOptions = null,
        string[]? args = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        configureLogging?.Invoke(builder.Logging);

        var options = overrideOptions ?? new ServerOptions();
        if (overrideOptions is null)
        {
            builder.Configuration.GetSection(ServerOptions.SectionName).Bind(options);
            ApplyEnvironmentOverrides(builder.Configuration, options, builder.Environment.IsProduction());
        }

        var hasAspNetUrls = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
        if (overrideOptions is not null || !hasAspNetUrls)
        {
            builder.WebHost.UseUrls(options.ListenUrl);
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxConcurrentConnections = Math.Clamp(options.KestrelMaxConcurrentConnections, 16, 10_000);
            kestrel.Limits.MaxConcurrentUpgradedConnections = Math.Clamp(options.KestrelMaxConcurrentUpgradedConnections, 16, 10_000);
            kestrel.Limits.MaxRequestBodySize = Math.Clamp(options.KestrelMaxRequestBodyBytes, 4096, 1024 * 1024);
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ServerTokenService>();
        builder.Services.AddSingleton<AuthChallengeStore>();
        builder.Services.AddSingleton<IdentityRegistryStore>();
        builder.Services.AddSingleton<ConnectionPresenceManager>();
        builder.Services.AddSingleton<PendingFriendRequestStore>();
        builder.Services.AddSingleton<RateLimiterAndDedupService>();
        builder.Services.AddSingleton<ThrottledLogService>();
        builder.Services.AddSingleton<ServerOverloadProtectionService>();
        builder.Services.AddSingleton<RemoteMusicRelayDispatcher>();
        builder.Services.AddSingleton<SocialHubRateLimitFilter>();
        builder.Services.AddHostedService<BackgroundCleanupService>();

        builder.Services.AddAuthentication(LabPetAuthDefaults.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LabPetTokenAuthenticationHandler>(LabPetAuthDefaults.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddRateLimiter(rateLimitOptions =>
        {
            rateLimitOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimitOptions.OnRejected = async (context, ct) =>
            {
                var overload = context.HttpContext.RequestServices.GetRequiredService<ServerOverloadProtectionService>();
                var throttledLog = context.HttpContext.RequestServices.GetRequiredService<ThrottledLogService>();
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("LabPet.Server.RateLimit");
                var remoteIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                overload.RecordRejection();
                throttledLog.LogWarning(
                    logger,
                    "HttpRateLimit",
                    $"{remoteIp}|{context.HttpContext.Request.Path}",
                    "HTTP 요청 빈도 제한 초과: Path={Path}, RemoteIp={RemoteIp}",
                    context.HttpContext.Request.Path.Value ?? "/",
                    remoteIp);
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsync("""{"error":"RATE_LIMITED"}""", ct).ConfigureAwait(false);
            };

            rateLimitOptions.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => CreatePerIpPartition(ctx, options)),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => CreateGlobalPartition(ctx, options)),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => CreateConcurrencyPartition(ctx, options)));
        });

        builder.Services.AddSignalR(hub =>
        {
            hub.MaximumReceiveMessageSize = 32 * 1024;
            hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(35);
            hub.EnableDetailedErrors = false;
            hub.AddFilter<SocialHubRateLimitFilter>();
        }).AddJsonProtocol(json =>
        {
            json.PayloadSerializerOptions.PropertyNamingPolicy = ProtocolJson.PacketOptions.PropertyNamingPolicy;
            json.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var serverBootId = Guid.NewGuid().ToString("N");
        var app = builder.Build();

        var forwardedOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        forwardedOptions.KnownProxies.Clear();
        forwardedOptions.KnownIPNetworks.Clear();
        if (options.TrustedProxies is { Length: > 0 })
        {
            foreach (var proxy in options.TrustedProxies)
            {
                if (IPAddress.TryParse(proxy?.Trim(), out var parsedProxy))
                {
                    forwardedOptions.KnownProxies.Add(parsedProxy);
                }
            }
        }
        if (forwardedOptions.KnownProxies.Count == 0)
        {
            forwardedOptions.KnownProxies.Add(IPAddress.Loopback);
            forwardedOptions.KnownProxies.Add(IPAddress.IPv6Loopback);
        }
        app.UseForwardedHeaders(forwardedOptions);

        var enforceHttps = options.EnforceHttps || (overrideOptions is null && app.Environment.IsProduction());
        if (enforceHttps)
        {
            app.Use(async (context, next) =>
            {
                var isLocalHealth = context.Request.Path.StartsWithSegments(SocialHubConstants.HealthPath, StringComparison.OrdinalIgnoreCase)
                    && context.Connection.RemoteIpAddress is { } remoteIp
                    && IPAddress.IsLoopback(remoteIp);

                if (!context.Request.IsHttps && !isLocalHealth)
                {
                    context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                    await context.Response.WriteAsJsonAsync(new { error = "HTTPS_REQUIRED" }).ConfigureAwait(false);
                    return;
                }

                await next(context).ConfigureAwait(false);
            });
        }

        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet(SocialHubConstants.HealthPath, () =>
            Results.Ok(new
            {
                status = "ok",
                bootId = serverBootId
            }));

        app.MapPost(SocialHubConstants.AuthChallengePath, (
            HttpContext httpContext,
            AuthChallengeRequest req,
            AuthChallengeStore challenges,
            ServerOverloadProtectionService overload) =>
        {
            if (!RemoteDtoValidator.ValidateAuthChallengeRequest(req))
            {
                return Results.BadRequest(new { error = "INVALID_IDENTITY_OR_CAPACITY" });
            }

            if (!overload.AllowNewConnections)
            {
                overload.RecordRejection();
                return Results.Json(new { error = "SERVER_OVERLOADED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
            var issued = challenges.IssueChallenge(req.PetId, req.DevicePublicKeyBase64, remoteIp);
            if (issued is null)
            {
                overload.RecordRejection();
                return Results.Json(new { error = "INVALID_IDENTITY_OR_CAPACITY" }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            return Results.Ok(issued);
        });

        app.MapPost(SocialHubConstants.AuthTokenPath, (
            HttpContext httpContext,
            AuthTokenRequest req,
            AuthChallengeStore challenges,
            IdentityRegistryStore registry,
            ServerTokenService tokens,
            RateLimiterAndDedupService rateLimiter,
            ServerOverloadProtectionService overload) =>
        {
            if (!RemoteDtoValidator.ValidateAuthTokenRequest(req))
            {
                return Results.BadRequest(new { error = "INVALID_REQUEST" });
            }

            if (!challenges.TryConsumeChallenge(req.Nonce, req.PetId, req.DevicePublicKeyBase64, out var issuedAtUtc))
            {
                return Results.Json(new { error = "INVALID_OR_EXPIRED_CHALLENGE" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var canonical = RemoteAuthSigner.BuildChallengeCanonicalString(
                req.Nonce,
                req.PetId.Trim(),
                req.DevicePublicKeyBase64,
                issuedAtUtc);

            if (!RemoteAuthSigner.VerifyChallengeSignature(req.DevicePublicKeyBase64, canonical, req.SignatureBase64))
            {
                return Results.Json(new { error = "INVALID_SIGNATURE" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!registry.IsKnownIdentity(req.PetId))
            {
                if (!overload.AllowNewConnections)
                {
                    overload.RecordRejection();
                    return Results.Json(new { error = "SERVER_OVERLOADED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (!rateLimiter.AllowNewRegistration(remoteIp))
                {
                    overload.RecordRejection();
                    return Results.Json(new { error = "RATE_LIMITED" }, statusCode: StatusCodes.Status429TooManyRequests);
                }
            }

            if (!registry.TryAuthenticateOrRegister(
                req.PetId,
                req.DevicePublicKeyBase64,
                req.FriendCode,
                req.OwnerName,
                req.PetName,
                out var record,
                out var authError) || record is null)
            {
                var status = authError switch
                {
                    "PUBLIC_KEY_MISMATCH" => StatusCodes.Status403Forbidden,
                    "REGISTRY_CAPACITY_REACHED" => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status400BadRequest
                };
                return Results.Json(new { error = authError }, statusCode: status);
            }

            var (accessToken, expiresAtUtc) = tokens.IssueToken(record.PetId, record.KeyThumbprint, record.FriendCode);
            return Results.Ok(new AuthTokenResponse
            {
                AccessToken = accessToken,
                VerifiedPetId = record.PetId,
                ExpiresAtUtc = expiresAtUtc
            });
        });

        app.MapHub<SocialHub>(SocialHubConstants.HubPath);
        return app;
    }

    private static string ResolveEndpointBucket(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.Equals(SocialHubConstants.AuthChallengePath, StringComparison.OrdinalIgnoreCase))
        {
            return "auth-challenge";
        }

        if (path.Equals(SocialHubConstants.AuthTokenPath, StringComparison.OrdinalIgnoreCase))
        {
            return "auth-token";
        }

        if (path.Equals(SocialHubConstants.HealthPath, StringComparison.OrdinalIgnoreCase))
        {
            return "health";
        }

        return "bypass";
    }

    private static RateLimitPartition<string> CreatePerIpPartition(HttpContext ctx, ServerOptions options)
    {
        var endpoint = ResolveEndpointBucket(ctx);
        if (endpoint == "bypass")
        {
            return RateLimitPartition.GetNoLimiter("bypass");
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var limit = endpoint switch
        {
            "auth-challenge" => Math.Clamp(options.AuthChallengePerIpPerMinute, 2, 2000),
            "auth-token" => Math.Clamp(options.AuthTokenPerIpPerMinute, 2, 2000),
            _ => Math.Clamp(options.HealthPerIpPerMinute, 5, 5000)
        };

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{endpoint}|ip|{ip}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    private static RateLimitPartition<string> CreateGlobalPartition(HttpContext ctx, ServerOptions options)
    {
        var endpoint = ResolveEndpointBucket(ctx);
        if (endpoint == "bypass")
        {
            return RateLimitPartition.GetNoLimiter("bypass");
        }

        var limit = endpoint switch
        {
            "auth-challenge" => Math.Clamp(options.AuthChallengeGlobalPerMinute, 10, 20_000),
            "auth-token" => Math.Clamp(options.AuthTokenGlobalPerMinute, 10, 20_000),
            _ => Math.Clamp(options.HealthGlobalPerMinute, 20, 50_000)
        };

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{endpoint}|global",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    private static RateLimitPartition<string> CreateConcurrencyPartition(HttpContext ctx, ServerOptions options)
    {
        var endpoint = ResolveEndpointBucket(ctx);
        if (endpoint == "bypass")
        {
            return RateLimitPartition.GetNoLimiter("bypass");
        }

        var limit = endpoint switch
        {
            "auth-challenge" => Math.Clamp(options.AuthChallengeMaxConcurrency, 2, 512),
            "auth-token" => Math.Clamp(options.AuthTokenMaxConcurrency, 2, 512),
            _ => Math.Clamp(options.AuthChallengeMaxConcurrency, 4, 512)
        };

        return RateLimitPartition.GetConcurrencyLimiter(
            $"{endpoint}|conc",
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = limit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    }

    private static void ApplyEnvironmentOverrides(IConfiguration config, ServerOptions target, bool isProduction)
    {
        var listenUrl = config["LABPET_LISTEN_URL"];
        if (!string.IsNullOrWhiteSpace(listenUrl)) target.ListenUrl = listenUrl.Trim();

        var secret = config["LABPET_SERVER_SECRET"];
        if (!string.IsNullOrWhiteSpace(secret)) target.ServerSecret = secret;

        var requireSecret = config["LABPET_REQUIRE_SECRET"];
        if (bool.TryParse(requireSecret, out var req)) target.RequireConfiguredSecret = req;
        else if (isProduction) target.RequireConfiguredSecret = true;

        var enforceHttps = config["LABPET_ENFORCE_HTTPS"];
        if (bool.TryParse(enforceHttps, out var https)) target.EnforceHttps = https;
        else if (isProduction) target.EnforceHttps = true;

        var trustedProxies = config["LABPET_TRUSTED_PROXIES"];
        if (!string.IsNullOrWhiteSpace(trustedProxies))
        {
            target.TrustedProxies = trustedProxies
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var dataDir = config["LABPET_DATA_DIR"];
        if (!string.IsNullOrWhiteSpace(dataDir)) target.DataDirectory = dataDir.Trim();
    }
}
