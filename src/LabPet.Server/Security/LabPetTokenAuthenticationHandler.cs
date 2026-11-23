using System.Security.Claims;
using System.Text.Encodings.Web;
using LabPet.Shared.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LabPet.Server.Security;

public static class LabPetAuthDefaults
{
    public const string SchemeName = "LabPetBearer";
    public const string KeyThumbprintClaim = "labpet:key_thumb";
    public const string FriendCodeClaim = "labpet:friend_code";
}

/// <summary>
/// HTTP Authorization: Bearer 및 SignalR WebSocket access_token 쿼리를 검증하는 경량 인증 핸들러.
/// </summary>
public sealed class LabPetTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ServerTokenService tokenService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? rawToken = null;

        if (Request.Headers.TryGetValue("Authorization", out var authValues))
        {
            var header = authValues.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                rawToken = header["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(rawToken) &&
            Request.Path.StartsWithSegments(SocialHubConstants.HubPath, StringComparison.OrdinalIgnoreCase) &&
            Request.Query.TryGetValue("access_token", out var queryToken))
        {
            rawToken = queryToken.ToString().Trim();
        }

        if (string.IsNullOrEmpty(rawToken))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!tokenService.TryValidateToken(rawToken, out var claims) || claims is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("INVALID_OR_EXPIRED_TOKEN"));
        }

        var identityClaims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, claims.PetId),
            new Claim(LabPetAuthDefaults.KeyThumbprintClaim, claims.KeyThumbprint),
            new Claim(LabPetAuthDefaults.FriendCodeClaim, claims.FriendCode)
        };
        var identity = new ClaimsIdentity(identityClaims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
