using System.Collections.Concurrent;
using System.Security.Cryptography;
using LabPet.Server.Options;
using LabPet.Shared.Protocol;

namespace LabPet.Server.Security;

/// <summary>
/// 메모리 기반 1회용 논스 챌린지 저장소 (상한 2,048개, TTL 60초, 재사용 불가).
/// </summary>
public sealed class AuthChallengeStore(ServerOptions options)
{
    private const int MaxPendingChallenges = 2048;

    private sealed record ChallengeEntry(
        string PetId,
        string PublicKeyBase64,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, ChallengeEntry> _challenges = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(Math.Clamp(options.ChallengeLifetimeSeconds, 15, 300));

    public AuthChallengeResponse? IssueChallenge(string petId, string devicePublicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(petId) || petId.Length > 64
            || !RemoteAuthSigner.IsValidDevicePublicKey(devicePublicKeyBase64))
        {
            return null;
        }

        if (_challenges.Count >= MaxPendingChallenges)
        {
            PruneExpired(DateTimeOffset.UtcNow);
            if (_challenges.Count >= MaxPendingChallenges)
            {
                return null;
            }
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var now = DateTimeOffset.UtcNow;
        var expires = now + _lifetime;
        _challenges[nonce] = new ChallengeEntry(petId.Trim(), devicePublicKeyBase64, now, expires);

        return new AuthChallengeResponse
        {
            Nonce = nonce,
            IssuedAtUtc = now,
            ExpiresAtUtc = expires
        };
    }

    public bool TryConsumeChallenge(
        string nonce,
        string petId,
        string devicePublicKeyBase64,
        out DateTimeOffset issuedAtUtc)
    {
        issuedAtUtc = default;
        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(petId))
        {
            return false;
        }

        if (!_challenges.TryRemove(nonce, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return false;
        }

        if (!string.Equals(entry.PetId, petId.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.PublicKeyBase64, devicePublicKeyBase64, StringComparison.Ordinal))
        {
            return false;
        }

        issuedAtUtc = entry.IssuedAtUtc;
        return true;
    }

    public void PruneExpired(DateTimeOffset now)
    {
        foreach (var kv in _challenges)
        {
            if (kv.Value.ExpiresAtUtc < now)
            {
                _challenges.TryRemove(kv.Key, out _);
            }
        }
    }
}
