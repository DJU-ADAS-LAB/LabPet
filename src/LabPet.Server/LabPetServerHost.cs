using System.Net;
using LabPet.Server.Hubs;
using LabPet.Server.Options;
using LabPet.Server.Security;
using LabPet.Server.Services;
using LabPet.Server.Storage;
using LabPet.Shared.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;

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
            ApplyEnvironmentOverrides(builder.Configuration, options);
        }

        var hasAspNetUrls = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
        if (overrideOptions is not null || !hasAspNetUrls)
        {
            builder.WebHost.UseUrls(options.ListenUrl);
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ServerTokenService>();
        builder.Services.AddSingleton<AuthChallengeStore>();
        builder.Services.AddSingleton<IdentityRegistryStore>();
        builder.Services.AddSingleton<ConnectionPresenceManager>();
        builder.Services.AddSingleton<PendingFriendRequestStore>();
        builder.Services.AddSingleton<RateLimiterAndDedupService>();
        builder.Services.AddHostedService<BackgroundCleanupService>();

        builder.Services.AddAuthentication(LabPetAuthDefaults.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LabPetTokenAuthenticationHandler>(LabPetAuthDefaults.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddSignalR(hub =>
        {
            hub.MaximumReceiveMessageSize = 32 * 1024;
            hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(35);
            hub.EnableDetailedErrors = false;
        }).AddJsonProtocol(json =>
        {
            json.PayloadSerializerOptions.PropertyNamingPolicy = ProtocolJson.PacketOptions.PropertyNamingPolicy;
            json.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var serverBootId = Guid.NewGuid().ToString("N");
        var app = builder.Build();

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            KnownProxies = { IPAddress.Loopback, IPAddress.IPv6Loopback }
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet(SocialHubConstants.HealthPath, (ConnectionPresenceManager presence) =>
            Results.Ok(new
            {
                status = "ok",
                service = "LabPet.Server",
                bootId = serverBootId,
                onlinePets = presence.OnlinePetCount,
                activeConnections = presence.ActiveConnectionCount,
                utcNow = DateTimeOffset.UtcNow
            }));

        app.MapPost(SocialHubConstants.AuthChallengePath, (AuthChallengeRequest req, AuthChallengeStore challenges) =>
        {
            if (req is null)
            {
                return Results.BadRequest(new { error = "INVALID_REQUEST" });
            }

            var issued = challenges.IssueChallenge(req.PetId, req.DevicePublicKeyBase64);
            return issued is null
                ? Results.BadRequest(new { error = "INVALID_IDENTITY_OR_CAPACITY" })
                : Results.Ok(issued);
        });

        app.MapPost(SocialHubConstants.AuthTokenPath, (
            AuthTokenRequest req,
            AuthChallengeStore challenges,
            IdentityRegistryStore registry,
            ServerTokenService tokens) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.PetId) || string.IsNullOrWhiteSpace(req.Nonce))
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

            if (!registry.TryAuthenticateOrRegister(
                req.PetId,
                req.DevicePublicKeyBase64,
                req.FriendCode,
                req.OwnerName,
                req.PetName,
                out var record,
                out var authError) || record is null)
            {
                var status = string.Equals(authError, "PUBLIC_KEY_MISMATCH", StringComparison.Ordinal)
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status400BadRequest;
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

    private static void ApplyEnvironmentOverrides(IConfiguration config, ServerOptions target)
    {
        var listenUrl = config["LABPET_LISTEN_URL"];
        if (!string.IsNullOrWhiteSpace(listenUrl)) target.ListenUrl = listenUrl.Trim();

        var secret = config["LABPET_SERVER_SECRET"];
        if (!string.IsNullOrWhiteSpace(secret)) target.ServerSecret = secret;

        var requireSecret = config["LABPET_REQUIRE_SECRET"];
        if (bool.TryParse(requireSecret, out var req)) target.RequireConfiguredSecret = req;

        var dataDir = config["LABPET_DATA_DIR"];
        if (!string.IsNullOrWhiteSpace(dataDir)) target.DataDirectory = dataDir.Trim();
    }
}
