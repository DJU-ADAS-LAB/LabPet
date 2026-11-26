using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabPet.Server.Options;

namespace LabPet.Server.Security;

public sealed record ValidatedTokenClaims(
    string PetId,
    string KeyThumbprint,
    string FriendCode,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// 최소 비용 운영을 위해 외부 인증 DB 없이 HMAC-SHA256 서명 단기 Bearer 토큰을 발급·검증한다.
/// </summary>
public sealed class ServerTokenService
{
    private sealed record TokenPayload(string PetId, string Thumbprint, string FriendCode, long ExpMs);

    private readonly byte[] _signingKey;
    private readonly TimeSpan _tokenLifetime;

    private static readonly string[] WeakSecretPlaceholders =
    [
        "changeme",
        "replace",
        "default",
        "example",
        "secret123",
        "password",
        "labpet-dev"
    ];

    public ServerTokenService(ServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServerSecret))
        {
            if (options.RequireConfiguredSecret)
            {
                throw new InvalidOperationException(
                    "LABPET_SERVER_SECRET(ServerSecret)가 설정되지 않았습니다. 운영 환경에서는 32바이트 이상 비밀키를 반드시 구성해야 합니다.");
            }

            // 개발·테스트 환경 전용 프로세스 단위 휘발성 키 (로그로 출력하지 않음)
            _signingKey = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            var trimmedSecret = options.ServerSecret.Trim();
            if (options.RequireConfiguredSecret)
            {
                ValidateConfiguredSecret(trimmedSecret);
            }

            _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(trimmedSecret));
        }

        _tokenLifetime = TimeSpan.FromMinutes(Math.Clamp(options.TokenLifetimeMinutes, 10, 1440));
    }

    internal static void ValidateConfiguredSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("서버 비밀키가 비어 있습니다.");
        }

        if (Encoding.UTF8.GetByteCount(secret) < 32)
        {
            throw new InvalidOperationException("운영 환경 서버 비밀키는 UTF-8 기준 32바이트 이상이어야 합니다.");
        }

        var distinctChars = secret.Distinct().Count();
        if (distinctChars < 10)
        {
            throw new InvalidOperationException("운영 환경 서버 비밀키의 엔트로피(고유 문자 수)가 부족합니다.");
        }

        foreach (var placeholder in WeakSecretPlaceholders)
        {
            if (secret.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("운영 환경 서버 비밀키에 기본 또는 플레이스홀더 문자열을 사용할 수 없습니다.");
            }
        }
    }

    public (string Token, DateTimeOffset ExpiresAtUtc) IssueToken(string petId, string keyThumbprint, string friendCode)
    {
        var expiresAt = DateTimeOffset.UtcNow + _tokenLifetime;
        var payload = new TokenPayload(petId, keyThumbprint, friendCode, expiresAt.ToUnixTimeMilliseconds());
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var payloadB64 = Base64UrlEncode(payloadBytes);
        var sigBytes = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payloadB64));
        var token = $"{payloadB64}.{Base64UrlEncode(sigBytes)}";
        return (token, expiresAt);
    }

    public bool TryValidateToken(string? token, out ValidatedTokenClaims? claims)
    {
        claims = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024)
        {
            return false;
        }

        var dotIdx = token.IndexOf('.');
        if (dotIdx <= 0 || dotIdx >= token.Length - 1)
        {
            return false;
        }

        var payloadPart = token[..dotIdx];
        var sigPart = token[(dotIdx + 1)..];

        try
        {
            var expectedSig = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payloadPart));
            var actualSig = Base64UrlDecode(sigPart);
            if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
            {
                return false;
            }

            var payloadBytes = Base64UrlDecode(payloadPart);
            var payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes);
            if (payload is null || string.IsNullOrWhiteSpace(payload.PetId) || string.IsNullOrWhiteSpace(payload.Thumbprint))
            {
                return false;
            }

            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpMs);
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            claims = new ValidatedTokenClaims(payload.PetId, payload.Thumbprint, payload.FriendCode ?? string.Empty, expiresAt);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        var mod = padded.Length % 4;
        if (mod == 2) padded += "==";
        else if (mod == 3) padded += "=";
        else if (mod == 1) throw new FormatException("Invalid Base64Url length.");
        return Convert.FromBase64String(padded);
    }
}
