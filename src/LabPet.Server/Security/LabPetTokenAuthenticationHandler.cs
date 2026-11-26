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
/// HTTP 및 SignalR WebSocket 요청의 Authorization Bearer 헤더만 검증한다.
/// URL·프록시·서버 로그 유출 방지를 위해 쿼리스트링 access_token은 허용하지 않는다.
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
